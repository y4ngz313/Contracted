using System;
using System.Collections.Generic;
using System.Text;

namespace Y4NGZCompany.Experience.Diagnostics
{
    /// <summary>
    /// Unity-free aggregation and ranking core for the Path LOS caller capture probe.
    /// Collects per-call timing data keyed by (enemy type, calling path) and builds
    /// the final ranked report.
    /// </summary>
    internal sealed class PathLosCallerCaptureCore
    {
        private readonly Dictionary<string, EnemyState> _enemyStates = new Dictionary<string, EnemyState>();
        private readonly Dictionary<(string EnemyType, string Path), Entry> _entries = new Dictionary<(string EnemyType, string Path), Entry>();

        private int _sampledCalls;

        /// <summary>
        /// Reference type on purpose: the accumulators are mutated in place after a
        /// dictionary lookup, which a struct copy would silently discard.
        /// </summary>
        private sealed class Entry
        {
            public long TotalTicks;
            public int TotalCalls;
            public int SampledCalls;
        }

        private sealed class EnemyState
        {
            public int CallCount;
            public string LastSampledPath;
            public int SampledCount;
        }

        /// <summary>
        /// Called before a call is recorded. Returns the path to use (null if a stack walk
        /// is needed) and whether this call is being sampled.
        /// </summary>
        internal (string FallbackPath, bool ShouldSample) BeginCall(string enemyType)
        {
            if (!_enemyStates.TryGetValue(enemyType, out var state))
            {
                state = new EnemyState();
                _enemyStates[enemyType] = state;
            }

            state.CallCount++;

            // First 32 calls per enemy type are sampled, then every 64th after that.
            bool shouldSample = state.CallCount <= 32 || (state.CallCount - 32) % 64 == 0;

            if (shouldSample)
            {
                _sampledCalls++;
                state.SampledCount++;
                return (null, true);
            }

            return (state.LastSampledPath, false);
        }

        /// <summary>
        /// Records a completed call with timing and path.
        /// </summary>
        internal void CommitCall(string enemyType, string path, long ticks, bool sampled)
        {
            if (path == null) return;

            var key = (enemyType, path);
            if (!_entries.TryGetValue(key, out var entry))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.TotalTicks += ticks;
            entry.TotalCalls++;
            if (sampled) entry.SampledCalls++;

            if (sampled && _enemyStates.TryGetValue(enemyType, out var state))
            {
                state.LastSampledPath = path;
            }
        }

        internal void Reset()
        {
            _entries.Clear();
            _enemyStates.Clear();
            _sampledCalls = 0;
        }

        /// <summary>
        /// Builds the final ranked report. All parameters are window-level aggregates
        /// provided by the caller (which has access to Unity timing).
        /// </summary>
        internal string BuildReport(
            float windowSeconds,
            int windowFrames,
            long totalTicks,
            int totalCalls)
        {
            var sb = new StringBuilder();

            double totalMs = TicksToMilliseconds(totalTicks);
            double msPerFrame = windowFrames > 0 ? totalMs / windowFrames : 0;
            double sampledShare = totalCalls > 0 ? _sampledCalls * 100.0 / totalCalls : 0;

            sb.Append("[MoonContracts.Timing] Path LOS capture ");
            sb.AppendFormat("{0:F1}s ", windowSeconds);
            sb.AppendFormat("{0}f ", windowFrames);
            sb.AppendFormat("{0}calls ", totalCalls);
            sb.AppendFormat("{0:F1}ms ", totalMs);
            sb.AppendFormat("({0:F3}ms/f) ", msPerFrame);
            sb.AppendFormat("sampled={0:F1}%", sampledShare);

            // Top 12 rows by total ms.
            var rows = new List<ReportRow>();
            foreach (var kv in _entries)
            {
                rows.Add(new ReportRow
                {
                    EnemyType = kv.Key.EnemyType,
                    Path = kv.Key.Path,
                    TotalTicks = kv.Value.TotalTicks,
                    TotalCalls = kv.Value.TotalCalls,
                });
            }
            rows.Sort((a, b) => Math.Sign(b.TotalTicks - a.TotalTicks));

            int shown = Math.Min(rows.Count, 12);
            for (int i = 0; i < shown; i++)
            {
                var r = rows[i];
                double rMs = TicksToMilliseconds(r.TotalTicks);
                double rMsPerFrame = windowFrames > 0 ? rMs / windowFrames : 0;
                sb.AppendLine();
                sb.AppendFormat("  {0} {1}  {2}calls {3:F1}ms ({4:F3}ms/f)",
                    r.EnemyType, r.Path, r.TotalCalls, rMs, rMsPerFrame);
            }

            // One-line summary per enemy type.
            var enemySummaries = new Dictionary<string, EnemySummary>();
            foreach (var kv in _entries)
            {
                if (!enemySummaries.TryGetValue(kv.Key.EnemyType, out var es))
                {
                    es = new EnemySummary();
                    enemySummaries[kv.Key.EnemyType] = es;
                }
                es.PathCount++;
                es.TotalCalls += kv.Value.TotalCalls;
                es.TotalTicks += kv.Value.TotalTicks;
            }

            sb.AppendLine();
            sb.Append("  Per enemy:");
            foreach (var kv in enemySummaries)
            {
                double eMs = TicksToMilliseconds(kv.Value.TotalTicks);
                sb.AppendLine();
                sb.AppendFormat("    {0}: {1} paths, {2} calls, {3:F1}ms",
                    kv.Key, kv.Value.PathCount, kv.Value.TotalCalls, eMs);
            }

            return sb.ToString();
        }

        private static double TicksToMilliseconds(long ticks)
        {
            return ticks * 1000.0 / StopwatchFrequency;
        }

        /// <summary>Set by the Unity-dependent caller before building a report.</summary>
        internal static long StopwatchFrequency = 10_000_000L;

        private struct ReportRow
        {
            public string EnemyType;
            public string Path;
            public long TotalTicks;
            public int TotalCalls;
        }

        private sealed class EnemySummary
        {
            public int PathCount;
            public int TotalCalls;
            public long TotalTicks;
        }
    }
}