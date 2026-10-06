using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Congregation
{
    // #845: the optional ceremony sidecar beside Y4NGZCompany.dll: the ceremony audio and the
    // knife, drumstick, drum and ritual fire pit props (see docs/assets/28-congregation-props-sidecar.md). Every
    // accessor is null-safe: an absent bundle or clip logs one warning and the ceremony simply
    // plays without that sound; an absent prop is reported once in one Info line and skipped.
    internal static class CongregationPropsAssets
    {
        internal const string FileName = "y4ngz-congregation-props.bundle";
        internal const string KnifeName = "CongregationKnife";
        internal const string DrumstickName = "CongregationDrumstick";
        internal const string DrumName = "CongregationDrum";
        internal const string RitualFirePitName = "CongregationRitualFirePit";
        private static AssetBundle _bundle;
        private static bool _attempted;
        private static bool _optionalReported;
        private static int _lastHit = -1;
        private static readonly Dictionary<string, UnityEngine.Object> Assets = new Dictionary<string, UnityEngine.Object>(StringComparer.OrdinalIgnoreCase);
        // Names already reported; the null is cached in Assets, so each miss is warned once.
        private static readonly HashSet<string> Missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly string[] HitNames = { "CongregationHit1", "CongregationHit2", "CongregationHit3" };
        // Props the current bundle carries; an older or partial bundle without them is reported, not a fault.
        private static readonly string[] OptionalPropNames = { KnifeName, DrumstickName, DrumName, RitualFirePitName };

        internal static AudioClip Blows => Load<AudioClip>("CongregationBlows");
        internal static AudioClip DrumLoop => Load<AudioClip>("CongregationDrumLoop");
        internal static AudioClip TribalLoop => Load<AudioClip>("CongregationTribalLoop");
        // Visual-only prefabs (MeshFilter/MeshRenderer, no colliders or scripts); pivot contract in
        // the sidecar doc. Null until the bundle carries them.
        internal static GameObject Knife => TryLoadOptional<GameObject>(KnifeName);
        internal static GameObject Drumstick => TryLoadOptional<GameObject>(DrumstickName);
        internal static GameObject Drum => TryLoadOptional<GameObject>(DrumName);
        internal static GameObject RitualFirePit => TryLoadOptional<GameObject>(RitualFirePitName);

        // One of the three body hits, never the same one twice in a row; null if none load.
        internal static AudioClip RandomHit()
        {
            int loaded = HitNames.Count(n => Load<AudioClip>(n) != null);
            if (loaded == 0) return null;
            int pick;
            do pick = UnityEngine.Random.Range(0, HitNames.Length);
            while (Load<AudioClip>(HitNames[pick]) == null || (pick == _lastHit && loaded > 1));
            _lastHit = pick;
            return Load<AudioClip>(HitNames[pick]);
        }

        internal static T Load<T>(string name) where T : UnityEngine.Object
        {
            T value = LoadCached<T>(name, out bool fresh);
            if (fresh && value == null && _bundle != null && Missing.Add(name))
                ContractsBootstrap.Log?.LogWarning("[Congregation.Props] " + FileName + " has no " + typeof(T).Name
                    + " named '" + name + "'; every use of it is silently skipped until the bundle is rebuilt.");
            return value;
        }

        /// <summary>Quiet form of <see cref="Load{T}"/> for assets the bundle may not carry yet. The
        /// first call reports, in one Info line for the session, which optional props are absent;
        /// no call ever warns.</summary>
        internal static T TryLoadOptional<T>(string name) where T : UnityEngine.Object
        {
            if (!_optionalReported)
            {
                _optionalReported = true;
                var absent = OptionalPropNames.Where(n => LoadCached<GameObject>(n, out _) == null).ToArray();
                if (absent.Length > 0)
                    ContractsBootstrap.Log?.LogInfo("[Congregation.Props] Optional props not in " + FileName + " (older or partial bundle): "
                        + string.Join(", ", absent) + "; they are skipped.");
            }
            return LoadCached<T>(name, out _);
        }

        private static T LoadCached<T>(string name, out bool fresh) where T : UnityEngine.Object
        {
            if (!_attempted)
            {
                _attempted = true;
                string path = Path.Combine(Path.GetDirectoryName(typeof(CongregationPropsAssets).Assembly.Location), FileName);
                if (File.Exists(path)) _bundle = AssetBundle.LoadFromFile(path);
                // Optional sidecar: absent or unreadable is one warning, and every lookup returns null.
                if (_bundle == null)
                    ContractsBootstrap.Log?.LogWarning("[Congregation.Props] Optional sidecar " + FileName
                        + " missing or unreadable; ceremony audio and props are skipped.");
            }
            fresh = !Assets.TryGetValue(name, out var cached);
            if (!fresh) return cached as T;
            var value = _bundle != null ? _bundle.LoadAsset<T>(name) : null;
            Assets[name] = value;
            return value;
        }
    }
}
