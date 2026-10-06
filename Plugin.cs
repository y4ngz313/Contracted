using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Core;
using Y4NGZCore.Diagnostics;
using Y4NGZCore.Lifecycle;

namespace Y4NGZCompany
{
    [BepInPlugin(Guid, Name, Version)]
    // Hard: Contracted compiles against Y4NGZCore (placement layer, shared keypad,
    // compat shims) and must load after it.
    [BepInDependency("com.y4ngz.core", "1.0.10")]
    // Soft, not hard (#731): #660 moved the theme/visibility/motion substrate into Y4NGZUI.dll,
    // and Y4NGZUI is an optional companion of every Y4NGZ package, this one included. Contracted
    // no longer references that assembly: the legacy probe facades read Y4NGZCore's neutral
    // substrate (UiThemeState, UiThemeCommands, the two HUD signals), which Y4NGZUI publishes
    // into when installed and which answers with the Default palette, a visible HUD and a
    // resting offset when it is not. The flag only guarantees Y4NGZUI chainloads first when
    // present, so its config is bound before any contract UI reads the palette.
    [BepInDependency(Y4NGZCore.Lifecycle.ModuleHarmonyIds.Ui, BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("evaisa.lethallib", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.github.teamxiaolan.dawnlib", BepInDependency.DependencyFlags.HardDependency)]
    // Soft: the terminal takeover reflects into ITAPI when present; contractlist works without it.
    [BepInDependency("WhiteSpike.InteractiveTerminalAPI", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.sigurd.csync", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("LethalNetworkAPI", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("com.rune580.LethalCompanyInputUtils", BepInDependency.DependencyFlags.HardDependency)]
    [BepInDependency("Zaggy1024.OpenBodyCams", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("imabatby.lethallevelloader", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("darmuh.TerminalStuff", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("com.github.darmuh.LethalConstellations", BepInDependency.DependencyFlags.SoftDependency)]
    [BepInDependency("Zaggy1024.PathfindingLib", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft: the atmosphere plugin owns its own lifecycle since #393 and this mod plays
    // fine without it. The flag only guarantees it chainloads first, so Bundy's
    // escalation music bridge sees it registered on the first perception tick.
    [BepInDependency("com.y4ngz.atmosphere", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft: the blood FX plugin owns its own lifecycle since #393. Bundy's blood goes
    // through BundyBloodFXBridge, which falls back to the legacy particle rigs when this
    // is absent. The flag only guarantees it chainloads first, so the bridge's one-time
    // availability probe sees it registered.
    [BepInDependency("com.y4ngz.bloodfx", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft, not hard: FacilityMeltdown only adds the apparatus-pull security shutdown and
    // the meltdown alarm-light sync, so this mod stays installable without it. The flag
    // also guarantees Meltdown chainloads first, which is what lets FacilityMeltdownCompat
    // resolve MeltdownAPI by name during our own load.
    [BepInDependency("me.loaforc.facilitymeltdown", BepInDependency.DependencyFlags.SoftDependency)]
    // Soft (#727): WeatherRegistry only lends its weather names to the mid-round weather
    // alert on the contract card. Detection itself reads vanilla fields, so this mod works
    // without it; the flag guarantees the registry chainloads first so the name lookup can
    // resolve its API by name on the first alert.
    [BepInDependency(Core.Compatibility.WeatherChangeAlerts.WeatherRegistryGuid, BepInDependency.DependencyFlags.SoftDependency)]
    // Soft (#802): Lategame Upgrades owns its five contracts; Contracted only decides which one
    // a moon gets and hands it over through LategameUpgradesCompat by reflection, so this mod
    // works unchanged without it. The flag guarantees LGU chainloads first, which is what lets
    // the shim resolve its contract singleton and attach the shop-suppression prefixes during
    // our own load instead of waiting for a later retry.
    [BepInDependency(Core.Compatibility.LategameUpgradesCompat.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
    // Soft (#1195): Wesley's Interiors secondary objectives become the card's side objective,
    // read by name through WesleysInteriorToolsCompat, so this mod works unchanged without them;
    // the flag only guarantees Wesley's tools chainload first when present.
    [BepInDependency(Core.Compatibility.WesleysInteriorToolsCompat.PluginGuid, BepInDependency.DependencyFlags.SoftDependency)]
    // #613 task 2.1: the Wider Ship Mod / 2 Story Ship / CustomPosters soft dependencies that
    // used to be declared here moved to Y4NGZShipSystems' own plugin attribute list, with the
    // ShipLayoutModIntegration code that is their only reason to exist. Contracted does nothing
    // with those three mods, and naming their GUID constants would mean referencing a Ship
    // Systems type from Contracted — the one direction this split does not allow.
    //
    // Soft: Ship Systems is a separate plugin since #613. Contracted works without it; the flag
    // only guarantees it chainloads first when present, so a monitor claim probe or a ship-layout
    // subscription it publishes into Y4NGZCore is already there on Contracted's first frame.
    [BepInDependency(Y4NGZCore.Lifecycle.ModuleHarmonyIds.ShipSystems, BepInDependency.DependencyFlags.SoftDependency)]
    public sealed class Plugin : BaseUnityPlugin
    {
        // #612 task 1.4: the identity Contracted keeps through the split. Aliased to the Core
        // constant so the three plugin GUIDs are declared in one table; the literal value is
        // unchanged, which is what keeps existing config files and save data readable.
        public const string Guid = ModuleHarmonyIds.Contracted;
        // #694: the display name mod managers show for this plugin's config. The GUID
        // (and therefore the config filename and save identity) is unchanged.
        public const string Name = "Contracted";
        // 0.2.0: CctvSupportApi.DetectionTimeMultiplierProvider seam for companion mods
        // (per-player camera detection-time scaling, used by Y4NGZUpgrades' Chameleon).
        // 0.3.0: QuotaOverhaulRules.DeathPenaltyFractionModifier seam (Y4NGZUpgrades'
        // Veteran) and CctvSupportApi.TryDisableCamera/IsCameraDisabled per-camera
        // shutdown API (Y4NGZUpgrades' Field Mechanic). Also CctvSupportApi
        // TryBeginLockdown/TryEndLockdown/IsLockdownActive lockdown seam and
        // GetCameraIds/GetCameraLabel camera enumeration (Y4NGZUpgrades'
        // Field Operations tablet).
        public const string Version = "1.0.0";

        /// <summary>
        /// This build's wire-protocol revision, advertised through
        /// <see cref="Y4NGZCore.Modules.Net.ModuleHandshakeService"/> (#613 task 2.2). Covers this
        /// assembly's named messages, RPCs and replicated NetworkVariable layouts, not
        /// <see cref="Version"/>. Bump when an older peer would misread the wire contract.
        /// </summary>
        // Revision 3: replicated Blacksite traversal and animation clocks change the soldier wire layout.
        public const int ContractedProtocolRevision = 3;

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        /// <summary>
        /// Name of the GameObject that owns <see cref="Y4NGZCompanyHost"/>. Every per-frame
        /// tick and every teardown this plugin performs hangs off that object's lifetime.
        /// </summary>
        internal const string HostObjectName = "Y4NGZCompany_Host";

        /// <summary>
        /// The MonoBehaviour anything in this assembly should run a coroutine on. Never use the
        /// plugin component itself: it lives on BepInEx's shared, externally destroyable
        /// manager object (see the #393 note in <see cref="Awake"/>), while this is our own.
        /// </summary>
        internal static MonoBehaviour CoroutineHost { get; private set; }

        // #612 task 1.4: one Harmony owner per future assembly, not one shared owner for all
        // three. Before this, every bootstrap alias resolved to com.y4ngz.company and a single
        // instance was handed to all six modules, so UnpatchSelf() from any future DLL would
        // have stripped the other two's patches. The instances are all created here only
        // because the build is still monolithic; at the split each moves to its own plugin's
        // Awake and nothing else about the wiring changes.
        private Harmony _harmonyContracted;
        // #613 task 2.1: _harmonyShipSystems is gone — Ship Systems constructs its own Harmony
        // from the same id inside its own plugin's Awake.
        // #614 task 3.1: so is _harmonyTakeover, for the same reason. Monitor Takeover is
        // Y4NGZMonitorTakeover.dll now and constructs a Harmony on
        // ModuleHarmonyIds.MonitorTakeover in MonitorTakeoverPlugin.Awake. Contracted must not
        // construct it, must not patch under it, and — critically — must not UnpatchSelf it: that
        // is the whole reason the ids were split apart in #612 task 1.4.
        private Y4NGZCompanyHost _host;

        /// <summary>
        /// Set by this component's own <c>OnApplicationQuit</c>, which Unity raises on every
        /// active MonoBehaviour before it starts destroying them. Read by
        /// <see cref="OnDestroy"/> only to choose the log line — never to change what is torn
        /// down. <see cref="Y4NGZCompanyHost"/> keeps its own copy of the same flag because the
        /// two components can be destroyed in either order.
        /// </summary>
        private bool _applicationQuitting;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // #612 task 1.4 / #613 task 2.1: bind this plugin's own log channels before anything
            // can log. This was ModuleLog.BindAll(Logger) while the build was one DLL. It cannot
            // stay BindAll now that Ship Systems is a separate plugin: BepInEx chainload order is
            // not ours to choose, and if Contracted awoke second, BindAll would silently rebind
            // Ship Systems' three channels back to Contracted's logger — every Ship Systems line
            // for the rest of the session would carry the wrong plugin's prefix.
            //
            // #614 task 3.1: Contracted binds Contracts and Bundy, and nothing else. The Takeover
            // channel left with the code — Y4NGZMonitorTakeover binds it in its own Awake — for
            // exactly the reason the ShipSystems/Store/Terminal channels are not bound here.
            //
            // With Ship Systems NOT installed, nothing ever binds those three. That is handled,
            // not left dangling: every ModuleLog accessor falls back to CoreDiagnostics.Log
            // rather than returning null (see ModuleLog, "Never null"), so a shared Core service
            // reached from Contracted that happens to log on a Ship Systems channel writes to
            // Y4NGZCore's own BepInEx source instead of throwing.
            ModuleLog.Contracts = Logger;
            ModuleLog.Bundy = Logger;
            ModuleTickRegistry.WarningSink = message => Logger.LogWarning(message);

            // #393: do not put runtime components on BepInEx's shared "BepInEx_Manager" object.
            // Verified against BepInEx 5.4.23.5 Chainloader.Start IL: DontDestroyOnLoad on the
            // manager object is unconditional, and Chainloader/HideManagerGameObject gates only
            // the hideFlags = HideAndDontSave store. With the BepInEx default (false) the shared
            // object is therefore findable via GameObject.Find("BepInEx_Manager"), and any mod
            // doing Find-based cleanup can destroy it plus every plugin component on it — which
            // is how LethalCCTV's bootstrap died at frame 0 in one profile. A self-owned,
            // hide-flagged host is unreachable by name, so our lifetime is ours alone.
            var hostObject = new GameObject(HostObjectName)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            UnityEngine.Object.DontDestroyOnLoad(hostObject);
            _host = hostObject.AddComponent<Y4NGZCompanyHost>();
            CoroutineHost = _host;

            NetcodeRuntimeBootstrap.Initialize(Logger, typeof(Plugin).Assembly);

            // #613 task 2.2 - the cross-plugin module handshake. Contracted joins it for the same
            // reason Ship Systems does: what the handshake compares is the module SET of the whole
            // install, and a plugin that does not announce is indistinguishable from a plugin that
            // is not there. Revision covers this assembly's own named messages (the contract
            // progression, quota sync, escalation state and announcement wire names), not its
            // Version - see ShipSystemsPlugin.ShipSystemsProtocolRevision for the bump rule.
            Y4NGZCore.Modules.Net.ModuleHandshakeService.Publish(
                Y4NGZCore.Lifecycle.ModuleIds.Contracted,
                ContractedProtocolRevision,
                Version);

            Y4NGZCompanyConfigMigration.MigrateConfigSafely(Config, Logger);
            // #660: UiTheme and the whole "06 - UI Theme" block (player HUD included) moved to
            // Y4NGZUI.dll, which binds them on its own ConfigFile in its own Awake — before this
            // one when it is installed, because our plugin attribute soft-depends on com.y4ngz.ui.
            // What this assembly still owns are the contract-side seams Y4NGZUI must not know
            // about. #731: they are written into Y4NGZCore's UiPresentationSeams rather than onto
            // Y4NGZUI's own fields, because Contracted no longer references Y4NGZUI at all —
            // Y4NGZUI reads the seams lazily at each use site, so this works whichever of the
            // two loaded first and is simply inert when Y4NGZUI is not installed.
            //
            // The round-end presentation gate. GameplayUiVisibility used to read
            // ContractPerformanceReportUi directly; the dependency now points this way.
            Y4NGZCore.Modules.Hud.UiPresentationSeams.RoundEndPresentationActiveProbe =
                () => Y4NGZCompany.Contracts._Shared.ContractPerformanceReportUi.IsRoundEndPresentationActive;
            // The performance-report theme applicator: contract UI stays exempt from the
            // general-UI gate, so Y4NGZUI's report patch calls back into the applicator that
            // lives with the report code.
            Y4NGZCore.Modules.Hud.UiPresentationSeams.PerformanceReportApplicator =
                Y4NGZCompany.Experience.UITheme.PerformanceReportThemeApplicator.Apply;
            // The protected-slot bridge: an already-shipped Y4NGZUpgrades writes the resolver
            // onto this assembly's legacy UiTheme facade field by reflection; Y4NGZUI reads it
            // through this source until Upgrades#352 writes Y4NGZUI directly.
            Y4NGZCore.Modules.Hud.UiPresentationSeams.ProtectedSlotResolverSource =
                () => Y4NGZCompany.Experience.UITheme.UiTheme.ProtectedSlotResolver;
            // #614 task 3.1: QuotaProgressionRegistry.Initialize left with the registry. It is
            // Monitor Takeover's ("Quota milestone registry, rewards and unlocks" in the plan's
            // ownership table) and MonitorTakeoverPlugin.Awake now calls it, unconditionally and
            // outside TakeoverBootstrap's config gate, exactly as this line was.

            _harmonyContracted = new Harmony(ModuleHarmonyIds.Contracted);

            // #471: relocation mods can return a SandSpider indoors with a null homeNode,
            // making the owning peer throw from every subsequent vanilla AI interval.
            _harmonyContracted.PatchAll(typeof(Y4NGZCompany.Core.Compatibility.SandSpiderSearchStateGuardPatch));

            // #453: local input-state watchdog. Bound and patched before any module so a module
            // that strands an interact flag during its own init is already being observed. It is
            // deliberately not inside InitializeModule: it must run whether or not a contract,
            // escalation or ship system is active.
            Y4NGZCompany.Experience.Diagnostics.LocalInputStateWatchdog.Initialize(Config, Logger, _harmonyContracted);

            // #614 task 3.1: the QuotaProgressionUnlockPatches PatchAll that stood here is gone.
            // It was always Monitor Takeover's — it patches StartOfRound.PositionSuitsOnRack and
            // the two Terminal purchase methods to enforce quota milestone *unlocks*, which the
            // plan's ownership table assigns to Monitor Takeover — and it was applied here under
            // ModuleHarmonyIds.MonitorTakeover only because the code was still in this assembly.
            // MonitorTakeoverPlugin.Awake applies it now, still outside the TakeoverBootstrap
            // call so a config-disabled or bundle-less takeover still gates purchases.

            // #629: quota rollover publication moved with the progression registry into
            // Y4NGZMonitorTakeover. Contracted stages objective outcome data in Core, but must
            // not patch a second publisher when both feature DLLs are installed.
            // #660: the sixteen theme/settings/pause patches and the four substrate Initialize
            // calls that stood here moved to Y4NGZUI's Plugin.Awake, applied under
            // ModuleHarmonyIds.Ui. Contracted applies no general-UI patches any more; the seams
            // it still owns are installed above, right after config migration.

            // #612 task 1.4: three entry points, one per future assembly, in the order the
            // pre-split build initialised its six modules. Contracted's two modules are named
            // directly because this file is Contracted's own plugin; the other two assemblies
            // are reached only through their entry point, so nothing here names another
            // feature's bootstrap type. At the split, the two calls below move to the two new
            // plugins' Awake and this file keeps only the Contracted block.
            //
            // Host object, not the plugin's own: BundyBootstrap AddComponents its debug
            // hotkey behaviour here when DebugLogs is on, and that component must share the
            // hardened lifetime rather than BepInEx's shared object.
            InitializeModule("Bundy", () => Y4NGZCompany.Bootstrap.BundyPlugin.Initialize(Config, Logger, hostObject, _harmonyContracted));
            InitializeModule("Contracts", () => Y4NGZCompany.Bootstrap.ContractsBootstrap.Initialize(Config, Logger, _harmonyContracted));
            // #613 task 2.1: the Ship Systems call that used to be here is gone. That feature is
            // Y4NGZShipSystems.dll now and BepInEx runs its Awake; Contracted must not initialise
            // it, must not construct its Harmony, and must not tear it down.
            // #614 task 3.1: and so is the Monitor Takeover call, for exactly the same reasons.
            // That feature is Y4NGZMonitorTakeover.dll now.

            // #660: the GameplayUiVisibility tick that stood here is registered by Y4NGZUI's
            // Plugin now, under ModuleHarmonyIds.Ui — same order constant, same lambda shape.
            // Registering it here would have left it owned by Contracted, so Contracted's host
            // dying would strip the UI plugin's visibility publish out of a pump Y4NGZUI's own
            // live host was still driving — the per-owner defect ModuleTickRegistry latches
            // against (see the #614 note that used to stand here for the identical case).

            RegisterContractedTeardowns();

            // Only now that every module is initialized does the host start ticking and
            // become responsible for teardown.
            _host.Activate(_harmonyContracted, Logger);

            EffectiveConfigInventory.Write(Config, Logger);

            SplitAssemblyDiagnostics.Report(Logger);
            Logger.LogInfo($"{Name} v{Version} loaded.");
        }

        /// <summary>
        /// Safety net only, and it must stay one. The real teardown lives on
        /// <see cref="Y4NGZCompanyHost"/>; this runs the same sequence solely for the two cases
        /// that host cannot cover — see the three reachable branches below.
        ///
        /// <para><b>It must NOT tear down while the host is alive (#613 follow-up).</b> Same
        /// defect, same shape, as the one diagnosed on <c>ShipSystemsPlugin.OnDestroy</c> — see
        /// the long note there for the observed log evidence. This component rides on BepInEx's
        /// shared <c>BepInEx_Manager</c> object, which with the BepInEx default
        /// <c>Chainloader/HideManagerGameObject=false</c> is reachable by
        /// <c>GameObject.Find("BepInEx_Manager")</c> and is destroyed outright by some mods'
        /// cleanup. Running the teardown then removes this owner's tick registrations from
        /// <see cref="ModuleTickRegistry"/> (permanently — a torn-down owner may not
        /// re-register) and unpatches both Harmony owners, while
        /// <c>Y4NGZCompany_Host</c> is alive and still pumping. The plugin would be dead for the
        /// rest of the session with no error, because the loud message lives on
        /// <see cref="Y4NGZCompanyHost.OnDestroy"/>, which never ran.</para>
        ///
        /// <para><b>The three reachable branches, exactly.</b></para>
        /// <list type="number">
        /// <item><description><see cref="Y4NGZCompanyHost.HasTornDown"/> — the host already ran
        /// the shutdown. Nothing to do.</description></item>
        /// <item><description>A host that exists <em>and</em> is activated — the plugin is running
        /// normally and something destroyed BepInEx's shared object out from under it. Warn once
        /// and leave every patch and registration applied.</description></item>
        /// <item><description>Everything else — no host, or a host that was created but never
        /// activated. <see cref="Awake"/> assigns <c>_host</c> long before it reaches
        /// <c>_host.Activate</c>, so a throw in between leaves a live, unactivated host;
        /// <see cref="Y4NGZCompanyHost.OnDestroy"/> returns at its <c>!_activated</c> early-out in
        /// that state, making this branch the only path that can unwind the partial
        /// <c>Awake</c>. <c>TearDown</c> tolerates a null array and null elements, which is the
        /// shape a pre-<c>Activate</c> failure leaves behind (#614 task 3.1 narrowed the parameter
        /// from a <c>Harmony[]</c> to a single <c>Harmony</c>, since Contracted now owns exactly
        /// one Harmony id; <c>TearDown</c> still tolerates a null).</description></item>
        /// </list>
        /// </summary>
        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
        }

        private void OnDestroy()
        {
            if (Y4NGZCompanyHost.HasTornDown)
                return;

            // Unity's overloaded null: a destroyed host compares equal to null and falls through
            // to the real teardown below. So does a host that exists but never reached Activate -
            // nothing else will ever tear that state down. Only a LIVE, ACTIVATED host means "the
            // plugin is still running and this is not a shutdown".
            if (_host != null && _host.IsActivated)
            {
                // Same branch, two very different situations. On an ordinary application quit
                // Unity may destroy BepInEx's manager object — and this component with it —
                // before it reaches the host, so this branch is on the normal exit path too.
                // Warning about "mid-session" destruction and "gameplay continues normally"
                // there would put a false alarm in every log the mod ever produces, which is
                // how a real one stops being read. The teardown decision is unchanged either
                // way: the host is alive and will run the shutdown from its own OnDestroy.
                if (_applicationQuitting)
                {
                    Logger.LogDebug(
                        "[Y4NGZCompany] Plugin component destroyed during application quit; "
                        + $"'{HostObjectName}' is still alive and owns the shutdown.");
                    return;
                }

                Logger.LogWarning(
                    "[Y4NGZCompany] The plugin component's GameObject was destroyed mid-session "
                    + $"(frame={Time.frameCount}; BepInEx [Chainloader] HideManagerGameObject is "
                    + "false, so BepInEx_Manager is destroyable by any mod's Find-based cleanup). "
                    + $"'{HostObjectName}' is alive, so nothing was torn down: the tick "
                    + "registrations and every Harmony patch this plugin applied remain ACTIVE and "
                    + "gameplay continues normally.");
                return;
            }

            Y4NGZCompanyHost.TearDown(_harmonyContracted, Logger);
        }

        /// <summary>
        /// Contracted's shutdown subscribers. Ship Systems' and Monitor Takeover's are
        /// registered by their own entry points, which is the point: after the split each
        /// plugin registers only what it owns and <see cref="ModuleTickRegistry"/> supplies the
        /// ordering that used to come from one hand-written block.
        /// </summary>
        private static void RegisterContractedTeardowns()
        {
            // #660: the TerminalThemeBridge / VanillaUiThemePolish / GameplayHudMotion /
            // GameplayUiVisibility teardowns that opened this sequence moved to Y4NGZUI's
            // Plugin.Awake with the systems they shut down, registered under
            // ModuleHarmonyIds.Ui at the same ModuleTeardownOrder constants.
            ModuleTickRegistry.RegisterTeardown(
                ModuleHarmonyIds.Contracted,
                ModuleTeardownOrder.LocalInputStateWatchdog,
                "LocalInputStateWatchdog",
                Y4NGZCompany.Experience.Diagnostics.LocalInputStateWatchdog.Shutdown);
        }

        internal static void InitializeModule(string label, Action initialize)
        {
            try
            {
                initialize();
            }
            catch (Exception ex)
            {
                Log.LogError($"Y4NGZCompany module '{label}' failed to initialize: {ex}");
            }
        }
    }

    /// <summary>
    /// Runtime host for the Contracted plugin: owns the per-frame tick and the teardown that
    /// used to live on <see cref="Plugin"/> itself.
    ///
    /// #393: this rides on the plugin's own <c>Y4NGZCompany_Host</c> GameObject
    /// (<see cref="HideFlags.HideAndDontSave"/> + <c>DontDestroyOnLoad</c>) rather than on
    /// BepInEx's shared manager object, which with the default
    /// <c>Chainloader/HideManagerGameObject=false</c> is discoverable by
    /// <c>GameObject.Find("BepInEx_Manager")</c> and therefore destroyable by any other mod's
    /// cleanup. Losing this component means the ship-systems tick, the quota tick and the
    /// gameplay-UI visibility publish all stop, and <c>Harmony.UnpatchSelf</c> strips every
    /// patch this plugin applied — so an unexpected destroy is logged loudly.
    /// </summary>
    internal sealed class Y4NGZCompanyHost : MonoBehaviour
    {
        private static bool _tornDown;

        private Harmony _harmony;
        private ManualLogSource _log;
        private bool _activated;
        private bool _applicationQuitting;

        /// <summary>True once the shutdown sequence has run, from either owner.</summary>
        internal static bool HasTornDown => _tornDown;

        /// <summary>
        /// True once <see cref="Activate"/> has run — i.e. once <c>Awake</c> finished and this
        /// host became responsible for the tick and the teardown.
        ///
        /// <para>Read by <see cref="Plugin.OnDestroy"/>, which must distinguish "the shared
        /// BepInEx manager object died under a running plugin" (this is true: leave everything
        /// applied) from "Awake threw after creating the host but before activating it" (this is
        /// false: nobody else will ever tear the partial state down, so the safety net must). This
        /// host's own <c>OnDestroy</c> returns early in the second case, so without this the
        /// teardown would be unreachable from either side.</para>
        /// </summary>
        internal bool IsActivated => _activated;

        internal void Activate(Harmony harmony, ManualLogSource log)
        {
            _harmony = harmony;
            _log = log;
            _activated = true;
        }

        /// <summary>
        /// #612 task 1.4: the six hand-written calls that used to live here are now six
        /// registrations in <see cref="ModuleTickRegistry"/>, run in the order declared by
        /// <see cref="ModuleTickOrder"/> — the same order, written down instead of implied by
        /// the sequence of statements in this method. The reason it had to move is that after
        /// the split there are three plugins and three hosts, and the relative order of six
        /// callbacks spread across three <c>LateUpdate</c>s would be decided by Unity's script
        /// execution order rather than by anything this project controls. Two of the six have
        /// documented ordering requirements (the Bundy backstop and, especially, the input
        /// watchdog running last), so "decided by the user's mod folder" is not an option.
        ///
        /// <para>The registry also isolates a throwing subscriber, which this method did not:
        /// an exception from any of the six used to abort the whole frame's pump, taking the
        /// watchdog's sample with it.</para>
        /// </summary>
        private void LateUpdate()
        {
            if (!_activated)
                return;

            // #613 task 2.1: frame-scoped. Both plugins' hosts pump the whole registry so
            // the ModuleTickOrder sequence survives having more than one host, and the
            // frame token is what stops the second pump of the same frame running every
            // subscriber a second time. See ModuleTickRegistry.Tick(int).
            ModuleTickRegistry.Tick(Time.frameCount);
        }

        private void OnApplicationQuit()
        {
            _applicationQuitting = true;
        }

        private void OnDestroy()
        {
            if (!_activated)
                return;

            if (!_applicationQuitting)
            {
                _log?.LogError(
                    "[Y4NGZCompany] Host destroyed outside application shutdown " +
                    $"(frame={Time.frameCount}, host='{(gameObject != null ? gameObject.name : "<null>")}', " +
                    $"scene='{(gameObject != null ? gameObject.scene.name : "<null>")}'). " +
                    "Ship systems, quota progression, the gameplay-UI publish and every Harmony " +
                    "patch this plugin applied are going down with it.");
            }

            TearDown(_harmony, _log);
        }

        /// <summary>
        /// Idempotent: the first caller (host or the plugin's safety-net OnDestroy) wins.
        ///
        /// <para>#612 task 1.4: the seven hand-written shutdown calls became seven
        /// registrations, run in <see cref="ModuleTeardownOrder"/>. Same order; each isolated
        /// rather than all seven sharing one try/catch, because one plugin's faulty shutdown
        /// must not silently skip another plugin's. See <see cref="ModuleTeardownOrder"/> for
        /// why that one difference is deliberate.</para>
        ///
        /// <para><b>One Harmony owner, one <c>UnpatchSelf</c> (#614 task 3.1).</b> This used to
        /// unpatch an array, because one host object owned two of the three future assemblies'
        /// Harmony ids. Monitor Takeover's is its own plugin's now, and unpatching another
        /// plugin's id from here is the exact failure the split's separate ids exist to make
        /// impossible.</para>
        /// </summary>
        internal static void TearDown(Harmony harmony, ManualLogSource log)
        {
            if (_tornDown)
                return;
            _tornDown = true;

            // #612 task 1.4 (F7): the owners this host is responsible for, named explicitly.
            // Today that is all three, because one host object owns all three future assemblies.
            // At the split this line keeps only ModuleHarmonyIds.Contracted and the other two
            // plugins' hosts name their own — which is the entire reason the registry latches
            // per owner instead of once per process. A process-wide latch would have the first
            // host to die tear down two plugins that are still running, and then silence their
            // own OnDestroy when it came.
            // #613 task 2.1: ShipSystems is no longer in this list. Its own host names it, and a
            // host that tore down an owner belonging to a plugin that is still running is exactly
            // the failure the per-owner latch was built to prevent.
            // #614 task 3.1: neither is MonitorTakeover, for the same reason. This host now names
            // exactly one owner — the one it is the host of.
            ModuleTickRegistry.RunTeardown(ModuleHarmonyIds.Contracted);

            if (harmony == null)
                return;

            try
            {
                harmony.UnpatchSelf();
            }
            catch (Exception ex)
            {
                log?.LogWarning(
                    "Y4NGZCompany shutdown warning: UnpatchSelf failed for Harmony id "
                    + $"'{harmony.Id}': {ex.Message}");
            }
        }
    }
}
