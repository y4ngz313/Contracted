using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using BepInEx.Configuration;
using BepInEx.Logging;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using UnityEngine.InputSystem;
using Y4NGZCore.Diagnostics;

namespace Y4NGZCompany.Experience.Diagnostics
{
    /// <summary>
    /// #453: local-player input-state watchdog.
    ///
    /// Session 3's host lost the crosshair, E (interact) and G (discard) mid-run and the logs
    /// carried zero signal: every candidate fault (a stranded grenade-throw coroutine in
    /// Y4NGZUpgrades, a stuck <c>inSpecialInteractAnimation</c>, or both) is silent. This runs
    /// once per <see cref="Y4NGZCompanyHost"/> LateUpdate on every session — contract active or
    /// not — and turns the next such incident into one greppable line.
    ///
    /// Flag semantics verified against Assembly-CSharp v81 IL (Mono.Cecil read of the shipped
    /// <c>Lethal Company_Data/Managed/Assembly-CSharp.dll</c>), not model knowledge:
    /// <list type="bullet">
    /// <item>Every monitored member is a <b>public instance field</b> on
    /// <c>GameNetcodeStuff.PlayerControllerB</c>, except <c>isMenuOpen</c>, which is a public
    /// field on <c>QuickMenuManager</c> (reached through <c>PlayerControllerB.quickMenuManager</c>).
    /// Fields cannot be setter-hooked by Harmony; see <see cref="WriterAttributionTargets"/>.</item>
    /// <item>Crosshair: <c>PlayerControllerB.SetHoverTipAndCurrentInteractTrigger</c> returns
    /// immediately when <c>isGrabbingObjectAnimation</c>, <c>inSpecialMenu</c> or
    /// <c>quickMenuManager.isMenuOpen</c> is set, and later disables <c>cursorIcon</c> when
    /// <c>inSpecialInteractAnimation</c> is set and the hovered trigger is a
    /// <c>specialCharacterAnimation</c>. That is the crosshair-loss path.</item>
    /// <item>Interact: the same method is what assigns <c>hoveringOverTrigger</c>, so a stuck
    /// <c>inSpecialInteractAnimation</c>/<c>isGrabbingObjectAnimation</c> silently kills E.
    /// <c>InteractTriggerUseConditionsMet</c> is the only reader of <c>disableInteract</c>.</item>
    /// <item>Discard: <c>Discard_performed</c> bails on <c>isGrabbingObjectAnimation</c>,
    /// <c>activatingItem</c> and <c>inSpecialInteractAnimation</c>. The bound InputAction is
    /// named <b>"Discard"</b>, not "DiscardHeldObject" (that is the PlayerControllerB method);
    /// verified from <c>PlayerActions..ctor</c> / <c>PlayerControllerB.OnEnable</c> IL.</item>
    /// <item>Movement/look: <c>PlayerControllerB.Update</c> zeroes <c>moveInputVector</c> when
    /// <c>disableMoveInput</c> or <c>inSpecialInteractAnimation</c> is set (unless
    /// <c>isClimbingLadder</c>/<c>inShockingMinigame</c>); <c>PlayerLookInput</c> reads
    /// <c>inSpecialInteractAnimation</c> the same way.</item>
    /// <item><b>No vanilla writer clears <c>inSpecialInteractAnimation</c> on death.</b>
    /// <c>PlayerControllerB.KillPlayer</c> does not touch the field in v81 IL, which matches the
    /// observed "death did not clear it". It is hooked anyway so the log shows the death
    /// happening while the flag survived.</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// The type is public only so that other plugins can reach
    /// <see cref="PushExternalMenuLatch"/>/<see cref="PopExternalMenuLatch"/>; every other member
    /// stays internal or private, and nothing here ever mutates game state.
    /// </remarks>
    public static class LocalInputStateWatchdog
    {
        internal const string LogTag = "[InputWatchdog]";

        /// <summary>
        /// Seconds a monitored flag may stay set before it counts as stuck. The seated-station
        /// exception lives in <see cref="LocalInputStuckThreshold"/>, which owns both numbers.
        /// </summary>
        private const float StuckSeconds = LocalInputStuckThreshold.StuckSeconds;

        /// <summary>
        /// Gap, in seconds, between successive warns within one stuck episode. The first warn is
        /// the full-detail rising edge at <see cref="StuckSeconds"/>; every later one waits the
        /// next entry here, and the last entry repeats forever. A single unresolved episode
        /// therefore costs 5 lines in its first ~75s and then one line per 40s — comfortably
        /// under the "at most one line per 30s" ceiling #507 asks for, instead of the old flat
        /// 1 Hz stream. The ladder is episode-scoped: it resets whenever the set of anomalous
        /// flags changes, so a genuinely new fault is still reported in full within 3s.
        /// </summary>
        private static readonly float[] WarnBackoffSeconds = { 5f, 10f, 20f, 40f };

        /// <summary>Throttle on the Info recovery line and on the rising-edge stack capture.</summary>
        private const float ClearLogIntervalSeconds = 1f;
        private const float TerminalLookupIntervalSeconds = 1f;
        private const float EdgeCaptureIntervalSeconds = 15f;
        private const int MaxEdgeCapturesPerSession = 12;

        /// <summary>How many recent writer-attribution events the warn line replays.</summary>
        private const int WriterRingSize = 8;

