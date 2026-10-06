using System;
using System.Collections;
using System.IO;
using BepInEx;
using UnityEngine;
using UnityEngine.Networking;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Assets;

namespace Y4NGZCompany.Contracts.WasteDisposal
{
    /// <summary>Loads the supplied loose audio and owns all state-aware incinerator playback.</summary>
    internal sealed class WasteDisposalIncineratorAudio : MonoBehaviour
    {
        private const string AudioFolder = "WasteDisposal";
        private const double BurnLoopStartAfterSeconds = 0.60d;

        /// <summary>
        /// #716 E13: the idle and burn loops used to cut dead on every state change. A short
        /// ramp keeps the machine from clicking off mid-cycle.
        /// </summary>
        private const float LoopFadeSeconds = 0.22f;

        private AudioSource _idle;
        private AudioSource _burn;
        private AudioSource _oneShot;
        private AudioSource _fault;
        private float _idleVolume;
        private float _burnVolume;
        private Coroutine _idleFade;
        private Coroutine _burnFade;
        private AudioClip _idleLoop;
        private AudioClip _burnStart;
        private AudioClip _burnLoop;
        private AudioClip _complete;
        private AudioClip _buttonStart;
        private AudioClip _buttonStop;
        private AudioClip _doorOpenClip;
        private AudioClip _doorClose;
        private AudioClip _itemPlace1;
        private AudioClip _itemPlace2;
        private AudioClip _ember1;
        private AudioClip _ember2;
        private WasteDisposalIncineratorController.IncineratorState _state;
        private bool _hasState;
        private bool _doorIsOpen;
        private bool _hasDoorState;

        internal void Initialize(Transform socket)
        {
            transform.position = socket != null ? socket.position : transform.position;
            _idleVolume = 0.20f;
            _burnVolume = 0.58f;
            _idle = CreateSource("IdleLoop", loop: true, volume: _idleVolume, maxDistance: 26f);
            _burn = CreateSource("BurnLoop", loop: true, volume: _burnVolume, maxDistance: 42f);
            _oneShot = CreateSource("IncineratorCues", loop: false, volume: 0.78f, maxDistance: 40f);
            // #716 E13: the malfunction had no cue at all beyond screen text. The loaded set has
            // no dedicated fault clip, so the stop cue is reused on its own detuned source,
            // which reads as the machine tripping rather than as somebody pressing stop.
            _fault = CreateSource("IncineratorFault", loop: false, volume: 0.95f, maxDistance: 46f);
            _fault.pitch = 0.68f;
            StartCoroutine(LoadAll());
        }

        internal void SetState(WasteDisposalIncineratorController.IncineratorState state)
        {
            bool transition = _hasState && _state != state;
            _state = state;
            _hasState = true;

            switch (state)
            {
                case WasteDisposalIncineratorController.IncineratorState.Loading:
                case WasteDisposalIncineratorController.IncineratorState.Paused:
                    StopBurn();
                    PlayIdle();
                    break;
                case WasteDisposalIncineratorController.IncineratorState.Malfunctioned:
                    StopBurn();
                    PlayIdle();
                    // Driven by the replicated state, so every peer hears the fault once.
                    if (transition) PlayMalfunctionCue();
                    break;
                case WasteDisposalIncineratorController.IncineratorState.Burning:
                    StopIdle();
                    if (transition)
                        StartBurnTransition();
                    else
                        PlayBurnLoopNow();
                    break;
                case WasteDisposalIncineratorController.IncineratorState.Complete:
                    StopIdle();
                    StopBurn();
                    if (transition && _complete != null)
                        _oneShot.PlayOneShot(_complete, 0.9f);
                    break;
            }
        }

        internal void SetDoorOpen(bool open)
        {
            bool changed = _hasDoorState && _doorIsOpen != open;
            _doorIsOpen = open;
            _hasDoorState = true;
            if (!changed || _oneShot == null)
                return;
            AudioClip clip = open ? _doorOpenClip : _doorClose;
            if (clip != null) _oneShot.PlayOneShot(clip, 0.72f);
        }

        internal void PlayStartButton()
        {
            if (_buttonStart != null) _oneShot.PlayOneShot(_buttonStart, 0.85f);
        }

        internal void PlayStopButton()
        {
            if (_buttonStop != null) _oneShot.PlayOneShot(_buttonStop, 0.85f);
        }

        internal void PlayItemPlaced(int index)
        {
            AudioClip clip = index % 2 == 0 ? _itemPlace1 : _itemPlace2;
            if (clip != null) _oneShot.PlayOneShot(clip, 0.82f);
        }

        internal void PlayEmber(int index)
        {
            AudioClip clip = index % 2 == 0 ? _ember1 : _ember2;
            if (clip != null) _oneShot.PlayOneShot(clip, 0.68f);
        }

        private AudioSource CreateSource(string name, bool loop, float volume, float maxDistance)
        {
            GameObject obj = new GameObject(name);
            obj.transform.SetParent(transform, false);
            AudioSource source = obj.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Linear;
            source.minDistance = 2f;
            source.maxDistance = maxDistance;
            source.volume = volume;
            return source;
        }

