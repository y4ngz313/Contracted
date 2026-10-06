using System;
using System.Diagnostics;
using System.Text;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Bundy.AI;

internal enum BundyRuntimeStep
{
    Update = 0,
    DoAIInterval = 1,
    EnsureAgentOnNavMesh = 2,
    RecoverMovement = 3,
    Perception = 4,
    MovementDestination = 5,
    BaseAIInterval = 6,
    // #300: added to split the Tier-1 RecoverMovement plateau between its two candidates, and to
    // attribute the constant Tier-3 drip that lives in the untimed tail of Update.
    HuntDistance = 7,
    StuckRecovery = 8,
    UpdateTail = 9,
}

/// <summary>
/// Narrow, config-gated runtime timings for Bundy. The hot path uses fixed arrays and
/// Stopwatch ticks only; strings are created once per report window.
/// </summary>
internal static class BundyRuntimePerformanceDiagnostics
{
    private static readonly string[] Labels =
    {
        "Update",
        "DoAIInterval",
        "EnsureAgentOnNavMesh",
        "RecoverMovement",
        "Perception",
        "MovementDestination",
        "BaseAIInterval",
        "HuntDistance",
        "StuckRecovery",
        "UpdateTail",
    };

    private static readonly long[] Calls = new long[Labels.Length];
    private static readonly long[] TotalTicks = new long[Labels.Length];
    private static readonly long[] MaxTicks = new long[Labels.Length];
    private static readonly long[] SlowCalls = new long[Labels.Length];
    private static float _windowStartedAt;
    private static float _nextReportAt;

    internal static long Begin()
    {
        return Enabled ? Stopwatch.GetTimestamp() : 0L;
    }

    internal static void End(BundyRuntimeStep step, long started)
    {
        if (started == 0L)
            return;

        long elapsedTicks = Math.Max(0L, Stopwatch.GetTimestamp() - started);
        int index = (int)step;
        Calls[index]++;
        TotalTicks[index] += elapsedTicks;
        if (elapsedTicks > MaxTicks[index])
            MaxTicks[index] = elapsedTicks;

        double elapsedMilliseconds = TicksToMilliseconds(elapsedTicks);
        if (elapsedMilliseconds >= SlowCallMilliseconds)
            SlowCalls[index]++;

        if (step == BundyRuntimeStep.Update || step == BundyRuntimeStep.DoAIInterval)
            MaybeReport();
    }

    private static bool Enabled =>
        BundyPlugin.ModConfig?.RuntimePerformanceTiming?.Value == true;

    private static float ReportSeconds => Mathf.Clamp(
        BundyPlugin.ModConfig?.RuntimePerformanceReportSeconds?.Value ?? 10f,
        1f,
        120f);

    private static double SlowCallMilliseconds => Mathf.Clamp(
        BundyPlugin.ModConfig?.RuntimePerformanceSlowCallMilliseconds?.Value ?? 2f,
        0.25f,
        50f);

    private static void MaybeReport()
    {
        float now = Time.realtimeSinceStartup;
        if (_nextReportAt <= 0f)
        {
            _windowStartedAt = now;
            _nextReportAt = now + ReportSeconds;
            return;
        }

        if (now < _nextReportAt)
            return;

        var report = new StringBuilder(640);
        report.Append("[BundyPerf] windowSeconds=")
            .Append(Math.Max(0f, now - _windowStartedAt).ToString("0.##"))
            .Append(" slowThresholdMs=")
            .Append(SlowCallMilliseconds.ToString("0.##"));

        for (int i = 0; i < Labels.Length; i++)
        {
            long calls = Calls[i];
            if (calls <= 0L)
                continue;

            report.Append(" | ")
                .Append(Labels[i])
                .Append(" calls=").Append(calls)
                .Append(" avgMs=").Append((TicksToMilliseconds(TotalTicks[i]) / calls).ToString("0.###"))
                .Append(" maxMs=").Append(TicksToMilliseconds(MaxTicks[i]).ToString("0.###"))
                .Append(" slow=").Append(SlowCalls[i]);
        }

        BundyPlugin.Log?.LogWarning(report.ToString());
        Array.Clear(Calls, 0, Calls.Length);
        Array.Clear(TotalTicks, 0, TotalTicks.Length);
        Array.Clear(MaxTicks, 0, MaxTicks.Length);
        Array.Clear(SlowCalls, 0, SlowCalls.Length);
        _windowStartedAt = now;
        _nextReportAt = now + ReportSeconds;
    }

    private static double TicksToMilliseconds(long ticks)
    {
        return ticks * 1000d / Stopwatch.Frequency;
    }
}
