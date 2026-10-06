using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// #756. The Pest Control trap's authored cues, loaded once per session from the loose
    /// <c>ContractAudio\PestControl</c> folder the csproj stages beside the DLL (the same loose-file
    /// pipeline Waste Disposal and Containment Breach use). Everything here is optional: a missing
    /// or unreadable file leaves its slot null and the controller falls back to the vanilla
    /// placeholder it played before.
    /// </summary>
    internal static class PestControlTrapAudio
    {
        private const string AudioFolder = "PestControl";
        private const string LogPrefix = "[MoonContracts.PestControl]";

        private static readonly string[] LeverFileNames = { "lever_mx_1.ogg", "lever_2.ogg" };
        private const string DropFileName = "heavy_door_lock-unlock_1.ogg";
        private const string RaiseFileName = "heavy_door_open-close_1.ogg";

        private static readonly List<AudioClip> LeverClipList = new List<AudioClip>(2);
        private static bool _loadStarted;

        /// <summary>Lever throw; one of these is picked at random per pull. Empty until loaded.</summary>
        internal static IReadOnlyList<AudioClip> LeverClips => LeverClipList;

        /// <summary>Cage landing. Null until loaded or when the file is missing.</summary>
        internal static AudioClip DropClip { get; private set; }

        /// <summary>Winch raise start. Null until loaded or when the file is missing.</summary>
        internal static AudioClip RaiseClip { get; private set; }

        internal static AudioClip PickLeverClip()
        {
            if (LeverClipList.Count == 0)
                return null;

            return LeverClipList[UnityEngine.Random.Range(0, LeverClipList.Count)];
        }

        /// <summary>
        /// Kicks off the one-time load on <paramref name="host"/>. Safe to call from every trap;
        /// only the first call does anything. The load is asynchronous, so a lever pulled in the
        /// first second of a round may still hit the vanilla fallback.
        /// </summary>
        internal static void EnsureLoaded(MonoBehaviour host)
        {
            if (_loadStarted || host == null)
                return;

            _loadStarted = true;
            host.StartCoroutine(LoadAll());
        }

        private static IEnumerator LoadAll()
        {
            for (int i = 0; i < LeverFileNames.Length; i++)
            {
                yield return LoadClip(LeverFileNames[i], clip => LeverClipList.Add(clip));
            }

            yield return LoadClip(DropFileName, clip => DropClip = clip);
            yield return LoadClip(RaiseFileName, clip => RaiseClip = clip);

            ContractsBootstrap.Log?.LogInfo(
                $"{LogPrefix} Trap audio loaded: lever={LeverClipList.Count}/{LeverFileNames.Length} " +
                $"drop={(DropClip != null ? "ok" : "fallback")} raise={(RaiseClip != null ? "ok" : "fallback")}");
        }

        private static IEnumerator LoadClip(string fileName, Action<AudioClip> assign)
        {
            string path = ResolveAudioPath(fileName);
            if (string.IsNullOrEmpty(path))
            {
                ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Trap audio file missing: {fileName}");
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, GetAudioType(path)))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    ContractsBootstrap.Log?.LogWarning($"{LogPrefix} Failed loading trap audio '{fileName}': {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                    yield break;

                clip.name = "LGU_PestTrap_" + Path.GetFileNameWithoutExtension(fileName);
                assign?.Invoke(clip);
            }
        }

        private static AudioType GetAudioType(string path)
        {
            string extension = Path.GetExtension(path)?.ToLowerInvariant();
            if (extension == ".ogg") return AudioType.OGGVORBIS;
            if (extension == ".mp3") return AudioType.MPEG;
            return AudioType.WAV;
        }

        private static string ResolveAudioPath(string fileName)
        {
            string assemblyDir = Path.GetDirectoryName(typeof(ContractsBootstrap).Assembly.Location) ?? string.Empty;
            string[] candidates =
            {
                Path.Combine(assemblyDir, "ContractAudio", AudioFolder, fileName),
                Path.Combine(Paths.PluginPath, "Y4NGZCompany", "ContractAudio", AudioFolder, fileName),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "ContractAudio", AudioFolder, fileName),
                Path.Combine(Paths.PluginPath, "ContractAudio", AudioFolder, fileName),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ContractAudio", AudioFolder, fileName),
            };
            for (int i = 0; i < candidates.Length; i++)
            {
                if (File.Exists(candidates[i])) return candidates[i];
            }

            return null;
        }
    }
}
