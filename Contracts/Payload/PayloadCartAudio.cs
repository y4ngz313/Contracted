using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.Networking;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Assets;
namespace Y4NGZCompany.Contracts.Payload
{
    internal sealed class PayloadCartAudio : MonoBehaviour
    {
        private const string PayloadAudioFolder = "Payload";
        private const string ContainmentAudioFolder = "Containment Breach";
        private const string RollingClip01FileName = "CartRolling_01.wav";
        private const string RollingClip02FileName = "CartRolling_02.wav";
        private const string MagnetClangFileName = "MagnetClang_01.mp3";
        private const string MagnetEngageFileName = "MagnetEngage_01.mp3";
        private const string CartJolt01FileName = "CartJolt_01.mp3";
        private const string CartJolt02FileName = "CartJolt_02.mp3";
        private const string ShovelClangFileName = "ShovelClang_01.mp3";
        private const string CartRunawayFileName = "CartRunaway_01.mp3";
        private const string FallbackClangFileName = "freesound_community-metallic-door-shut-98740.mp3";
        internal const float RollingVolume = 1f;
        private const float RollingMinDistance = 4f;
        private const float RollingMaxDistance = 30f;
        private const float RollingStartSpeed = 0.08f;
        private const float RollingStopSpeed = 0.04f;
        private const float RollingSpeedReference = 4.0f;
        private const float RollingFadeStopSeconds = 0.05f;
        private const float RollingNoiseInterval = 0.45f;
        private const float OneShotMinDistance = 4f;
        private const float OneShotMaxDistance = 30f;

        private static readonly AudioClip[] RollingClips = new AudioClip[2];
        private static readonly AudioClip[] JoltClips = new AudioClip[2];
        private static readonly HashSet<string> MissingClipWarnings = new HashSet<string>();
        private static bool _loadsStarted;
        private static bool _loadsComplete;
        private static string _resolvedAudioRoot;
        private static AudioClip _magnetClangClip;
        private static AudioClip _magnetEngageClip;
        private static AudioClip _shovelClangClip;
        private static AudioClip _runawayClip;
        private static AudioClip _fallbackClangClip;
        private static int _nextLoopIndex;
        private static bool _vanillaClangLogged;
        private static int _nextJoltIndex;

        private AudioSource _rollingSource;
        private Vector3 _lastPosition;
        private bool _lastPositionValid;
        private float _observedSpeed;
        private bool _rolling;
        private int _rollingClipIndex;
        private Coroutine _fadeCoroutine;
        private float _nextNoiseAt;
        private bool _rollingStartLogged;
        private bool _rollingStopLogged;

        private void Awake()
        {
            EnsureRollingSource();
        }

        private void Update()
        {
            EnsureRollingSource();
            UpdateObservedSpeed();
            UpdateRollingLoop();
        }

        /// <summary>
        /// Kicks off the cart's clip loads. Every load — including the directory probing that resolves
        /// each file's path — is staged one clip per frame (#299). #1233 S8: ContractsBootstrap starts
        /// it on Plugin.CoroutineHost at plugin init, so the decode is paid at load time rather than
        /// across the nineteen frames after touchdown; the cart's own OnNetworkSpawn call is the
        /// no-op that proves the load was started.
        /// </summary>
        internal static void EnsureAudioLoads(MonoBehaviour host)
        {
            if (host == null || _loadsStarted || _loadsComplete)
                return;

            _loadsStarted = true;
            host.StartCoroutine(LoadAllClipsStaged());
        }

