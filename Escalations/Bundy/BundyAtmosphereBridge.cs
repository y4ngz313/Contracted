using System;
using System.Runtime.CompilerServices;
using BepInEx.Bootstrap;
using GameNetcodeStuff;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>
    /// The one call Bundy makes into Y4NGZAtmosphere (#393). Atmosphere is a soft
    /// dependency, so this type exists to keep its absence from being fatal.
    ///
    /// The guard has to be a separate, non-inlined method rather than an `if` at the
    /// call site: the JIT resolves every type a method body touches when it compiles
    /// that method, so a direct CompanyAudioManager reference inside
    /// ReportEscalationAudioSignal would throw a TypeLoadException on the first
    /// perception tick when the plugin is missing, whatever the guard said. Keeping
    /// the reference behind <see cref="MethodImplOptions.NoInlining"/> means the
    /// atmosphere assembly is only ever loaded on a machine that has it.
    ///
    /// The cue this feeds is the escalation music tier. If this stops reaching
    /// CompanyAudioManager the tier never fires and nothing else reports the fault,
    /// so the resolution is logged once at load.
    /// </summary>
    internal static class BundyAtmosphereBridge
    {
        private const string AtmosphereGuid = "com.y4ngz.atmosphere";

        private static bool _resolved;
        private static bool _available;

        /// <summary>True when Y4NGZAtmosphere is loaded and its audio layer is callable.</summary>
        internal static bool Available
        {
            get
            {
                if (!_resolved)
                {
                    _resolved = true;
                    _available = Chainloader.PluginInfos != null &&
                        Chainloader.PluginInfos.ContainsKey(AtmosphereGuid);
                    if (_available)
                        Plugin.Log?.LogInfo("[Bundy] Atmosphere present: escalation music cue enabled.");
                    else
                        Plugin.Log?.LogInfo("[Bundy] Atmosphere absent: escalation music cue disabled.");
                }

                return _available;
            }
        }

        /// <summary>
        /// Reports the stalker's range and hunt state to the escalation music layer.
        /// No-ops when Atmosphere is not installed.
        /// </summary>
        internal static void ReportStalker(PlayerControllerB target, float distance, bool hunting)
        {
            if (!Available)
                return;

            try
            {
                ReportStalkerCore(target, distance, hunting);
            }
            catch (Exception ex)
            {
                _available = false;
                Plugin.Log?.LogWarning($"[Bundy] Atmosphere stalker report failed; cue disabled for this session: {ex.Message}");
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReportStalkerCore(PlayerControllerB target, float distance, bool hunting)
        {
            Y4NGZCompany.Experience.Audio.CompanyAudioManager.ReportStalker(target, distance, hunting);
        }
    }
}
