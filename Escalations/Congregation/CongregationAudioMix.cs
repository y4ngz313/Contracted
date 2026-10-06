using System;

namespace Y4NGZCompany.Escalations.Congregation
{
    /// <summary>One ceremony emitter's distance-to-gain curve: linear segments between keys, with no
    /// Unity types so the regression suite can drive it. Unity's custom rolloff reads the curve at
    /// distance / <see cref="MaxDistance"/> and ignores minDistance, so the key times are normalized
    /// and each key carries the slopes of its neighbouring segments.</summary>
    internal sealed class CongregationAudioProfile
    {
        private readonly float[] _meters;
        private readonly float[] _gains;

        internal CongregationAudioProfile(float volume, float maxDistance, float[] meters, float[] gains)
        {
            if (meters == null || gains == null || meters.Length < 2 || meters.Length != gains.Length)
                throw new ArgumentException("A ceremony audio profile needs at least two keys with one gain each.");
            if (meters[0] != 0f || meters[meters.Length - 1] != maxDistance)
                throw new ArgumentException("A ceremony audio profile must run from 0 m to its max distance.");
            for (int i = 0; i < meters.Length; i++)
            {
                if (gains[i] < 0f || gains[i] > 1f)
                    throw new ArgumentException("A ceremony audio gain must lie in [0, 1].");
                if (i > 0 && (meters[i] <= meters[i - 1] || gains[i] > gains[i - 1]))
                    throw new ArgumentException("Ceremony audio keys must be strictly increasing in distance and non-increasing in gain.");
            }

            Volume = volume;
            MaxDistance = maxDistance;
            _meters = (float[])meters.Clone();
            _gains = (float[])gains.Clone();
        }

        internal float Volume { get; }
        internal float MaxDistance { get; }
        internal int KeyCount => _meters.Length;

        internal float KeyTime(int i) => _meters[i] / MaxDistance;
        internal float KeyValue(int i) => _gains[i];
        internal float InTangent(int i) => i == 0 ? 0f : Slope(i - 1);
        internal float OutTangent(int i) => i == _meters.Length - 1 ? 0f : Slope(i);

        /// <summary>Gain at a distance in metres, linear between keys and clamped outside the curve.</summary>
        internal float GainAt(float meters)
        {
            if (meters <= _meters[0])
                return _gains[0];
            int last = _meters.Length - 1;
            if (meters >= _meters[last])
                return _gains[last];
            int segment = 0;
            while (meters > _meters[segment + 1])
                segment++;
            float t = (meters - _meters[segment]) / (_meters[segment + 1] - _meters[segment]);
            return _gains[segment] + (_gains[segment + 1] - _gains[segment]) * t;
        }

        // Normalized slope of the segment from key i to key i + 1.
        private float Slope(int i) => (_gains[i + 1] - _gains[i]) / (KeyTime(i + 1) - KeyTime(i));
    }

    /// <summary>Ceremony audio ranges (#1336, Lawson's D2): the horn carries across the moon, the drum
    /// to about 250 m, the chant to about 100 m. Custom curves replace logarithmic rolloff, which
    /// falls to a whisper a few minDistances out whatever the max distance. The horn's last key is a
    /// floor Unity holds beyond 500 m, so it stays audible anywhere on the moon.</summary>
    internal static class CongregationAudioMix
    {
        internal const float ChantSingerVolume = 0.60f;
        // Measured peak of CongregationTribalLoop. Singers share one sample grid so their levels add;
        // 2.0 x 0.492 = 0.984 FS keeps the summed chant under full scale at any singer count.
        internal const float TribalLoopPeak = 0.492f;
        internal const float ChantChoirCap = 2.0f;

        internal static readonly CongregationAudioProfile Horn = new CongregationAudioProfile(1f, 500f,
            new[] { 0f, 30f, 150f, 300f, 450f, 500f }, new[] { 1f, 1f, 0.70f, 0.40f, 0.20f, 0.15f });
        internal static readonly CongregationAudioProfile Drum = new CongregationAudioProfile(1f, 250f,
            new[] { 0f, 15f, 80f, 180f, 250f }, new[] { 1f, 1f, 0.60f, 0.22f, 0f });
        internal static readonly CongregationAudioProfile Chant = new CongregationAudioProfile(ChantSingerVolume, 100f,
            new[] { 0f, 6f, 30f, 70f, 100f }, new[] { 1f, 1f, 0.50f, 0.15f, 0f });

        /// <summary>Per-singer chant volume with <paramref name="singers"/> chanting on this peer.</summary>
        internal static float ChantVolumeFor(int singers) => Math.Min(ChantSingerVolume, ChantChoirCap / Math.Max(1, singers));
    }
}