        private const int FlagInSpecialInteractAnimation = 0;
        private const int FlagEnteringSpecialAnimation = 1;
        private const int FlagIsGrabbingObjectAnimation = 2;
        private const int FlagDisableInteract = 3;
        private const int FlagDisableMoveInput = 4;
        private const int FlagDisableLookInput = 5;
        private const int FlagActivatingItem = 6;
        private const int FlagTwoHanded = 7;
        private const int FlagInTerminalMenu = 8;
        private const int FlagIsMenuOpen = 9;
        private const int FlagCount = 10;

        /// <summary>
        /// #1087: the four flags a seated operator station (the CCTV monitor desk) holds together
        /// for the whole session, which is minutes, not seconds. Asserted as a set they get
        /// <see cref="LocalInputStuckThreshold.SeatedStationStuckSeconds"/>; any partial subset
        /// keeps the 3s threshold, so a flag left behind after the station releases the player is
        /// still reported promptly. Mask only — no dependency on whichever plugin seats them.
        /// </summary>
        private const int SeatedStationMask =
            (1 << FlagInSpecialInteractAnimation)
            | (1 << FlagDisableInteract)
            | (1 << FlagDisableMoveInput)
            | (1 << FlagDisableLookInput);

        private static readonly string[] FlagNames =
        {
            "inSpecialInteractAnimation",
            "enteringSpecialAnimation",
            "isGrabbingObjectAnimation",
            "disableInteract",
            "disableMoveInput",
            "disableLookInput",
            "activatingItem",
            "twoHanded",
            "inTerminalMenu",
            "isMenuOpen"
        };

        // Quiet-path state. Fixed-size arrays allocated once; the per-frame path only reads
        // fields, compares floats and writes into these, so a session with every flag clear
        // allocates nothing after Initialize.
        private static readonly bool[] _current = new bool[FlagCount];
        private static readonly bool[] _wasTrue = new bool[FlagCount];
        private static readonly float[] _trueSince = new float[FlagCount];
        private static readonly bool[] _reportedStuck = new bool[FlagCount];
        private static readonly bool[] _legitimate = new bool[FlagCount];

        private static readonly WriterEvent[] _writerRing = new WriterEvent[WriterRingSize];
        private static int _writerRingNext;
        private static int _writerRingCount;

        private static ManualLogSource _log;
        // #694: always on; retired from the config surface. The watchdog costs nothing
        // while every flag is clear, so there was no reason for players to touch it.
        private static ConfigEntry<bool> _enabled;
        /// <summary>
        /// Per flag: true while that flag's stuck state was actually reported at Warning.
        /// The recovery line matches it — a Warning episode is worth an Info recovery, a
        /// landing-window Debug episode is not. Per flag rather than one global, because
        /// <see cref="LogCleared"/> reports one named flag and ten can be stuck at once, and
        /// because <c>_reportedStuck</c> is set even on passes where the backoff throttles the
        /// report away, which must not leave a flag claiming a Warning that never printed.
        /// </summary>
        private static readonly bool[] _stuckReportedAtWarning = new bool[FlagCount];
        private static bool _initialized;
        private static bool _anyTracked;

        private static float _nextWarnAt;
        private static int _warnEpisodeMask;
        private static int _warnEpisodeStep;
        private static float _nextClearLogAt;
        private static int _suppressedClearLines;

        /// <summary>
        /// #1087: true while every <see cref="SeatedStationMask"/> flag is asserted, and the time
        /// that stopped being true. The hold timers in <see cref="_trueSince"/> are per flag and
        /// keep pointing at the start of the seated session, so this is what a flag left behind
        /// after the station releases the player is timed from.
        /// </summary>
        private static bool _seatedStationMaskComplete;
        private static float _seatedStationMaskReleasedAt = float.NegativeInfinity;

        /// <summary>
        /// Opt-in "some non-vanilla UI owns input right now" counter, driven by
        /// <see cref="PushExternalMenuLatch"/>/<see cref="PopExternalMenuLatch"/>.
        /// </summary>
        private static int _externalMenuLatches;
        private static float _lastEdgeCaptureAt = float.NegativeInfinity;
        private static int _edgeCaptures;

        // Cached lookup handles. All rebuilt on demand and only from the (throttled) warn path,
        // never per frame.
        private static InputActionAsset _actionAsset;
        private static InputAction _interactAction;
        private static InputAction _discardAction;
        private static InputAction _activateItemAction;
        private static InputAction _switchItemAction;
        private static Terminal _terminal;
        private static float _nextTerminalLookupAt;

        /// <summary>
        /// Label per hooked writer method, so the postfix allocates nothing after the first call
        /// for a given target.
        /// </summary>
        private static readonly Dictionary<MethodBase, string> _writerLabels =
            new Dictionary<MethodBase, string>();

        private static StringBuilder _builder;