        private void PlayMalfunctionCue()
        {
            if (_fault != null && _buttonStop != null) _fault.PlayOneShot(_buttonStop, 0.95f);
        }

        private void PlayIdle()
        {
            if (_idle == null || _idleLoop == null)
                return;
            CancelFade(ref _idleFade, _idle, _idleVolume);
            if (_idle.isPlaying)
                return;
            _idle.clip = _idleLoop;
            _idle.Play();
        }

        private void StopIdle()
        {
            FadeOutAndStop(_idle, ref _idleFade, _idleVolume);
        }

        private void StartBurnTransition()
        {
            // The scheduled restart owns this source immediately, so it must be a hard stop:
            // a fade here would still be ramping the volume down when the loop comes back.
            CancelFade(ref _burnFade, _burn, _burnVolume);
            if (_burn != null) _burn.Stop();
            if (_burnStart != null)
                _oneShot.PlayOneShot(_burnStart, 0.92f);
            if (_burnLoop == null)
                return;
            _burn.clip = _burnLoop;
            _burn.PlayScheduled(AudioSettings.dspTime + (_burnStart != null ? BurnLoopStartAfterSeconds : 0.01d));
        }

        private void PlayBurnLoopNow()
        {
            if (_burn == null || _burnLoop == null)
                return;
            CancelFade(ref _burnFade, _burn, _burnVolume);
            if (_burn.isPlaying)
                return;
            _burn.clip = _burnLoop;
            _burn.Play();
        }

        private void StopBurn()
        {
            FadeOutAndStop(_burn, ref _burnFade, _burnVolume);
        }

        private void CancelFade(ref Coroutine handle, AudioSource source, float restoreVolume)
        {
            if (handle != null)
            {
                StopCoroutine(handle);
                handle = null;
            }
            if (source != null) source.volume = restoreVolume;
        }

        private void FadeOutAndStop(AudioSource source, ref Coroutine handle, float restoreVolume)
        {
            if (source == null)
                return;
            if (handle != null)
            {
                StopCoroutine(handle);
                handle = null;
            }
            if (!source.isPlaying || !isActiveAndEnabled)
            {
                source.Stop();
                source.volume = restoreVolume;
                return;
            }
            handle = StartCoroutine(FadeOutRoutine(source, restoreVolume));
        }

        private IEnumerator FadeOutRoutine(AudioSource source, float restoreVolume)
        {
            float start = source.volume;
            float elapsed = 0f;
            while (elapsed < LoopFadeSeconds && source != null)
            {
                elapsed += Time.unscaledDeltaTime;
                source.volume = Mathf.Lerp(start, 0f, Mathf.Clamp01(elapsed / LoopFadeSeconds));
                yield return null;
            }
            if (source == null)
                yield break;
            source.Stop();
            source.volume = restoreVolume;
        }

        private IEnumerator LoadAll()
        {
            yield return LoadClip("idle_loop.wav", value => _idleLoop = value);
            yield return LoadClip("burn_start.wav", value => _burnStart = value);
            yield return LoadClip("burn_loop.wav", value => _burnLoop = value);
            yield return LoadClip("bake_complete.ogg", value => _complete = value);
            yield return LoadClip("button_start.ogg", value => _buttonStart = value);
            yield return LoadClip("button_stop.ogg", value => _buttonStop = value);
            yield return LoadClip("door_open.ogg", value => _doorOpenClip = value);
            yield return LoadClip("door_close.wav", value => _doorClose = value);
            yield return LoadClip("item_place_01.ogg", value => _itemPlace1 = value);
            yield return LoadClip("item_place_02.wav", value => _itemPlace2 = value);
            yield return LoadClip("ember_01.wav", value => _ember1 = value);
            yield return LoadClip("ember_02.wav", value => _ember2 = value);

            if (!_hasState)
                yield break;
            if (_state == WasteDisposalIncineratorController.IncineratorState.Burning)
                PlayBurnLoopNow();
            else if (_state == WasteDisposalIncineratorController.IncineratorState.Loading
                     || _state == WasteDisposalIncineratorController.IncineratorState.Paused
                     || _state == WasteDisposalIncineratorController.IncineratorState.Malfunctioned)
                PlayIdle();
        }

        private IEnumerator LoadClip(string fileName, Action<AudioClip> assign)
        {
            AudioClip bundled = CompressedContractAudio.Load("contracts/" + AudioFolder + "/" + fileName);
            if (bundled != null)
            {
                assign?.Invoke(bundled);
                yield break;
            }

            string path = ResolveAudioPath(fileName);
            if (string.IsNullOrEmpty(path))
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.WasteDisposal] Audio file missing: {fileName}");
                yield break;
            }

            using (UnityWebRequest request = UnityWebRequestMultimedia.GetAudioClip(new Uri(path).AbsoluteUri, GetAudioType(path)))
            {
                yield return request.SendWebRequest();
                if (request.result != UnityWebRequest.Result.Success)
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.WasteDisposal] Failed loading '{fileName}': {request.error}");
                    yield break;
                }

                AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null)
                    yield break;
                clip.name = "LGU_Incinerator_" + Path.GetFileNameWithoutExtension(fileName);
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
