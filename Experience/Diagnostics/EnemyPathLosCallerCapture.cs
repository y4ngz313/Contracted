using System;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core;

namespace Y4NGZCompany.Experience.Diagnostics
{
    /// <summary>
    /// Opt-in diagnostic probe that captures caller attribution for
    /// <c>EnemyAI.PathIsIntersectedByLineOfSight</c> during a timed window.
    ///
    /// Nothing is patched while the config key is 0: the RoundManager.Update landing
    /// hook is installed only while the key is above 0, and the patch on the method
    /// itself only while a window is open.
    ///
    /// Window opens when the ship lands on a moon while the config key is &gt; 0,
    /// or when the key is changed at runtime to &gt; 0. One window at a time;
    /// at the end of the window a ranked summary is printed and the patch is removed.
    /// </summary>
    internal static class EnemyPathLosCallerCapture
    {
        private static Harmony _harmony;
        private static ConfigEntry<float> _captureSeconds;
        private static ManualLogSource _log;

        private static readonly PathLosCallerCaptureCore _core = new PathLosCallerCaptureCore();

        // Window state.
        private static bool _windowOpen;
        private static float _windowEndTime;
        private static float _windowStartTime;
        private static int _windowStartFrame;

        // Landing edge detection.
        private static bool _shipWasLanded;
        private static MethodInfo _rmUpdate;
        private static bool _landingHookPatched;

        // Patch tracking.
        private static MethodInfo _losOriginal;
        private static MethodInfo _losPrefixMethod;
        private static MethodInfo _losPostfixMethod;
        private static bool _losPatched;

        // Per-call state passed from prefix to postfix (single-threaded).
        private static string _pendingPath;
        private static bool _pendingSampled;

        internal static void Initialize(ConfigFile config, ManualLogSource log)
        {
            _log = log;
            _harmony = new Harmony(ContractsBootstrap.PLUGIN_GUID + ".pathloscapture");

            // Seed the core's tick-frequency constant once.
            PathLosCallerCaptureCore.StopwatchFrequency = Stopwatch.Frequency;

            _captureSeconds = config.Bind(
                ContractedConfigSections.Diagnostics,
                "Path LOS Caller Capture Seconds",
                0f,
                new ConfigDescription(
                    "When set above 0, opens a capture window on the next landing (or immediately) "
                    + "that records every call to EnemyAI.PathIsIntersectedByLineOfSight, grouped "
                    + "by enemy type and calling mod path, then prints a ranked summary. "
                    + "Costs frame time while the window is open; leave at 0 for zero overhead.",
                    new AcceptableValueRange<float>(0f, 60f)));

            _captureSeconds.SettingChanged += OnSettingChanged;
            SyncLandingHook();
        }

        /// <summary>
        /// Installs the RoundManager.Update landing hook while the key is above 0 and
        /// removes it at 0, so an unused probe patches nothing. Never called from inside
        /// the hook itself.
        /// </summary>
        private static void SyncLandingHook()
        {
            bool wanted = _captureSeconds != null && _captureSeconds.Value > 0f;
            if (wanted == _landingHookPatched) return;

            _rmUpdate ??= AccessTools.Method(typeof(RoundManager), "Update");
            if (wanted)
            {
                var round = StartOfRound.Instance;
                _shipWasLanded = round != null && round.shipHasLanded;
                _harmony.Patch(_rmUpdate, postfix: new HarmonyMethod(
                    AccessTools.Method(typeof(EnemyPathLosCallerCapture), nameof(RoundManagerUpdatePostfix))));
                _landingHookPatched = true;
            }
            else
            {
                _landingHookPatched = false;
                _harmony.Unpatch(_rmUpdate, HarmonyPatchType.Postfix, _harmony.Id);
            }
        }

        private static void OnSettingChanged(object sender, EventArgs e)
        {
            // BepInEx dispatches this from its own config plumbing; a throw here would
            // propagate into that dispatch, so nothing is allowed to escape.
            try
            {
                if (_captureSeconds == null) return;

                float val = _captureSeconds.Value;
                if (val > 0f && !_windowOpen && !_losPatched)
                {
                    // Open immediately if we are already on a moon.
                    var round = StartOfRound.Instance;
                    if (round != null && round.shipHasLanded)
                        OpenWindow(val);
                }
                else if (val <= 0f && _windowOpen)
                {
                    // Not inside the patched method here, so finishing (and unpatching)
                    // straight away is safe.
                    _windowOpen = false;
                    FinishWindow();
                }
                SyncLandingHook();
            }
            catch (Exception ex)
            {
                _windowOpen = false;
                SafeRemoveLosPatch();
                _log?.LogWarning(
                    $"[MoonContracts.Timing] Path LOS capture setting-changed error: {ex.Message}");
            }
        }