        /// <summary>
        /// The writer methods this hooks for <c>inSpecialInteractAnimation</c> attribution.
        ///
        /// Harmony cannot hook a field store, and the flag is a field in every shipped build
        /// (verified above), so the "setter postfix" the issue asks for is impossible. What is
        /// possible is this enumerable table of known writers, each given a shared postfix that
        /// records "this ran, and here is the local player's flag value afterwards". Combined
        /// with the rising-edge stack capture it names the culprit in the common cases.
        ///
        /// Entries are resolved by name and every miss degrades to one Debug line, so a vanilla
        /// rename or a refactor in our own code cannot break plugin load. Compiler-generated
        /// iterator writers (<c>InteractTrigger/&lt;specialInteractAnimation&gt;d__68.MoveNext</c>,
        /// <c>PlayerControllerB/&lt;timeSpecialAnimation&gt;d__489.MoveNext</c>,
        /// <c>PestControlLeverPlayerAnimation/&lt;HoldRoutine&gt;.MoveNext</c>) are deliberately
        /// not hooked: their names are unstable across compilations and a per-MoveNext postfix
        /// runs every frame of the coroutine.
        ///
        /// <para><b>Vanilla only, since #612 task 1.4.</b> This array used to carry six more
        /// entries naming <c>PestControlLeverPlayerAnimation</c>, three <c>BundyVictim*</c>
        /// types and <c>ShipSystemsController</c> — that is, one static field with compile-time
        /// <c>typeof</c> references to all three future DLLs at once, inside a type the plan
        /// moves to Core, where none of those types exist. Those six now arrive through
        /// <see cref="InputWatchdogWriterRegistry"/>, which each feature calls at startup. The
        /// vanilla entries stay here because Assembly-CSharp is referenced by every assembly
        /// including Core, so there is nothing to invert, and because the "verified against v81
        /// IL" reasoning above belongs next to the list it justifies.</para>
        /// </summary>
        private static readonly (Type Owner, string TypeName, string MethodName)[] WriterAttributionTargets =
        {
            // Vanilla writers (Assembly-CSharp v81).
            (typeof(PlayerControllerB), null, "IsInSpecialAnimationClientRpc"),
            (typeof(PlayerControllerB), null, "SpawnPlayerAnimation"),
            // Does NOT write the flag; hooked so the log proves death happened and it survived.
            (typeof(PlayerControllerB), null, "KillPlayer"),
            (typeof(InteractTrigger), null, "StopSpecialAnimation"),
            (typeof(InteractTrigger), null, "CancelLadderAnimation"),
            (typeof(EnemyAI), null, "CancelSpecialAnimationWithPlayer"),
        };

        /// <summary>
        /// The Harmony instance feature-registered writers are patched with, captured at
        /// <see cref="Initialize"/>.
        ///
        /// <para>While the build is monolithic this is Contracted's instance for every feature,
        /// exactly as before. After the split each plugin patches its own writers under its own
        /// owner id, and this field becomes the one Core holds for whichever plugin owns the
        /// watchdog — at which point a feature registering a writer it wants unpatched with its
        /// own DLL needs to pass its own Harmony. That is a deliberate follow-up, not an
        /// oversight: today all six writers are unpatched by the same
        /// <c>UnpatchSelf()</c> that unpatched them before this change, so nothing regresses.</para>
        /// </summary>
        private static Harmony _writerAttributionHarmony;

        private struct WriterEvent
        {
            internal float Time;
            internal int Frame;
            internal string Label;
            internal bool FlagAfter;
        }

        internal static bool Enabled => _enabled?.Value ?? false;

        /// <summary>
        /// Declares that a mod-owned menu is open and legitimately holds
        /// <c>QuickMenuManager.isMenuOpen</c> set. The watchdog's vanilla ground truth is
        /// <c>menuContainer.activeSelf</c>, which a custom canvas never touches, so without this
        /// an open modded UI reads as a stuck flag (#507: 108 purchase-menu false positives in
        /// one session).
        ///
        /// Pair every call with exactly one <see cref="PopExternalMenuLatch"/>, ideally in the
        /// same open/close pair that toggles the canvas. Balancing is the caller's job; a leaked
        /// latch only blinds this one report, never gameplay. Purely observational: nothing here
        /// writes to the player, the quick menu or the cursor.
        /// </summary>
        public static void PushExternalMenuLatch()
        {
            _externalMenuLatches++;
        }

        /// <summary>Releases one <see cref="PushExternalMenuLatch"/>. Never goes negative.</summary>
        public static void PopExternalMenuLatch()
        {
            if (_externalMenuLatches > 0)
                _externalMenuLatches--;
        }

