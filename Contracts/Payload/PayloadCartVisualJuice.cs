using UnityEngine;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Contracts.Payload
{
    internal sealed class PayloadCartVisualJuice : MonoBehaviour
    {
        private const string VisualChildName = "PayloadCart_Visual";
        private const float SpeedSmooth = 14f;
        private const float MovingSpeedThreshold = 0.08f;
        private const float SpeedJoltThreshold = 1.1f;
        private const float StartRockDegrees = -4.5f;
        private const float StopRockDegrees = 6.5f;
        private const float RockSmoothTime = 0.24f;
        private const float VerticalSmoothTime = 0.12f;
        private const float StepJoltLogThreshold = 0.12f;
        internal const float JoltSfxCooldownSeconds = 0.25f;
        private const float JoltIntensityReference = 0.36f;
        private const float NoisePositionAmplitude = 0.012f;
        private const float NoiseRotationAmplitude = 1.15f;

        private Transform _visual;
        private Vector3 _baseLocalPosition;
        private Quaternion _baseLocalRotation;
        private bool _baseCaptured;
        private Vector3 _lastWorldPosition;
        private bool _lastWorldPositionValid;
        private float _smoothedSpeed;
        private float _lastSmoothedSpeed;
        private readonly PayloadCartGroundSampler.SampleState _groundSampleState = new PayloadCartGroundSampler.SampleState();
        private float _rockPitch;
        private float _rockPitchVelocity;
        private float _verticalOffset;
        private float _verticalVelocity;
        private float _lastFrontBackHeightDelta;
        private bool _stepLogged;
        private bool _joltLogged;
        private float _noiseSeed;
        private float _nextJoltSfxAt;

        private void Awake()
        {
            _noiseSeed = Random.value * 100f;
            RefreshVisualRoot();
        }

        internal void RefreshVisualRoot()
        {
            Transform visual = transform.Find(VisualChildName);
            if (visual == null && transform.childCount > 0)
                visual = transform.GetChild(0);

            if (visual == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload.Juice] Visual child '{VisualChildName}' not found; cart visual juice disabled.");
                _visual = null;
                _baseCaptured = false;
                return;
            }

            if (_visual == visual && _baseCaptured)
                return;

            _visual = visual;
            _baseLocalPosition = _visual.localPosition;
            _baseLocalRotation = _visual.localRotation;
            _baseCaptured = true;
            // The wheel footprint the ground sampler caches is read off this hierarchy (#716 B7).
            PayloadCartGroundSampler.InvalidateFootprintCache();
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload.Juice] Visual root resolved: '{_visual.name}', " +
                $"baseLocalPos={_baseLocalPosition}, baseLocalRot={_baseLocalRotation.eulerAngles}.");
        }

        private void LateUpdate()
        {
            if (_visual == null || !_baseCaptured)
            {
                RefreshVisualRoot();
                return;
            }

            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            float instantSpeed = 0f;
            Vector3 worldPosition = transform.position;
            if (_lastWorldPositionValid)
            {
                Vector3 delta = worldPosition - _lastWorldPosition;
                delta.y = 0f;
                instantSpeed = delta.magnitude / dt;
            }
            _lastWorldPosition = worldPosition;
            _lastWorldPositionValid = true;

            float blend = 1f - Mathf.Exp(-SpeedSmooth * dt);
            _lastSmoothedSpeed = _smoothedSpeed;
            _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, instantSpeed, blend);
            ApplyStopStartRock(_smoothedSpeed - _lastSmoothedSpeed);

            float groundPitch = 0f;
            float groundRoll = 0f;
            float verticalTarget = 0f;
            if (PayloadCartGroundSampler.TrySampleCartGround(transform, _groundSampleState, out PayloadCartGroundSampler.GroundSample sample))
            {
                // The server now tilts and grounds the root collider. Only cosmetic rock/noise belongs here.
                groundPitch = groundRoll = verticalTarget = 0f;
                float heightDelta = sample.FrontY - sample.RearY;
                float stepDelta = Mathf.Abs(heightDelta - _lastFrontBackHeightDelta);
                if (stepDelta >= StepJoltLogThreshold)
                {
                    PlayStepJoltAudio(stepDelta);
                    if (!_stepLogged)
                    {
                        _stepLogged = true;
                        ContractsBootstrap.Log?.LogInfo(
                            $"[MoonContracts.Payload.Juice] Reactive step motion detected: frontY={sample.FrontY:0.###}, " +
                            $"rearY={sample.RearY:0.###}, basePitch={groundPitch:0.#}deg, " +
                            $"baseRoll={groundRoll:0.#}deg, verticalTarget={verticalTarget:0.###}.");
                    }
                }
                _lastFrontBackHeightDelta = heightDelta;
            }

            _rockPitch = Mathf.SmoothDamp(_rockPitch, 0f, ref _rockPitchVelocity, RockSmoothTime, Mathf.Infinity, dt);
            _verticalOffset = Mathf.SmoothDamp(_verticalOffset, verticalTarget, ref _verticalVelocity, VerticalSmoothTime, Mathf.Infinity, dt);

            float speed01 = _smoothedSpeed > MovingSpeedThreshold ? Mathf.Clamp01(_smoothedSpeed / 4f) : 0f;
            float noiseX = Noise(_noiseSeed, Time.time * 13.1f) * NoisePositionAmplitude * speed01;
            float noiseY = Noise(_noiseSeed + 17f, Time.time * 17.7f) * NoisePositionAmplitude * speed01;
            float noisePitch = Noise(_noiseSeed + 29f, Time.time * 15.3f) * NoiseRotationAmplitude * speed01;
            float noiseYaw = Noise(_noiseSeed + 41f, Time.time * 11.9f) * NoiseRotationAmplitude * speed01 * 0.65f;
            float noiseRoll = Noise(_noiseSeed + 53f, Time.time * 19.4f) * NoiseRotationAmplitude * speed01;

            _visual.localPosition = _baseLocalPosition + Vector3.up * (_verticalOffset + noiseY) + Vector3.right * noiseX;
            Quaternion groundBaseRotation = Quaternion.Euler(-groundPitch, 0f, groundRoll);
            Quaternion offsetRotation = Quaternion.Euler(_rockPitch + noisePitch, noiseYaw, noiseRoll);
            _visual.localRotation = _baseLocalRotation * groundBaseRotation * offsetRotation;
        }

        private void PlayStepJoltAudio(float stepDelta)
        {
            if (_smoothedSpeed <= MovingSpeedThreshold || Time.time < _nextJoltSfxAt)
                return;

            _nextJoltSfxAt = Time.time + JoltSfxCooldownSeconds;
            float terrainIntensity = Mathf.Clamp01(stepDelta / JoltIntensityReference);
            float speedIntensity = Mathf.Clamp01(_smoothedSpeed / 4f);
            float intensity = Mathf.Clamp01(Mathf.Max(terrainIntensity, speedIntensity * 0.65f));
            PayloadCartAudio.PlayJoltAt(transform.position, intensity);
        }

        private void ApplyStopStartRock(float speedDelta)
        {
            if (Mathf.Abs(speedDelta) < SpeedJoltThreshold)
                return;

            bool starting = speedDelta > 0f;
            float impulse = starting ? StartRockDegrees : StopRockDegrees;
            _rockPitch = Mathf.Clamp(_rockPitch + impulse, -12f, 12f);
            if (!_joltLogged)
            {
                _joltLogged = true;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Payload.Juice] Stop/start visual rock triggered: speedDelta={speedDelta:0.##}m/s, " +
                    $"impulse={impulse:0.#}deg.");
            }
        }

        private static float Noise(float x, float y)
        {
            return (Mathf.PerlinNoise(x, y) - 0.5f) * 2f;
        }
    }
}
