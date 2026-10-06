using System;
using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations._Shared;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 Q11: one open scope. <c>using var hitch = BlacksiteHitchTracer.Scope(...)</c>; a
    /// default handle (tracer off, not a marker scope) does nothing when disposed.
    /// </summary>
    internal readonly struct BlacksiteHitchScopeHandle : IDisposable
    {
        private readonly BlacksiteHitchScope _scope;
        private readonly bool _marked, _traced;

        internal BlacksiteHitchScopeHandle(BlacksiteHitchScope scope, bool marked, bool traced)
        {
            _scope = scope; _marked = marked; _traced = traced;
        }

        public void Dispose()
        {
            if (_traced) BlacksiteHitchTracer.Exit();
            if (_marked) BlacksiteHitchTracer.EndMarker(_scope);
        }
    }

    /// <summary>
    /// #874 Q11 (F11): names the code behind any frame over 100 ms. The 23 September log had a
    /// 1524 ms and a 442 ms frame nobody could attribute, because every existing timer reports only
    /// its own method and only when it breaches its own threshold.
    ///
    /// Server-only, round-only and diagnostics-only. The escalation runtime calls
    /// <see cref="EnsureServer"/> on every frame that passes its own round gate (server, landed,
    /// not in the ship phase, not leaving), and that call is the tracer's heartbeat: a tracer whose
    /// previous frame had no heartbeat retires without judging it
    /// (<see cref="BlacksiteHitchLedger.StillInRound"/>), so nothing is printed in orbit or once the
    /// ship starts to leave even before the round reset's <see cref="Stop"/>. It also retires the
    /// moment logging is switched off. It runs
    /// first in the frame (execution order -32000), so the time between two of its Updates is one
    /// whole frame, render included; the frame just ended is then judged by
    /// <see cref="BlacksiteHitchLedger.CloseFrame"/> and at most one line is printed:
    /// <c>[UAT][Hitch] t=&lt;time&gt; frame=&lt;n&gt; ms=&lt;x&gt; scope=&lt;name&gt; scopeMs=&lt;y&gt;</c> when one
    /// scope spent at least half the frame, otherwise
    /// <c>[UAT][Hitch] t=&lt;time&gt; frame=&lt;n&gt; ms=&lt;x&gt; scope=unattributed largest=&lt;name|none&gt; &lt;y&gt;ms</c>.
    /// #1305: every 10 s of closed frames it also prints one <c>[UAT][Cost]</c> line
    /// (<see cref="BlacksiteHitchLedger.FlushCost"/>) with the live soldiers' census.
    ///
    /// Zero cost when off: a scope that is not also a profiler marker is one static bool read.
    /// The profiler markers (the one-shot landing, touchdown and deployment frames) are always
    /// begun and ended, which costs nothing measurable unless a profiler is recording.
    /// </summary>
    [DefaultExecutionOrder(-32000)]
    internal sealed class BlacksiteHitchTracer : MonoBehaviour
    {
        private static readonly BlacksiteHitchLedger Ledger = new BlacksiteHitchLedger(Stopwatch.Frequency);
        private static readonly ProfilerMarker[] Markers = CreateMarkers();
        private static BlacksiteHitchTracer _instance;
        private static bool _active;
        // #874 Q11: the last frame the round-gated runtime called EnsureServer.
        private static int _heartbeatFrame = -1;
        private long _frameStart;
        // #1305: the live soldiers the cost census walks; refilled at each flush.
        private static readonly List<BlacksiteSoldierAI> CensusSoldiers = new List<BlacksiteSoldierAI>();

        private static ProfilerMarker[] CreateMarkers()
        {
            var markers = new ProfilerMarker[BlacksiteHitchLedger.ScopeCount];
            for (int i = 0; i < markers.Length; i++)
                if (BlacksiteHitchLedger.IsMarker((BlacksiteHitchScope)i))
                    markers[i] = new ProfilerMarker(BlacksiteHitchLedger.Name((BlacksiteHitchScope)i));
            return markers;
        }

        internal static BlacksiteHitchScopeHandle Scope(BlacksiteHitchScope scope)
        {
            bool marked = BlacksiteHitchLedger.IsMarker(scope);
            if (!_active && !marked) return default;
            if (marked) Markers[(int)scope].Begin();
            if (_active) Ledger.Enter(scope, Stopwatch.GetTimestamp());
            return new BlacksiteHitchScopeHandle(scope, marked, _active);
        }

        internal static void Exit() => Ledger.Exit(Stopwatch.GetTimestamp());
        internal static void EndMarker(BlacksiteHitchScope scope) => Markers[(int)scope].End();

        /// <summary>#1305: one physics query in the cost window; a static bool read when off.</summary>
        internal static void CountPhysicsQuery() { if (_active) Ledger.CountPhysicsQuery(); }

        /// <summary>#1305: one built [UAT] line in the cost window; a static bool read when off.</summary>
        internal static void CountUatLine() { if (_active) Ledger.CountUatLine(); }

        /// <summary>Called by the escalation runtime on every server round frame, behind its round
        /// gate. It is both the tracer's start and its heartbeat.</summary>
        internal static void EnsureServer()
        {
            _heartbeatFrame = Time.frameCount;
            if (_instance != null || !BlacksiteDiagnostics.Enabled) return;
            var host = new GameObject("BlacksiteHitchTracer");
            DontDestroyOnLoad(host);
            _instance = host.AddComponent<BlacksiteHitchTracer>();
            Ledger.Reset();
            _active = true;
        }

        /// <summary>Round reset: the next round's first server frame starts a fresh tracer.</summary>
        internal static void Stop()
        {
            if (_instance != null) UnityEngine.Object.Destroy(_instance.gameObject);
            Retire();
        }

        private static void Retire()
        {
            _instance = null;
            _active = false;
            Ledger.Reset();
        }

        private void Update()
        {
            if (!BlacksiteDiagnostics.Enabled || !BlacksiteHitchLedger.StillInRound(_heartbeatFrame, Time.frameCount))
            { UnityEngine.Object.Destroy(gameObject); Retire(); return; }
            long now = Stopwatch.GetTimestamp();
            if (_frameStart != 0)
            {
                double frameMilliseconds = (now - _frameStart) * 1000d / Stopwatch.Frequency;
                string line = Ledger.CloseFrame(Time.frameCount - 1, frameMilliseconds);
                if (line != null) BlacksiteDiagnostics.Log("Hitch", line);
                if (Ledger.CostWindowDue) FlushCost();
            }
            else Ledger.Reset();
            _frameStart = now;
        }

        /// <summary>#1305: census of the live soldiers, then the window's [UAT][Cost] line.</summary>
        private static void FlushCost()
        {
            MoonEscalationState.CollectActiveBlacksiteSoldiers(CensusSoldiers);
            for (int i = 0; i < CensusSoldiers.Count; i++) CensusSoldiers[i].AddToCostCensus(Ledger);
            CensusSoldiers.Clear();
            bool helmetShadowsOff = ContractsBootstrap.BlacksiteHelmetLightShadows != null
                && !ContractsBootstrap.BlacksiteHelmetLightShadows.Value;
            BlacksiteDiagnostics.Log("Cost", Ledger.FlushCost(BlacksiteDiagnostics.OutputMuted, helmetShadowsOff));
        }

        private void OnDestroy()
        {
            if (_instance == this) Retire();
        }
    }
}