        internal static void Initialize(ConfigFile config, ManualLogSource log, Harmony harmony)
        {
            if (_initialized)
                return;

            _log = log;

            // #612 task 1.4: registered before the config bind and before the enable check,
            // because the pre-split host called Tick() every frame regardless of both. Tick()
            // self-guards on _initialized and _enabled, so registering unconditionally is exact
            // parity, and the registry's order table is what keeps this last in the frame.
            // Owner is Contracted because Contracted initialises the watchdog today and its host
            // is what tears it down. The plan moves this file to Y4NGZCore, which has no host of
            // its own — at that point the owner becomes whichever plugin calls Initialize, and
            // this argument is where that decision gets recorded.
            Y4NGZCore.Lifecycle.ModuleTickRegistry.RegisterTick(
                Y4NGZCore.Lifecycle.ModuleHarmonyIds.Contracted,
                Y4NGZCore.Lifecycle.ModuleTickOrder.LocalInputStateWatchdog,
                "LocalInputStateWatchdog",
                Tick);

            // A real bind, not a constant. The constant it replaces made the "Disabled by
            // config" branch below unreachable. The key is new on purpose: the old name was
            // never written to any .cfg, so binding it now would be binding whatever a hand
            // edit happened to leave there.
            _enabled = config?.Bind(
                Y4NGZCompany.Core.ContractedConfigSections.Diagnostics,
                "Local Input State Watchdog Enabled",
                true,
                "Watches the local player's input flags and reports only when one stays set long "
                + "enough to mean the player has genuinely lost interact, movement or look "
                + "control. Costs ten field reads per frame while nothing is wrong. Replaces an "
                + "older watchdog key that was never bound and so never had any effect.");

            _initialized = true;

            // Applied before the enable check, and unconditionally. Tick() re-reads the key
            // live, so the watchdog can be switched on mid-session through a config manager —
            // but the writer ring is what names the culprit, and a ring that only started
            // filling at the moment the key flipped would hand that session reports with empty
            // writers[]. The patches are postfix-only bookkeeping into a fixed-size ring, so
            // paying for them while the watchdog is off is cheaper than the report being
            // useless when it is turned on. Same argument as the tick registration above.
            ApplyWriterAttributionPatches(harmony);

            if (_enabled == null || !_enabled.Value)
            {
                _log?.LogInfo($"{LogTag} Disabled by config; no flags are monitored.");
                return;
            }

            _log?.LogInfo(
                $"{LogTag} Monitoring {FlagCount} local-player input flags "
                + $"(stuck threshold {StuckSeconds:0.#}s, then backing off to one line per "
                + $"{WarnBackoffSeconds[WarnBackoffSeconds.Length - 1]:0.#}s per episode).");
        }

        internal static void Shutdown()
        {
            ResetTracking();
            _actionAsset = null;
            _interactAction = null;
            _discardAction = null;
            _activateItemAction = null;
            _switchItemAction = null;
            _terminal = null;
            _nextTerminalLookupAt = 0f;
            _writerRingNext = 0;
            _writerRingCount = 0;

            // #612 task 1.4: drop the late-registration subscription. The Harmony patches
            // themselves are removed by the owner's UnpatchSelf, but a live subscription would
            // keep this static type reachable from Core's registry and would try to patch
            // through a Harmony instance that has already unpatched itself.
            InputWatchdogWriterRegistry.TargetRegistered -= OnWriterTargetRegistered;
            _writerAttributionHarmony = null;
        }

