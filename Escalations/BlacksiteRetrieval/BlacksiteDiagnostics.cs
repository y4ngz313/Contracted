using System;
using System.Collections.Generic;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// Opt-in, state-change-oriented diagnostics used by the Blacksite UAT pass.
    /// The existing ShadowRaidDebugLogging switch owns this output because the
    /// Shadow Raid cache and Blacksite retrieval encounter share the same runtime.
    /// </summary>
    internal static class BlacksiteDiagnostics
    {
        private static readonly Dictionary<string, float> NextAllowedAt =
            new Dictionary<string, float>(StringComparer.Ordinal);

        // #874 G5: per-member detail capture window. Statically initialised with
        // the configured seconds from ContractsBootstrap on plugin startup.
        internal static readonly BlacksiteDetailCapture DetailCapture = new BlacksiteDetailCapture();

        internal static bool Enabled =>
            ContractsBootstrap.ShadowRaidDebugEnabled != null && ContractsBootstrap.ShadowRaidDebugEnabled.Value;

        /// <summary>#1305: drop built [UAT] lines at the sink, except [UAT][Cost].</summary>
        internal static bool OutputMuted =>
            ContractsBootstrap.BlacksiteUatOutputMuted != null && ContractsBootstrap.BlacksiteUatOutputMuted.Value;

        internal static void Reset()
        {
            NextAllowedAt.Clear();
            DetailCapture.Reset();
        }

        /// <summary>
        /// #874 G5: true while a detail capture window is open for this soldier.
        /// Allocation-free: one Dictionary lookup.
        ///
        /// The key is the soldier's NETWORK IDENTITY, never its squad ordinal. Member indices
        /// run 0..3 WITHIN a squad, so two squads on one moon share every index and squad B
        /// member 0's stall would open, extend and cap squad A member 0's window. This is the
        /// same reason BlacksiteNavigator.MotionRequestKey is keyed the way it is; both keys
        /// come from the same place so a member folds against itself under either gate.
        /// </summary>
        internal static bool IsDetailOpen(int diagnosticKey)
        {
            return DetailCapture.IsOpen(diagnosticKey, Time.time);
        }

        /// <summary>
        /// #874 G5: open or extend a detail capture window for this soldier.
        /// Logs [UAT][Capture] open once per cycle; extensions are silent except at the cap.
        /// <paramref name="diagnosticKey"/> is the network identity that keys the window;
        /// <paramref name="memberIndex"/> is only the printed label.
        /// </summary>
        internal static void OpenDetailWindow(int diagnosticKey, int memberIndex, string reason)
        {
            float now = Time.time;
            DetailCapture.Open(diagnosticKey, reason, now, out bool firstOpen, out string capEvent);
            if (firstOpen)
                Log("Capture", $"open member={memberIndex}, id={diagnosticKey}, reason={reason}, seconds={DetailCapture.ConfiguredSeconds:0.0}");
            else if (capEvent != null)
                Log("Capture", $"open member={memberIndex}, id={diagnosticKey}, reason={reason}, seconds={DetailCapture.ConfiguredSeconds:0.0}, cap={capEvent}");
        }

        /// <summary>
        /// #874 G5: call once per frame per soldier to retire an expired window and log its close.
        /// MUST be called BEFORE any OpenDetailWindow in the same pass: a re-open on the very
        /// frame the old window expires would otherwise extend it and swallow the close line.
        /// </summary>
        internal static void CloseExpiredDetailWindow(int diagnosticKey, int memberIndex)
        {
            if (DetailCapture.TryCloseExpired(diagnosticKey, Time.time, out float duration))
                Log("Capture", $"close member={memberIndex}, id={diagnosticKey}, seconds={duration:0.0}");
        }

        internal static void Log(string area, string message)
        {
            if (!Enabled || string.IsNullOrWhiteSpace(message))
                return;

            // #1305: counted before the mute so the cost line's line rate holds in both A/B runs.
            BlacksiteHitchTracer.CountUatLine();
            if (OutputMuted && !string.Equals(area, "Cost", StringComparison.Ordinal))
                return;

            // #874 S4.0a: every UAT line carries the monotonic frame clock. The five 2026-09-09
            // analyses had to reconstruct time from the [UAT][Decision] heartbeat, which is only
            // emitted at 1 Hz per member, so every measured interval in those documents carried a
            // one-second reconstruction error. Time.time is the same clock every Blacksite deadline
            // is written against, so a scored log needs no reconstruction at all. Main thread only:
            // every caller is a MonoBehaviour tick or a server-authoritative mission step.
            using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.UatSink))
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite][UAT][{area ?? "General"}] t={Time.time:0.00} {message}");
        }

        internal static void LogRateLimited(
            string key,
            float minimumIntervalSeconds,
            string area,
            string message)
        {
            if (!string.IsNullOrWhiteSpace(message)
                && AllowRateLimited(string.IsNullOrWhiteSpace(key) ? area : key, minimumIntervalSeconds))
                Log(area, message);
        }

        /// <summary>
        /// #1233 S8: whether a line under <paramref name="key"/> may print now, claiming the next
        /// interval when it may. Ask it BEFORE building an expensive message: the answer is one
        /// dictionary read, and a caller that built the message first paid for every line the
        /// limiter then dropped.
        /// </summary>
        internal static bool AllowRateLimited(string key, float minimumIntervalSeconds)
        {
            if (!Enabled)
                return false;
            string normalizedKey = string.IsNullOrWhiteSpace(key) ? "General" : key;
            float now = Time.realtimeSinceStartup;
            if (NextAllowedAt.TryGetValue(normalizedKey, out float next) && now < next)
                return false;
            NextAllowedAt[normalizedKey] = now + Mathf.Max(0.1f, minimumIntervalSeconds);
            return true;
        }
    }
}
