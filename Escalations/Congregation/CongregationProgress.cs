using System;

namespace Y4NGZCompany.Escalations.Congregation
{
    /// <summary>The ceremony's tunable numbers (#845 D). The defaults are Lawson's 2026-09-26
    /// decisions; <c>ContractsBootstrap</c> holds them as fixed settings and the server builds the
    /// model from those.</summary>
    internal struct CongregationProgressSettings
    {
        internal float RedPrayThreshold;
        internal float AllPrayThreshold;
        internal float BeatProgress;
        internal float PrayLoopProgress;
        internal float AllPresentProgressPerSecond;
        internal float StallRuinSeconds;
        internal float SpeedUpEtaSeconds;
        internal float SpeedUpProgress;

        internal static CongregationProgressSettings Default => new CongregationProgressSettings
        {
            RedPrayThreshold = CongregationProgress.DefaultRedPrayThreshold,
            AllPrayThreshold = CongregationProgress.DefaultAllPrayThreshold,
            BeatProgress = CongregationProgress.DefaultBeatProgress,
            PrayLoopProgress = CongregationProgress.DefaultPrayLoopProgress,
            AllPresentProgressPerSecond = CongregationProgress.DefaultAllPresentProgressPerSecond,
            StallRuinSeconds = CongregationProgress.DefaultStallRuinSeconds,
            SpeedUpEtaSeconds = CongregationProgress.DefaultSpeedUpEtaSeconds,
            SpeedUpProgress = CongregationProgress.DefaultSpeedUpProgress,
        };
    }

    /// <summary>Server-owned ceremony progress, 0-100, with no Unity types so the regression suite
    /// can drive it. Contributions are drum beats, completed PrayLoop cycles and the all-present
    /// bonus; a stare freezes everything; thresholds only advance; nothing decays. The drum clock
    /// helpers turn the synced start/ramp times into the drum loop's content time on every peer.</summary>
    internal sealed class CongregationProgress
    {
        internal const float DefaultRedPrayThreshold = 33f;
        internal const float DefaultAllPrayThreshold = 66f;
        internal const float SummonThreshold = 100f;
        internal const float DefaultBeatProgress = 1f;
        internal const float DefaultPrayLoopProgress = 1f;
        internal const float DefaultAllPresentProgressPerSecond = 0.2f;
        internal const float DefaultStallRuinSeconds = 120f;
        internal const float DefaultSpeedUpEtaSeconds = 30f;
        internal const float DefaultSpeedUpProgress = 85f;
        internal const float DefaultDrumSpeedUpTempo = 1.12f;
        internal const float DefaultDrumSpeedUpRampSeconds = 5f;

        /// <summary>PrayLoop clip length: one completed cycle per praying congregant.</summary>
        internal const float PrayLoopSeconds = 4f;
        /// <summary>One strike every 2.000 s of drum-loop content time; the loop starts on a strike.</summary>
        internal const double DrumBeatSeconds = 2.0;
        /// <summary>The loops enter as the third sacred blow decays.</summary>
        internal const double LoopEntrySeconds = 11.6;
        /// <summary>Length of the blows clip; a late joiner past it does not hear them.</summary>
        internal const double BlowsSeconds = 11.98;
        /// <summary>Window of the exponential moving average behind the ETA.</summary>
        internal const float RateWindowSeconds = 10f;

        // Arrival timeout: longest spawn-to-spot path at travel pace, with slack, clamped.
        internal const float ArrivalTravelSpeed = 1.65f;
        internal const float ArrivalPathSlack = 1.5f;
        internal const float ArrivalExtraSeconds = 20f;
        internal const float MinArrivalTimeoutSeconds = 45f;
        internal const float MaxArrivalTimeoutSeconds = 150f;

        internal const int StageDrumming = 0;
        internal const int StageRedPray = 1;
        internal const int StageAllPray = 2;
        internal const int StageSummon = 3;

        private readonly CongregationProgressSettings _settings;
        private float _prayerSeconds;

        internal CongregationProgress(CongregationProgressSettings settings)
        {
            _settings = settings;
        }

        internal float Progress { get; private set; }
        /// <summary>Thresholds crossed so far: <see cref="StageDrumming"/> .. <see cref="StageSummon"/>.</summary>
        internal int Stage { get; private set; }
        internal float SecondsWithoutProgress { get; private set; }
        /// <summary>Progress per second, exponential moving average over ~<see cref="RateWindowSeconds"/>.</summary>
        internal float Rate { get; private set; }
        /// <summary>Latched once the projected time to Summon is short or progress is high.</summary>
        internal bool SpeedUp { get; private set; }
        internal bool Stalled => Stage < StageSummon && SecondsWithoutProgress >= _settings.StallRuinSeconds;
        /// <summary>The objective strip's number: rounded down, so 100 only shows at Summon.</summary>
        internal int Percent => Math.Max(0, Math.Min(100, (int)Math.Floor(Progress)));

