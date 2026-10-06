using System.Collections;

using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Facility.Shared;

namespace Y4NGZCompany.Contracts.BlackoutAudit
{
    /// <summary>
    /// Purely cosmetic, purely local: smoke, intermittent sparks, and a strained
    /// long-range hum on a breaker box whose zone has not been restored yet, so
    /// players can tell broken breakers from working ones and home in on them by
    /// ear. Driven from ApplyBlackoutZonePowerState off zone.Restored, which the
    /// host snapshot keeps consistent on every machine — no netcode here.
    /// </summary>
    internal sealed class BlackoutBreakerDistressEffect : MonoBehaviour
    {
        // #775: 45 m / 32 m carried the drone across most of a wing, and because the drone
        // is the vanilla hum clip slowed down, the healthy hum that takes over on restore
        // read as the same noise continuing. Two corridors is enough to home in on it.
        private const float LoopMaxDistance = 24f;
        private const float CrackleMaxDistance = 18f;

        private const string RootObjectName = "BlackoutBreakerDistress";

        /// <summary>#775: the spark cone fires out of the panel face, pitched up so the
        /// streaks arc away from the box rather than sliding along the wall.</summary>
        private const float SparkConePitchDegrees = 35f;

        /// <summary>#716 (WP-E E13). The drone used to cut dead on restore, which read as a glitch
        /// rather than as a repair. Short enough that the breaker still feels fixed on the pull.</summary>
        private const float LoopFadeOutSeconds = 0.2f;

        private static Material _sparkMaterial;

        private const float FlashPeakIntensity = 3.4f;
        private const float FlashDecayPerSecond = 26f;

        private ParticleSystem _smoke;
        private ParticleSystem _sparks;
        private Light _flash;
        private AudioSource _loop;
        private AudioSource _crackle;
        private AudioClip _crackleClip;
        private BreakerBox _breaker;
        private bool _distressed;
        private float _nextBurstTime;
        private Coroutine _loopFade;

        /// <summary>
        /// <paramref name="anchor"/> is the world point the smoke and sparks leave from
        /// and <paramref name="faceForward"/> the horizontal direction the panel faces.
        /// The caller resolves both from the renderer bounds and the mounted wall (#775);
        /// the old placement used the prefab pivot plus 0.3 m, which on the Z-up vanilla
        /// mesh put the burst above and beside the box rather than on it, and inherited
        /// the prefab rotation so the cone fired straight up.
        /// </summary>
        internal static BlackoutBreakerDistressEffect Attach(BreakerBox breaker, Vector3 anchor, Vector3 faceForward)
        {
            if (breaker == null || breaker.transform == null)
                return null;

            // A zone rebuild (client snapshot, contract re-entry) drops the zone object that
            // held the old effect. Without this the orphan stays distressed forever and its
            // drone never stops, no matter what the breaker does.
            DestroyOrphans(breaker.transform);

            var root = new GameObject(RootObjectName);
            root.transform.SetParent(breaker.transform, worldPositionStays: false);
            root.transform.position = anchor;
            // World-space orientation: +Z out of the panel, +Y world up, so the smoke
            // (which rises along local +Y) and the cone (local +Z) both ignore the
            // breaker mesh's Z-up authoring.
            Vector3 forward = faceForward;
            forward.y = 0f;
            root.transform.rotation = forward.sqrMagnitude > 1e-4f
                ? Quaternion.LookRotation(forward.normalized, Vector3.up)
                : Quaternion.identity;

            var effect = root.AddComponent<BlackoutBreakerDistressEffect>();
            effect.Build(breaker);
            return effect;
        }

        private static void DestroyOrphans(Transform breakerTransform)
        {
            for (int i = breakerTransform.childCount - 1; i >= 0; i--)
            {
                Transform child = breakerTransform.GetChild(i);
                if (child == null || child.name != RootObjectName)
                    continue;

                var stale = child.GetComponent<BlackoutBreakerDistressEffect>();
                if (stale != null && stale._loop != null)
                    stale._loop.Stop();
                Destroy(child.gameObject);
            }
        }