        // ── Landing detection and window lifecycle ─────────────────────

        /// <summary>
        /// The only place the patch is added or removed. Runs outside the patched
        /// method, so Harmony is never asked to unpatch while the prefix is on the stack.
        /// </summary>
        private static void RoundManagerUpdatePostfix()
        {
            try
            {
                if (_captureSeconds == null) return;

                var round = StartOfRound.Instance;
                bool landed = round != null && round.shipHasLanded;

                // The run ended (quit to menu) or the ship left the moon: stop measuring.
                // Without this the window would stay open forever and the next landing
                // would be skipped, because a window is only opened when none is running.
                if (!landed && _windowOpen)
                    _windowOpen = false;

                // Expiry. The prefix also flips this flag, but it never unpatches.
                if (_windowOpen && Time.unscaledTime >= _windowEndTime)
                    _windowOpen = false;

                // Finish first, so a landing on this same frame can open a fresh window.
                if (!_windowOpen && _losPatched)
                    FinishWindow();

                if (landed && !_shipWasLanded && _captureSeconds.Value > 0f
                    && !_windowOpen && !_losPatched)
                {
                    OpenWindow(_captureSeconds.Value);
                }

                _shipWasLanded = landed;
            }
            catch (Exception ex)
            {
                // Leave no window running and no patch installed, so _losPatched and
                // Harmony cannot disagree about the method's state.
                _windowOpen = false;
                SafeRemoveLosPatch();
                _log?.LogWarning(
                    $"[MoonContracts.Timing] Path LOS capture landing detection error: {ex.Message}");
            }
        }

        private static void OpenWindow(float duration)
        {
            _core.Reset();
            _totalWindowTicks = 0;
            _totalWindowCalls = 0;
            _windowStartTime = Time.unscaledTime;
            _windowEndTime = _windowStartTime + duration;
            _windowStartFrame = Time.frameCount;
            ApplyLosPatch();
            // Measured only once the patch is actually in place.
            _windowOpen = true;
        }

        /// <summary>Removes the patch and writes the one summary block for the window.</summary>
        private static void FinishWindow()
        {
            _windowOpen = false;
            SafeRemoveLosPatch();

            float elapsed = Time.unscaledTime - _windowStartTime;
            int frames = Time.frameCount - _windowStartFrame;
            string report = _core.BuildReport(elapsed, frames,
                _totalWindowTicks, _totalWindowCalls);

            _log?.LogInfo(report);

            _totalWindowTicks = 0;
            _totalWindowCalls = 0;
        }

        private static long _totalWindowTicks;
        private static int _totalWindowCalls;

        // ── Dynamic LOS patching ───────────────────────────────────────

        private static void ApplyLosPatch()
        {
            if (_losPatched) return;

            _losOriginal ??= AccessTools.Method(typeof(EnemyAI), "PathIsIntersectedByLineOfSight");
            _losPrefixMethod ??= AccessTools.Method(typeof(EnemyPathLosCallerCapture), nameof(LosPrefix));
            _losPostfixMethod ??= AccessTools.Method(typeof(EnemyPathLosCallerCapture), nameof(LosPostfix));

            _harmony.Patch(_losOriginal,
                prefix: new HarmonyMethod(_losPrefixMethod),
                postfix: new HarmonyMethod(_losPostfixMethod));

            _losPatched = true;
        }

        /// <summary>
        /// Unpatch that never throws and always clears <see cref="_losPatched"/>, so a
        /// failed removal cannot leave the probe believing a window can still be opened.
        /// </summary>
        private static void SafeRemoveLosPatch()
        {
            if (!_losPatched) return;
            _losPatched = false;

            try
            {
                _losOriginal ??= AccessTools.Method(typeof(EnemyAI), "PathIsIntersectedByLineOfSight");
                if (_losOriginal == null) return;

                // Only this probe's own prefix and postfix on this one method; other
                // mods' patches on it are untouched.
                _harmony.Unpatch(_losOriginal, HarmonyPatchType.Prefix, _harmony.Id);
                _harmony.Unpatch(_losOriginal, HarmonyPatchType.Postfix, _harmony.Id);
            }
            catch (Exception ex)
            {
                _log?.LogWarning(
                    $"[MoonContracts.Timing] Path LOS capture unpatch failed: {ex.Message}");
            }
        }

        // ── Harmony prefix / postfix on PathIsIntersectedByLineOfSight ─