        internal float EtaSeconds => Progress >= SummonThreshold
            ? 0f
            : Rate > 0.0001f ? (SummonThreshold - Progress) / Rate : float.PositiveInfinity;

        /// <summary>Applies one server tick and returns how many thresholds it crossed (the caller
        /// advances one ritual phase per crossing, in order).</summary>
        internal int Advance(float dt, int beatsThisTick, int prayingCount, bool allPresent, bool staring)
        {
            if (dt <= 0f || Stage >= StageSummon)
                return 0;
            // A stare pauses the ceremony outright: no gain, no stall time, the rate held.
            if (staring)
                return 0;

            float gain = Math.Max(0, beatsThisTick) * _settings.BeatProgress;
            _prayerSeconds += dt * Math.Max(0, prayingCount);
            if (_prayerSeconds >= PrayLoopSeconds)
            {
                int loops = (int)(_prayerSeconds / PrayLoopSeconds);
                _prayerSeconds -= loops * PrayLoopSeconds;
                gain += loops * _settings.PrayLoopProgress;
            }
            if (allPresent)
                gain += _settings.AllPresentProgressPerSecond * dt;

            Progress = Math.Min(SummonThreshold, Progress + gain);
            SecondsWithoutProgress = gain > 0f ? 0f : SecondsWithoutProgress + dt;
            float alpha = 1f - (float)Math.Exp(-dt / RateWindowSeconds);
            Rate += (gain / dt - Rate) * alpha;
            if (!SpeedUp && (Progress >= _settings.SpeedUpProgress || EtaSeconds <= _settings.SpeedUpEtaSeconds))
                SpeedUp = true;

            int before = Stage;
            while (Stage < StageSummon && Progress >= Threshold(Stage + 1))
                Stage++;
            return Stage - before;
        }

        internal float Threshold(int stage)
        {
            switch (stage)
            {
                case StageRedPray: return _settings.RedPrayThreshold;
                case StageAllPray: return _settings.AllPrayThreshold;
                case StageSummon: return SummonThreshold;
                default: return 0f;
            }
        }

        /// <summary>Drum pitch at <paramref name="time"/>: 1 until the ramp starts, then linear to
        /// <paramref name="fastTempo"/> over <paramref name="rampSeconds"/>. A negative ramp start
        /// means no speed-up.</summary>
        internal static float DrumTempoAt(double time, double rampStart, float fastTempo, float rampSeconds)
        {
            if (rampStart < 0d || time <= rampStart)
                return 1f;
            if (rampSeconds <= 0f)
                return fastTempo;
            double k = Math.Min(1d, (time - rampStart) / rampSeconds);
            return (float)(1d + (fastTempo - 1d) * k);
        }

        /// <summary>Drum-loop content seconds played between <paramref name="loopStart"/> and
        /// <paramref name="time"/>: the integral of <see cref="DrumTempoAt"/>, so a loop restarted
        /// after the speed-up runs at the current cadence from its first sample.</summary>
        internal static double DrumContentSeconds(double loopStart, double time, double rampStart, float fastTempo, float rampSeconds)
        {
            if (time <= loopStart)
                return 0d;
            return TempoIntegral(time, rampStart, fastTempo, rampSeconds)
                - TempoIntegral(loopStart, rampStart, fastTempo, rampSeconds);
        }

        /// <summary>Strikes due since <paramref name="loopStart"/>: the first lands on the loop's
        /// first sample, then one per <see cref="DrumBeatSeconds"/> of content time.</summary>
        internal static int BeatsDue(double loopStart, double time, double rampStart, float fastTempo, float rampSeconds)
        {
            if (loopStart < 0d || time < loopStart)
                return 0;
            double content = DrumContentSeconds(loopStart, time, rampStart, fastTempo, rampSeconds);
            return (int)Math.Floor(content / DrumBeatSeconds + 1e-6) + 1;
        }

        /// <summary>Per-ceremony arrival timeout from the longest spawn-to-spot path.</summary>
        internal static float ArrivalTimeoutSeconds(float longestPathMetres)
        {
            float seconds = Math.Max(0f, longestPathMetres) / ArrivalTravelSpeed * ArrivalPathSlack + ArrivalExtraSeconds;
            return Math.Max(MinArrivalTimeoutSeconds, Math.Min(MaxArrivalTimeoutSeconds, seconds));
        }

        private static double TempoIntegral(double t, double rampStart, float fastTempo, float rampSeconds)
        {
            if (rampStart < 0d || t <= rampStart)
                return t;
            double extra = fastTempo - 1d;
            double since = t - rampStart;
            if (rampSeconds <= 0f)
                return t + extra * since;
            if (since <= rampSeconds)
                return t + extra * since * since / (2d * rampSeconds);
            return t + extra * (rampSeconds * 0.5d + (since - rampSeconds));
        }
    }
}