        /// <summary>
        /// One pass per LateUpdate. Quiet path: ten field reads, ten bool compares, no
        /// allocation and no string work.
        /// </summary>
        internal static void Tick()
        {
            if (!_initialized || _enabled == null || !_enabled.Value)
                return;

            var round = StartOfRound.Instance;
            var player = round != null ? round.localPlayerController : null;
            if (player == null)
            {
                // Main menu, or between scene loads. Nothing to compare against, and a stale
                // timer would fire a bogus warn the moment the player respawns.
                if (_anyTracked)
                    ResetTracking();
                return;
            }

            var quickMenu = player.quickMenuManager;
            float now = Time.realtimeSinceStartup;

            _current[FlagInSpecialInteractAnimation] = player.inSpecialInteractAnimation;
            _current[FlagEnteringSpecialAnimation] = player.enteringSpecialAnimation;
            _current[FlagIsGrabbingObjectAnimation] = player.isGrabbingObjectAnimation;
            _current[FlagDisableInteract] = player.disableInteract;
            _current[FlagDisableMoveInput] = player.disableMoveInput;
            _current[FlagDisableLookInput] = player.disableLookInput;
            _current[FlagActivatingItem] = player.activatingItem;
            _current[FlagTwoHanded] = player.twoHanded;
            _current[FlagInTerminalMenu] = player.inTerminalMenu;
            _current[FlagIsMenuOpen] = quickMenu != null && quickMenu.isMenuOpen;

            // #1087: seated-station mask bookkeeping, before the per-flag pass that uses it.
            int assertedMask = 0;
            for (int i = 0; i < FlagCount; i++)
            {
                if (_current[i])
                    assertedMask |= 1 << i;
            }

            bool seatedStationMaskComplete =
                LocalInputStuckThreshold.IsSeatedStationMask(assertedMask, SeatedStationMask);
            if (seatedStationMaskComplete)
                _seatedStationMaskReleasedAt = float.NegativeInfinity;
            else if (_seatedStationMaskComplete)
                _seatedStationMaskReleasedAt = now;
            _seatedStationMaskComplete = seatedStationMaskComplete;
            float secondsSinceSeatedRelease = now - _seatedStationMaskReleasedAt;

            bool anyTrue = false;
            bool anyStuck = false;
            bool risingSpecialInteract = false;

            for (int i = 0; i < FlagCount; i++)
            {
                if (_current[i])
                {
                    anyTrue = true;
                    if (!_wasTrue[i])
                    {
                        _wasTrue[i] = true;
                        _trueSince[i] = now;
                        if (i == FlagInSpecialInteractAnimation)
                            risingSpecialInteract = true;
                        continue;
                    }

                    if (LocalInputStuckThreshold.IsStuck(
                            (SeatedStationMask & (1 << i)) != 0,
                            seatedStationMaskComplete,
                            now - _trueSince[i],
                            secondsSinceSeatedRelease))
                    {
                        anyStuck = true;
                    }
                }
                else if (_wasTrue[i])
                {
                    float duration = now - _trueSince[i];
                    _wasTrue[i] = false;
                    _trueSince[i] = 0f;
                    if (_reportedStuck[i])
                    {
                        _reportedStuck[i] = false;
                        LogCleared(i, duration, now);
                    }
                }
            }

            _anyTracked = anyTrue;

            if (risingSpecialInteract)
                CaptureSpecialInteractRisingEdge(now);

            if (!anyStuck)
                return;

            // From here on a flag has genuinely been held for >3s, so string work is fair game.
            // This branch runs at most once per frame during an episode and the log line itself
            // is on the backoff ladder below.
            bool terminalUiOpen = ResolveTerminalInUse(now);
            int anomalousMask = 0;
            int longestIndex = -1;
            float longestDuration = 0f;
            for (int i = 0; i < FlagCount; i++)
            {
                _legitimate[i] = _current[i] && IsLegitimateContext(i, player, quickMenu, terminalUiOpen);
                if (!_current[i] || _legitimate[i])
                    continue;

                float duration = now - _trueSince[i];
                if (!LocalInputStuckThreshold.IsStuck(
                        (SeatedStationMask & (1 << i)) != 0,
                        seatedStationMaskComplete,
                        duration,
                        secondsSinceSeatedRelease))
                {
                    continue;
                }

                anomalousMask |= 1 << i;
                _reportedStuck[i] = true;
                // Cleared here and set only by an EmitStuckWarning that actually printed at
                // Warning, so a pass the backoff throttles below leaves this false.
                _stuckReportedAtWarning[i] = false;
                // Longest among the *anomalous* flags: a legitimately two-handed shovel carried
                // for five minutes would otherwise always win and bury the real signal.
                if (duration > longestDuration)
                {
                    longestDuration = duration;
                    longestIndex = i;
                }
            }

            if (anomalousMask == 0)
            {
                // Everything still set is legitimate: the episode is over as far as reporting is
                // concerned, so the next genuine fault starts the ladder from the top again.
                _warnEpisodeMask = 0;
                _warnEpisodeStep = 0;
                return;
            }

            if (anomalousMask != _warnEpisodeMask)
            {
                // A different set of flags is stuck than the one being backed off. That is new
                // information, so report it at full cadence rather than at the old ladder rung.
                _warnEpisodeMask = anomalousMask;
                _warnEpisodeStep = 0;
                _nextWarnAt = now;
            }

            if (now < _nextWarnAt)
            {
                // Folded into the Recovered line's "(+N throttled)" tail, so the log still says
                // how much of the episode went unprinted.
                _suppressedClearLines++;
                return;
            }

            int step = _warnEpisodeStep;
            _warnEpisodeStep = Math.Min(step + 1, WarnBackoffSeconds.Length);
            _nextWarnAt = now + WarnBackoffSeconds[Math.Min(step, WarnBackoffSeconds.Length - 1)];
            // Vanilla's landing and leaving sequences hold disableLookInput (and friends) for
            // the whole cinematic, which is well past the stuck threshold. That is not a fault,
            // and warning on it every single landing trains the reader to ignore the line. The
            // window is "a round is in progress but the ship has not settled": between leaving
            // orbit and shipHasLanded, and again from the moment it lifts off. Inside it the
            // same report is still emitted, at Debug, so a genuine fault that starts during
            // landing is still on record.
            bool landingSequence = round != null && !round.inShipPhase && !round.shipHasLanded;
            EmitStuckWarning(player, quickMenu, now, longestIndex, longestDuration, terminalUiOpen, landingSequence, anomalousMask);
        }

        /// <summary>
        /// Records that a known writer of <c>inSpecialInteractAnimation</c> just ran. Shared by
        /// every entry in <see cref="WriterAttributionTargets"/>; <c>__originalMethod</c> is what
        /// makes one postfix enough.
        /// </summary>
        private static void WriterAttributionPostfix(MethodBase __originalMethod)
        {
            try
            {
                var round = StartOfRound.Instance;
                var player = round != null ? round.localPlayerController : null;

                _writerRing[_writerRingNext] = new WriterEvent
                {
                    Time = Time.realtimeSinceStartup,
                    Frame = Time.frameCount,
                    Label = DescribeMethod(__originalMethod),
                    FlagAfter = player != null && player.inSpecialInteractAnimation,
                };
                _writerRingNext = (_writerRingNext + 1) % WriterRingSize;
                if (_writerRingCount < WriterRingSize)
                    _writerRingCount++;
            }
            catch
            {
                // A diagnostic must never be able to break the method it observes.
            }
        }

