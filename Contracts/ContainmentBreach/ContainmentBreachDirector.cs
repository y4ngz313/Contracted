using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using TMPro;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Networking;
using UnityEngine.Rendering;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Core.Compat;
using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Facility.Shared;
using SlidingLeafJambClipper = Y4NGZ.Compatibility.SlidingLeafJambClipper;
namespace Y4NGZCompany.Contracts.ContainmentBreach
{
    internal enum ContainmentVoiceLine
    {
        Entry,
        Lockdown,
        Wave1,
        Wave2,
        Wave3,
        End
    }

    internal sealed class ContainmentBreachDirector : MonoBehaviour
    {
        private static readonly Dictionary<ContainmentVoiceLine, string> ClipFiles =
            new Dictionary<ContainmentVoiceLine, string>
            {
                { ContainmentVoiceLine.Entry, "Containment Breach entry.mp3" },
                { ContainmentVoiceLine.Lockdown, "Containment Breach lockdown.mp3" },
                { ContainmentVoiceLine.Wave1, "Containment Breach wave 1.mp3" },
                { ContainmentVoiceLine.Wave2, "Containment Breach wave 2.mp3" },
                { ContainmentVoiceLine.Wave3, "Containment Breach wave 3.mp3" },
                { ContainmentVoiceLine.End, "Containment Breach end.mp3" },
            };

        private const string SlamClipFileName = "freesound_community-metallic-door-shut-98740.mp3";
        private const string ContainmentLockdownBars = "ContainmentLockdownBars";
        private const string ContainmentLockdownDoors = "ContainmentLockdownDoors";
        private const float GatePrepareRetryInterval = 0.75f;
        private const float GateMissingAnchorLogInterval = 5f;
        private const double GatePreparationSlowStepMilliseconds = 12d;
        private const int GateRendererCensusItemsPerSlice = 256;
        private const double GateRendererCensusMillisecondsPerSlice = 2d;
        private const double EntranceInteractionMillisecondsPerSlice = 2d;
        private const float GateVisualMaxScale = 512f;
        private const float GateDoorClearance = 0.06f;
        private const float GateDoorRendererSearchRadius = 5.5f;
        private const float GateDoorAssemblySearchRadius = 3.75f;
        private const int GateDoorAssemblyMaxRenderers = 48;

        // The gate must sit INSIDE the frame opening without covering the frame (issue #171),
        // so the opening is measured from the vanilla door LEAF geometry and then pulled in by
        // a hair. The old whole-door-AABB-plus-padding sizing is what made the bars overhang.
        private const float GateOpeningInset = 0.03f;
        // Fixed real-world panel thickness. Vanilla door leaves measure 0.135-0.153 and the
        // vanilla frame is 0.215 deep, so 0.12 reads as a thin plate and stays inside the frame.
        private const float GateDoorPanelThickness = 0.12f;
        private const float GateBlockerThickness = 0.28f;
        // The plate is a surface-mounted shutter: its back face sits this far in front of the
        // furthest door-side protrusion inside the opening. The vanilla facility leaf carries
        // separate handle meshes reaching 0.12-0.17 m from the leaf centre (mineshaft is the
        // deepest), so a plate centred 0.06 m off the leaf had the handles poking through it.
        private const float GateHandleClearance = 0.02f;
        // Front extent assumed when nothing in the door assembly could be measured: covers the
        // deepest vanilla handle (mineshaft, 0.165 m) with a little to spare.
        private const float GateFallbackFrontExtent = 0.17f;

        // Fallbacks for when leaf geometry cannot be measured. Sourced from
        // Y4NGZContainmentDoorOpeningProbe2 (2026-07-29) against the three vanilla interiors:
        //   fire exit     1.512 W x 3.236-3.414 H (width identical across all 8 host prefabs)
        //   main entrance 2.750-3.355 W x 3.068-3.525 H
        private const float GateMainOpeningFallbackWidth = 2.98f;
        private const float GateMainOpeningFallbackHeight = 3.07f;
        private const float GateFireOpeningFallbackWidth = 1.51f;
        private const float GateFireOpeningFallbackHeight = 3.28f;

        // Clamps that keep a mismeasured door from producing an absurd gate. Wide enough to
        // cover every measured vanilla opening with headroom, tight enough to catch nonsense.
        private const float GateOpeningMinWidth = 1.1f;
        private const float GateOpeningMaxWidth = 4.6f;
        private const float GateOpeningMinHeight = 1.9f;
        private const float GateOpeningMaxHeight = 5f;
        private static ContainmentBreachDirector _instance;
        private static MethodInfo _applyBurningMethod;
        private static bool _burnEffectLookupAttempted;
        private static bool _burnEffectMissingLogged;
        private static AudioClip _slamClip;
        private static AudioClip _slideClip;

        private readonly Dictionary<ContainmentVoiceLine, AudioClip> _clips =
            new Dictionary<ContainmentVoiceLine, AudioClip>();

        private readonly List<GameObject> _lockdownPanels = new List<GameObject>();
        private readonly HashSet<InteractTrigger> _hiddenEntranceTriggerSet = new HashSet<InteractTrigger>();
        private readonly List<EntranceInteractionState> _hiddenEntranceInteractions = new List<EntranceInteractionState>();
        private readonly List<AudioSource> _speakerSources = new List<AudioSource>();
        private bool _loadingStarted;
        private bool _lockdownActive;
        private bool _gatesPrepared;
        private Coroutine _gatePrepareCoroutine;
        private Coroutine _entranceHideCoroutine;
        private bool _preparedGatesClosedForActiveLockdown;
        /// <summary>True only once the sliced sweep has actually hidden every endpoint (#716).</summary>
        private bool _entranceInteractionsHidden;

        /// <summary>True from the moment a sweep starts until the lockdown clears or it is cancelled.</summary>
        private bool _entranceHideRequested;
        private bool _lockdownSlamPlayed;
        private bool _lockdownSlamRetryStarted;
        private float _nextVoiceStartTime;
        private float _nextGatePrepareAttemptTime;
        private float _nextGateMissingAnchorLogTime;

        private const float VoiceVolume = 1.65f;
        private const float SlamVolume = 0.62f;
        private const float SlideVolume = 0.5f;

        internal static bool LockdownActive => _instance != null && _instance._lockdownActive;

        internal static void EnsureInstance()
        {
            if (_instance != null)
                return;

            var go = new GameObject("LGU_ContainmentBreachDirector");
            DontDestroyOnLoad(go);
            _instance = go.AddComponent<ContainmentBreachDirector>();
        }

        internal static void ResetDirector()
        {
            if (_instance == null)
                return;

            _instance.SetLockdown(active: false);
            _instance.ClearSpeakers();
        }

        internal static float PlayVoice(ContainmentVoiceLine line)
        {
            EnsureInstance();
            return _instance.PlayVoiceLocal(line);
        }

        internal static float GetVoiceLength(ContainmentVoiceLine line, float fallbackSeconds)
        {
            EnsureInstance();
            _instance.StartLoadingClips();
            if (_instance._clips.TryGetValue(line, out AudioClip clip) && clip != null)
                return Mathf.Max(0f, clip.length);

            return Mathf.Max(0f, fallbackSeconds);
        }

        internal static void PrepareLockdownGates()
        {
            EnsureInstance();
            _instance.PrepareLockdownGatesLocal();
        }

        internal static void EnsureLockdownGatesPrepared()
        {
            EnsureInstance();
            _instance.EnsureLockdownGatesPreparedLocal();
        }

        internal static void BeginLockdown()
        {
            EnsureInstance();
            _instance.SetLockdown(active: true);
        }

        internal static void PlayLockdownSlam()
        {
            EnsureInstance();
            _instance.PlayLockdownSlamLocal();
        }

        internal static void EndLockdown()
        {
            if (_instance == null)
                return;

            _instance.SetLockdown(active: false);
        }

        internal static void IgniteInteriorEnemies()
        {
            EnsureInstance();
            _instance.IgniteInteriorEnemiesLocal();
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(gameObject);
                return;
            }

            _instance = this;
            StartLoadingClips();
        }
        private void Update()
        {
            if (!_lockdownActive || _preparedGatesClosedForActiveLockdown)
                return;

            if (!_gatesPrepared)
                EnsureLockdownGatesPreparedLocal();
            if (!_gatesPrepared)
                return;
            DisableInteriorEntranceInteractions();

            // A lockdown can begin while only the exterior teleports exist. When the paired
            // dungeon endpoints arrive, close the newly staged gates immediately for every
            // holder (contract, alarm, or drill), without replaying the activation slam.
            for (int i = 0; i < _lockdownPanels.Count; i++)
            {
                GameObject panel = _lockdownPanels[i];
                if (panel == null)
                    continue;

                ContainmentLockdownPanel animator = panel.GetComponent<ContainmentLockdownPanel>();
                if (animator != null)
                    animator.Close();
            }

            _preparedGatesClosedForActiveLockdown = true;
            ContractsBootstrap.Log?.LogInfo("[MoonContracts] Containment lockdown closed gates prepared after paired dungeon endpoints became available.");
        }


        private void StartLoadingClips()
        {
            if (_loadingStarted)
                return;

            _loadingStarted = true;
            StartCoroutine(LoadClips());
        }

        private IEnumerator LoadClips()
        {
            string folder = ResolveAudioFolder();
            if (string.IsNullOrWhiteSpace(folder))
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Containment voice folder not found.");
                yield break;
            }

            foreach (var pair in ClipFiles)
            {
                string path = Path.Combine(folder, pair.Value);
                if (!File.Exists(path))
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Missing containment voice file: {path}");
                    continue;
                }

