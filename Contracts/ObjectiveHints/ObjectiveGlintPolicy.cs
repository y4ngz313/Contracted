using System.Collections.Generic;

namespace Y4NGZCompany.Contracts.ObjectiveHints
{
    /// <summary>Local, per-round first-sight latch. Suppressed first sightings never replay later.</summary>
    internal sealed class ObjectiveGlintPolicy
    {
        internal const float DurationSeconds = 1.2f;
        internal const float RampInSeconds = 0.15f;
        internal const float RampOutSeconds = 0.6f;

        private readonly HashSet<int> _seenTargets = new HashSet<int>();

        internal static bool Enabled(bool masterEnabled, bool glintEnabled)
        {
            return masterEnabled && glintEnabled;
        }

        internal bool TryConsume(int targetId, bool enabled, bool pityActive, bool outlined)
        {
            if (!_seenTargets.Add(targetId))
                return false;

            return enabled && !pityActive && !outlined;
        }

        internal void Reset()
        {
            _seenTargets.Clear();
        }

        internal static float Envelope(float elapsedSeconds)
        {
            if (elapsedSeconds < 0f || elapsedSeconds >= DurationSeconds)
                return 0f;
            if (elapsedSeconds < RampInSeconds)
                return elapsedSeconds / RampInSeconds;

            float fadeStartsAt = DurationSeconds - RampOutSeconds;
            return elapsedSeconds <= fadeStartsAt
                ? 1f
                : (DurationSeconds - elapsedSeconds) / RampOutSeconds;
        }
    }
}