        private static IEnumerator LoadAllClipsStaged()
        {
            // Nothing here may run on the frame the load is started.
            yield return null;

            var requests = new List<ClipRequest>
            {
                new ClipRequest(PayloadAudioFolder, RollingClip01FileName, AudioType.WAV, clip => RollingClips[0] = clip),
                new ClipRequest(PayloadAudioFolder, RollingClip02FileName, AudioType.WAV, clip => RollingClips[1] = clip),
                new ClipRequest(PayloadAudioFolder, MagnetClangFileName, AudioType.MPEG, clip => _magnetClangClip = clip),
                new ClipRequest(PayloadAudioFolder, MagnetEngageFileName, AudioType.MPEG, clip => _magnetEngageClip = clip),
                new ClipRequest(PayloadAudioFolder, CartJolt01FileName, AudioType.MPEG, clip => JoltClips[0] = clip),
                new ClipRequest(PayloadAudioFolder, CartJolt02FileName, AudioType.MPEG, clip => JoltClips[1] = clip),
                new ClipRequest(PayloadAudioFolder, ShovelClangFileName, AudioType.MPEG, clip => _shovelClangClip = clip),
                new ClipRequest(PayloadAudioFolder, CartRunawayFileName, AudioType.MPEG, clip => _runawayClip = clip),
                new ClipRequest(ContainmentAudioFolder, FallbackClangFileName, AudioType.MPEG, clip => _fallbackClangClip = clip),
            };

            double cpuMilliseconds = 0.0;
            int frames = 1;
            int resolved = 0;
            for (int i = 0; i < requests.Count; i++)
            {
                ClipRequest request = requests[i];
                long segmentStart = System.Diagnostics.Stopwatch.GetTimestamp();
                AudioClip bundled = CompressedContractAudio.Load("contracts/" + request.Subfolder + "/" + request.FileName);
                if (bundled != null)
                {
                    request.Assign?.Invoke(bundled);
                    resolved++;
                    cpuMilliseconds += ToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - segmentStart);
                    yield return null;
                    frames++;
                    continue;
                }
                string path = ResolveAudioPath(request.Subfolder, request.FileName);
                cpuMilliseconds += ToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - segmentStart);

                if (!string.IsNullOrEmpty(path))
                {
                    IEnumerator load = LoadClip(path, request.FileName, request.AudioType, request.Assign, ms => cpuMilliseconds += ms);
                    while (load.MoveNext())
                    {
                        yield return load.Current;
                        frames++;
                    }

                    resolved++;
                }

                // One clip per frame: the disk probe, the request and the decode all stay off any
                // single frame's critical path.
                yield return null;
                frames++;
            }