                using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(ToFileUri(path), AudioType.MPEG))
                {
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Failed loading containment voice '{path}': {request.error}");
                        continue;
                    }

                    AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                    if (clip != null)
                    {
                        clip.name = Path.GetFileNameWithoutExtension(path);
                        _clips[pair.Key] = clip;
                    }
                }
            }

            string slamPath = Path.Combine(folder, SlamClipFileName);
            if (File.Exists(slamPath))
            {
                using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(ToFileUri(slamPath), AudioType.MPEG))
                {
                    yield return request.SendWebRequest();
                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Failed loading containment slam '{slamPath}': {request.error}");
                    }
                    else
                    {
                        AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                        if (clip != null)
                        {
                            clip.name = Path.GetFileNameWithoutExtension(slamPath);
                            _slamClip = clip;
                        }
                    }
                }
            }
            else
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Missing containment slam file: {slamPath}");
            }

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Loaded {_clips.Count}/{ClipFiles.Count} containment voice clips.");
        }

        private static string ResolveAudioFolder()
        {
            var candidates = new[]
            {
                Path.Combine(Path.GetDirectoryName(typeof(ContractsBootstrap).Assembly.Location) ?? string.Empty, "ContractAudio", "Containment Breach"),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "ContractAudio", "Containment Breach"),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "ContractAudio", "Containment Breach"),
                Path.Combine(Paths.PluginPath, "ContractAudio", "Containment Breach"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ContractAudio", "Containment Breach")
            };

            foreach (string candidate in candidates)
            {
                if (Directory.Exists(candidate))
                    return candidate;
            }

            try
            {
                return Directory.GetDirectories(Paths.PluginPath, "Containment Breach", SearchOption.AllDirectories)
                    .FirstOrDefault(path => File.Exists(Path.Combine(path, ClipFiles[ContainmentVoiceLine.Entry])));
            }
            catch
            {
                return null;
            }
        }

        private static string ToFileUri(string path)
        {
            return new Uri(path).AbsoluteUri;
        }

        private float PlayVoiceLocal(ContainmentVoiceLine line)
        {
            StartLoadingClips();

            float clipLength = GetLoadedOrFallbackLength(line);
            float scheduledStart = Mathf.Max(Time.time, _nextVoiceStartTime);
            _nextVoiceStartTime = scheduledStart + clipLength + 3f;

            if (!LocalPlayerIsInside())
                return scheduledStart;

            if (!_clips.TryGetValue(line, out AudioClip clip) || clip == null)
            {
                StartCoroutine(PlayWhenLoaded(line, scheduledStart));
                return scheduledStart;
            }

            StartCoroutine(PlayVoiceAtTime(line, clip, scheduledStart));
            return scheduledStart;
        }

        private float GetLoadedOrFallbackLength(ContainmentVoiceLine line)
        {
            if (_clips.TryGetValue(line, out AudioClip clip) && clip != null)
                return Mathf.Max(0f, clip.length);

            switch (line)
            {
                case ContainmentVoiceLine.Entry:
                    return 7.5f;
                case ContainmentVoiceLine.Lockdown:
                    return 3.5f;
                default:
                    return 3f;
            }
        }

        private IEnumerator PlayVoiceAtTime(ContainmentVoiceLine line, AudioClip clip, float scheduledStart)
        {
            while (Time.time < scheduledStart)
                yield return null;

            PlayVoiceClipNow(clip);
        }

        private void PlayVoiceClipNow(AudioClip clip)
        {
            if (clip == null || !LocalPlayerIsInside())
                return;

            EnsureSpeakers();
            if (_speakerSources.Count == 0)
            {
                var player = GameNetworkManager.Instance?.localPlayerController;
                if (player == null)
                    return;

                PlayClipAt(clip, player.transform.position + Vector3.up * 2.4f, VoiceVolume, 70f, paFilters: true);
                return;
            }

            foreach (AudioSource source in _speakerSources)
            {
                if (source != null)
                    source.PlayOneShot(clip, VoiceVolume);
            }

            PlayLocalCeilingRepeater(clip);
        }

        private IEnumerator PlayWhenLoaded(ContainmentVoiceLine line, float scheduledStart)
        {
            float until = Mathf.Max(Time.time + 4f, scheduledStart + 4f);
            while (Time.time < until)
            {
                if (_clips.TryGetValue(line, out AudioClip clip) && clip != null)
                {
                    yield return PlayVoiceAtTime(line, clip, scheduledStart);
                    yield break;
                }

                yield return null;
            }
        }

        private void EnsureSpeakers()
        {
            _speakerSources.RemoveAll(source => source == null);
            if (_speakerSources.Count > 0)
                return;

            var positions = new List<Vector3>();
            var entrances = FindInteriorEntrances();
            foreach (var entrance in entrances.Take(4))
            {
                Transform point = entrance.transform;
                positions.Add(point.position + Vector3.up * 2.35f);
            }

            if (RoundManager.Instance?.allEnemyVents != null)
            {
                foreach (var vent in RoundManager.Instance.allEnemyVents.Where(vent => vent?.floorNode != null).Take(4))
                    positions.Add(vent.floorNode.position + Vector3.up * 2.35f);
            }

            positions = DistinctPositions(positions, 4f)
                .Take(6)
                .ToList();

            for (int i = 0; i < positions.Count; i++)
            {
                var speaker = new GameObject($"LGU_ContainmentSpeaker_{i + 1}");
                speaker.transform.position = positions[i];
                speaker.transform.SetParent(transform, true);
                var source = speaker.AddComponent<AudioSource>();
                ConfigurePaSource(source, 135f);
                _speakerSources.Add(source);
            }
        }

        private void ClearSpeakers()
        {
            foreach (AudioSource source in _speakerSources)
            {
                if (source != null)
                    Destroy(source.gameObject);
            }

            _speakerSources.Clear();
        }

        private static IEnumerable<Vector3> DistinctPositions(IEnumerable<Vector3> positions, float minDistance)
        {
            var accepted = new List<Vector3>();
            foreach (Vector3 position in positions)
            {
                bool tooClose = false;
                for (int i = 0; i < accepted.Count; i++)
                {
                    if (Vector3.Distance(position, accepted[i]) < minDistance)
                    {
                        tooClose = true;
                        break;
                    }
                }

                if (!tooClose)
                {
                    accepted.Add(position);
                    yield return position;
                }
            }
        }

        private void SetLockdown(bool active)
        {
            if (_lockdownActive == active && active)
                return;

            _lockdownActive = active;

            if (active)
            {
                _lockdownSlamPlayed = false;
                _lockdownSlamRetryStarted = false;
                _preparedGatesClosedForActiveLockdown = false;
                EnsureSpeakers();
                DisableInteriorEntranceInteractions();
                CloseLockdownGates();
            }
            else
            {
                _lockdownSlamPlayed = false;
                _lockdownSlamRetryStarted = false;
                _preparedGatesClosedForActiveLockdown = false;
                RestoreInteriorEntranceInteractions();
                ReleaseLockdownPanels();
            }
        }

        private void EnsureLockdownGatesPreparedLocal()
        {
            if (_gatesPrepared && _lockdownPanels.Count > 0)
                return;

            float now = Time.time;
            if (now < _nextGatePrepareAttemptTime)
                return;

            _nextGatePrepareAttemptTime = now + GatePrepareRetryInterval;
            bool logMissingAnchors = now >= _nextGateMissingAnchorLogTime;
            PrepareLockdownGatesLocal(logMissingAnchors);
            if (_gatesPrepared)
                _nextGateMissingAnchorLogTime = 0f;
            else if (logMissingAnchors)
                _nextGateMissingAnchorLogTime = now + GateMissingAnchorLogInterval;
        }

        private void PrepareLockdownGatesLocal(bool logMissingAnchors = true)
        {
            if ((_gatesPrepared && _lockdownPanels.Count > 0) || _gatePrepareCoroutine != null)
                return;

            ReleaseLockdownPanels(immediate: true);
            _gatePrepareCoroutine = StartCoroutine(PrepareLockdownGatesOverFrames(logMissingAnchors));
        }

        private IEnumerator PrepareLockdownGatesOverFrames(bool logMissingAnchors)
        {
            // StartCoroutine runs synchronously until its first yield. Defer all scene discovery so
            // callers such as BeginLockdown never inherit the preparation spike on their own frame.
            yield return null;

            float wallStart = Time.realtimeSinceStartup;
            double cpuMilliseconds = 0d;
            double slowestStepMilliseconds = 0d;
            int preparationFrames = 0;
            var anchors = new List<GateAnchor>();
            List<EntranceTeleport> entrances = FindAllEntrances();
            if (entrances.Count == 0)
            {
                if (logMissingAnchors)
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts] Containment lockdown is waiting for entrance teleport endpoints.");
                _gatePrepareCoroutine = null;
                yield break;
            }

            List<IGrouping<int, EntranceTeleport>> pairs = entrances
                .GroupBy(entrance => entrance.entranceId)
                .ToList();
            bool incompletePairFound = false;
            for (int i = 0; i < pairs.Count; i++)
            {
                IGrouping<int, EntranceTeleport> pair = pairs[i];
                bool hasInterior = pair.Any(entrance => !entrance.isEntranceToBuilding);
                bool hasExterior = pair.Any(entrance => entrance.isEntranceToBuilding);
                if (hasInterior && hasExterior)
                    continue;

                incompletePairFound = true;
                if (logMissingAnchors)
                {
                    string missingSide = !hasInterior ? "interior" : "exterior";
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts] Containment lockdown entrance {pair.Key} is waiting for its {missingSide} endpoint; " +
                        "preparation remains pending.");
                }
            }

            if (incompletePairFound)
            {
                _gatePrepareCoroutine = null;
                yield break;
            }

            long censusStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            Renderer[] sceneRenderers = FindObjectsByType<Renderer>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            double censusMilliseconds = ElapsedGatePreparationMilliseconds(censusStarted);
            cpuMilliseconds += censusMilliseconds;
            slowestStepMilliseconds = Math.Max(slowestStepMilliseconds, censusMilliseconds);
            LogSlowGatePreparationStep("renderer-census", -1, censusMilliseconds);
            preparationFrames++;
            yield return null;

            var rendererMatches = new Dictionary<int, EntranceDoorRendererMatch>();
            int rendererIndex = 0;
            while (rendererIndex < sceneRenderers.Length)
            {
                long sliceStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                int processed = 0;
                while (rendererIndex < sceneRenderers.Length &&
                    processed < GateRendererCensusItemsPerSlice)
                {
                    AccumulateEntranceDoorRendererMatches(
                        sceneRenderers[rendererIndex],
                        entrances,
                        rendererMatches);
                    rendererIndex++;
                    processed++;
                    if (ElapsedGatePreparationMilliseconds(sliceStarted) >= GateRendererCensusMillisecondsPerSlice)
                        break;
                }

                double sliceMilliseconds = ElapsedGatePreparationMilliseconds(sliceStarted);
                cpuMilliseconds += sliceMilliseconds;
                slowestStepMilliseconds = Math.Max(slowestStepMilliseconds, sliceMilliseconds);
                LogSlowGatePreparationStep("renderer-index", -1, sliceMilliseconds);
                preparationFrames++;
                yield return null;
            }

            // Door renderers are now scored once for all endpoints. Anchor resolution below only
            // consumes the cached winner and measures the opening; it never walks the scene again.

            // Consume one cached doorway winner per frame, then stage at most one physical gate on
            // each following frame so opening measurement and object creation remain isolated.
            for (int pairIndex = 0; pairIndex < pairs.Count; pairIndex++)
            {
                var endpointPositions = new List<Vector3>();
                foreach (EntranceTeleport entrance in pairs[pairIndex]
                    .OrderBy(endpoint => endpoint.isEntranceToBuilding ? 1 : 0))
                {
                    Vector3 endpointPosition = GetEntrancePosition(entrance);
                    if (endpointPositions.Any(position => Vector3.SqrMagnitude(position - endpointPosition) < 0.0625f))
                        continue;

                    endpointPositions.Add(endpointPosition);
                    long stepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                    if (TryCreateEntranceGateAnchor(entrance, rendererMatches, out GateAnchor anchor))
                        anchors.Add(anchor);
                    double stepMilliseconds = ElapsedGatePreparationMilliseconds(stepStarted);
                    cpuMilliseconds += stepMilliseconds;
                    slowestStepMilliseconds = Math.Max(slowestStepMilliseconds, stepMilliseconds);
                    LogSlowGatePreparationStep("resolve-anchor", entrance.entranceId, stepMilliseconds);
                    preparationFrames++;
                    yield return null;
                }
            }

            if (anchors.Count == 0)
            {
                if (logMissingAnchors)
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts] Containment lockdown found no usable paired entrance endpoints; gates will retry.");
                _gatePrepareCoroutine = null;
                yield break;
            }

            foreach (GateAnchor anchor in anchors)
            {
                long stepStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                GameObject panel = CreateGatePanel(anchor);
                if (panel != null)
                    _lockdownPanels.Add(panel);
                double stepMilliseconds = ElapsedGatePreparationMilliseconds(stepStarted);
                cpuMilliseconds += stepMilliseconds;
                slowestStepMilliseconds = Math.Max(slowestStepMilliseconds, stepMilliseconds);
                LogSlowGatePreparationStep("stage-gate", anchor.EntranceId, stepMilliseconds);
                preparationFrames++;
                yield return null;
            }

            _gatesPrepared = _lockdownPanels.Count > 0;
            _gatePrepareCoroutine = null;
            if (_gatesPrepared)
            {
                float wallMilliseconds = (Time.realtimeSinceStartup - wallStart) * 1000f;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Containment lockdown prepared {_lockdownPanels.Count} retracted physical gate(s) " +
                    $"across {preparationFrames} sliced frame(s): cpuMs={cpuMilliseconds:F2}, " +
                    $"slowestStepMs={slowestStepMilliseconds:F2}, wallMs={wallMilliseconds:F2}.");
            }
            else if (logMissingAnchors)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Containment lockdown prepared 0 gates; entrance anchors are not available yet.");
        }

        private static double ElapsedGatePreparationMilliseconds(long started)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d /
                System.Diagnostics.Stopwatch.Frequency;
        }

        private static void LogSlowGatePreparationStep(string step, int entranceId, double milliseconds)
        {
            if (milliseconds < GatePreparationSlowStepMilliseconds)
                return;

            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.Perf] Containment lockdown {step} exceeded the slice budget: " +
                $"entranceId={entranceId}, elapsedMs={milliseconds:F2}.");
        }

        private void CloseLockdownGates()
        {
            PrepareLockdownGatesLocal();

            foreach (GameObject panel in _lockdownPanels)
            {
                if (panel == null)
                    continue;

                ContainmentLockdownPanel animator = panel.GetComponent<ContainmentLockdownPanel>();
                if (animator != null)
                    animator.Close();
            }

            _preparedGatesClosedForActiveLockdown =
                _gatesPrepared && _lockdownPanels.Count > 0;

            PlayLockdownSlamLocal();

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Containment lockdown gate close started for {_lockdownPanels.Count} physical gate(s); slam triggered.");
        }

        private void ReleaseLockdownPanels(bool immediate = false)
        {
            if (_gatePrepareCoroutine != null)
            {
                StopCoroutine(_gatePrepareCoroutine);
                _gatePrepareCoroutine = null;
            }

            foreach (GameObject panel in _lockdownPanels)
            {
                if (panel == null)
                    continue;

                var animator = panel.GetComponent<ContainmentLockdownPanel>();
                if (!immediate && animator != null)
                    animator.OpenAndDestroy();
                else
                    Destroy(panel);
            }

            if (_lockdownPanels.Count > 0 && !immediate)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Containment lockdown released {_lockdownPanels.Count} gate(s).");

            _lockdownPanels.Clear();
            _gatesPrepared = false;
            _preparedGatesClosedForActiveLockdown = false;
            if (!immediate)
            {
                _nextGatePrepareAttemptTime = 0f;
                _nextGateMissingAnchorLogTime = 0f;
            }
        }

        private void DisableInteriorEntranceInteractions()
        {
            // #716: two callers reach this per lockdown - SetLockdown on the transition, and Update
            // again on the frame the gates finish preparing. Restarting the sweep there would stop
            // an in-flight coroutine and re-run the whole scene census for no gain, so the first
            // sweep owns the job for the lifetime of this lockdown. Only clearing the lockdown
            // (RestoreInteriorEntranceInteractions) releases the latch, which is also what lets a
            // cancelled sweep be started again by the next one.
            if (_entranceHideRequested)
                return;

            _entranceHideRequested = true;
            _entranceHideCoroutine = StartCoroutine(DisableInteriorEntranceInteractionsOverFrames());
        }

        /// <summary>
        /// #716: the sweep used to run whole on the lockdown transition frame - one
        /// <c>FindObjectsOfType&lt;InteractTrigger&gt;</c> census plus a descriptor-string test of every
        /// trigger against every entrance endpoint. It now runs on the same sliced stage machine the
        /// gate preparation uses: the scene lookups get their own stage, then endpoints are hidden a
        /// slice at a time under <see cref="EntranceInteractionMillisecondsPerSlice"/>. The end state
        /// is identical; the hide simply completes a couple of frames after the transition.
        /// </summary>
        private IEnumerator DisableInteriorEntranceInteractionsOverFrames()
        {
            // StartCoroutine runs synchronously up to the first yield, so defer every scene lookup
            // and leave the lockdown transition frame itself paying nothing.
            yield return null;

            long censusStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            // #503: one trigger census for every endpoint in this sweep instead of one per endpoint.
            InteractTrigger[] census = FindObjectsOfType<InteractTrigger>();
            List<EntranceTeleport> entrances = FindAllEntrances();
            LogSlowGatePreparationStep("entrance-census", -1, ElapsedGatePreparationMilliseconds(censusStarted));
            yield return null;

            int entranceIndex = 0;
            while (entranceIndex < entrances.Count)
            {
                long sliceStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                while (entranceIndex < entrances.Count)
                {
                    HideEntranceInteractions(entrances[entranceIndex], census);
                    entranceIndex++;
                    if (ElapsedGatePreparationMilliseconds(sliceStarted) >= EntranceInteractionMillisecondsPerSlice)
                        break;
                }

                LogSlowGatePreparationStep("entrance-hide", -1, ElapsedGatePreparationMilliseconds(sliceStarted));
                if (entranceIndex < entrances.Count)
                    yield return null;
            }

            _entranceHideCoroutine = null;
            // Only now are the endpoints actually blocked. Callers that read
            // _entranceInteractionsHidden must not believe that during the sliced window above.
            _entranceInteractionsHidden = true;
            if (_hiddenEntranceInteractions.Count > 0)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Containment lockdown hid {_hiddenEntranceInteractions.Count} entrance interaction(s) across both endpoint sides.");
        }

        private void HideEntranceInteractions(EntranceTeleport entrance, InteractTrigger[] census)
        {
            foreach (InteractTrigger trigger in FindEntranceInteractionTriggers(entrance, census))
            {
                // The census now spans several frames, so a trigger can have been destroyed since
                // it was collected; the null test below is what keeps that safe.
                if (trigger == null || !_hiddenEntranceTriggerSet.Add(trigger))
                    continue;

                _hiddenEntranceInteractions.Add(new EntranceInteractionState(trigger));
                trigger.interactable = false;
                trigger.hoverTip = string.Empty;
                trigger.disabledHoverTip = string.Empty;
                trigger.enabled = false;
            }
        }

        private void RestoreInteriorEntranceInteractions()
        {
            if (_entranceHideCoroutine != null)
            {
                StopCoroutine(_entranceHideCoroutine);
                _entranceHideCoroutine = null;
            }

            // Released before the early return below: a sweep cancelled with nothing hidden yet
            // still has to be restartable by the next lockdown.
            _entranceHideRequested = false;

            if (!_entranceInteractionsHidden && _hiddenEntranceInteractions.Count == 0)
                return;

            int restored = 0;
            for (int i = 0; i < _hiddenEntranceInteractions.Count; i++)
            {
                if (_hiddenEntranceInteractions[i].Restore())
                    restored++;
            }

            _hiddenEntranceInteractions.Clear();
            _hiddenEntranceTriggerSet.Clear();
            _entranceInteractionsHidden = false;
            if (restored > 0)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Containment lockdown restored {restored} entrance interaction(s) across both endpoint sides.");
        }

        private static GameObject CreateGatePanel(GateAnchor anchor)
        {
            // The sliding door pair is the gate (issue #171). The old bar panel stays as the
            // fallback so a client still running an older lgucontracts.bundle keeps working
            // instead of losing lockdown gates entirely — the bundle ships via GitHub Releases
            // and can lag the DLL.
            GameObject prefab = ContractAssetLoader.ContainmentLockdownDoorsPrefab;
            bool slidingDoors = prefab != null;
            if (prefab == null)
            {
                prefab = ContractAssetLoader.ContainmentLockdownBarsPrefab;
                if (prefab != null)
                {
                    ContractsBootstrap.Log?.LogWarning(
                        "[MoonContracts] Containment lockdown sliding door prefab is unavailable; falling back to the legacy dropping bar panel. " +
                        "Update lgucontracts.bundle to get the frame-fitted sliding doors.");
                }
            }

            if (prefab == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Containment lockdown required bundled gate prefab is unavailable; no gate spawned.");
                return null;
            }

            return CreatePrefabGatePanel(prefab, anchor, slidingDoors);
        }

        private static GameObject CreatePrefabGatePanel(GameObject prefab, GateAnchor anchor, bool slidingDoors)
        {
            try
            {
                var root = new GameObject("LGU_ContainmentLockdownGate");
                root.transform.position = anchor.ClosedPosition;
                root.transform.rotation = anchor.Rotation;

                GameObject visual = Instantiate(prefab, root.transform);
                visual.name = slidingDoors ? ContainmentLockdownDoors : ContainmentLockdownBars;
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one;
                // #716: one hierarchy walk for the whole gate. The material pass, the renderable-root
                // resolve, all nine orientation probes, the world-bounds measure and the log line used
                // to run their own GetComponentsInChildren<Renderer>; nothing below this line adds or
                // removes a renderer, so the same array serves every one of them.
                Renderer[] gateRenderers = visual.GetComponentsInChildren<Renderer>(true);
                if (slidingDoors)
                    ApplySlidingDoorMaterial(visual, gateRenderers);
                else
                    ApplyBundledGateMaterial(visual, gateRenderers);

                if (!ConfigurePrefabGateVisual(visual, gateRenderers, anchor.Width, anchor.Height, out GateVisualFit fit))
                {
                    Destroy(root);
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Containment lockdown prefab '{prefab.name}' has no usable renderers; no gate spawned.");
                    return null;
                }

                RemoveGatePhysics(visual);
                var halves = slidingDoors
                    ? ResolveGateSlidingHalves(visual.transform)
                    : new List<ContainmentLockdownPanel.SlidingHalf>();
                AddGatePhysics(visual, halves, anchor.Width, anchor.Height, fit.LocalBounds.center, fit.VisualScale);
                NavMeshObstacle obstacle = AddGateNavMeshObstacle(root, anchor);
                SetLayerRecursively(root, ResolveGatePhysicsLayer());

                var animator = root.AddComponent<ContainmentLockdownPanel>();
                if (halves.Count >= 2)
                {
                    animator.PrepareSlidingHalves(halves, obstacle);
                }
                else
                {
                    // Legacy single-panel drop: the whole gate travels down from above the door.
                    Vector3 openPosition = anchor.ClosedPosition + Vector3.up * (anchor.Height + 0.75f);
                    animator.Prepare(openPosition, anchor.ClosedPosition, obstacle);
                    if (slidingDoors)
                    {
                        ContractsBootstrap.Log?.LogWarning(
                            $"[MoonContracts] Containment lockdown sliding prefab '{prefab.name}' exposed {halves.Count} slidable half/halves " +
                            "instead of 2; falling back to a vertical drop for this gate.");
                    }
                }

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts] Containment lockdown using bundled physical gate prefab '{prefab.name}' at {FormatVector(anchor.ClosedPosition)}, " +
                    $"size=({anchor.Width:F2},{anchor.Height:F2}), doorClass={anchor.DoorClass}, openingMeasured={anchor.OpeningMeasured}, " +
                    $"slidingHalves={halves.Count}, renderers={gateRenderers.Length}, " +
                    $"colliders={visual.GetComponentsInChildren<Collider>(true).Length}, navMeshObstacle={(obstacle != null ? "yes" : "no")}, " +
                    $"orientation={fit.OrientationLabel}, " +
                    $"prefabLocalBounds=center{FormatVector(fit.LocalBounds.center)} size{FormatVector(fit.LocalBounds.size)}, " +
                    $"visualScale={FormatVector(fit.VisualScale)}, visualLocalPosition={FormatVector(fit.VisualLocalPosition)}, " +
                    $"rendererWorldBounds=center{FormatVector(fit.WorldBounds.center)} size{FormatVector(fit.WorldBounds.size)}.");
                return root;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Failed creating bundled containment gate prefab '{prefab?.name}': {e.Message}");
                return null;
            }
        }

        /// <summary>
        /// Finds the two authored door halves under the visual root and works out which way each
        /// one retracts. Direction and travel come from the measured bounds rather than child
        /// names, so an axis flip during FBX import cannot send both halves the same way.
        /// </summary>
        private static List<ContainmentLockdownPanel.SlidingHalf> ResolveGateSlidingHalves(Transform visual)
        {
            var halves = new List<ContainmentLockdownPanel.SlidingHalf>();
            if (visual == null)
                return halves;

            for (int i = 0; i < visual.childCount; i++)
            {
                Transform child = visual.GetChild(i);
                if (child == null)
                    continue;

                if (!TrySubtreeBoundsInSpace(visual, child, out Bounds inVisual))
                    continue;

                if (inVisual.size.x <= 0.001f)
                    continue;

                float direction = inVisual.center.x >= 0f ? 1f : -1f;
                float travel = inVisual.size.x;
                // #827: the half's closed outer edge is the jamb plane. Everything the leaf pushes
                // past it while retracted is clipped away each frame, so the leaf grows out of the
                // frame edge instead of popping into view on the wall.
                float jambX = direction > 0f ? inVisual.max.x : inVisual.min.x;
                SlidingLeafJambClipper clipper = SlidingLeafJambClipper.TryCreate(child, visual, direction, jambX, out string clipFailure);
                if (clipper == null)
                {
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts] Containment lockdown half '{child.name}' cannot be jamb-clipped ({clipFailure}); it will slide unclipped.");
                }

                halves.Add(new ContainmentLockdownPanel.SlidingHalf(
                    child,
                    child.localPosition,
                    child.localPosition + Vector3.right * (direction * travel),
                    clipper));
            }

            return halves;
        }

        private static bool TrySubtreeBoundsInSpace(Transform space, Transform subtree, out Bounds bounds)
        {
            bounds = default;
            if (space == null || subtree == null)
                return false;

            bool initialized = false;
            foreach (Renderer renderer in subtree.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null || renderer.transform == null)
                    continue;

                if (!TryGetRendererMeshBounds(renderer, out Bounds meshBounds))
                    continue;

                Matrix4x4 toSpace = space.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                EncapsulateTransformedBounds(meshBounds, toSpace, ref bounds, ref initialized);
            }

            return initialized;
        }

        /// <summary>
        /// Blocks enemy pathing while the gate is shut. Without this the lockdown contained
        /// nothing — enemies walked straight through the gate (issue #171). The obstacle lives on
        /// the gate root, which never moves, so carving stays cheap and stationary; the animator
        /// arms it once the halves have finished closing.
        /// </summary>
        private static NavMeshObstacle AddGateNavMeshObstacle(GameObject root, GateAnchor anchor)
        {
            try
            {
                var obstacle = root.AddComponent<NavMeshObstacle>();
                obstacle.shape = NavMeshObstacleShape.Box;
                obstacle.center = Vector3.zero;
                obstacle.size = new Vector3(anchor.Width, anchor.Height, GateBlockerThickness);
                obstacle.carving = true;
                obstacle.carveOnlyStationary = true;
                obstacle.enabled = false;
                return obstacle;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Containment lockdown could not add a NavMeshObstacle: {e.Message}");
                return null;
            }
        }

        private static void ApplyBundledGateMaterial(GameObject visual, Renderer[] gateRenderers)
        {
            Material fallbackMaterial = ContractAssetLoader.ContainmentLockdownBarsMaterial;
            Texture opacity = ContractAssetLoader.ContainmentLockdownBarsOpacityTexture;
            if (visual == null)
                return;

            foreach (Renderer renderer in gateRenderers ?? visual.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                    continue;

                Material[] sources = renderer.sharedMaterials;
                if ((sources == null || sources.Length == 0) && fallbackMaterial != null)
                    sources = new[] { fallbackMaterial };
                if (sources == null || sources.Length == 0)
                    continue;

                Material[] runtimeMaterials = new Material[sources.Length];
                for (int i = 0; i < sources.Length; i++)
                {
                    Material source = sources[i] != null ? sources[i] : fallbackMaterial;
                    if (source == null)
                        continue;

                    Material runtimeMaterial = new Material(source)
                    {
                        name = source.name + "_RuntimeCutout"
                    };
                    ConfigureGateCutoutMaterial(runtimeMaterial, opacity);
                    runtimeMaterials[i] = runtimeMaterial;
                }

                renderer.sharedMaterials = runtimeMaterials;
            }
        }

        /// <summary>
        /// Runtime material pass for the sliding doors. Unlike the bar panel these are SOLID —
        /// running them through the bars' alpha-cutout path would punch the fence opacity map's
        /// holes straight through the door plate.
        /// </summary>
        private static void ApplySlidingDoorMaterial(GameObject visual, Renderer[] gateRenderers)
        {
            if (visual == null)
                return;

            Material fallbackMaterial = ContractAssetLoader.ContainmentLockdownDoorsMaterial
                ?? ContractAssetLoader.ContainmentLockdownBarsMaterial;

            foreach (Renderer renderer in gateRenderers ?? visual.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                    continue;

                Material[] sources = renderer.sharedMaterials;
                if ((sources == null || sources.Length == 0) && fallbackMaterial != null)
                    sources = new[] { fallbackMaterial };
                if (sources == null || sources.Length == 0)
                    continue;

                Material[] runtimeMaterials = new Material[sources.Length];
                for (int i = 0; i < sources.Length; i++)
                {
                    Material source = sources[i] != null ? sources[i] : fallbackMaterial;
                    if (source == null)
                        continue;

                    Material runtimeMaterial = new Material(source)
                    {
                        name = source.name + "_RuntimeSolid"
                    };
                    ForceGateSolidOpaqueState(runtimeMaterial);
                    runtimeMaterials[i] = runtimeMaterial;
                }

                renderer.sharedMaterials = runtimeMaterials;
            }
        }

        private static void ForceGateSolidOpaqueState(Material material)
        {
            if (material == null)
                return;

            material.renderQueue = (int)RenderQueue.Geometry;
            material.SetOverrideTag("RenderType", "Opaque");
            SetMaterialFloatIfPresent(material, "_SurfaceType", 0f);
            SetMaterialFloatIfPresent(material, "_RenderQueueType", 1f);
            SetMaterialFloatIfPresent(material, "_BlendMode", 0f);
            SetMaterialFloatIfPresent(material, "_SrcBlend", (float)BlendMode.One);
            SetMaterialFloatIfPresent(material, "_DstBlend", (float)BlendMode.Zero);
            SetMaterialFloatIfPresent(material, "_ZWrite", 1f);
            SetMaterialFloatIfPresent(material, "_TransparentZWrite", 0f);
            SetMaterialFloatIfPresent(material, "_AlphaCutoffEnable", 0f);
            SetMaterialFloatIfPresent(material, "_AlphaClip", 0f);
            SetMaterialFloatIfPresent(material, "_Cull", (float)CullMode.Off);
            SetMaterialFloatIfPresent(material, "_DoubleSidedEnable", 1f);
            SetMaterialFloatIfPresent(material, "_CullMode", (float)CullMode.Off);
            SetMaterialFloatIfPresent(material, "_CullModeForward", (float)CullMode.Off);
            SetMaterialFloatIfPresent(material, "_TransparentCullMode", (float)CullMode.Off);
            material.DisableKeyword("_ALPHATEST_ON");
            material.DisableKeyword("_ALPHACLIPPING_ON");
            material.DisableKeyword("_AlphaClip");
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_BLENDMODE_ALPHA");
            material.DisableKeyword("_BLENDMODE_PRE_MULTIPLY");
            material.DisableKeyword("_BLENDMODE_ADD");
            material.DisableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
            material.EnableKeyword("_DOUBLESIDED_ON");
            material.SetShaderPassEnabled("TransparentDepthPrepass", false);
            material.SetShaderPassEnabled("TransparentDepthPostpass", false);
            material.SetShaderPassEnabled("TransparentBackface", false);
        }

        private static bool ConfigurePrefabGateVisual(
            GameObject visual,
            Renderer[] gateRenderers,
            float width,
            float height,
            out GateVisualFit fit)
        {
            fit = default;
            if (visual == null)
                return false;

            gateRenderers ??= visual.GetComponentsInChildren<Renderer>(true);
            SetActiveRecursively(visual, true);
            PrepareGateRenderers(gateRenderers);

            Transform renderRoot = ResolveGateRenderableRoot(visual, gateRenderers);
            if (!ChooseGateVisualOrientation(visual.transform, renderRoot, gateRenderers, width, height, out GateVisualOrientation orientation))
                return false;

            if (renderRoot != null && renderRoot != visual.transform)
                renderRoot.localRotation = orientation.Rotation;

            Bounds localBounds = orientation.Bounds;
            Vector3 size = localBounds.size;
            float xScale = size.x > 0.001f ? Mathf.Clamp(width / size.x, 0.05f, GateVisualMaxScale) : 1f;
            float yScale = size.y > 0.001f ? Mathf.Clamp(height / size.y, 0.05f, GateVisualMaxScale) : 1f;
            // Thickness is a fixed real-world value, not a by-product of how wide the opening is.
            // Scaling depth with width is what made a wide gate read as a thick slab (issue #171).
            float zScale = size.z > 0.001f
                ? Mathf.Clamp(GateDoorPanelThickness / size.z, 0.01f, GateVisualMaxScale)
                : Mathf.Min(xScale, yScale);
            visual.transform.localScale = new Vector3(xScale, yScale, zScale);
            visual.transform.localPosition = -Vector3.Scale(localBounds.center, visual.transform.localScale);

            if (!TryCalculateWorldRendererBounds(gateRenderers, out Bounds worldBounds))
                worldBounds = new Bounds(visual.transform.position, Vector3.zero);

            fit = new GateVisualFit(
                orientation.Bounds,
                worldBounds,
                orientation.Label,
                visual.transform.localScale,
                visual.transform.localPosition);
            return true;
        }

        private static void ConfigureGateCutoutMaterial(Material material, Texture opacity)
        {
            if (material == null)
                return;

            ForceGateOpaqueAlphaTestState(material);
            SetMaterialTextureIfPresent(material, "_AlphaMap", opacity);
            SetMaterialTextureIfPresent(material, "_OpacityMap", opacity);
            SetMaterialTextureIfPresent(material, "_TransparencyMap", opacity);
        }

        private static void ForceGateOpaqueAlphaTestState(Material material)
        {
            if (material == null)
                return;

            material.renderQueue = (int)RenderQueue.AlphaTest;
            material.SetOverrideTag("RenderType", "TransparentCutout");
            SetMaterialFloatIfPresent(material, "_SurfaceType", 0f);
            SetMaterialFloatIfPresent(material, "_RenderQueueType", 1f);
            SetMaterialFloatIfPresent(material, "_BlendMode", 0f);
            SetMaterialFloatIfPresent(material, "_SrcBlend", (float)BlendMode.One);
            SetMaterialFloatIfPresent(material, "_DstBlend", (float)BlendMode.Zero);
            SetMaterialFloatIfPresent(material, "_ZWrite", 1f);
            SetMaterialFloatIfPresent(material, "_TransparentZWrite", 0f);
            SetMaterialFloatIfPresent(material, "_AlphaCutoffEnable", 1f);
            SetMaterialFloatIfPresent(material, "_AlphaClip", 1f);
            SetMaterialFloatIfPresent(material, "_AlphaCutoff", 0.45f);
            SetMaterialFloatIfPresent(material, "_AlphaCutoffPrepass", 0.45f);
            SetMaterialFloatIfPresent(material, "_AlphaCutoffPostpass", 0.45f);
            SetMaterialFloatIfPresent(material, "_AlphaCutoffShadow", 0.45f);
            SetMaterialFloatIfPresent(material, "_Cutoff", 0.45f);
            SetMaterialFloatIfPresent(material, "_Mode", 1f);
            SetMaterialFloatIfPresent(material, "_CullMode", (float)CullMode.Off);
            SetMaterialFloatIfPresent(material, "_CullModeForward", (float)CullMode.Off);
            SetMaterialFloatIfPresent(material, "_TransparentCullMode", (float)CullMode.Off);
            SetMaterialFloatIfPresent(material, "_DoubleSidedEnable", 1f);
            SetMaterialFloatIfPresent(material, "_AlphaToMask", 1f);
            material.DisableKeyword("_SURFACE_TYPE_TRANSPARENT");
            material.DisableKeyword("_BLENDMODE_ALPHA");
            material.DisableKeyword("_BLENDMODE_PRE_MULTIPLY");
            material.DisableKeyword("_BLENDMODE_ADD");
            material.DisableKeyword("_ENABLE_FOG_ON_TRANSPARENT");
            material.EnableKeyword("_ALPHATEST_ON");
            material.EnableKeyword("_ALPHACLIPPING_ON");
            material.EnableKeyword("_AlphaClip");
            material.EnableKeyword("_DOUBLESIDED_ON");
            material.SetShaderPassEnabled("TransparentDepthPrepass", false);
            material.SetShaderPassEnabled("TransparentDepthPostpass", false);
            material.SetShaderPassEnabled("TransparentBackface", false);
        }

        private static void SetMaterialFloatIfPresent(Material material, string property, float value)
        {
            if (material != null && material.HasProperty(property))
                material.SetFloat(property, value);
        }

        private static void SetMaterialTextureIfPresent(Material material, string property, Texture texture)
        {
            if (material != null && texture != null && material.HasProperty(property))
                material.SetTexture(property, texture);
        }

        private static Transform ResolveGateRenderableRoot(GameObject visual, Renderer[] renderers)
        {
            if (visual == null)
                return null;

            if (visual.GetComponent<Renderer>() != null)
                return visual.transform;

            if (renderers == null || renderers.Length == 0)
                return visual.transform;

            Transform commonImmediateChild = null;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                Transform current = renderer.transform;
                while (current != null && current.parent != visual.transform)
                    current = current.parent;

                if (current == null)
                    return visual.transform;

                if (commonImmediateChild == null)
                {
                    commonImmediateChild = current;
                    continue;
                }

                if (commonImmediateChild != current)
                    return visual.transform;
            }

            return commonImmediateChild != null ? commonImmediateChild : visual.transform;
        }

        private static bool ChooseGateVisualOrientation(
            Transform visualRoot,
            Transform renderRoot,
            Renderer[] gateRenderers,
            float targetWidth,
            float targetHeight,
            out GateVisualOrientation best)
        {
            best = default;
            if (visualRoot == null)
                return false;

            // #716: the mesh-local bounds of a renderer do not change when the render root is
            // rotated, so the MeshFilter lookup that resolves them runs once here instead of once
            // per renderer per orientation. Only the renderer-to-root matrix is recomputed below.
            GateRendererSample[] samples = BuildGateRendererSamples(gateRenderers);

            var candidates = new[]
            {
                new GateVisualOrientation("identity", Quaternion.identity),
                new GateVisualOrientation("x+90", Quaternion.Euler(90f, 0f, 0f)),
                new GateVisualOrientation("x-90", Quaternion.Euler(-90f, 0f, 0f)),
                new GateVisualOrientation("y+90", Quaternion.Euler(0f, 90f, 0f)),
                new GateVisualOrientation("y-90", Quaternion.Euler(0f, -90f, 0f)),
                new GateVisualOrientation("z+90", Quaternion.Euler(0f, 0f, 90f)),
                new GateVisualOrientation("z-90", Quaternion.Euler(0f, 0f, -90f)),
                new GateVisualOrientation("x+180", Quaternion.Euler(180f, 0f, 0f)),
                new GateVisualOrientation("y+180", Quaternion.Euler(0f, 180f, 0f)),
            };

            bool canRotateRenderRoot = renderRoot != null && renderRoot != visualRoot;
            bool found = false;
            float bestScore = float.MinValue;
            for (int i = 0; i < candidates.Length; i++)
            {
                GateVisualOrientation candidate = candidates[i];
                if (!canRotateRenderRoot && candidate.Label != "identity")
                    continue;

                if (canRotateRenderRoot)
                    renderRoot.localRotation = candidate.Rotation;

                if (!TryCalculateLocalRendererBounds(visualRoot, samples, out Bounds bounds))
                    continue;

                Vector3 size = bounds.size;
                if (size.x <= 0.001f || size.y <= 0.001f)
                    continue;

                float score = ScoreGateVisualOrientation(size, targetWidth, targetHeight);
                if (!found || score > bestScore)
                {
                    best = new GateVisualOrientation(candidate.Label, candidate.Rotation, bounds);
                    bestScore = score;
                    found = true;
                }
            }

            if (found && canRotateRenderRoot)
                renderRoot.localRotation = best.Rotation;

            return found;
        }

        private static float ScoreGateVisualOrientation(Vector3 size, float targetWidth, float targetHeight)
        {
            float width = Mathf.Max(size.x, 0.001f);
            float height = Mathf.Max(size.y, 0.001f);
            float depth = Mathf.Max(size.z, 0.001f);
            float targetAspect = Mathf.Max(0.001f, targetWidth / Mathf.Max(0.001f, targetHeight));
            float aspect = width / height;
            float aspectPenalty = Mathf.Abs(Mathf.Log(Mathf.Max(0.001f, aspect / targetAspect)));
            float thinness = Mathf.Clamp(Mathf.Max(width, height) / depth, 0f, 80f);
            float uprightBonus = Mathf.Clamp(height / Mathf.Max(depth, 0.001f), 0f, 80f);
            float lyingFlatPenalty = size.z > size.y ? (size.z - size.y) * 4f : 0f;
            return (width * height) + thinness * 0.2f + uprightBonus * 0.15f - aspectPenalty * 2f - lyingFlatPenalty;
        }

        private static void PrepareGateRenderers(Renderer[] gateRenderers)
        {
            if (gateRenderers == null)
                return;

            foreach (Renderer renderer in gateRenderers)
            {
                if (renderer == null)
                    continue;

                renderer.enabled = true;
                renderer.forceRenderingOff = false;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                renderer.receiveShadows = true;
                renderer.lightProbeUsage = LightProbeUsage.BlendProbes;
            }
        }

        private static void RemoveGatePhysics(GameObject root)
        {
            Collider[] colliders = root.GetComponentsInChildren<Collider>(true);
            for (int i = 0; i < colliders.Length; i++)
            {
                if (colliders[i] != null)
                {
                    colliders[i].enabled = false;
                    Destroy(colliders[i]);
                }
            }

            Rigidbody[] bodies = root.GetComponentsInChildren<Rigidbody>(true);
            for (int i = 0; i < bodies.Length; i++)
            {
                if (bodies[i] != null)
                {
                    bodies[i].detectCollisions = false;
                    bodies[i].isKinematic = true;
                    Destroy(bodies[i]);
                }
            }
        }

        private static void AddGatePhysics(
            GameObject root,
            List<ContainmentLockdownPanel.SlidingHalf> halves,
            float width,
            float height,
            Vector3 localCenter,
            Vector3 visualScale)
        {
            var body = root.AddComponent<Rigidbody>();
            body.isKinematic = true;
            body.useGravity = false;
            body.constraints = RigidbodyConstraints.FreezeAll;

            if (halves != null && halves.Count >= 2)
            {
                // One blocker per half so the collision travels with the panel that owns it and
                // there is no invisible wall left standing in a retracted doorway.
                foreach (ContainmentLockdownPanel.SlidingHalf half in halves)
                    AddGateHalfCollider(half.Transform, visualScale);

                return;
            }

            var collider = root.AddComponent<BoxCollider>();
            Vector3 scale = root.transform.lossyScale;
            collider.size = new Vector3(
                width / Mathf.Max(0.001f, Mathf.Abs(scale.x)),
                height / Mathf.Max(0.001f, Mathf.Abs(scale.y)),
                GateBlockerThickness / Mathf.Max(0.001f, Mathf.Abs(scale.z)));
            collider.center = localCenter;
        }

        /// <summary>
        /// Blocker for one sliding half. The authored halves are identity children of the scaled
        /// visual root, so their local axes are the gate's own (x across the opening, y up,
        /// z through the doorway) and <paramref name="visualScale"/> is the exact local-to-world
        /// factor — no lossyScale approximation, which a yawed non-uniform scale would corrupt.
        /// </summary>
        private static void AddGateHalfCollider(Transform half, Vector3 visualScale)
        {
            if (half == null)
                return;

            if (!TrySubtreeBoundsInSpace(half, half, out Bounds localBounds))
                return;

            float zScale = Mathf.Abs(visualScale.z);
            float localThickness = zScale > 0.001f
                ? Mathf.Max(localBounds.size.z, GateBlockerThickness / zScale)
                : localBounds.size.z;

            var collider = half.gameObject.AddComponent<BoxCollider>();
            collider.center = localBounds.center;
            collider.size = new Vector3(localBounds.size.x, localBounds.size.y, localThickness);
        }

        private static int ResolveGatePhysicsLayer()
        {
            int defaultLayer = LayerMask.NameToLayer("Default");
            if (defaultLayer >= 0)
                return defaultLayer;

            int roomLayer = LayerMask.NameToLayer("Room");
            return roomLayer >= 0 ? roomLayer : 0;
        }

        private static void SetLayerRecursively(GameObject root, int layer)
        {
            if (root == null || layer < 0)
                return;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                    transforms[i].gameObject.layer = layer;
            }
        }

        private static void SetActiveRecursively(GameObject root, bool active)
        {
            if (root == null)
                return;

            Transform[] transforms = root.GetComponentsInChildren<Transform>(true);
            for (int i = 0; i < transforms.Length; i++)
            {
                if (transforms[i] != null)
                    transforms[i].gameObject.SetActive(active);
            }
        }

        /// <summary>#716: one renderer's contribution to the orientation probe, with the mesh-local
        /// bounds resolved once instead of on every one of the nine orientation passes.</summary>
        private readonly struct GateRendererSample
        {
            internal GateRendererSample(Renderer renderer, Bounds meshBounds, bool hasMeshBounds)
            {
                Renderer = renderer;
                MeshBounds = meshBounds;
                HasMeshBounds = hasMeshBounds;
            }

            internal Renderer Renderer { get; }

            internal Bounds MeshBounds { get; }

            internal bool HasMeshBounds { get; }
        }

        private static GateRendererSample[] BuildGateRendererSamples(Renderer[] gateRenderers)
        {
            if (gateRenderers == null || gateRenderers.Length == 0)
                return Array.Empty<GateRendererSample>();

            var samples = new List<GateRendererSample>(gateRenderers.Length);
            for (int i = 0; i < gateRenderers.Length; i++)
            {
                Renderer renderer = gateRenderers[i];
                if (renderer == null)
                    continue;

                bool hasMeshBounds = TryGetRendererMeshBounds(renderer, out Bounds meshBounds);
                samples.Add(new GateRendererSample(renderer, meshBounds, hasMeshBounds));
            }

            return samples.ToArray();
        }

        private static bool TryCalculateLocalRendererBounds(
            Transform root,
            GateRendererSample[] samples,
            out Bounds localBounds)
        {
            localBounds = default;
            if (root == null || samples == null)
                return false;

            Matrix4x4 worldToRoot = root.worldToLocalMatrix;
            bool initialized = false;
            for (int i = 0; i < samples.Length; i++)
            {
                GateRendererSample sample = samples[i];
                Renderer renderer = sample.Renderer;
                if (renderer == null)
                    continue;

                if (sample.HasMeshBounds)
                {
                    Matrix4x4 rendererToRoot = worldToRoot * renderer.transform.localToWorldMatrix;
                    EncapsulateTransformedBounds(sample.MeshBounds, rendererToRoot, ref localBounds, ref initialized);
                    continue;
                }

                // No mesh-local bounds, so the world AABB has to be re-read: it changes as the render
                // root rotates between orientation probes.
                EncapsulateTransformedBounds(renderer.bounds, worldToRoot, ref localBounds, ref initialized);
            }

            return initialized;
        }

        private static bool TryGetRendererMeshBounds(Renderer renderer, out Bounds bounds)
        {
            bounds = default;
            if (renderer == null)
                return false;

            if (renderer is SkinnedMeshRenderer skinned)
            {
                bounds = skinned.localBounds;
                return bounds.size.sqrMagnitude > 0.000001f;
            }

            MeshFilter filter = renderer.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
            {
                bounds = filter.sharedMesh.bounds;
                return bounds.size.sqrMagnitude > 0.000001f;
            }

            return false;
        }

        private static void EncapsulateTransformedBounds(
            Bounds sourceBounds,
            Matrix4x4 transform,
            ref Bounds targetBounds,
            ref bool initialized)
        {
            Vector3 min = sourceBounds.min;
            Vector3 max = sourceBounds.max;
            Vector3[] corners =
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(max.x, max.y, max.z),
            };

            for (int i = 0; i < corners.Length; i++)
            {
                Vector3 point = transform.MultiplyPoint3x4(corners[i]);
                if (!initialized)
                {
                    targetBounds = new Bounds(point, Vector3.zero);
                    initialized = true;
                }
                else
                {
                    targetBounds.Encapsulate(point);
                }
            }
        }

        private static bool TryCalculateWorldRendererBounds(Renderer[] renderers, out Bounds worldBounds)
        {
            worldBounds = default;
            if (renderers == null)
                return false;

            bool initialized = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                if (!initialized)
                {
                    worldBounds = renderer.bounds;
                    initialized = true;
                }
                else
                {
                    worldBounds.Encapsulate(renderer.bounds);
                }
            }

            return initialized;
        }

        private void PlayLockdownSlamLocal()
        {
            StartLoadingClips();
            if (_lockdownSlamPlayed)
                return;

            if (TryPlaySlamSound())
            {
                _lockdownSlamPlayed = true;
                return;
            }

            if (!_lockdownSlamRetryStarted)
                StartCoroutine(PlaySlamWhenAvailable());
        }

        private IEnumerator PlaySlamWhenAvailable()
        {
            _lockdownSlamRetryStarted = true;
            float until = Time.time + 3f;
            while (!_lockdownSlamPlayed && Time.time < until)
            {
                yield return new WaitForSeconds(0.15f);
                if (TryPlaySlamSound())
                    _lockdownSlamPlayed = true;
            }

            _lockdownSlamRetryStarted = false;
            if (!_lockdownSlamPlayed)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Containment lockdown slam could not play; no clip or playback position was available.");
        }

        private bool TryPlaySlamSound()
        {
            AudioClip clip = ResolveSlamClip();
            if (clip == null)
                return false;

            var positions = FindLockdownAudioPositions();
            if (positions.Count == 0)
                return false;

            // The slide layer fires WITH the slam, not after it. The panels only travel for a
            // fraction of a second, and a delayed impact was already ruled out once because it
            // read as the sound arriving late.
            AudioClip slideClip = ResolveGateSlideClip();
            foreach (Vector3 position in positions.Take(6))
            {
                PlayClipAt(clip, position, SlamVolume, 55f, paFilters: false);
                if (slideClip != null)
                    PlayClipAt(slideClip, position, SlideVolume, 45f, paFilters: false);
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Containment lockdown slam played at {positions.Count} point(s)" +
                (slideClip != null
                    ? $", layered with sliding-door travel '{slideClip.name}'."
                    : "; no vanilla slide clip was available to layer."));
            return true;
        }

        /// <summary>
        /// The travel sound for the sliding halves, taken from vanilla so no new audio ships.
        /// The ship's hangar door is the only heavy sliding-metal-door clip the game exposes as a
        /// plain field; the metal door creak is the backstop.
        /// </summary>
        private static AudioClip ResolveGateSlideClip()
        {
            if (_slideClip != null)
                return _slideClip;

            StartOfRound instance = StartOfRound.Instance;
            if (instance == null)
                return null;

            _slideClip = instance.openingHangarDoorAudio;
            if (_slideClip == null && instance.creakOpenDoorMetal != null)
                _slideClip = instance.creakOpenDoorMetal.FirstOrDefault(candidate => candidate != null);

            return _slideClip;
        }

        private List<Vector3> FindLockdownAudioPositions()
        {
            var positions = new List<Vector3>();
            foreach (GameObject panel in _lockdownPanels)
            {
                if (panel != null)
                    positions.Add(panel.transform.position + Vector3.up * 0.2f);
            }

            if (positions.Count == 0)
            {
                foreach (EntranceTeleport entrance in FindInteriorEntrances())
                    positions.Add(GetEntrancePosition(entrance) + Vector3.up * 1.2f);
            }

            if (positions.Count == 0)
            {
                foreach (EntranceTeleport entrance in FindInteriorEntrances())
                {
                    Transform point = entrance.entrancePoint != null ? entrance.entrancePoint : entrance.transform;
                    if (point != null)
                        positions.Add(point.position + Vector3.up * 1.2f);
                }
            }

            if (positions.Count == 0)
            {
                foreach (Vector3 position in GetInteriorEntranceFallbackPositions())
                    positions.Add(position + Vector3.up * 1.6f);
            }

            if (positions.Count == 0)
            {
                var player = GameNetworkManager.Instance?.localPlayerController;
                if (player != null)
                    positions.Add(player.transform.position + Vector3.up * 1.5f);
            }

            return DistinctPositions(positions, 3f)
                .Take(6)
                .ToList();
        }

        private static AudioClip ResolveSlamClip()
        {
            if (_slamClip != null)
                return _slamClip;

            if (StartOfRound.Instance?.shutDoorMetal != null && StartOfRound.Instance.shutDoorMetal.Length > 0)
                _slamClip = StartOfRound.Instance.shutDoorMetal.FirstOrDefault(clip => clip != null);

            return _slamClip;
        }

        private static void PlayLocalCeilingRepeater(AudioClip clip)
        {
            var player = GameNetworkManager.Instance?.localPlayerController;
            if (player == null || !player.isInsideFactory)
                return;

            Vector3 position = player.gameplayCamera != null
                ? player.gameplayCamera.transform.position + player.gameplayCamera.transform.forward * 4f + Vector3.up * 2.5f
                : player.transform.position + Vector3.up * 3.2f;

            PlayClipAt(clip, position, 0.55f, 42f, paFilters: true);
        }

        private static void ConfigurePaSource(AudioSource source, float maxDistance)
        {
            source.playOnAwake = false;
            source.volume = 1f;
            source.spatialBlend = 0.72f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 16f;
            source.maxDistance = maxDistance;
            source.spread = 55f;
            source.priority = 64;
            source.dopplerLevel = 0f;

            var lowPass = source.gameObject.AddComponent<AudioLowPassFilter>();
            lowPass.cutoffFrequency = 5200f;
            lowPass.lowpassResonanceQ = 1.15f;

            var highPass = source.gameObject.AddComponent<AudioHighPassFilter>();
            highPass.cutoffFrequency = 260f;
            highPass.highpassResonanceQ = 1.05f;
        }

        private static void PlayClipAt(AudioClip clip, Vector3 position, float volume, float maxDistance, bool paFilters)
        {
            if (clip == null)
                return;

            var go = new GameObject("LGU_ContainmentOneShotAudio");
            go.transform.position = position;
            var source = go.AddComponent<AudioSource>();
            source.clip = clip;
            source.volume = volume;
            source.spatialBlend = paFilters ? 0.72f : 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = paFilters ? 14f : 4f;
            source.maxDistance = maxDistance;
            source.dopplerLevel = 0f;
            if (paFilters)
            {
                var lowPass = go.AddComponent<AudioLowPassFilter>();
                lowPass.cutoffFrequency = 5200f;
                var highPass = go.AddComponent<AudioHighPassFilter>();
                highPass.cutoffFrequency = 260f;
            }
            source.Play();
            Destroy(go, clip.length + 0.35f);
        }

        private static List<EntranceTeleport> FindInteriorEntrances()
        {
            return FindAllEntrances()
                .Where(entrance => entrance != null && !entrance.isEntranceToBuilding)
                .OrderBy(entrance => entrance.entranceId)
                .ToList();
        }

        // #503: left unsliced deliberately. EntranceTeleport is a handful of scene objects, and
        // every caller is a transition (gate preparation, lockdown hide/restore, speaker setup)
        // gated behind GatePrepareRetryInterval or a one-shot state change - never a per-frame path.
        private static List<EntranceTeleport> FindAllEntrances()
        {
            return FindObjectsByType<EntranceTeleport>(FindObjectsInactive.Include, FindObjectsSortMode.None)
                .Where(entrance => entrance != null)
                .OrderBy(entrance => entrance.entranceId)
                .ThenBy(entrance => entrance.isEntranceToBuilding ? 1 : 0)
                .ToList();
        }

        /// <summary>#503/#716: the census is taken once per DisableInteriorEntranceInteractions
        /// sweep and passed down explicitly. The sweep is sliced across frames, so every consumer
        /// null-tests each entry before touching it.</summary>
        private static IEnumerable<InteractTrigger> FindEntranceInteractionTriggers(
            EntranceTeleport entrance,
            InteractTrigger[] census)
        {
            if (entrance == null)
                yield break;

            var seen = new HashSet<InteractTrigger>();
            foreach (Transform root in GetEntranceTriggerRoots(entrance))
            {
                if (root == null)
                    continue;

                InteractTrigger[] triggers = root.GetComponentsInChildren<InteractTrigger>(true);
                for (int i = 0; i < triggers.Length; i++)
                {
                    InteractTrigger trigger = triggers[i];
                    if (trigger != null && seen.Add(trigger))
                        yield return trigger;
                }
            }

            Vector3 entrancePosition = GetEntrancePosition(entrance);
            // #503: this used to run one unbounded FindObjectsOfType<InteractTrigger>() per
            // entrance endpoint, so a moon with several fire exits paid the scan several times
            // in the single frame that hides the interactions. The caller now takes one census
            // for the whole sweep; the scan still happens when nobody set a census up.
            InteractTrigger[] nearbyTriggers = census ?? FindObjectsOfType<InteractTrigger>();
            for (int i = 0; i < nearbyTriggers.Length; i++)
            {
                InteractTrigger trigger = nearbyTriggers[i];
                if (trigger == null || !seen.Add(trigger))
                    continue;

                if (LooksLikeEntranceInteraction(trigger, entrancePosition))
                    yield return trigger;
            }
        }

        private static List<Transform> GetEntranceTriggerRoots(EntranceTeleport entrance)
        {
            var roots = new List<Transform>();
            if (entrance == null)
                return roots;

            AddUniqueRoot(roots, entrance.transform);
            AddUniqueRoot(roots, entrance.entrancePoint);
            return roots;
        }

        private static void AddUniqueRoot(List<Transform> roots, Transform root)
        {
            if (root == null)
                return;

            for (int i = 0; i < roots.Count; i++)
            {
                if (roots[i] == root)
                    return;
            }

            roots.Add(root);
        }

        private static bool LooksLikeEntranceInteraction(InteractTrigger trigger, Vector3 entrancePosition)
        {
            if (trigger == null || trigger.transform == null)
                return false;

            if (HorizontalDistance(trigger.transform.position, entrancePosition) > 4.25f)
                return false;

            string descriptor = GetInteractionDescriptor(trigger);
            return descriptor.Contains("door") ||
                descriptor.Contains("entrance") ||
                descriptor.Contains("exit") ||
                descriptor.Contains("enter") ||
                descriptor.Contains("leave");
        }

        private static string GetInteractionDescriptor(InteractTrigger trigger)
        {
            if (trigger == null)
                return string.Empty;

            return (
                trigger.name + " " +
                (trigger.gameObject != null ? trigger.gameObject.name : string.Empty) + " " +
                (trigger.hoverTip ?? string.Empty) + " " +
                (trigger.disabledHoverTip ?? string.Empty) + " " +
                GetHierarchyNames(trigger.transform, 3)).ToLowerInvariant();
        }

        // #503: the synchronous FindLockdownGateAnchors pair and the rendererMatches-less
        // TryCreateEntranceGateAnchor overload lived here. They had no callers left once
        // PrepareLockdownGatesOverFrames took over anchor resolution with a frame-sliced
        // renderer census, and each entrance they touched paid its own unbounded scene scans.

        private static bool TryCreateEntranceGateAnchor(
            EntranceTeleport entrance,
            IReadOnlyDictionary<int, EntranceDoorRendererMatch> rendererMatches,
            out GateAnchor anchor)
        {
            anchor = default;
            if (entrance == null)
                return false;

            // Entrance ID 0 is the main entrance contract even when a custom interior does not
            // carry the vanilla InsideEntranceDoor tag.
            GateDoorClass doorClass = entrance.entranceId == 0
                ? GateDoorClass.MainEntrance
                : GateDoorClass.FireExit;
            string endpointSide = entrance.isEntranceToBuilding ? "exterior" : "interior";

            if (doorClass == GateDoorClass.MainEntrance
                && !entrance.isEntranceToBuilding
                && TryGetTaggedMainDoorAnchor(entrance, endpointSide, out anchor))
            {
                return true;
            }

            if (TryGetVisibleEntranceDoorAnchor(entrance, doorClass, endpointSide, rendererMatches, out anchor))
            {
                return true;
            }

            Transform endpoint = entrance.entrancePoint != null ? entrance.entrancePoint : entrance.transform;
            if (endpoint == null)
                return false;

            Quaternion rotation = Quaternion.Euler(0f, endpoint.eulerAngles.y, 0f);
            Vector3 forward = rotation * Vector3.forward;
            GetFallbackOpeningSize(doorClass, out float width, out float height);
            Vector3 closedPosition = endpoint.position + forward * GateDoorClearance + Vector3.up * (height * 0.5f);
            string provenance = doorClass == GateDoorClass.MainEntrance
                ? "fixed-main-fallback"
                : "fixed-fire-exit-fallback";
            anchor = new GateAnchor(
                closedPosition,
                rotation,
                width,
                height,
                doorClass,
                openingMeasured: false,
                entrance.entranceId,
                endpointSide,
                provenance);
            return true;
        }

        private static bool TryGetTaggedMainDoorAnchor(
            EntranceTeleport entrance,
            string endpointSide,
            out GateAnchor anchor)
        {
            anchor = default;
            GameObject door = GameObject.FindGameObjectWithTag("InsideEntranceDoor");
            if (door == null)
                return false;

            Bounds bounds = CalculateBounds(door);
            Vector3 entrancePosition = GetEntrancePosition(entrance);
            if (HorizontalDistance(bounds.center, entrancePosition) > GateDoorRendererSearchRadius)
                return false;

            Vector3 forward = ResolveDoorForward(bounds, door.transform, entrance);
            GateOpening opening = ResolveGateOpening(
                door.transform,
                forward,
                bounds,
                GateDoorClass.MainEntrance,
                entrancePosition);
            anchor = new GateAnchor(
                opening.Center + forward * ResolveGateStandoff(opening.FrontExtent),
                Quaternion.LookRotation(forward, Vector3.up),
                opening.Width,
                opening.Height,
                GateDoorClass.MainEntrance,
                opening.Measured,
                entrance.entranceId,
                endpointSide,
                opening.Provenance);
            return true;
        }

        private static bool TryGetVisibleEntranceDoorAnchor(
            EntranceTeleport entrance,
            GateDoorClass doorClass,
            string endpointSide,
            IReadOnlyDictionary<int, EntranceDoorRendererMatch> rendererMatches,
            out GateAnchor anchor)
        {
            anchor = default;
            if (entrance == null)
                return false;

            // #503: the census built by PrepareLockdownGatesOverFrames is the only source of door
            // renderers now. Without it there is nothing to resolve, and the removed fallback
            // would have re-scanned every Renderer in the scene on this one frame.
            if (rendererMatches == null ||
                !rendererMatches.TryGetValue(entrance.GetInstanceID(), out EntranceDoorRendererMatch match) ||
                match.Renderer == null)
            {
                return false;
            }

            Renderer renderer = match.Renderer;
            Bounds bounds = match.Bounds;

            Vector3 forward = ResolveDoorForward(bounds, renderer != null ? renderer.transform : null, entrance);
            Quaternion rotation = Quaternion.LookRotation(forward, Vector3.up);

            Vector3 entrancePosition = GetEntrancePosition(entrance);
            Transform doorRoot = ResolveDoorAssemblyRoot(
                renderer != null ? renderer.transform : null,
                entrancePosition);
            GateOpening opening = ResolveGateOpening(
                doorRoot,
                forward,
                bounds,
                doorClass,
                entrancePosition);
            Vector3 closedPosition = opening.Center + forward * ResolveGateStandoff(opening.FrontExtent);

            anchor = new GateAnchor(
                closedPosition,
                rotation,
                opening.Width,
                opening.Height,
                doorClass,
                opening.Measured,
                entrance.entranceId,
                endpointSide,
                opening.Provenance);
            return true;
        }

        /// <summary>
        /// Distance from the opening centre to the gate root along the door's forward axis. The
        /// plate's back face lands <see cref="GateHandleClearance"/> in front of the furthest
        /// door-side surface inside the opening (leaf, handle, push bar), so nothing on the vanilla
        /// door can intersect the shutter. The frame is deliberately not part of the extent: it is
        /// wider than the opening and never inside the plate's footprint.
        /// </summary>
        private static float ResolveGateStandoff(float frontExtent)
        {
            float extent = frontExtent > 0.001f ? frontExtent : GateFallbackFrontExtent;
            return extent + GateHandleClearance + GateDoorPanelThickness * 0.5f;
        }

        /// <summary>
        /// Furthest reach along <paramref name="forward"/> (from the opening centre) of any door
        /// assembly renderer that sits inside the opening footprint. Handles are separate child
        /// meshes on the vanilla facility door, so the leaf bounds alone under-report the depth.
        /// </summary>
        private static float MeasureDoorFrontExtent(
            Renderer[] renderers,
            Bounds opening,
            Vector3 forward,
            Vector3 right)
        {
            float extent = AxisSpan(opening, forward) * 0.5f;
            if (renderers == null)
                return extent;

            float halfWidth = AxisSpan(opening, right) * 0.5f;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null || renderer.transform == null)
                    continue;
                if (!TryGetRendererMeshBounds(renderer, out Bounds meshBounds))
                    continue;

                Bounds world = default;
                bool initialized = false;
                EncapsulateTransformedBounds(meshBounds, renderer.transform.localToWorldMatrix, ref world, ref initialized);
                if (!initialized || !FitsInsideOpening(world, opening, right, halfWidth))
                    continue;

                extent = Mathf.Max(extent, MaxExtentAlongAxis(world, opening.center, forward));
            }

            return extent;
        }

        /// <summary>
        /// True for renderers whose bounds sit within the opening's lateral and vertical span
        /// (plus a small margin). Frames, header lights and walls are wider or taller than the
        /// opening and fall out; leaves and handles stay in.
        /// </summary>
        private static bool FitsInsideOpening(Bounds candidate, Bounds opening, Vector3 right, float halfWidth)
        {
            const float margin = 0.06f;
            const float sizeMargin = 0.12f;
            float lateral = Mathf.Abs(Vector3.Dot(candidate.center - opening.center, right));
            if (lateral > halfWidth + margin)
                return false;
            if (candidate.max.y < opening.min.y - margin || candidate.min.y > opening.max.y + margin)
                return false;
            if (AxisSpan(candidate, right) > AxisSpan(opening, right) + sizeMargin)
                return false;
            return candidate.size.y <= opening.size.y + sizeMargin;
        }

        private static float MaxExtentAlongAxis(Bounds bounds, Vector3 origin, Vector3 axis)
        {
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            float best = float.NegativeInfinity;
            for (int corner = 0; corner < 8; corner++)
            {
                var point = new Vector3(
                    (corner & 1) == 0 ? min.x : max.x,
                    (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                best = Mathf.Max(best, Vector3.Dot(point - origin, axis));
            }

            return best;
        }

        private static void LogEntranceGateAnchor(GateAnchor anchor)
        {
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts] Containment lockdown entrance {anchor.EntranceId} {anchor.EndpointSide} endpoint: " +
                $"opening=({anchor.Width:F2},{anchor.Height:F2}), class={anchor.DoorClass}, " +
                $"measurement={anchor.MeasurementProvenance}, closed={FormatVector(anchor.ClosedPosition)} " +
                $"(standoff from opening centre includes handle clearance {GateHandleClearance:F2}m).");
        }

        private static void AccumulateEntranceDoorRendererMatches(
            Renderer renderer,
            List<EntranceTeleport> entrances,
            Dictionary<int, EntranceDoorRendererMatch> matches)
        {
            if (renderer == null ||
                !renderer.enabled ||
                renderer.transform == null ||
                renderer.gameObject == null ||
                !renderer.gameObject.activeInHierarchy ||
                entrances == null ||
                matches == null)
            {
                return;
            }

            Bounds bounds;
            try
            {
                bounds = renderer.bounds;
            }
            catch
            {
                return;
            }

            string descriptor = null;
            for (int i = 0; i < entrances.Count; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null)
                    continue;

                Vector3 entrancePosition = GetEntrancePosition(entrance);
                if (!TryScoreEntranceDoorBounds(bounds, descriptor, entrancePosition, out float score))
                    continue;

                if (descriptor == null)
                {
                    descriptor = GetRendererDescriptor(renderer);
                    if (descriptor.Contains("lgu_containmentlockdowngate") ||
                        descriptor.Contains("containmentlockdownbars"))
                    {
                        return;
                    }

                    if (!TryScoreEntranceDoorBounds(bounds, descriptor, entrancePosition, out score))
                        continue;
                }

                int key = entrance.GetInstanceID();
                if (matches.TryGetValue(key, out EntranceDoorRendererMatch existing) &&
                    existing.Score >= score)
                {
                    continue;
                }

                matches[key] = new EntranceDoorRendererMatch(renderer, bounds, score);
            }
        }

        private static bool TryScoreEntranceDoorBounds(
            Bounds bounds,
            string descriptor,
            Vector3 entrancePosition,
            out float score)
        {
            score = 0f;
            float height = bounds.size.y;
            float horizontalSpan = Mathf.Max(bounds.size.x, bounds.size.z);
            float horizontalThinness = Mathf.Min(bounds.size.x, bounds.size.z);
            float horizontalDistance = HorizontalDistance(bounds.center, entrancePosition);
            float expectedCenterHeight = entrancePosition.y + 1.55f;
            if (horizontalDistance > GateDoorRendererSearchRadius ||
                Mathf.Abs(bounds.center.y - expectedCenterHeight) > 3.25f ||
                height < 1.55f ||
                height > 5.4f ||
                horizontalSpan < 0.65f ||
                horizontalSpan > 4.8f ||
                horizontalThinness > 2.2f)
            {
                return false;
            }

            if (descriptor == null)
                return true;

            {
                if (descriptor.Contains("door"))
                    score += 8f;
                if (descriptor.Contains("fire"))
                    score += 5f;
                if (descriptor.Contains("exit"))
                    score += 5f;
                if (descriptor.Contains("entrance"))
                    score += 3f;
                if (descriptor.Contains("frame"))
                    score += 1.5f;
            }

            score -= horizontalDistance * 1.35f;
            score -= Mathf.Abs(height - 2.8f) * 0.45f;
            score -= Mathf.Max(0f, horizontalSpan - 2.6f) * 1.4f;
            score -= horizontalThinness * 0.25f;
            return score > -6f;
        }

        // #503: FindBestEntranceDoorRenderer and its per-renderer scorer lived here and ran an
        // unbounded FindObjectsOfType<Renderer>() once per entrance endpoint. Their only caller
        // was the synchronous FindLockdownGateAnchors path, which PrepareLockdownGatesOverFrames
        // replaced with a single frame-sliced renderer census feeding EntranceDoorRendererMatch.
        // Both are removed rather than sliced: the surviving census already does the same scoring
        // once for all endpoints.

        private static string GetRendererDescriptor(Renderer renderer)
        {
            if (renderer == null)
                return string.Empty;

            return (
                renderer.name + " " +
                (renderer.gameObject != null ? renderer.gameObject.name : string.Empty) + " " +
                GetHierarchyNames(renderer.transform, 4)).ToLowerInvariant();
        }

        private static string GetHierarchyNames(Transform transform, int maxParents)
        {
            if (transform == null)
                return string.Empty;

            string names = string.Empty;
            Transform current = transform;
            int count = 0;
            while (current != null && count <= maxParents)
            {
                names += current.name + " ";
                current = current.parent;
                count++;
            }

            return names;
        }

        private static Vector3 ResolveDoorForward(Bounds bounds, Transform rendererTransform, EntranceTeleport entrance)
        {
            Vector3 forward = FlattenForward(rendererTransform);
            float horizontalX = Mathf.Abs(bounds.size.x);
            float horizontalZ = Mathf.Abs(bounds.size.z);
            float largestHorizontal = Mathf.Max(horizontalX, horizontalZ);
            float smallestHorizontal = Mathf.Min(horizontalX, horizontalZ);
            if (largestHorizontal > 0.001f && smallestHorizontal / largestHorizontal < 0.55f)
                forward = horizontalX <= horizontalZ ? Vector3.right : Vector3.forward;

            Vector3 entrancePosition = GetEntrancePosition(entrance);
            Vector3 doorToEntrance = entrancePosition - bounds.center;
            doorToEntrance.y = 0f;
            if (doorToEntrance.sqrMagnitude > 0.001f && Vector3.Dot(forward, doorToEntrance) < 0f)
                forward = -forward;

            if (forward.sqrMagnitude < 0.001f)
                forward = FlattenForward(entrance != null ? entrance.transform : null);

            return forward.normalized;
        }

        private static float GetDoorBoundsWidth(Bounds bounds, Vector3 forward)
        {
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            if (right.sqrMagnitude < 0.001f)
                right = Vector3.right;

            return Mathf.Abs(right.x) * bounds.size.x + Mathf.Abs(right.z) * bounds.size.z;
        }

        // A vanilla door leaf fills the frame opening; the frame, hinges, backing sprite and
        // surrounding wall do not. Names/materials that positively identify a leaf across the
        // three vanilla interiors: DoorMesh (facility + mineshaft), FancyDoor (manor),
        // ClosetRedDoor (every fire exit host).
        private static readonly string[] GateDoorLeafHints =
        {
            "doormesh", "fancydoor", "closetreddoor", "doorleaf"
        };

        private static readonly string[] GateDoorLeafRejects =
        {
            "frame", "hinge", "light", "radar", "sprite", "map", "railing", "wall", "plane", "button"
        };

        /// <summary>
        /// Measures the frame OPENING from the vanilla door-leaf geometry under
        /// <paramref name="doorRoot"/>. The leaves fill the opening, so their combined span is the
        /// gate size. The whole-object AABB is NOT, because it includes the frame — sizing from it
        /// plus padding is exactly why the old bars overhung the doorway (issue #171).
        /// </summary>
        private static bool TryMeasureDoorOpening(
            Transform doorRoot,
            Vector3 forward,
            Vector3 entrancePosition,
            out GateOpening opening)
        {
            opening = default;
            if (doorRoot == null)
                return false;

            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            if (right.sqrMagnitude < 0.001f)
                right = Vector3.right;

            Renderer[] renderers = doorRoot.GetComponentsInChildren<Renderer>(true);
            if (renderers.Length == 0 || renderers.Length > GateDoorAssemblyMaxRenderers)
                return false;

            Bounds combined = default;
            bool initialized = false;
            int leafCount = 0;
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (!TryGetDoorLeafWorldBounds(renderer, doorRoot, out Bounds leaf))
                    continue;
                if (HorizontalDistance(leaf.center, entrancePosition) > GateDoorAssemblySearchRadius)
                    continue;

                if (!initialized)
                {
                    combined = leaf;
                    initialized = true;
                }
                else
                {
                    combined.Encapsulate(leaf);
                }

                leafCount++;
            }

            if (!initialized || leafCount == 0)
                return false;

            float width = AxisSpan(combined, right) - GateOpeningInset * 2f;
            float height = combined.size.y - GateOpeningInset * 2f;
            if (width < GateOpeningMinWidth || width > GateOpeningMaxWidth)
                return false;
            if (height < GateOpeningMinHeight || height > GateOpeningMaxHeight)
                return false;

            float frontExtent = MeasureDoorFrontExtent(renderers, combined, forward, right);
            opening = new GateOpening(width, height, combined.center, "door-assembly", frontExtent);
            return true;
        }

        private static bool TryResolveValidatedRendererOpening(
            Bounds bounds,
            Vector3 forward,
            out GateOpening opening)
        {
            opening = default;
            float width = GetDoorBoundsWidth(bounds, forward) - GateOpeningInset * 2f;
            float height = bounds.size.y - GateOpeningInset * 2f;
            float depth = AxisSpan(bounds, forward);
            if (width < GateOpeningMinWidth || width > GateOpeningMaxWidth)
                return false;
            if (height < GateOpeningMinHeight || height > GateOpeningMaxHeight)
                return false;
            if (depth <= 0f || depth > 1.4f)
                return false;

            // A scored renderer may be the leaf alone (handles are separate meshes) or the whole
            // assembly; never trust it to be shallower than the deepest vanilla handle.
            float frontExtent = Mathf.Max(depth * 0.5f, GateFallbackFrontExtent);
            opening = new GateOpening(width, height, bounds.center, "validated-renderer-bounds", frontExtent);
            return true;
        }

        private static bool TryGetDoorLeafWorldBounds(Renderer renderer, Transform doorRoot, out Bounds bounds)
        {
            bounds = default;
            if (renderer == null || renderer.transform == null)
                return false;

            if (!TryGetRendererMeshBounds(renderer, out Bounds meshBounds))
                return false;

            string descriptor = GetDoorLeafDescriptor(renderer, doorRoot);
            bool hinted = ContainsAnyToken(descriptor, GateDoorLeafHints);
            if (!hinted && ContainsAnyToken(descriptor, GateDoorLeafRejects))
                return false;

            bool initialized = false;
            EncapsulateTransformedBounds(meshBounds, renderer.transform.localToWorldMatrix, ref bounds, ref initialized);
            if (!initialized)
                return false;

            Vector3 size = bounds.size;
            if (size.y < 1.5f || size.y > 5.2f)
                return false;

            float horizontalMax = Mathf.Max(size.x, size.z);
            float horizontalMin = Mathf.Min(size.x, size.z);
            if (horizontalMax < 0.5f || horizontalMax > 3.2f)
                return false;
            if (horizontalMin > 0.6f)
                return false;

            return true;
        }

        private static string GetDoorLeafDescriptor(Renderer renderer, Transform doorRoot)
        {
            string names = renderer.name + " ";
            Transform current = renderer.transform;
            int guard = 0;
            while (current != null && guard++ < 5)
            {
                names += current.name + " ";
                if (current == doorRoot)
                    break;

                current = current.parent;
            }

            Material[] materials = renderer.sharedMaterials;
            if (materials != null)
            {
                for (int i = 0; i < materials.Length; i++)
                {
                    if (materials[i] != null)
                        names += materials[i].name + " ";
                }
            }

            return names.ToLowerInvariant();
        }

        private static bool ContainsAnyToken(string descriptor, string[] tokens)
        {
            for (int i = 0; i < tokens.Length; i++)
            {
                if (descriptor.Contains(tokens[i]))
                    return true;
            }

            return false;
        }

        private static float AxisSpan(Bounds bounds, Vector3 axis)
        {
            return Mathf.Abs(axis.x) * bounds.size.x
                + Mathf.Abs(axis.y) * bounds.size.y
                + Mathf.Abs(axis.z) * bounds.size.z;
        }

        /// <summary>
        /// The opening for a door, measured from its leaves when possible and otherwise taken from
        /// the per-class constants. Per Lawson (2026-07-29) there is no per-interior size table:
        /// leaf measurement is one rule that adapts on its own, and the constants are the floor.
        /// </summary>
        private static GateOpening ResolveGateOpening(
            Transform doorRoot,
            Vector3 forward,
            Bounds fallbackBounds,
            GateDoorClass doorClass,
            Vector3 entrancePosition)
        {
            if (TryMeasureDoorOpening(doorRoot, forward, entrancePosition, out GateOpening measured))
                return measured;

            if (TryResolveValidatedRendererOpening(fallbackBounds, forward, out GateOpening rendererOpening))
                return rendererOpening;

            GetFallbackOpeningSize(doorClass, out float width, out float height);
            Vector3 center = fallbackBounds.size.sqrMagnitude > 0.0001f
                ? new Vector3(fallbackBounds.center.x, fallbackBounds.min.y + height * 0.5f, fallbackBounds.center.z)
                : entrancePosition + Vector3.up * (height * 0.5f);
            string provenance = doorClass == GateDoorClass.MainEntrance
                ? "fixed-main-fallback"
                : "fixed-fire-exit-fallback";
            return new GateOpening(width, height, center, provenance, GateFallbackFrontExtent);
        }

        private static void GetFallbackOpeningSize(GateDoorClass doorClass, out float width, out float height)
        {
            if (doorClass == GateDoorClass.MainEntrance)
            {
                width = GateMainOpeningFallbackWidth;
                height = GateMainOpeningFallbackHeight;
                return;
            }

            width = GateFireOpeningFallbackWidth;
            height = GateFireOpeningFallbackHeight;
        }

        /// <summary>
        /// Walks a bounded number of ancestors from a scored renderer while the subtree remains a
        /// small, nearby assembly. This includes sibling leaves on custom double doors without
        /// accidentally measuring an entire room or dungeon tile.
        /// </summary>
        private static Transform ResolveDoorAssemblyRoot(
            Transform rendererTransform,
            Vector3 entrancePosition)
        {
            if (rendererTransform == null)
                return null;

            Transform best = rendererTransform;
            Transform current = rendererTransform;
            int guard = 0;
            while (current.parent != null && guard++ < 5)
            {
                Transform candidate = current.parent;
                Renderer[] renderers = candidate.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0 || renderers.Length > GateDoorAssemblyMaxRenderers)
                    break;

                bool hasNearbyRenderer = false;
                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    if (renderer != null
                        && HorizontalDistance(renderer.bounds.center, entrancePosition) <= GateDoorAssemblySearchRadius)
                    {
                        hasNearbyRenderer = true;
                        break;
                    }
                }
                if (!hasNearbyRenderer)
                    break;

                best = candidate;
                current = candidate;
            }

            return best;
        }

        /// <summary>
        /// The door's facing axis. Prefers the transform's own forward, but corrects it to the
        /// geometry's thin horizontal axis when the door is clearly planar — mineshaft reuses the
        /// facility entrance doors at a different yaw, so neither signal alone is reliable.
        /// </summary>
        private static List<Vector3> GetInteriorEntranceFallbackPositions()
        {
            try
            {
                return InteriorAnchorService.GetInteriorEntrancePositions()
                    .Where(position => position != Vector3.zero)
                    .ToList();
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Containment lockdown entrance fallback failed: {e.Message}");
                return new List<Vector3>();
            }
        }

        private static Vector3 GetEntrancePosition(EntranceTeleport entrance)
        {
            if (entrance == null)
                return Vector3.zero;

            return entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static Vector3 FlattenForward(Transform transform)
        {
            Vector3 forward = transform != null ? transform.forward : Vector3.forward;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.001f)
                forward = Vector3.forward;
            return forward.normalized;
        }

        private static Bounds CalculateBounds(GameObject obj)
        {
            var renderers = obj.GetComponentsInChildren<Renderer>(true);
            if (renderers != null && renderers.Length > 0)
            {
                Bounds bounds = renderers[0].bounds;
                for (int i = 1; i < renderers.Length; i++)
                {
                    if (renderers[i] != null)
                        bounds.Encapsulate(renderers[i].bounds);
                }

                return bounds;
            }

            var colliders = obj.GetComponentsInChildren<Collider>(true);
            if (colliders != null && colliders.Length > 0)
            {
                Bounds bounds = colliders[0].bounds;
                for (int i = 1; i < colliders.Length; i++)
                {
                    if (colliders[i] != null)
                        bounds.Encapsulate(colliders[i].bounds);
                }

                return bounds;
            }

            return new Bounds(obj.transform.position + Vector3.up * 1.6f, new Vector3(3.4f, 3.4f, 0.5f));
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F2},{value.y:F2},{value.z:F2})";
        }

        private enum GateDoorClass
        {
            MainEntrance,
            FireExit
        }

        private readonly struct EntranceDoorRendererMatch
        {
            internal EntranceDoorRendererMatch(Renderer renderer, Bounds bounds, float score)
            {
                Renderer = renderer;
                Bounds = bounds;
                Score = score;
            }

            internal Renderer Renderer { get; }
            internal Bounds Bounds { get; }
            internal float Score { get; }
        }


        private struct GateAnchor
        {
            internal readonly Vector3 ClosedPosition;
            internal readonly Quaternion Rotation;
            internal readonly float Width;
            internal readonly float Height;
            internal readonly GateDoorClass DoorClass;
            internal readonly bool OpeningMeasured;
            internal readonly int EntranceId;
            internal readonly string EndpointSide;
            internal readonly string MeasurementProvenance;

            internal GateAnchor(
                Vector3 closedPosition,
                Quaternion rotation,
                float width,
                float height,
                GateDoorClass doorClass,
                bool openingMeasured,
                int entranceId = -1,
                string endpointSide = "service",
                string measurementProvenance = "fixed-fire-exit-fallback")
            {
                ClosedPosition = closedPosition;
                Rotation = rotation;
                Width = width;
                Height = height;
                DoorClass = doorClass;
                OpeningMeasured = openingMeasured;
                EntranceId = entranceId;
                EndpointSide = endpointSide ?? "unknown";
                MeasurementProvenance = measurementProvenance ?? "unknown";
            }
        }

        /// <summary>
        /// A measured frame opening: the span the two sliding halves must fill, and the centre
        /// of that span in world space.
        /// </summary>
        private struct GateOpening
        {
            internal readonly float Width;
            internal readonly float Height;
            internal readonly Vector3 Center;
            internal readonly bool Measured;
            internal readonly string Provenance;
            /// <summary>
            /// How far, along the door's forward axis from <see cref="Center"/>, the door assembly
            /// reaches inside the opening (leaf face, handles, push bars). The gate plate is placed
            /// in front of this so the vanilla door cannot intersect it.
            /// </summary>
            internal readonly float FrontExtent;

            internal GateOpening(float width, float height, Vector3 center, string provenance, float frontExtent)
            {
                Width = width;
                Height = height;
                Center = center;
                Provenance = provenance ?? "unknown";
                Measured = !Provenance.StartsWith("fixed-", StringComparison.Ordinal);
                FrontExtent = frontExtent;
            }
        }

        private struct EntranceInteractionState
        {
            private readonly InteractTrigger _trigger;
            private readonly bool _enabled;
            private readonly bool _interactable;
            private readonly string _hoverTip;
            private readonly string _disabledHoverTip;

            internal EntranceInteractionState(InteractTrigger trigger)
            {
                _trigger = trigger;
                _enabled = trigger != null && trigger.enabled;
                _interactable = trigger != null && trigger.interactable;
                _hoverTip = trigger != null ? trigger.hoverTip : string.Empty;
                _disabledHoverTip = trigger != null ? trigger.disabledHoverTip : string.Empty;
            }

            internal bool Restore()
            {
                if (_trigger == null)
                    return false;

                _trigger.enabled = _enabled;
                _trigger.interactable = _interactable;
                _trigger.hoverTip = _hoverTip;
                _trigger.disabledHoverTip = _disabledHoverTip;
                return true;
            }
        }

        private struct GateVisualFit
        {
            internal readonly Bounds LocalBounds;
            internal readonly Bounds WorldBounds;
            internal readonly string OrientationLabel;
            internal readonly Vector3 VisualScale;
            internal readonly Vector3 VisualLocalPosition;

            internal GateVisualFit(
                Bounds localBounds,
                Bounds worldBounds,
                string orientationLabel,
                Vector3 visualScale,
                Vector3 visualLocalPosition)
            {
                LocalBounds = localBounds;
                WorldBounds = worldBounds;
                OrientationLabel = orientationLabel;
                VisualScale = visualScale;
                VisualLocalPosition = visualLocalPosition;
            }
        }

        private struct GateVisualOrientation
        {
            internal readonly string Label;
            internal readonly Quaternion Rotation;
            internal readonly Bounds Bounds;

            internal GateVisualOrientation(string label, Quaternion rotation)
                : this(label, rotation, default)
            {
            }

            internal GateVisualOrientation(string label, Quaternion rotation, Bounds bounds)
            {
                Label = label;
                Rotation = rotation;
                Bounds = bounds;
            }
        }

        private static bool LocalPlayerIsInside()
        {
            var player = GameNetworkManager.Instance?.localPlayerController;
            return player != null && player.isInsideFactory && !player.isPlayerDead;
        }

        private void IgniteInteriorEnemiesLocal()
        {
            MethodInfo method = ResolveApplyBurningMethod();
            if (method == null)
            {
                ContractsBootstrap.Log?.LogInfo("[MoonContracts] Containment end burn effect skipped because Y4NGZUpgrades is not installed.");
                return;
            }

            // #503: left unsliced deliberately. This runs once, on the contract's end-sequence
            // transition, and the burn has to land on the same frame for every interior creature;
            // spreading it over passes would ignite stragglers after the sequence has moved on.
            EnemyAI[] enemies = FindObjectsOfType<EnemyAI>();
            int ignited = 0;
            foreach (EnemyAI enemy in enemies)
            {
                if (enemy == null || enemy.isEnemyDead || enemy.isOutside)
                    continue;

                if (TryApplyLGUBurning(enemy, method))
                    ignited++;
            }

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts] Containment end ignited {ignited} interior creature(s).");
        }

        private static bool TryApplyLGUBurning(EnemyAI enemy, MethodInfo method)
        {
            try
            {
                if (method == null)
                    return false;

                method.Invoke(null, new object[] { enemy, null, 18f, 3, 0.55f });
                return true;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts] Failed to ignite enemy '{enemy?.gameObject?.name}': {e.Message}");
                return false;
            }
        }

        private static MethodInfo ResolveApplyBurningMethod()
        {
            if (_burnEffectLookupAttempted)
                return _applyBurningMethod;

            _burnEffectLookupAttempted = true;

            // Presence gate first: AccessTools.TypeByName walks every type in every loaded
            // assembly on a miss, which froze contract completion for seconds in a large
            // pack. With the companion mod absent there is nothing to find, so skip the
            // scan entirely (issue #287).
            if (!Y4NGZUpgradesCompat.IsLoaded)
            {
                if (!_burnEffectMissingLogged)
                {
                    _burnEffectMissingLogged = true;
                    ContractsBootstrap.Log?.LogInfo("[MoonContracts] Y4NGZUpgrades is not installed; burning effect lookup skipped.");
                }

                return null;
            }

            // Scoped to the one assembly that can own the type — never a global type scan.
            Type type = null;
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!string.Equals(assembly.GetName().Name, Y4NGZUpgradesCompat.ASSEMBLY_NAME, StringComparison.Ordinal))
                    continue;

                type = assembly.GetType("Y4NGZUpgrades.Effects.EnemyStatusEffects", false);
                if (type != null)
                    break;
            }

            // ApplyBurning is internal static, so NonPublic is required here.
            _applyBurningMethod = type?.GetMethod(
                "ApplyBurning",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                null,
                new[] { typeof(EnemyAI), typeof(GameNetcodeStuff.PlayerControllerB), typeof(float), typeof(int), typeof(float) },
                null);

            if (_applyBurningMethod == null && !_burnEffectMissingLogged)
            {
                _burnEffectMissingLogged = true;
                ContractsBootstrap.Log?.LogWarning("[MoonContracts] Y4NGZUpgrades burning effect not found.");
            }

            return _applyBurningMethod;
        }
    }

    /// <summary>
    /// Drives a containment lockdown gate. The gate root sits in the doorway permanently and the
    /// two authored door halves slide laterally in their own local space (issue #171), retracting
    /// into the frame jambs when open. While retracted the halves are hidden and non-blocking, so
    /// nothing is visible or solid until the lockdown actually fires.
    ///
    /// The legacy single-panel vertical drop is kept for the bar-panel fallback, which a client on
    /// an older lgucontracts.bundle will still get.
    /// </summary>
    internal sealed class ContainmentLockdownPanel : MonoBehaviour
    {
        private const float GateCloseDuration = 0.28f;
        private const float GateOpenDuration = 0.42f;

        internal struct SlidingHalf
        {
            internal readonly Transform Transform;
            internal readonly Vector3 ClosedLocalPosition;
            internal readonly Vector3 OpenLocalPosition;
            /// <summary>Hides the part of the leaf outside the jamb while it travels; null keeps a plain slide.</summary>
            internal readonly SlidingLeafJambClipper Clipper;

            internal SlidingHalf(Transform transform, Vector3 closedLocalPosition, Vector3 openLocalPosition)
                : this(transform, closedLocalPosition, openLocalPosition, null)
            {
            }

            internal SlidingHalf(
                Transform transform,
                Vector3 closedLocalPosition,
                Vector3 openLocalPosition,
                SlidingLeafJambClipper clipper)
            {
                Transform = transform;
                ClosedLocalPosition = closedLocalPosition;
                OpenLocalPosition = openLocalPosition;
                Clipper = clipper;
            }
        }

        private readonly List<SlidingHalf> _halves = new List<SlidingHalf>();
        private NavMeshObstacle _obstacle;
        private Coroutine _routine;
        private bool _slidingMode;
        private Vector3 _openPosition;
        private Vector3 _closedPosition;

        internal void PrepareSlidingHalves(List<SlidingHalf> halves, NavMeshObstacle obstacle)
        {
            _slidingMode = true;
            _obstacle = obstacle;
            _halves.Clear();
            if (halves != null)
                _halves.AddRange(halves);

            _closedPosition = transform.position;
            _openPosition = transform.position;
            foreach (SlidingHalf half in _halves)
            {
                if (half.Transform != null)
                    half.Transform.localPosition = half.OpenLocalPosition;
            }

            ApplyJambClips();
            SetObstacleArmed(false);
            SetGateVisible(false);
        }

        private void OnDestroy()
        {
            foreach (SlidingHalf half in _halves)
                half.Clipper?.Dispose();
        }

        /// <summary>
        /// Re-clips every half against its jamb for the halves' current positions. Runs before
        /// the renderers are shown and after every position write, so no frame ever draws a leaf
        /// outside the opening.
        /// </summary>
        private void ApplyJambClips()
        {
            foreach (SlidingHalf half in _halves)
                half.Clipper?.Apply();
        }

        internal void Prepare(Vector3 openPosition, Vector3 closedPosition, NavMeshObstacle obstacle = null)
        {
            _slidingMode = false;
            _obstacle = obstacle;
            _openPosition = openPosition;
            _closedPosition = closedPosition;
            transform.position = _openPosition;
            SetObstacleArmed(false);
        }

        internal void Close()
        {
            if (_slidingMode)
            {
                ApplyJambClips();
                SetGateVisible(true);
                Restart(AnimateHalves(closing: true, GateCloseDuration, destroyWhenDone: false));
                return;
            }

            Restart(AnimateRoot(transform.position, _closedPosition, GateCloseDuration, destroyWhenDone: false, armObstacle: true));
        }

        internal void OpenAndDestroy()
        {
            SetObstacleArmed(false);
            if (_slidingMode)
            {
                Restart(AnimateHalves(closing: false, GateOpenDuration, destroyWhenDone: true));
                return;
            }

            Restart(AnimateRoot(transform.position, _openPosition, GateOpenDuration, destroyWhenDone: true, armObstacle: false));
        }

        private void Restart(IEnumerator routine)
        {
            if (_routine != null)
                StopCoroutine(_routine);

            _routine = StartCoroutine(routine);
        }

        private IEnumerator AnimateHalves(bool closing, float duration, bool destroyWhenDone)
        {
            var from = new Vector3[_halves.Count];
            var to = new Vector3[_halves.Count];
            for (int i = 0; i < _halves.Count; i++)
            {
                SlidingHalf half = _halves[i];
                from[i] = half.Transform != null ? half.Transform.localPosition : Vector3.zero;
                to[i] = closing ? half.ClosedLocalPosition : half.OpenLocalPosition;
            }

            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                float eased = Ease(Mathf.Clamp01(elapsed / duration));
                for (int i = 0; i < _halves.Count; i++)
                {
                    Transform half = _halves[i].Transform;
                    if (half != null)
                        half.localPosition = Vector3.Lerp(from[i], to[i], eased);
                }

                ApplyJambClips();
                yield return null;
            }

            for (int i = 0; i < _halves.Count; i++)
            {
                Transform half = _halves[i].Transform;
                if (half != null)
                    half.localPosition = to[i];
            }

            ApplyJambClips();
            if (closing)
                SetObstacleArmed(true);
            else
                SetGateVisible(false);

            if (destroyWhenDone)
                Destroy(gameObject);
        }

        private IEnumerator AnimateRoot(Vector3 from, Vector3 to, float duration, bool destroyWhenDone, bool armObstacle)
        {
            float elapsed = 0f;
            while (elapsed < duration)
            {
                elapsed += Time.deltaTime;
                transform.position = Vector3.Lerp(from, to, Ease(Mathf.Clamp01(elapsed / duration)));
                yield return null;
            }

            transform.position = to;
            if (armObstacle)
                SetObstacleArmed(true);

            if (destroyWhenDone)
                Destroy(gameObject);
        }

        private static float Ease(float t)
        {
            return 1f - Mathf.Pow(1f - t, 3f);
        }

        private void SetGateVisible(bool visible)
        {
            foreach (SlidingHalf half in _halves)
            {
                if (half.Transform == null)
                    continue;

                foreach (Renderer renderer in half.Transform.GetComponentsInChildren<Renderer>(true))
                {
                    if (renderer != null)
                        renderer.enabled = visible;
                }

                foreach (Collider collider in half.Transform.GetComponentsInChildren<Collider>(true))
                {
                    if (collider != null)
                        collider.enabled = visible;
                }
            }
        }

        /// <summary>
        /// Carving is only switched on once the gate has stopped moving, so the navmesh is never
        /// re-cut against a travelling obstacle.
        /// </summary>
        private void SetObstacleArmed(bool armed)
        {
            if (_obstacle == null)
                return;

            _obstacle.enabled = armed;
        }
    }

    [HarmonyPatch(typeof(EntranceTeleport), "TeleportPlayer")]
    internal static class ContainmentExitBlockPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(EntranceTeleport __instance)
        {
            return !ContainmentBreachDirector.LockdownActive;
        }
    }
}