        private static void ApplyWriterAttributionPatches(Harmony harmony)
        {
            if (harmony == null)
                return;

            _writerAttributionHarmony = harmony;

            int patched = 0;
            int missed = 0;

            foreach (var target in WriterAttributionTargets)
            {
                if (TryPatchWriter(
                        harmony,
                        target.Owner,
                        target.TypeName,
                        target.MethodName,
                        "vanilla"))
                {
                    patched++;
                }
                else
                {
                    missed++;
                }
            }

            // #612 task 1.4: drain whatever features registered before the watchdog initialised,
            // then keep hooking new registrations as they arrive. The watchdog is deliberately
            // initialised ahead of every module (Plugin.Awake), so in practice every feature
            // registration arrives through the event — but draining first makes the order
            // irrelevant, which is what a registry has to guarantee if a future plugin's Awake
            // ever runs before this one's.
            var registered = InputWatchdogWriterRegistry.Targets;
            for (int i = 0; i < registered.Count; i++)
            {
                if (TryPatchWriter(
                        harmony,
                        registered[i].Owner,
                        null,
                        registered[i].MethodName,
                        registered[i].FeatureLabel))
                {
                    patched++;
                }
                else
                {
                    missed++;
                }
            }

            InputWatchdogWriterRegistry.TargetRegistered += OnWriterTargetRegistered;

            _log?.LogInfo(
                $"{LogTag} inSpecialInteractAnimation is a public field, so no setter hook exists; "
                + $"attribution runs off {patched} known writer method(s) ({missed} unavailable) plus a "
                + "rising-edge stack capture.");
        }

        /// <summary>
        /// Hooks a writer a feature registered after the watchdog went live. Silent on success:
        /// the startup line above already states the count, and one line per late registration
        /// would be noise in the log of every session.
        /// </summary>
        private static void OnWriterTargetRegistered(InputWatchdogWriterTarget target)
        {
            Harmony harmony = _writerAttributionHarmony;
            if (harmony == null)
                return;

            TryPatchWriter(harmony, target.Owner, null, target.MethodName, target.FeatureLabel);
        }

