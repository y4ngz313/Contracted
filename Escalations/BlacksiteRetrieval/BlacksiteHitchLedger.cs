using System.Globalization;
using System.Text;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 Q11: the scopes the hitch tracer attributes a slow frame to. The first six are also
    /// Unity Profiler markers (<see cref="BlacksiteHitchLedger.IsMarker"/>): the one-shot frames the
    /// 23 September log measured without being able to name (begin landing job 42-70 ms, aircraft
    /// inbound 70-87 ms, the Payload cart's landed frame 80 ms, touchdown 113 ms, the deploy
    /// slices 25-28 ms). The next three are the per-frame owners the same log blamed for the
    /// 187 ms and 442 ms frames; they are tracer scopes only, because the profiler already names a
    /// MonoBehaviour's Update. #1305: the rest split the soldier crew's frame into its phases for
    /// the 10 s cost window.
    /// </summary>
    internal enum BlacksiteHitchScope : byte
    {
        BeginLandingJob,
        AircraftInbound,
        PayloadCartActivation,
        TouchdownNavigation,
        ShipLanded,
        SoldierDeploySlice,
        ShipUpdate,
        SoldierUpdate,
        MissionUpdate,
        SquadPerception,
        SoldierVanillaUpdate,
        SoldierAnimation,
        SoldierPresentation,
        SoldierLights,
        SoldierAudio,
        SoldierNavigator,
        SoldierCombat,
        SoldierBrain,
        SoldierAimPose,
        SoldierLateUpdate,
        SoldierDiagnostics,
        UatSink,
    }

    /// <summary>
    /// #874 Q11: one frame's time per scope, and the verdict when the frame closes. Unity-free so
    /// the regression suite drives it with synthetic clocks; <c>BlacksiteHitchTracer</c> feeds it
    /// the Stopwatch.
    ///
    /// Time is EXCLUSIVE: a scope opened inside another (the landed notification inside the ship's
    /// Update) charges its own time to itself and not to the enclosing scope, so the top scope is
    /// the code that actually spent the frame. #1305: a hitch names a scope only when that scope
    /// spent at least <see cref="AttributionShare"/> of the frame
    /// (<c>frame=N ms=X scope=&lt;name&gt; scopeMs=Y</c>); otherwise the frame was not ours to name
    /// (<c>frame=N ms=X scope=unattributed largest=&lt;name|none&gt; Yms</c>).
    ///
    /// #1305: every closed frame also adds into a cost window of
    /// <see cref="CostWindowMilliseconds"/> of frame time; <see cref="FlushCost"/> prints the mean
    /// exclusive ms per scope per frame, the window's counters and the soldier census.
    /// </summary>
    internal sealed class BlacksiteHitchLedger
    {
        /// <summary>A frame longer than this prints one line.</summary>
        internal const double HitchMilliseconds = 100d;
        /// <summary>#1305: a hitch names its largest scope only at this share of the frame.</summary>
        internal const double AttributionShare = 0.5d;
        /// <summary>#1305: closed frame time per cost line.</summary>
        internal const double CostWindowMilliseconds = 10000d;
        internal const int ScopeCount = 22;
        private const int MaxDepth = 8;

        private static readonly string[] Names =
        {
            "Blacksite.BeginLandingJob",
            "Blacksite.AircraftInbound",
            "Payload.CartActivation",
            "Blacksite.TouchdownNavigation",
            "Blacksite.ShipLanded",
            "Blacksite.SoldierDeploySlice",
            "BlacksiteShipController.Update",
            "BlacksiteSoldierAI.Update",
            "BlacksiteRetrievalMissionController.Update",
            "Squad.Perception",
            "Soldier.VanillaUpdate",
            "Soldier.Animation",
            "Soldier.Presentation",
            "Soldier.Lights",
            "Soldier.Audio",
            "Soldier.Navigator",
            "Soldier.Combat",
            "Soldier.Brain",
            "Soldier.AimPoseIK",
            "Soldier.LateUpdate",
            "Soldier.Diagnostics",
            "Uat.Sink",
        };

        internal static string Name(BlacksiteHitchScope scope) => Names[(int)scope];
        internal static bool IsMarker(BlacksiteHitchScope scope) => scope <= BlacksiteHitchScope.SoldierDeploySlice;

        /// <summary>
        /// #874 Q11: whether the frame that just ended was a round frame. The round-gated escalation
        /// runtime beats once per round frame, later in the frame than the tracer, so at the start of
        /// frame <paramref name="frame"/> the previous frame counts only if it beat.
        /// </summary>
        internal static bool StillInRound(int heartbeatFrame, int frame) => heartbeatFrame >= frame - 1;

        private readonly double _millisecondsPerTick;
        private readonly double[] _exclusive = new double[ScopeCount];
        private readonly byte[] _openScope = new byte[MaxDepth];
        private readonly long[] _openStart = new long[MaxDepth];
        private readonly long[] _openChildTicks = new long[MaxDepth];
        private int _depth;
        // Scopes past MaxDepth are not timed, only counted, so their exits still pair.
        private int _overflow;

        // #1305: the cost window.
        private readonly double[] _windowExclusive = new double[ScopeCount];
        private readonly StringBuilder _costLine = new StringBuilder(1024);
        private double _windowMilliseconds;
        private int _windowFrames;
        private int _physicsQueries;
        private int _uatLines;
        private int _soldiers, _animatorLayers, _shadowLights, _agents, _skinnedRenderers;

        internal BlacksiteHitchLedger(long ticksPerSecond)
        {
            _millisecondsPerTick = 1000d / ticksPerSecond;
        }

        /// <summary>#1305: the window holds at least <see cref="CostWindowMilliseconds"/> of frames.</summary>
        internal bool CostWindowDue => _windowFrames > 0 && _windowMilliseconds >= CostWindowMilliseconds;

        internal void CountPhysicsQuery() => _physicsQueries++;
        internal void CountUatLine() => _uatLines++;

        /// <summary>#1305: one live soldier's share of the census printed by the next <see cref="FlushCost"/>.</summary>
        internal void AddSoldierCensus(int animatorLayers, int shadowLights, bool agentEnabled, int skinnedRenderers)
        {
            _soldiers++;
            _animatorLayers += animatorLayers;
            _shadowLights += shadowLights;
            if (agentEnabled) _agents++;
            _skinnedRenderers += skinnedRenderers;
        }

        internal void Enter(BlacksiteHitchScope scope, long now)
        {
            if (_depth >= MaxDepth) { _overflow++; return; }
            _openScope[_depth] = (byte)scope;
            _openStart[_depth] = now;
            _openChildTicks[_depth] = 0;
            _depth++;
        }

        internal void Exit(long now)
        {
            if (_overflow > 0) { _overflow--; return; }
            // A scope that opened before the tracer started has nothing to close.
            if (_depth == 0) return;
            _depth--;
            long elapsed = now - _openStart[_depth];
            _exclusive[_openScope[_depth]] += (elapsed - _openChildTicks[_depth]) * _millisecondsPerTick;
            if (_depth > 0) _openChildTicks[_depth - 1] += elapsed;
        }

        /// <summary>
        /// Ends the frame: adds it to the cost window, returns the line body for a frame over
        /// <see cref="HitchMilliseconds"/>, null otherwise, and clears the frame either way.
        /// </summary>
        internal string CloseFrame(int frame, double frameMilliseconds)
        {
            int top = -1;
            double topMilliseconds = 0d;
            for (int i = 0; i < ScopeCount; i++)
            {
                double exclusive = _exclusive[i];
                _windowExclusive[i] += exclusive;
                if (exclusive > topMilliseconds) { topMilliseconds = exclusive; top = i; }
            }
            _windowMilliseconds += frameMilliseconds;
            _windowFrames++;
            ResetFrame();
            if (frameMilliseconds <= HitchMilliseconds) return null;
            string head = "frame=" + frame.ToString(CultureInfo.InvariantCulture)
                + " ms=" + frameMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);
            string scopeMilliseconds = topMilliseconds.ToString("0.0", CultureInfo.InvariantCulture);
            if (top >= 0 && topMilliseconds >= AttributionShare * frameMilliseconds)
                return head + " scope=" + Names[top] + " scopeMs=" + scopeMilliseconds;
            return head + " scope=unattributed largest=" + (top < 0 ? "none" : Names[top]) + " " + scopeMilliseconds + "ms";
        }

        /// <summary>
        /// #1305: the cost line body for the window so far, then clears the window.
        /// </summary>
        internal string FlushCost(bool uatMuted, bool helmetShadowsOff)
        {
            CultureInfo invariant = CultureInfo.InvariantCulture;
            int frames = _windowFrames;
            double perFrame = frames > 0 ? 1d / frames : 0d;
            double seconds = _windowMilliseconds / 1000d;
            double owned = 0d;
            for (int i = 0; i < ScopeCount; i++) owned += _windowExclusive[i] * perFrame;

            StringBuilder line = _costLine.Clear();
            line.Append("window=").Append(seconds.ToString("0.00", invariant)).Append('s')
                .Append(" frames=").Append(frames.ToString(invariant))
                .Append(" frameMs=").Append((_windowMilliseconds * perFrame).ToString("0.00", invariant))
                .Append(" soldiers=").Append(_soldiers.ToString(invariant))
                .Append(" ownedMs=").Append(owned.ToString("0.000", invariant))
                .Append(" |");
            for (int i = 0; i < ScopeCount; i++)
                line.Append(' ').Append(Names[i]).Append('=').Append((_windowExclusive[i] * perFrame).ToString("0.000", invariant));
            line.Append(" | physicsQueries/f=").Append((_physicsQueries * perFrame).ToString("0.00", invariant))
                .Append(" uatLines/s=").Append((seconds > 0d ? _uatLines / seconds : 0d).ToString("0.0", invariant))
                .Append(" uatMuted=").Append(uatMuted ? "on" : "off")
                .Append(" animLayers=").Append(_animatorLayers.ToString(invariant))
                .Append(" shadowLights=").Append(_shadowLights.ToString(invariant))
                .Append(" agents=").Append(_agents.ToString(invariant))
                .Append(" skinnedPerSoldier=").Append((_soldiers > 0 ? (double)_skinnedRenderers / _soldiers : 0d).ToString("0.00", invariant))
                .Append(" helmetShadows=").Append(helmetShadowsOff ? "off" : "on");
            ResetWindow();
            return line.ToString();
        }

        /// <summary>Clears the open frame and the cost window.</summary>
        internal void Reset()
        {
            ResetFrame();
            ResetWindow();
        }

        private void ResetFrame()
        {
            for (int i = 0; i < ScopeCount; i++) _exclusive[i] = 0d;
            _depth = 0;
            _overflow = 0;
        }

        private void ResetWindow()
        {
            for (int i = 0; i < ScopeCount; i++) _windowExclusive[i] = 0d;
            _windowMilliseconds = 0d;
            _windowFrames = 0;
            _physicsQueries = 0;
            _uatLines = 0;
            _soldiers = _animatorLayers = _shadowLights = _agents = _skinnedRenderers = 0;
        }
    }
}
