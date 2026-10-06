using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.Whistleblower
{
    // Loads the Whistleblower combat foley (.ogg pools deployed under
    // ContractAudio/Whistleblower) once per game session.
    internal static class WhistleblowerSoundLibrary
    {
        private static readonly string[] SwingMissFiles =
        {
            "universal_swing_miss_light_ufx_1.ogg",
            "universal_swing_miss_light_ufx_2.ogg",
        };

        private static readonly string[] BluntHitFiles =
        {
            "blunt_hit_body_ufx_1.ogg",
            "blunt_hit_body_ufx_2.ogg",
            "blunt_hit_body_ufx_3.ogg",
            "blunt_hit_body_ufx_4.ogg",
        };

        // #982: the punch landing on a player. It plays at the victim, not at him, so it
        // is its own pool rather than a reuse of the blunt-hit foley he takes himself.
        private static readonly string[] PunchImpactFiles =
        {
            "impact_body_ufx_2.ogg",
        };

        private static readonly string[] FootstepFiles =
        {
            "footstep_concrete_ufx_1.ogg",
            "footstep_concrete_ufx_2.ogg",
            "footstep_concrete_ufx_3.ogg",
            "footstep_concrete_ufx_4.ogg",
        };

        private static readonly List<AudioClip> SwingMiss = new List<AudioClip>();
        private static readonly List<AudioClip> BluntHits = new List<AudioClip>();
        private static readonly List<AudioClip> PunchImpacts = new List<AudioClip>();
        private static readonly List<AudioClip> Footsteps = new List<AudioClip>();
        private static bool _loadStarted;

        /// <summary>
        /// Starts the one-time load. #984: called from plugin bootstrap against the plugin's
        /// own persistent host (ContractsBootstrap.Initialize), because loading eleven .ogg
        /// files over UnityWebRequest from WhistleblowerEnemyAI.Start left the first seconds
        /// after he spawned silent — the first swing and the first hit had no clip yet. The
        /// Start call stays as the fallback for a pool that never warmed.
        /// </summary>
        internal static void EnsureLoaded(MonoBehaviour host)
        {
            if (_loadStarted || host == null)
                return;

            _loadStarted = true;
            host.StartCoroutine(LoadAll());
        }

        internal static AudioClip RandomSwingMiss() => RandomFrom(SwingMiss);

        internal static AudioClip RandomFootstep() => RandomFrom(Footsteps);

        /// <summary>#982: the body impact for a landed punch, played at the victim on every client.</summary>
        internal static AudioClip RandomPunchImpact() => RandomFrom(PunchImpacts);

        internal static AudioClip DeterministicBluntHit(int seed)
        {
            if (BluntHits.Count == 0)
                return null;
            return BluntHits[Mathf.Abs(seed) % BluntHits.Count];
        }

        private static AudioClip RandomFrom(List<AudioClip> pool)
        {
            if (pool.Count == 0)
                return null;
            return pool[UnityEngine.Random.Range(0, pool.Count)];
        }

        private static IEnumerator LoadAll()
        {
            string directory = ResolveAudioDirectory();
            if (directory == null)
            {
                // #984: the preload now runs at plugin init, long before a round exists, so a
                // directory that cannot be resolved there must not permanently disable the
                // foley. Release the latch and let the in-round Start call try again.
                _loadStarted = false;
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts] Whistleblower sound directory not found; retrying on first spawn.");
                yield break;
            }

            yield return LoadPool(directory, SwingMissFiles, SwingMiss);
            yield return LoadPool(directory, BluntHitFiles, BluntHits);
            yield return LoadPool(directory, PunchImpactFiles, PunchImpacts);
            yield return LoadPool(directory, FootstepFiles, Footsteps);
            // #984: same reasoning — a load that produced nothing at all (files missing at
            // init, storage not ready) stays retryable rather than latching silence in.
            if (SwingMiss.Count == 0 && BluntHits.Count == 0 && PunchImpacts.Count == 0 && Footsteps.Count == 0)
                _loadStarted = false;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Whistleblower foley loaded: {SwingMiss.Count} swing, {BluntHits.Count} blunt, {PunchImpacts.Count} punch impact, {Footsteps.Count} footstep clips.");
        }

        private static IEnumerator LoadPool(string directory, string[] files, List<AudioClip> target)
        {
            foreach (string file in files)
            {
                string path = Path.Combine(directory, file);
                if (!File.Exists(path))
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Whistleblower sound missing: {path}");
                    continue;
                }

                using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(
                    new Uri(path).AbsoluteUri, AudioType.OGGVORBIS))
                {
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        ContractsBootstrap.Log?.LogWarning(
                            $"[MoonContracts] Failed loading Whistleblower sound '{file}': {request.error}");
                        continue;
                    }

                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        clip.name = Path.GetFileNameWithoutExtension(file);
                        target.Add(clip);
                    }
                }
            }
        }

        private static string ResolveAudioDirectory()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(ContractsBootstrap).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(assemblyDir, "ContractAudio", "Whistleblower"),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "ContractAudio", "Whistleblower"),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "ContractAudio", "Whistleblower"),
                Path.Combine(Paths.PluginPath, "ContractAudio", "Whistleblower"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ContractAudio", "Whistleblower"),
            };

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate) && File.Exists(Path.Combine(candidate, FootstepFiles[0])))
                    return candidate;
            }

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate))
                    return candidate;
            }

            return null;
        }
    }
}