        /// <summary>
        /// Resolve and postfix one writer method. Returns false, having logged one Debug line,
        /// for every reason a target can be unavailable — a vanilla rename, a type that did not
        /// load, an abstract or generic method, or Harmony refusing the patch. A diagnostic
        /// hook that cannot be applied must never fail the load of the feature that asked
        /// for it.
        /// </summary>
        private static bool TryPatchWriter(
            Harmony harmony,
            Type owner,
            string typeName,
            string methodName,
            string sourceLabel)
        {
            try
            {
                Type resolved = owner ?? AccessTools.TypeByName(typeName);
                MethodInfo method = resolved != null ? AccessTools.Method(resolved, methodName) : null;
                if (method == null || method.IsAbstract || method.ContainsGenericParameters)
                {
                    _log?.LogDebug(
                        $"{LogTag} Writer-attribution hook target not found ({sourceLabel}): "
                        + $"{typeName ?? owner?.FullName}.{methodName}");
                    return false;
                }

                harmony.Patch(
                    method,
                    postfix: new HarmonyMethod(
                        AccessTools.Method(
                            typeof(LocalInputStateWatchdog),
                            nameof(WriterAttributionPostfix))));
                return true;
            }
            catch (Exception ex)
            {
                _log?.LogDebug(
                    $"{LogTag} Writer-attribution hook failed ({sourceLabel}) for "
                    + $"{typeName ?? owner?.FullName}.{methodName}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Ground truth for "this flag being set is normal right now". Where a cheap ground-truth
        /// read exists the flag is suppressed outright; where it does not (notably
        /// <c>disableInteract</c>, which only <c>InteractTriggerUseConditionsMet</c> ever reads
        /// and which nothing in vanilla writes) the flag still warns and the context block below
        /// carries what the reader needs to dismiss it.
        /// </summary>
        private static bool IsLegitimateContext(
            int index,
            PlayerControllerB player,
            QuickMenuManager quickMenu,
            bool terminalUiOpen)
        {
            switch (index)
            {
                case FlagInSpecialInteractAnimation:
                    // Driving the terminal holds this set for as long as the player stays in it,
                    // exactly as it holds inTerminalMenu (:FlagInTerminalMenu) and the move/look
                    // gates below. Same two-part test as those: the player's own flag plus the
                    // Terminal's ground truth, so a stranded inTerminalMenu still warns.
                    return player.isClimbingLadder
                        || player.inShockingMinigame
                        || player.inVehicleAnimation
                        || player.inAnimationWithEnemy != null
                        || (player.inTerminalMenu && terminalUiOpen);

                case FlagEnteringSpecialAnimation:
                    return player.inVehicleAnimation || player.inAnimationWithEnemy != null;

                case FlagIsGrabbingObjectAnimation:
                    // The vanilla grab animation is well under a second; >3s is always wrong.
                    return false;

                case FlagDisableInteract:
                    // No cheap ground truth: nothing in vanilla writes it, so every setter is a
                    // mod. Always warn and let the context block resolve it.
                    return false;

                case FlagDisableMoveInput:
                case FlagDisableLookInput:
                    return player.isTypingChat
                        || player.inSpecialMenu
                        || player.isClimbingLadder
                        || player.inShockingMinigame
                        || player.inVehicleAnimation
                        || player.inAnimationWithEnemy != null
                        || (quickMenu != null && quickMenu.isMenuOpen)
                        || (player.inTerminalMenu && terminalUiOpen);

                case FlagActivatingItem:
                    return player.inShockingMinigame
                        || (player.currentlyHeldObjectServer != null && player.currentlyHeldObjectServer.isBeingUsed);

                case FlagTwoHanded:
                    return player.currentlyHeldObjectServer != null
                        && player.currentlyHeldObjectServer.itemProperties != null
                        && player.currentlyHeldObjectServer.itemProperties.twoHanded;

                case FlagInTerminalMenu:
                    return terminalUiOpen;

                case FlagIsMenuOpen:
                    // menuContainer.activeSelf is the vanilla pause menu only; a mod canvas that
                    // sets isMenuOpen to take the cursor leaves it false. Two escape hatches:
                    // the explicit latch, and a visible cursor, which in this game means some UI
                    // genuinely owns input (nothing sets isMenuOpen and re-locks the cursor).
                    // Neither holding still means the flag is stuck and still warns.
                    return (quickMenu != null
                            && quickMenu.menuContainer != null
                            && quickMenu.menuContainer.activeSelf)
                        || _externalMenuLatches > 0
                        || Cursor.lockState == CursorLockMode.None;

                default:
                    return false;
            }
        }

        private static void EmitStuckWarning(
            PlayerControllerB player,
            QuickMenuManager quickMenu,
            float now,
            int longestIndex,
            float longestDuration,
            bool terminalUiOpen,
            bool landingSequence,
            int anomalousMask)
        {
            var builder = _builder ?? (_builder = new StringBuilder(768));
            builder.Length = 0;

            builder.Append(LogTag)
                .Append(" Local input state stuck: longest ")
                .Append(longestIndex >= 0 ? FlagNames[longestIndex] : "<none>")
                .Append('=')
                .Append(longestDuration.ToString("0.0"))
                .Append("s. flags[");

            bool first = true;
            for (int i = 0; i < FlagCount; i++)
            {
                if (!_current[i])
                    continue;

                if (!first)
                    builder.Append(", ");
                first = false;

                builder.Append(FlagNames[i])
                    .Append('=')
                    .Append((now - _trueSince[i]).ToString("0.0"))
                    .Append('s');
                if (_legitimate[i])
                    builder.Append("(legit)");
            }

            builder.Append("] held=");
            AppendHeldItem(builder, player);

            builder.Append(" actions[");
            AppendActionStates(builder);

            builder.Append("] ctx[dead=").Append(player.isPlayerDead)
                .Append(" ladder=").Append(player.isClimbingLadder)
                .Append(" vehicle=").Append(player.inVehicleAnimation)
                .Append(" enemyAnim=").Append(player.inAnimationWithEnemy != null ? player.inAnimationWithEnemy.GetType().Name : "none")
                .Append(" shocking=").Append(player.inShockingMinigame)
                .Append(" specialMenu=").Append(player.inSpecialMenu)
                .Append(" typingChat=").Append(player.isTypingChat)
                .Append(" terminalInUse=").Append(terminalUiOpen)
                .Append(" quickMenuContainer=").Append(quickMenu != null && quickMenu.menuContainer != null && quickMenu.menuContainer.activeSelf)
                .Append(" extMenuLatches=").Append(_externalMenuLatches)
                .Append(" cursorLock=").Append(Cursor.lockState)
                .Append(" landingSequence=").Append(landingSequence)
                .Append(" sinking=").Append(player.sinkingValue.ToString("0.00"))
                .Append("] upgrades[");
            Y4NGZUpgradesInputProbe.Append(builder, player);

            builder.Append("] writers[");
            AppendWriterRing(builder, now);

            builder.Append("] frame=").Append(Time.frameCount);

            // Every flag this line reported as anomalous carries the level it was reported at,
            // so each one's own recovery matches its own episode.
            for (int i = 0; i < FlagCount; i++)
            {
                if ((anomalousMask & (1 << i)) != 0)
                    _stuckReportedAtWarning[i] = !landingSequence;
            }

            if (landingSequence)
                _log?.LogDebug(builder.ToString());
            else
                _log?.LogWarning(builder.ToString());
        }

        private static void AppendHeldItem(StringBuilder builder, PlayerControllerB player)
        {
            var held = player.currentlyHeldObjectServer;
            if (held == null)
            {
                builder.Append("none");
                return;
            }

            var properties = held.itemProperties;
            builder.Append('\'')
                .Append(properties != null && !string.IsNullOrEmpty(properties.itemName)
                    ? properties.itemName
                    : held.GetType().Name)
                .Append("'(twoHandedItem=")
                .Append(properties != null && properties.twoHanded)
                .Append(" isBeingUsed=")
                .Append(held.isBeingUsed)
                .Append(')');
        }

        private static void AppendActionStates(StringBuilder builder)
        {
            ResolveInputActions();
            AppendActionState(builder, "Interact", _interactAction);
            builder.Append(' ');
            // "Discard", not "DiscardHeldObject": verified against PlayerActions..ctor IL.
            AppendActionState(builder, "Discard", _discardAction);
            builder.Append(' ');
            AppendActionState(builder, "ActivateItem", _activateItemAction);
            builder.Append(' ');
            AppendActionState(builder, "SwitchItem", _switchItemAction);
        }

        private static void AppendActionState(StringBuilder builder, string name, InputAction action)
        {
            builder.Append(name).Append('=');
            if (action == null)
            {
                builder.Append("unresolved");
                return;
            }

            builder.Append(action.enabled ? "enabled" : "DISABLED");
        }

        private static void AppendWriterRing(StringBuilder builder, float now)
        {
            if (_writerRingCount == 0)
            {
                builder.Append("none seen");
                return;
            }

            int start = (_writerRingNext - _writerRingCount + WriterRingSize) % WriterRingSize;
            for (int offset = 0; offset < _writerRingCount; offset++)
            {
                if (offset > 0)
                    builder.Append(", ");

                ref var entry = ref _writerRing[(start + offset) % WriterRingSize];
                builder.Append(entry.Label)
                    .Append(" -")
                    .Append((now - entry.Time).ToString("0.0"))
                    .Append("s(f")
                    .Append(entry.Frame)
                    .Append(" flagAfter=")
                    .Append(entry.FlagAfter)
                    .Append(')');
            }
        }

        private static void LogCleared(int index, float duration, float now)
        {
            if (now < _nextClearLogAt)
            {
                _suppressedClearLines++;
                return;
            }

            _nextClearLogAt = now + ClearLogIntervalSeconds;
            string suffix = _suppressedClearLines > 0
                ? $" (+{_suppressedClearLines} throttled)"
                : string.Empty;
            _suppressedClearLines = 0;

            // A recovery is only news at Info when the episode it closes was news at Warning.
            // A landing-window episode reported at Debug gets a Debug recovery to match.
            string recovered =
                $"{LogTag} Recovered: {FlagNames[index]} cleared after {duration:0.0}s stuck "
                + $"(frame={Time.frameCount}, t={now:0.0}){suffix}";
            if (_stuckReportedAtWarning[index])
                _log?.LogInfo(recovered);
            else
                _log?.LogDebug(recovered);
            _stuckReportedAtWarning[index] = false;
        }

        /// <summary>
        /// Fires on the false-&gt;true edge of <c>inSpecialInteractAnimation</c>.
        ///
        /// The stack this captures is the watchdog's own LateUpdate frame, <b>not</b> the writer:
        /// the field store already happened, possibly frames earlier and possibly inside a
        /// coroutine that is no longer on any stack. It is logged because the caller chain into
        /// the tick still distinguishes "the host tick observed it" from a nested reentry, and
        /// because the writer ring beside it is what actually names the culprit.
        /// </summary>
        private static void CaptureSpecialInteractRisingEdge(float now)
        {
            if (_edgeCaptures >= MaxEdgeCapturesPerSession || now - _lastEdgeCaptureAt < EdgeCaptureIntervalSeconds)
                return;

            _lastEdgeCaptureAt = now;
            _edgeCaptures++;

            var builder = _builder ?? (_builder = new StringBuilder(768));
            builder.Length = 0;
            builder.Append(LogTag)
                .Append(" inSpecialInteractAnimation false->true (frame=")
                .Append(Time.frameCount)
                .Append(", t=")
                .Append(now.ToString("0.00"))
                .Append("). writers[");
            AppendWriterRing(builder, now);
            builder.Append("] observer stack: ");

            try
            {
                builder.Append(new StackTrace(true).ToString());
            }
            catch (Exception ex)
            {
                builder.Append("<unavailable: ").Append(ex.Message).Append('>');
            }

            _log?.LogInfo(builder.ToString());
        }

        private static void ResolveInputActions()
        {
            var settings = IngamePlayerSettings.Instance;
            var input = settings != null ? settings.playerInput : null;
            var asset = input != null ? input.actions : null;
            if (asset == null)
            {
                _actionAsset = null;
                _interactAction = null;
                _discardAction = null;
                _activateItemAction = null;
                _switchItemAction = null;
                return;
            }

            if (ReferenceEquals(asset, _actionAsset))
                return;

            _actionAsset = asset;
            _interactAction = asset.FindAction("Interact", false);
            _discardAction = asset.FindAction("Discard", false);
            _activateItemAction = asset.FindAction("ActivateItem", false);
            _switchItemAction = asset.FindAction("SwitchItem", false);
        }

        /// <summary>
        /// FindObjectOfType is the only expensive call on the stuck path, and the stuck path runs
        /// every frame of an episode (only the warn line itself backs off). Cache the
        /// Terminal and retry at most once a second while it is missing, so a stuck episode in a
        /// scene without a Terminal cannot turn into a per-frame scene scan.
        /// </summary>
        private static bool ResolveTerminalInUse(float now)
        {
            if (_terminal == null && now >= _nextTerminalLookupAt)
            {
                _nextTerminalLookupAt = now + TerminalLookupIntervalSeconds;
                _terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
            }

            return _terminal != null && _terminal.terminalInUse;
        }

        private static string DescribeMethod(MethodBase method)
        {
            if (method == null)
                return "<unknown>";

            if (_writerLabels.TryGetValue(method, out string cached))
                return cached;

            var declaring = method.DeclaringType;
            string label = declaring != null ? declaring.Name + "." + method.Name : method.Name;
            _writerLabels[method] = label;
            return label;
        }

        private static void ResetTracking()
        {
            for (int i = 0; i < FlagCount; i++)
            {
                _current[i] = false;
                _wasTrue[i] = false;
                _trueSince[i] = 0f;
                _reportedStuck[i] = false;
                _legitimate[i] = false;
                _stuckReportedAtWarning[i] = false;
            }

            _anyTracked = false;
            _seatedStationMaskComplete = false;
            _seatedStationMaskReleasedAt = float.NegativeInfinity;
            _nextWarnAt = 0f;
            _warnEpisodeMask = 0;
            _warnEpisodeStep = 0;
            _nextClearLogAt = 0f;
            _suppressedClearLines = 0;
        }
    }
}