        internal void SetDistressed(bool distressed)
        {
            if (_distressed == distressed)
                return;

            _distressed = distressed;
            if (distressed)
            {
                if (_smoke != null)
                    _smoke.Play();
                if (_sparks != null)
                    _sparks.Play();
                // The vanilla hum at normal pitch would mask the distress drone —
                // silence it while broken; restore hands it back below.
                if (_breaker != null && _breaker.breakerBoxHum != null && _breaker.breakerBoxHum.isPlaying)
                    _breaker.breakerBoxHum.Stop();
                CancelLoopFade();
                if (_loop != null && _loop.clip != null && !_loop.isPlaying)
                    _loop.Play();
                _nextBurstTime = Time.time + Random.Range(0.4f, 1.5f);
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts] Blackout breaker distress started at {transform.position:F1} (drone={(_loop != null ? "on" : "no clip")}, range={LoopMaxDistance:0}m).");
            }
            else
            {
                // StopEmitting lets the last puffs dissipate instead of popping out.
                if (_smoke != null)
                    _smoke.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);
                if (_sparks != null)
                    _sparks.Stop(withChildren: true, ParticleSystemStopBehavior.StopEmitting);
                if (_flash != null)
                {
                    _flash.intensity = 0f;
                    _flash.enabled = false;
                }
                // The particles already dissipate rather than pop; the drone now does the same.
                FadeOutLoop();
                if (_breaker != null && _breaker.breakerBoxHum != null
                    && (_breaker.isPowerOn || _breaker.leversSwitchedOff <= 0)
                    && !_breaker.breakerBoxHum.isPlaying)
                {
                    _breaker.breakerBoxHum.Play();
                }
            }
        }

        private void CancelLoopFade()
        {
            if (_loopFade != null)
            {
                StopCoroutine(_loopFade);
                _loopFade = null;
            }

            if (_loop != null)
                _loop.volume = 1f;
        }

        /// <summary>
        /// #716 (WP-E E13). Ramps the distress drone out over
        /// <see cref="LoopFadeOutSeconds"/> and then stops it, restoring the volume so the next
        /// distress cycle starts at full. Falls back to a hard stop when the effect is not in a
        /// state that can run a coroutine (disabled, or being torn down with the zone).
        /// </summary>
        private void FadeOutLoop()
        {
            if (_loop == null)
                return;

            if (_loopFade != null)
            {
                StopCoroutine(_loopFade);
                _loopFade = null;
            }

            if (!_loop.isPlaying || !isActiveAndEnabled)
            {
                _loop.Stop();
                _loop.volume = 1f;
                return;
            }

            _loopFade = StartCoroutine(FadeOutLoopRoutine());
        }

        private IEnumerator FadeOutLoopRoutine()
        {
            float from = _loop.volume;
            float elapsed = 0f;
            while (elapsed < LoopFadeOutSeconds && _loop != null)
            {
                elapsed += Time.unscaledDeltaTime;
                _loop.volume = Mathf.Lerp(from, 0f, Mathf.Clamp01(elapsed / LoopFadeOutSeconds));
                yield return null;
            }

            if (_loop != null)
            {
                _loop.Stop();
                _loop.volume = 1f;
            }

            _loopFade = null;
            ContractsBootstrap.Log?.LogDebug(
                $"[MoonContracts] Blackout breaker distress drone stopped at {transform.position:F1}; vanilla hum {(_breaker != null && _breaker.breakerBoxHum != null && _breaker.breakerBoxHum.isPlaying ? "playing" : "silent")}.");
        }

        private void Update()
        {
            if (!_distressed)
                return;

            // A slow pitch wobble keeps the drone reading as "strained", clearly
            // apart from the steady healthy hum.
            if (_loop != null && _loop.isPlaying)
                _loop.pitch = 0.55f + Mathf.Sin(Time.time * 1.7f) * 0.045f;

            // Flash decay runs every frame while lit; the light is switched off entirely
            // once it has faded so an idle breaker costs no light at all.
            if (_flash != null && _flash.enabled)
            {
                float intensity = _flash.intensity - FlashDecayPerSecond * Time.deltaTime;
                if (intensity <= 0.02f)
                {
                    _flash.intensity = 0f;
                    _flash.enabled = false;
                }
                else
                {
                    _flash.intensity = intensity;
                }
            }

            if (_sparks == null)
                return;

            float now = Time.time;
            if (now < _nextBurstTime)
                return;

            _nextBurstTime = now + Random.Range(0.5f, 2.4f);
            _sparks.Emit(Random.Range(18, 40));
            if (_flash != null)
            {
                _flash.intensity = FlashPeakIntensity * Random.Range(0.8f, 1.15f);
                _flash.enabled = true;
            }
            if (_crackle != null && _crackleClip != null)
            {
                _crackle.pitch = Random.Range(1.25f, 1.6f);
                _crackle.PlayOneShot(_crackleClip, Random.Range(0.35f, 0.5f));
            }
        }

        private void Build(BreakerBox breaker)
        {
            _breaker = breaker;
            BuildSmoke();
            BuildSparks();
            BuildAudio(breaker);
        }

        private void BuildSmoke()
        {
            _smoke = FacilitySmokeEffectBuilder.BuildSmoke(transform, "Smoke");
        }

        private void BuildSparks()
        {
            var sparksObject = new GameObject("Sparks");
            sparksObject.transform.SetParent(transform, false);
            // Root +Z points out of the panel; pitch the cone up so the burst leaves the face.
            sparksObject.transform.localRotation = Quaternion.Euler(-SparkConePitchDegrees, 0f, 0f);
            _sparks = sparksObject.AddComponent<ParticleSystem>();

            // #726: the previous values (1.8-5 cm, 6-17 per burst, no growth curve, 7% screen
            // clamp) were invisible past arm's length. Sparks are now finger-length streaks
            // that arc and fall, thrown in bursts big enough to read across a room.
            var main = _sparks.main;
            main.playOnAwake = false;
            main.loop = true;
            main.startLifetime = new ParticleSystem.MinMaxCurve(0.14f, 0.42f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(1.4f, 3.6f);
            main.startSize = new ParticleSystem.MinMaxCurve(0.045f, 0.12f);
            main.startColor = new ParticleSystem.MinMaxGradient(
                new Color(1f, 0.98f, 0.72f, 1f),
                new Color(1f, 0.54f, 0.08f, 1f));
            main.gravityModifier = new ParticleSystem.MinMaxCurve(0.9f);
            main.maxParticles = 320;
            main.simulationSpace = ParticleSystemSimulationSpace.World;

            var emission = _sparks.emission;
            emission.rateOverTime = 0f;

            var shape = _sparks.shape;
            shape.shapeType = ParticleSystemShapeType.Cone;
            shape.angle = 42f;
            shape.radius = 0.05f;

            // Bright and full-size on the strike, shrinking to a tail as it cools.
            var sizeOverLifetime = _sparks.sizeOverLifetime;
            sizeOverLifetime.enabled = true;
            sizeOverLifetime.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 1f, 1f, 0.15f));

            var colorOverLifetime = _sparks.colorOverLifetime;
            colorOverLifetime.enabled = true;
            var gradient = new Gradient();
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(new Color(1f, 0.6f, 0.15f), 0.35f), new GradientColorKey(new Color(0.9f, 0.25f, 0.05f), 1f) },
                new[] { new GradientAlphaKey(1f, 0f), new GradientAlphaKey(1f, 0.6f), new GradientAlphaKey(0f, 1f) });
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(gradient);

            var renderer = sparksObject.GetComponent<ParticleSystemRenderer>();
            // Stretched along velocity so each spark is a streak rather than a dot.
            renderer.renderMode = ParticleSystemRenderMode.Stretch;
            renderer.lengthScale = 2.6f;
            renderer.velocityScale = 0.08f;
            renderer.minParticleSize = 0.006f;
            renderer.maxParticleSize = 0.3f;
            renderer.sharedMaterial = GetOrCreateSparkMaterial();
            _sparks.Play();

            // One point light per breaker, flashed on each burst and decayed in Update. The
            // incinerator's burn light runs at 2.35 in this project's HDRP setup, so the
            // flash peaks a little above that and is gone in a tenth of a second.
            var flashObject = new GameObject("SparkFlash");
            flashObject.transform.SetParent(transform, false);
            flashObject.transform.localPosition = Vector3.up * 0.15f;
            _flash = flashObject.AddComponent<Light>();
            _flash.type = LightType.Point;
            _flash.color = new Color(1f, 0.78f, 0.4f, 1f);
            _flash.range = 5.5f;
            _flash.intensity = 0f;
            _flash.shadows = LightShadows.None;
            _flash.enabled = false;
        }

        private void BuildAudio(BreakerBox breaker)
        {
            AudioClip humClip = breaker != null && breaker.breakerBoxHum != null
                ? breaker.breakerBoxHum.clip
                : null;
            _crackleClip = breaker != null ? breaker.switchPowerSFX : null;

            // Route through the same mixer group as the breaker's own audio so the
            // distress sounds sit in the game's diegetic mix (and respect the
            // player's volume settings) instead of playing on the raw master bus.
            UnityEngine.Audio.AudioMixerGroup mixerGroup = null;
            if (breaker != null)
            {
                if (breaker.breakerBoxHum != null)
                    mixerGroup = breaker.breakerBoxHum.outputAudioMixerGroup;
                if (mixerGroup == null && breaker.thisAudioSource != null)
                    mixerGroup = breaker.thisAudioSource.outputAudioMixerGroup;
            }

            if (humClip != null)
            {
                // The healthy hum, pitched down into a strained drone and pushed far
                // past the vanilla hum's range so a broken breaker is audible from
                // corridors away and clearly distinct from a working one.
                var loopObject = new GameObject("DistressLoop");
                loopObject.transform.SetParent(transform, false);
                _loop = loopObject.AddComponent<AudioSource>();
                _loop.clip = humClip;
                _loop.outputAudioMixerGroup = mixerGroup;
                _loop.loop = true;
                _loop.playOnAwake = false;
                _loop.volume = 1f;
                _loop.pitch = 0.55f;
                _loop.spatialBlend = 1f;
                _loop.rolloffMode = AudioRolloffMode.Linear;
                _loop.minDistance = 2f;
                _loop.maxDistance = LoopMaxDistance;
                _loop.dopplerLevel = 0f;
            }

            if (_crackleClip != null)
            {
                var crackleObject = new GameObject("DistressCrackle");
                crackleObject.transform.SetParent(transform, false);
                _crackle = crackleObject.AddComponent<AudioSource>();
                _crackle.outputAudioMixerGroup = mixerGroup;
                _crackle.playOnAwake = false;
                _crackle.spatialBlend = 1f;
                _crackle.rolloffMode = AudioRolloffMode.Linear;
                _crackle.minDistance = 1f;
                _crackle.maxDistance = CrackleMaxDistance;
                _crackle.dopplerLevel = 0f;
            }
        }

        private static Material GetOrCreateSparkMaterial()
        {
            if (_sparkMaterial != null)
                return _sparkMaterial;

            _sparkMaterial = FacilitySmokeEffectBuilder.CreateParticleMaterial(
                "Y4NGZ_BlackoutBreakerSpark",
                new Color(1f, 0.74f, 0.22f, 1f),
                additive: true);
            return _sparkMaterial;
        }
    }
}