            _loadsComplete = true;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Timing] Payload audio staged: {resolved}/{requests.Count} clip(s) over {frames} frame(s), " +
                $"mainThreadCpu={cpuMilliseconds:0.00}ms.");
        }

        private readonly struct ClipRequest
        {
            internal ClipRequest(string subfolder, string fileName, AudioType audioType, Action<AudioClip> assign)
            {
                Subfolder = subfolder;
                FileName = fileName;
                AudioType = audioType;
                Assign = assign;
            }

            internal string Subfolder { get; }
            internal string FileName { get; }
            internal AudioType AudioType { get; }
            internal Action<AudioClip> Assign { get; }
        }

        private static double ToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        }

        internal static void PlayMagnetClangAt(Vector3 position, float volume = 1f, float pitch = 1f)
        {
            AudioClip clip = ResolveMagnetClangClip();
            if (clip == null)
            {
                LogMissingClipOnce("magnet clang");
                return;
            }

            PlayOneShotAt(clip, position, volume, pitch, OneShotMaxDistance, "MagnetClang");
        }

        internal static void PlayShovelClangAt(Vector3 position, float volume = 0.9f, float pitch = 1f)
        {
            AudioClip clip = ResolveShovelClangClip();
            if (clip == null)
            {
                LogMissingClipOnce("shovel clang");
                return;
            }

            PlayOneShotAt(clip, position, volume, pitch, OneShotMaxDistance, "ShovelClang");
        }

        internal static void PlayMagnetEngageAt(Vector3 position, float volume = 1f, float pitch = 1f)
        {
            if (_magnetEngageClip == null)
            {
                LogMissingClipOnce("magnet engage");
                return;
            }

            PlayOneShotAt(_magnetEngageClip, position, volume, pitch, OneShotMaxDistance, "MagnetEngage");
        }

        internal static void PlayJoltAt(Vector3 position, float intensity)
        {
            AudioClip clip = SelectJoltClip();
            if (clip == null)
            {
                LogMissingClipOnce("cart jolt");
                return;
            }

            float amount = Mathf.Clamp01(intensity);
            float volume = Mathf.Lerp(0.3f, 0.85f, amount);
            float pitch = UnityEngine.Random.Range(0.92f, 1.08f);
            PlayOneShotAt(clip, position, volume, pitch, OneShotMaxDistance, "CartJolt");
        }

        internal static void PlayRunawayAt(Vector3 position, float volume = 0.95f, float pitch = 1f)
        {
            if (_runawayClip == null)
            {
                LogMissingClipOnce("cart runaway");
                return;
            }

            PlayOneShotAt(_runawayClip, position, volume, pitch, OneShotMaxDistance, "CartRunaway");
        }

        internal static void PlayClangAt(Vector3 position, float volume = 0.9f, float pitch = 1f)
        {
            PlayMagnetClangAt(position, volume, pitch);
        }

        private static AudioClip ResolveMagnetClangClip()
        {
            if (_magnetClangClip != null)
                return _magnetClangClip;

            return ResolveLegacyClangClip();
        }

        private static AudioClip ResolveShovelClangClip()
        {
            if (_shovelClangClip != null)
                return _shovelClangClip;

            return ResolveLegacyClangClip();
        }

        private static AudioClip ResolveLegacyClangClip()
        {
            AudioClip clip = StartOfRound.Instance != null && StartOfRound.Instance.shutDoorMetal != null && StartOfRound.Instance.shutDoorMetal.Length > 0
                ? StartOfRound.Instance.shutDoorMetal.FirstOrDefault(sound => sound != null)
                : null;
            if (clip != null)
            {
                if (!_vanillaClangLogged)
                {
                    _vanillaClangLogged = true;
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Audio] Using vanilla shutDoorMetal clang clip '{clip.name}'.");
                }
                return clip;
            }

            return _fallbackClangClip;
        }

        private static AudioClip SelectJoltClip()
        {
            int start = _nextJoltIndex++ % JoltClips.Length;
            for (int i = 0; i < JoltClips.Length; i++)
            {
                int index = (start + i) % JoltClips.Length;
                if (JoltClips[index] != null)
                    return JoltClips[index];
            }

            return null;
        }

        private static void PlayOneShotAt(AudioClip clip, Vector3 position, float volume, float pitch, float maxDistance, string label)
        {
            if (clip == null)
            {
                LogMissingClipOnce(label);
                return;
            }

            GameObject host = null;
            try
            {
                host = new GameObject("PayloadCart_" + label + "Sfx");
                host.transform.position = position;
                AudioSource source = host.AddComponent<AudioSource>();
                ConfigureSpatialSource(source, maxDistance, OneShotMinDistance, AudioRolloffMode.Logarithmic);
                source.pitch = Mathf.Clamp(pitch, 0.65f, 1.35f);
                source.volume = 1f;
                source.PlayOneShot(clip, Mathf.Clamp01(volume));
                Destroy(host, Mathf.Max(0.25f, clip.length / Mathf.Max(0.1f, source.pitch)) + 0.25f);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload.Audio] Failed playing {label} at {position}: {e.GetType().Name}: {e.Message}");
                if (host != null)
                    Destroy(host);
            }
        }

        private static void LogMissingClipOnce(string label)
        {
            if (!MissingClipWarnings.Add(label ?? "unknown"))
                return;

            ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload.Audio] {label} clip unavailable; playback skipped.");
        }

        private void EnsureRollingSource()
        {
            if (_rollingSource != null)
                return;

            _rollingSource = gameObject.AddComponent<AudioSource>();
            ConfigureSpatialSource(_rollingSource, RollingMaxDistance, RollingMinDistance, AudioRolloffMode.Logarithmic);
            _rollingSource.loop = true;
            _rollingSource.playOnAwake = false;
            _rollingSource.volume = 0f;
            _rollingSource.dopplerLevel = 0f;
        }

        private static void ConfigureSpatialSource(
            AudioSource source,
            float maxDistance,
            float minDistance = OneShotMinDistance,
            AudioRolloffMode rolloffMode = AudioRolloffMode.Logarithmic)
        {
            if (source == null)
                return;

            source.spatialBlend = 1f;
            source.rolloffMode = rolloffMode;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.priority = 96;
            source.dopplerLevel = 0f;
        }

        private void UpdateObservedSpeed()
        {
            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 position = transform.position;
            float instant = 0f;
            if (_lastPositionValid)
            {
                Vector3 delta = position - _lastPosition;
                delta.y = 0f;
                instant = delta.magnitude / dt;
            }

            _lastPosition = position;
            _lastPositionValid = true;
            float blend = 1f - Mathf.Exp(-18f * dt);
            _observedSpeed = Mathf.Lerp(_observedSpeed, instant, blend);
        }

        private void UpdateRollingLoop()
        {
            if (_rollingSource == null)
                return;

            bool shouldRoll = _rolling ? _observedSpeed > RollingStopSpeed : _observedSpeed > RollingStartSpeed;
            if (shouldRoll)
            {
                StartOrUpdateRollingLoop();
                return;
            }

            if (_rolling)
                StopRollingLoop();
        }

        private void StartOrUpdateRollingLoop()
        {
            AudioClip clip = SelectRollingClip();
            if (clip == null)
                return;

            if (_fadeCoroutine != null)
            {
                StopCoroutine(_fadeCoroutine);
                _fadeCoroutine = null;
            }

            if (!_rolling || _rollingSource.clip != clip)
            {
                _rollingSource.Stop();
                _rollingSource.clip = clip;
                _rollingSource.time = UnityEngine.Random.Range(0f, Mathf.Max(0.01f, clip.length));
                _rollingSource.Play();
                _rolling = true;
                if (!_rollingStartLogged)
                {
                    _rollingStartLogged = true;
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Audio] Rolling loop started: clip='{clip.name}', observedSpeed={_observedSpeed:0.##}m/s.");
                }
            }

            float speed01 = Mathf.Clamp01(_observedSpeed / RollingSpeedReference);
            _rollingSource.volume = RollingVolume;
            _rollingSource.pitch = Mathf.Lerp(0.82f, 1.16f, speed01);

            if (RoundManager.Instance != null && Time.time >= _nextNoiseAt)
            {
                _nextNoiseAt = Time.time + RollingNoiseInterval;
                RoundManager.Instance.PlayAudibleNoise(transform.position, 16f, Mathf.Lerp(0.25f, 0.65f, speed01));
            }
        }

        private AudioClip SelectRollingClip()
        {
            if (_rolling && _rollingClipIndex >= 0 && _rollingClipIndex < RollingClips.Length && RollingClips[_rollingClipIndex] != null)
                return RollingClips[_rollingClipIndex];

            int start = _nextLoopIndex++ % RollingClips.Length;
            for (int i = 0; i < RollingClips.Length; i++)
            {
                int index = (start + i) % RollingClips.Length;
                if (RollingClips[index] == null)
                    continue;

                _rollingClipIndex = index;
                return RollingClips[index];
            }

            return null;
        }

        private void StopRollingLoop()
        {
            _rolling = false;
            if (!_rollingStopLogged)
            {
                _rollingStopLogged = true;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Audio] Rolling loop stopping abruptly: observedSpeed={_observedSpeed:0.##}m/s.");
            }

            if (_fadeCoroutine != null)
                StopCoroutine(_fadeCoroutine);
            _fadeCoroutine = StartCoroutine(FadeStopRolling());
        }

        private IEnumerator FadeStopRolling()
        {
            float startVolume = _rollingSource != null ? _rollingSource.volume : 0f;
            float t = 0f;
            while (_rollingSource != null && t < RollingFadeStopSeconds)
            {
                t += Time.deltaTime;
                _rollingSource.volume = Mathf.Lerp(startVolume, 0f, Mathf.Clamp01(t / RollingFadeStopSeconds));
                yield return null;
            }

            if (_rollingSource != null)
            {
                _rollingSource.Stop();
                _rollingSource.volume = 0f;
            }
            _fadeCoroutine = null;
        }

        private static IEnumerator LoadClip(string path, string fileName, AudioType audioType, Action<AudioClip> assign, Action<double> reportCpu)
        {
            long requestStart = System.Diagnostics.Stopwatch.GetTimestamp();
            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, audioType))
            {
                UnityWebRequestAsyncOperation operation = request.SendWebRequest();
                reportCpu?.Invoke(ToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - requestStart));

                yield return operation;

                long decodeStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload.Audio] Failed loading audio '{path}': {request.error}");
                    reportCpu?.Invoke(ToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - decodeStart));
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip != null)
                {
                    clip.name = "Payload_" + Path.GetFileNameWithoutExtension(fileName);
                    assign?.Invoke(clip);
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Audio] Loaded audio '{clip.name}' from '{path}'.");
                }
                else
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload.Audio] Loaded audio '{path}' returned a null AudioClip.");
                }

                reportCpu?.Invoke(ToMilliseconds(System.Diagnostics.Stopwatch.GetTimestamp() - decodeStart));
            }
        }

        private static string ResolveAudioPath(string subfolder, string fileName)
        {
            // The first successful resolve pins the ContractAudio root, so later clips cost one
            // File.Exists instead of walking (and logging) the whole candidate list.
            if (!string.IsNullOrEmpty(_resolvedAudioRoot))
            {
                string cached = Path.Combine(_resolvedAudioRoot, subfolder, fileName);
                if (File.Exists(cached))
                    return cached;
            }

            string[] roots = AudioRoots();
            for (int i = 0; i < roots.Length; i++)
            {
                string candidate = Path.Combine(roots[i], subfolder, fileName);
                if (!File.Exists(candidate))
                    continue;

                if (_resolvedAudioRoot != roots[i])
                {
                    _resolvedAudioRoot = roots[i];
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Audio] ContractAudio root resolved to '{_resolvedAudioRoot}'.");
                }

                return candidate;
            }

            // Only a miss is worth the per-candidate detail; the probe list used to log ~50 lines on
            // the activation frame (#299).
            for (int i = 0; i < roots.Length; i++)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Audio] Audio candidate for '{fileName}': '{Path.Combine(roots[i], subfolder, fileName)}' exists=False.");

            ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload.Audio] Audio missing: ContractAudio/{subfolder}/{fileName}");
            return null;
        }

        private static string[] AudioRoots()
        {
            string assemblyDir = Path.GetDirectoryName(typeof(ContractsBootstrap).Assembly.Location) ?? string.Empty;
            return new[]
            {
                Path.Combine(assemblyDir, "ContractAudio"),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", "ContractAudio"),
                Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZUpgrades", "ContractAudio"),
                Path.Combine(Paths.PluginPath, "ContractAudio"),
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ContractAudio")
            };
        }
    }

    internal sealed class PayloadCartHitbox : MonoBehaviour, IHittable
    {
        private const string HitboxChildName = "PayloadCart_Hitbox";

        internal PayloadCartController Cart;
        private BoxCollider _collider;

        internal static PayloadCartHitbox Ensure(PayloadCartController cart)
        {
            if (cart == null)
                return null;

            Transform child = cart.transform.Find(HitboxChildName);
            if (child == null)
            {
                child = new GameObject(HitboxChildName).transform;
                child.SetParent(cart.transform, worldPositionStays: false);
            }

            child.gameObject.layer = ResolvePropsLayer();
            TrySetTag(child.gameObject, "Untagged");

            PayloadCartHitbox hitbox = child.GetComponent<PayloadCartHitbox>();
            if (hitbox == null)
                hitbox = child.gameObject.AddComponent<PayloadCartHitbox>();

            hitbox.Cart = cart;
            hitbox.ConfigureCollider();
            return hitbox;
        }

        public bool Hit(int force, Vector3 hitDirection, PlayerControllerB playerWhoHit = null, bool playHitSFX = false, int hitID = -1)
        {
            PayloadCartController cart = Cart != null ? Cart : GetComponentInParent<PayloadCartController>();
            Vector3 position = _collider != null ? _collider.bounds.ClosestPoint(transform.position + hitDirection.normalized * 0.25f) : transform.position;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload.Hit] Payload cart hit: force={force}, hitID={hitID}, playHitSFX={playHitSFX}, " +
                $"player={(playerWhoHit != null ? playerWhoHit.playerUsername : "unknown")}, pos={position}.");
            if (cart != null)
                cart.RequestPayloadClang(position, Mathf.Max(1, force));
            else
                PayloadCartAudio.PlayShovelClangAt(position, 0.85f, 1f);
            return true;
        }

        private void ConfigureCollider()
        {
            _collider = GetComponent<BoxCollider>();
            if (_collider == null)
                _collider = gameObject.AddComponent<BoxCollider>();

            _collider.isTrigger = false;
            _collider.enabled = true;

            BoxCollider rootBox = Cart != null ? Cart.GetComponent<BoxCollider>() : null;
            if (rootBox != null)
            {
                transform.localPosition = rootBox.center;
                transform.localRotation = Quaternion.identity;
                transform.localScale = Vector3.one;
                _collider.center = Vector3.zero;
                _collider.size = new Vector3(
                    Mathf.Max(0.35f, rootBox.size.x * 0.9f),
                    Mathf.Max(0.35f, rootBox.size.y * 0.85f),
                    Mathf.Max(0.35f, rootBox.size.z * 0.9f));
            }
            else
            {
                transform.localPosition = new Vector3(0f, 0.55f, 0f);
                transform.localRotation = Quaternion.identity;
                transform.localScale = Vector3.one;
                _collider.center = Vector3.zero;
                _collider.size = new Vector3(1.0f, 0.9f, 1.25f);
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload.Hit] Hittable child collider ready: layer={gameObject.layer}, " +
                $"center={_collider.center}, size={_collider.size}, rootCollider={(rootBox != null ? "yes" : "no")}.");
        }

        private static int ResolvePropsLayer()
        {
            int props = LayerMask.NameToLayer("Props");
            if (props >= 0)
                return props;

            return 6;
        }

        private static void TrySetTag(GameObject obj, string tag)
        {
            try
            {
                obj.tag = tag;
            }
            catch
            {
                obj.tag = "Untagged";
            }
        }
    }
}
