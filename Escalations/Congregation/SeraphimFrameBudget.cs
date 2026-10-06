using System;

namespace Y4NGZCompany.Escalations.Congregation
{
    /// <summary>#1336 S3: the throttles and switches that keep a living Seraphim cheap every frame.
    /// Unity-free so the CongregantAnimationRegression suite runs the same rules the enemy uses.</summary>
    internal static class SeraphimFrameBudget
    {
        // The aura rings turn at 7.5-16.5 deg/s, so a 20 Hz rotation reads as continuous motion.
        internal const float AuraUpdateIntervalSeconds = 0.05f;
        // Beyond this the 0.04 m-wide rings are below a pixel; the 10 m gap keeps a camera at the
        // boundary from flicking them on and off.
        internal const float AuraHideDistance = 120f;
        internal const float AuraShowDistance = 110f;
        internal const float LightPulseIntervalSeconds = 0.05f;
        // Room for the animated rings and feathers around the authored bind-pose box.
        internal const float RendererBoundsMargin = 2f;
        // An authored half-extent below this is a broken box, not a thin one.
        internal const float MinUsableBoundsExtent = 0.01f;
        internal const double SamplerWindowSeconds = 10.0;
        // A despawn flushes the last partial window only when it is long enough to average.
        internal const double FinalFlushMinimumSeconds = 1.0;

        internal static bool IsTickDue(float now, float lastTick, float interval)
        {
            return now - lastTick >= interval;
        }

        /// <summary>Hidden only beyond <see cref="AuraHideDistance"/>, shown again only inside
        /// <see cref="AuraShowDistance"/>; between the two the current state holds.</summary>
        internal static bool ResolveAuraVisible(float sqrDistance, bool currentlyVisible)
        {
            return currentlyVisible
                ? sqrDistance <= AuraHideDistance * AuraHideDistance
                : sqrDistance < AuraShowDistance * AuraShowDistance;
        }

        internal static float ExpandExtent(float extent)
        {
            return extent * RendererBoundsMargin;
        }

        internal static bool IsBoundsExtentUsable(float x, float y, float z)
        {
            return IsExtentUsable(x) && IsExtentUsable(y) && IsExtentUsable(z);
        }

        /// <summary>The skinned renderers recompute their bounds every frame during the rise (it
        /// starts underground and the Root correction moves the pose); afterwards the authored box
        /// is enough, once.</summary>
        internal static bool ShouldTightenRendererBounds(bool rising, bool rootCorrectionActive, bool alreadyTightened)
        {
            return !rising && !rootCorrectionActive && !alreadyTightened;
        }

        /// <summary>A living Seraphim flies by transform and never navigates, so vanilla's
        /// per-frame agent enable is skipped; disables and a dead body keep vanilla behaviour.</summary>
        internal static bool ShouldSuppressAgentEnable(bool enable, bool isEnemyDead)
        {
            return enable && !isEnemyDead;
        }

        private static bool IsExtentUsable(float extent)
        {
            // NaN fails the comparison.
            return extent >= MinUsableBoundsExtent && !float.IsInfinity(extent);
        }
    }

    /// <summary>#1336 S3: one Seraphim's Update/LateUpdate cost over a 10 s window, written as a
    /// single [MoonContracts.Timing] line so a playtest log shows where the frame went.</summary>
    internal sealed class SeraphimFrameCostWindow
    {
        private readonly double _ticksPerSecond;
        private bool _started;
        private double _windowStart;
        private double _lastFrameTime;
        private int _frames;
        private long _totalTicks;
        private long _maxTicks;
        private long _baseTicks;
        private long _visualTicks;
        private long _serverTicks;
        private long _lateTicks;
        private int _auraUpdates;
        private long _auraSegments;
        private int _auraCulledFrames;
        private int _agentToggles;

        internal SeraphimFrameCostWindow(long ticksPerSecond)
        {
            _ticksPerSecond = ticksPerSecond > 0 ? ticksPerSecond : 1;
        }

        /// <summary>Seconds from the window's start to the last frame <see cref="IsDue"/> saw.</summary>
        internal double ElapsedSeconds => _started ? _lastFrameTime - _windowStart : 0.0;

        internal void AddFrame(long totalTicks, long baseTicks, long visualTicks, long serverTicks)
        {
            _frames++;
            _totalTicks += totalTicks;
            if (totalTicks > _maxTicks)
                _maxTicks = totalTicks;
            _baseTicks += baseTicks;
            _visualTicks += visualTicks;
            _serverTicks += serverTicks;
        }

        internal void AddLateUpdate(long ticks)
        {
            _lateTicks += ticks;
        }

        internal void AddAuraUpdate(int segments)
        {
            _auraUpdates++;
            _auraSegments += segments;
        }

        internal void AddAuraCulledFrame()
        {
            _auraCulledFrames++;
        }

        internal void AddAgentToggle()
        {
            _agentToggles++;
        }

        /// <summary>Called once per frame; the first call starts the window.</summary>
        internal bool IsDue(double nowSeconds)
        {
            if (!_started)
            {
                _started = true;
                _windowStart = nowSeconds;
            }

            _lastFrameTime = nowSeconds;
            return nowSeconds - _windowStart >= SeraphimFrameBudget.SamplerWindowSeconds;
        }

        internal bool IsFinalFlushDue()
        {
            return ElapsedSeconds >= SeraphimFrameBudget.FinalFlushMinimumSeconds;
        }

        /// <summary>Formats the window and starts the next one at the last frame's time.</summary>
        internal string BuildReport(string role, int lights, int enabledLights, int shadowedLights)
        {
            double frames = Math.Max(1, _frames);
            string report = string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                "[MoonContracts.Timing] Seraphim frame cost {0:0.0}s {1}f role={2} update avg {3:0.000}ms max {4:0.000}ms " +
                "(base {5:0.000} visuals {6:0.000} server {7:0.000} late {8:0.000}) lights {9} enabled {10} shadowed {11} " +
                "aura updates {12} segments {13} culledFrames {14} agentToggles {15}",
                ElapsedSeconds,
                _frames,
                role,
                ToMilliseconds(_totalTicks) / frames,
                ToMilliseconds(_maxTicks),
                ToMilliseconds(_baseTicks) / frames,
                ToMilliseconds(_visualTicks) / frames,
                ToMilliseconds(_serverTicks) / frames,
                ToMilliseconds(_lateTicks) / frames,
                lights,
                enabledLights,
                shadowedLights,
                _auraUpdates,
                _auraSegments,
                _auraCulledFrames,
                _agentToggles);
            Reset(_lastFrameTime);
            return report;
        }

        internal void Reset(double nowSeconds)
        {
            _started = true;
            _windowStart = nowSeconds;
            _lastFrameTime = nowSeconds;
            _frames = 0;
            _totalTicks = 0;
            _maxTicks = 0;
            _baseTicks = 0;
            _visualTicks = 0;
            _serverTicks = 0;
            _lateTicks = 0;
            _auraUpdates = 0;
            _auraSegments = 0;
            _auraCulledFrames = 0;
            _agentToggles = 0;
        }

        private double ToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / _ticksPerSecond;
        }
    }
}