        /// <summary>
        /// Prefix: samples calls, walks the stack for sampled calls, and starts the stopwatch.
        /// Returns true always (never suppresses the original method).
        ///
        /// Never removes the patch: this runs with the patched method on the stack, so
        /// expiry and faults only clear <see cref="_windowOpen"/> (which stops all
        /// measurement immediately) and the RoundManager.Update postfix unpatches and
        /// reports on the next frame.
        /// </summary>
        private static bool LosPrefix(EnemyAI __instance, out long __state)
        {
            __state = 0;
            try
            {
                if (!_windowOpen) return true;
                if (Time.unscaledTime >= _windowEndTime)
                {
                    _windowOpen = false;
                    return true;
                }

                __state = Stopwatch.GetTimestamp();

                string enemyType = __instance.GetType().Name;
                var (fallbackPath, shouldSample) = _core.BeginCall(enemyType);

                if (shouldSample)
                {
                    _pendingPath = ExtractCallingPath();
                    _pendingSampled = true;
                }
                else
                {
                    _pendingPath = fallbackPath;
                    _pendingSampled = false;
                }
            }
            catch (Exception ex)
            {
                // Stop measuring now; the postfix on RoundManager.Update removes the patch.
                _windowOpen = false;
                __state = 0;
                _log?.LogWarning(
                    $"[MoonContracts.Timing] Path LOS capture disabled after exception: {ex.Message}");
            }

            return true;
        }

        /// <summary>
        /// Postfix: records the elapsed ticks for the call.
        /// </summary>
        private static void LosPostfix(EnemyAI __instance, long __state)
        {
            try
            {
                if (!_windowOpen || __state == 0) return;

                long elapsed = Stopwatch.GetTimestamp() - __state;
                var path = _pendingPath ?? "unknown";
                var sampled = _pendingSampled;

                string enemyType = __instance.GetType().Name;
                _core.CommitCall(enemyType, path, elapsed, sampled);

                _totalWindowTicks += elapsed;
                _totalWindowCalls++;
            }
            catch (Exception ex)
            {
                _log?.LogWarning(
                    $"[MoonContracts.Timing] Path LOS capture postfix error: {ex.Message}");
            }
        }

        // ── Stack walking ──────────────────────────────────────────────

        /// <summary>
        /// Extracts the first two real frames above PathIsIntersectedByLineOfSight,
        /// skipping the probe's own frames, the patched method itself, and every
        /// Harmony/MonoMod wrapper: dynamic methods (null declaring type), HarmonyLib.*,
        /// MonoMod.*, and the DMD detour stubs, whose method name reads "DMD&lt;...&gt;"
        /// and whose declaring type, when Mono gives one at all, is named "DMD...".
        /// Without those the ranked row would name the replacement method instead of the
        /// real caller.
        /// Returns: "OuterType.Method (Assembly) &gt; InnerType.Method"
        /// </summary>
        private static string ExtractCallingPath()
        {
            try
            {
                // skipFrames=2: StackTrace ctor + this method.
                var stack = new StackTrace(2, false);
                var frames = stack.GetFrames();
                if (frames == null || frames.Length == 0) return "unknown";

                int found = 0;
                string outer = null;
                string inner = null;

                foreach (var frame in frames)
                {
                    var method = frame.GetMethod();
                    if (method == null) continue;
                    var type = method.DeclaringType;
                    // Dynamic methods (Harmony/MonoMod detour stubs) have no declaring type.
                    if (type == null) continue;
                    var typeName = type.FullName ?? string.Empty;
                    var methodName = method.Name ?? string.Empty;

                    if (string.IsNullOrEmpty(typeName)) continue;
                    if (typeName.StartsWith("HarmonyLib.", StringComparison.Ordinal)) continue;
                    if (typeName.StartsWith("MonoMod.", StringComparison.Ordinal)) continue;
                    if (typeName.StartsWith("System.Diagnostics.", StringComparison.Ordinal)) continue;
                    // MonoMod DMD stubs, which Unity Mono does surface as real frames.
                    if (methodName.StartsWith("DMD<", StringComparison.Ordinal)) continue;
                    if (type.Name.StartsWith("DMD", StringComparison.Ordinal)) continue;
                    // The patched method itself is the origin of the walk, not a caller.
                    if (methodName == "PathIsIntersectedByLineOfSight") continue;
                    // Skip only the probe's own frames — not the rest of this mod.
                    // G1 exists to attribute calls to the owning mod, including Y4NGZCompany.
                    if (type == typeof(EnemyPathLosCallerCapture)) continue;
                    if (type == typeof(PathLosCallerCaptureCore)) continue;
                    // Skip compiler-generated closures and async state machines.
                    if (typeName.Contains("<")) continue;

                    found++;
                    if (found == 1)
                    {
                        inner = $"{type.Name}.{method.Name}";
                    }
                    else if (found == 2)
                    {
                        string assemblyName = type.Assembly.GetName().Name;
                        outer = $"{type.Name}.{method.Name} ({assemblyName})";
                        break;
                    }
                }

                if (inner == null) return "unknown";
                if (outer == null) return inner;
                return $"{outer} > {inner}";
            }
            catch
            {
                return "unknown";
            }
        }
    }
}