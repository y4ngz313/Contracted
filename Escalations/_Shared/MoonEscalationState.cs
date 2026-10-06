using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
using Y4NGZCompany.Escalations.Congregation;
using Y4NGZCompany.Escalations.Bundy.AI;
namespace Y4NGZCompany.Escalations._Shared
{
    internal enum RuntimeEscalationKind
    {
        Unrolled = 0,
        None = 1,
        Stranger = 2,
        Congregation = 3,
        BlacksiteRetrieval = 4
    }

    internal static class MoonEscalationState
    {
        private const string MsgState = "lgu.contracts.escalation.blacksite.state";
        private const string MsgCrateOpenRequest = "lgu.contracts.escalation.blacksite.crate.request";
        private const string MsgCrateOpened = "lgu.contracts.escalation.blacksite.crate.opened";
        // #611 task 1.3: the bundled Bundy plugin used to be reached by the assembly-qualified
        // string "Y4NGZCompany.Bootstrap.BundyPlugin, Y4NGZCompany". Bundy and this escalation
        // are the same feature and land in the same assembly after the split, so the type is
        // referenced directly instead — the literal named the monolith and would have resolved
        // to nothing, silently turning The Stranger off. The standalone name below stays: that
        // one really is a separately shipped mod.
        private const string BundyStandalonePluginTypeName = "Bundy.BundyPlugin, Bundy";
        private const string BundyChainsawEnemyTypeFieldName = "ChainsawEnemyType";
        private const double StrangerSpawnCacheFrameBudgetMilliseconds = 2.0;

        private static readonly List<GameObject> SpawnedObjects = new List<GameObject>();

        /// <summary>Set when the round's reset ran at ship-leave and deliberately left the
        /// escalation's entities standing (#540). Cleared by whichever teardown runs first:
        /// <see cref="CompleteDeferredTeardown"/> at vanilla's end-of-round sweep, or a plain
        /// <see cref="ResetRunState"/> from the end-of-game RPC if the sweep never ran.</summary>
        private static bool _departureTeardownPending;
        private static NetworkManager _registeredNetworkManager;
        private static bool _networkHandlersRegistered;
        /// <summary>#716/G2: resends the current escalation state to a player who joined after it
        /// started. Serviced from <see cref="EnsureNetworkHandlers"/>, which runs every tick.</summary>
        private static readonly EscalationLateJoinResync _lateJoinResync =
            new EscalationLateJoinResync("[MoonContracts.Blacksite]", SendStateToClient);
        private static RuntimeEscalationKind _runtimeEscalation = RuntimeEscalationKind.Unrolled;
        private static bool _scheduled;
        private static bool _rollFailed;
        private static bool _active;
        private static bool _strangerScheduled;
        private static bool _strangerActive;
        private static bool _strangerComplete;
        private static bool _strangerFailed;
        private static float _strangerTriggerTime;
        private static float _strangerActiveStartTime;
        private static EnemyAI _strangerEnemy;
        private static PlayerControllerB _strangerTarget;
        // #725: the hunted player's index into allPlayerScripts, replicated in the state
        // payload so every client can tell whether it is the one being hunted. -1 = none.
        private static int _strangerTargetPlayerIndex = -1;
        private static Coroutine _strangerSpawnRoutine;
        private static AsyncInstantiateOperation<GameObject> _strangerInstantiateOperation;
        private static bool _strangerSpawnInProgress;
        private static StrangerSpawnPlacementCache _strangerSpawnPlacementCache;
        private static Coroutine _strangerSpawnPlacementCacheRoutine;
        private static bool _strangerBundyPrefabPrewarmed;
        private static bool _landingInProgress;
        private static bool _announcementShown;
        private static bool _complete;
        private static float _triggerTime;
        private static float _landingStartTime;
        private static float _blacksiteActiveStartTime;
        private static int _cratesTotal;
        private static int _cratesOpened;
        // #1343 S2: a Blacksite abort is decided on the host only; replicated so every peer shows
        // the band once, on the rising edge. The stamp keeps a late joiner from replaying an old abort.
        private static bool _blacksiteAborted;
        private static double _blacksiteAbortServerTime;
        private static string _blacksiteAbortReason = string.Empty;
        // #1305: the landing plan the search sealed, carried to the ship and its touchdown.
        private static BlacksiteLandingPlan _pendingPlan;
        private static Vector3 _pendingOutsideMain;
        private static Vector3 _pendingInsideMain;
        private static Vector3 _pendingInsideFireExit;
        private static Vector3 _pendingOutsideFireExit;
        private static Vector3[] _pendingInteriorClearRoute = Array.Empty<Vector3>();
        private static BlacksiteShipController _blacksiteShip;
        private static BlacksiteRetrievalMissionController _blacksiteMission;
        private static readonly List<BlacksiteSoldierAI> BlacksiteSoldiers = new List<BlacksiteSoldierAI>();
        private static float _nextExtractionCheckTime;

        internal static bool HasLivingBlacksiteSoldiers => BlacksiteSoldiers.Exists(s => s != null && !s.isEnemyDead && !s.HasBoardedShip && s.IsSpawned);

        internal static bool IsBlacksiteActive =>
            _active || BlacksiteHardDriveObjectiveService.ActiveMission != null;
        internal static bool HasActiveEscalation =>
            _strangerActive || IsBlacksiteActive || CongregationEscalationState.IsActive;
        internal static string ActiveTitle =>
            _strangerActive
                ? "THE STRANGER"
                : IsBlacksiteActive ? "BLACKSITE RETRIEVAL" : CongregationEscalationState.ActiveTitle;

        /// <summary>Resets the run. <paramref name="deferEntityTeardown"/> keeps the escalation's
        /// spawned entities and props alive instead of retiring them here (#540): the ship-leave
        /// reset used to despawn Bundy, the Congregation and the Blacksite squad on the frame the
        /// lever's RPC landed, which is several seconds before vanilla itself clears the moon.
        /// Everything else - flags, timers, pending spawns - still resets immediately, and the
        /// round's outcome has already been captured by the caller either way.</summary>
        internal static void ResetRunState(bool deferEntityTeardown = false)
        {
            CancelStrangerSpawn("run reset");
            StopStrangerSpawnPlacementCache("run reset");
            if (deferEntityTeardown)
            {
                _departureTeardownPending = true;
            }
            else
            {
                _departureTeardownPending = false;
                BlacksiteHardDriveObjectiveService.ResetRunState();
                DespawnSpawnedObjects();
            }
            CongregationEscalationState.ResetRunState(deferEntityTeardown);
            _runtimeEscalation = RuntimeEscalationKind.Unrolled;
            _scheduled = false;
            _rollFailed = false;
            _active = false;
            _strangerScheduled = false;
            _strangerActive = false;
            _strangerComplete = false;
            _strangerFailed = false;
            _strangerTriggerTime = 0f;
            _strangerActiveStartTime = 0f;
            _strangerEnemy = null;
            _strangerTarget = null;
            _strangerTargetPlayerIndex = -1;
            _strangerSpawnRoutine = null;
            _strangerInstantiateOperation = null;
            _strangerSpawnInProgress = false;
            _strangerSpawnPlacementCache = null;
            _strangerBundyPrefabPrewarmed = false;
            _landingInProgress = false;
            _announcementShown = false;
            _complete = false;
            _triggerTime = 0f;
            _landingStartTime = 0f;
            _blacksiteActiveStartTime = 0f;
            _cratesTotal = 0;
            _cratesOpened = 0;
            _blacksiteAborted = false;
            _blacksiteAbortServerTime = 0d;
            _blacksiteAbortReason = string.Empty;
            EscalationAnnouncements.Clear();
            _pendingPlan = default;
            _pendingOutsideMain = Vector3.zero;
            _pendingInsideMain = Vector3.zero;
            _pendingInsideFireExit = Vector3.zero;
            _pendingOutsideFireExit = Vector3.zero;
            _pendingInteriorClearRoute = Array.Empty<Vector3>();
            _blacksiteShip = null;
            _blacksiteMission = null;
            BlacksiteSoldiers.Clear();
            _nextExtractionCheckTime = 0f;
            _cachedEntrances = null;
            // Per round, not per session: an overflow is moon-specific evidence, and a session latch
            // would hide it on every moon after the first one that tripped it.
            ResetLandingPlacementJob();
            ResetSoldierDeployment();
            // #874 Q11: the tracer is per round; the next round's first server frame starts another.
            BlacksiteHitchTracer.Stop();
        }

        /// <summary>Retires whatever the ship-leave reset left standing. Called from vanilla's own
        /// end-of-round sweep, so escalation entities live exactly as long as vanilla enemies do.
        /// Idempotent: the sweep already despawned every <c>EnemyAI</c> before this runs, so most of
        /// what remains is null, and only the props vanilla ignores - the Blacksite ship, the
        /// mission drive, the ritual fire - are still standing.</summary>
        internal static void CompleteDeferredTeardown(string reason)
        {
            if (!_departureTeardownPending)
                return;

            _departureTeardownPending = false;
            int standing = 0;
            for (int i = 0; i < SpawnedObjects.Count; i++)
            {
                if (SpawnedObjects[i] != null)
                    standing++;
            }

            BlacksiteHardDriveObjectiveService.ResetRunState();
            DespawnSpawnedObjects();
            CongregationEscalationState.CompleteDeferredTeardown(reason);
            BlacksiteMonsterTargeting.Reset();
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Escalation] Deferred departure teardown ran ({reason}); " +
                $"{standing} object(s) still standing when it did.");
        }

        /// <summary>Branch timing for the 2026-07-31 regression hunt. UpdateRuntime has been measured
        /// at 120.51ms in a single call (20:52:59, inside a 170ms hitch) and at 77.41ms in the 11:59
        /// session, but the method total cannot say which branch owns it. The steady-state path is
        /// cheap; the cost is almost certainly one of the one-shot roll/schedule/start branches, all of
        /// which spawn or instantiate. Name it before amortizing it - guessing has a poor record on this
        /// investigation (y4ngz313/Y4NGZCompany#198).
        ///
        /// Cost of the instrumentation: one QPC read per branch taken, and a write into preallocated
        /// arrays. Nothing allocates unless a call actually breaches the threshold.</summary>
        private const double SlowUpdateRuntimeThresholdMilliseconds = 25.0;
        private const int MaxTimedRuntimeSteps = 20;
        private static readonly string[] _runtimeStepNames = new string[MaxTimedRuntimeSteps];
        private static readonly double[] _runtimeStepMilliseconds = new double[MaxTimedRuntimeSteps];
        /// <summary>Sub-steps are reported but not attributed: their time is already inside the
        /// enclosing branch step, so counting both would make <c>unattributed</c> meaningless.</summary>
        private static readonly bool[] _runtimeStepIsSubStep = new bool[MaxTimedRuntimeSteps];
        private static int _runtimeStepCount;

        internal static void UpdateRuntime()
        {
            _runtimeStepCount = 0;
            long passStart = System.Diagnostics.Stopwatch.GetTimestamp();
            try
            {
                UpdateRuntimeCore();
            }
            finally
            {
                ReportSlowUpdateRuntime(ElapsedMilliseconds(passStart));
            }
        }

        private static void UpdateRuntimeCore()
        {
            long stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
            EnsureNetworkHandlers();
            RecordRuntimeStep("EnsureNetworkHandlers", stepStart);

            stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
            CongregationEscalationState.UpdateRuntime(canSchedule: false);
            RecordRuntimeStep("Congregation.UpdateRuntime(idle)", stepStart);

            if (!IsRoundRuntimeActive())
                return;

            if (!IsServer())
                return;

            // #874 Q11: the hitch tracer lives on the server for the round while Blacksite debug
            // logging is on; with logging off this is one config read per frame and nothing else.
            BlacksiteHitchTracer.EnsureServer();

            stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateStrangerReference();
            RecordRuntimeStep("UpdateStrangerReference", stepStart);

            if (_runtimeEscalation == RuntimeEscalationKind.Unrolled)
            {
                stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                RollRuntimeEscalation();
                RecordRuntimeStep("RollRuntimeEscalation", stepStart);
            }

            switch (_runtimeEscalation)
            {
                case RuntimeEscalationKind.None:
                    return;
                case RuntimeEscalationKind.Stranger:
                    stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    UpdateStrangerEscalation();
                    RecordRuntimeStep("UpdateStrangerEscalation", stepStart);
                    return;
                case RuntimeEscalationKind.Congregation:
                    if (!CongregationEscalationState.IsActiveOrScheduled && !CongregationEscalationState.HasRolledOrActive)
                    {
                        stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                        CongregationEscalationState.ScheduleRuntimeSelection();
                        RecordRuntimeStep("Congregation.ScheduleRuntimeSelection", stepStart);
                    }

                    stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    CongregationEscalationState.UpdateRuntime(canSchedule: true);
                    RecordRuntimeStep("Congregation.UpdateRuntime(schedule)", stepStart);
                    return;
                case RuntimeEscalationKind.BlacksiteRetrieval:
                    stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    UpdateBlacksiteRetrievalEscalation();
                    RecordRuntimeStep("UpdateBlacksiteRetrievalEscalation", stepStart);
                    return;
                default:
                    return;
            }
        }

        private static double ElapsedMilliseconds(long startTicks)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0
                   / System.Diagnostics.Stopwatch.Frequency;
        }

        /// <summary>Timestamp source for sub-steps recorded from the escalation states themselves, so
        /// the existing breakdown can name an owner inside a branch without a second report format
        /// (WP-A5). Paired with <see cref="RecordRuntimeStep"/>.</summary>
        internal static long RuntimeStepTimestamp()
        {
            return System.Diagnostics.Stopwatch.GetTimestamp();
        }

        internal static void RecordRuntimeStep(string stepName, long startTicks, bool subStep = false)
        {
            if (_runtimeStepCount >= MaxTimedRuntimeSteps)
                return;

            _runtimeStepNames[_runtimeStepCount] = stepName;
            _runtimeStepMilliseconds[_runtimeStepCount] = ElapsedMilliseconds(startTicks);
            _runtimeStepIsSubStep[_runtimeStepCount] = subStep;
            _runtimeStepCount++;
        }

        /// <summary>Warning level on purpose: Info is filtered off disk in the diagnostic profiles
        /// that read these logs. Unthrottled because a call this far above budget is rare, and the
        /// CCTV harness already proved that throttling the report loses the one pass worth reading.</summary>
        private static void ReportSlowUpdateRuntime(double passMilliseconds)
        {
            if (passMilliseconds < SlowUpdateRuntimeThresholdMilliseconds)
                return;

            double attributed = 0.0;
            var line = new System.Text.StringBuilder(256);
            line.Append("[MoonContracts.Timing] Slow MoonEscalationState.UpdateRuntime ");
            line.Append(passMilliseconds.ToString("0.00"));
            line.Append("ms (escalation=");
            line.Append(_runtimeEscalation);
            line.Append("):");
            for (int i = 0; i < _runtimeStepCount; i++)
            {
                if (!_runtimeStepIsSubStep[i])
                    attributed += _runtimeStepMilliseconds[i];
                if (_runtimeStepMilliseconds[i] < 0.5)
                    continue;
                line.Append(' ');
                line.Append(_runtimeStepNames[i]);
                line.Append('=');
                line.Append(_runtimeStepMilliseconds[i].ToString("0.00"));
                line.Append("ms");
            }

            double unattributed = passMilliseconds - attributed;
            if (unattributed >= 0.5)
            {
                line.Append(" unattributed=");
                line.Append(unattributed.ToString("0.00"));
                line.Append("ms");
            }

            ContractsBootstrap.Log?.LogWarning(line.ToString());
        }

        private static void UpdateBlacksiteRetrievalEscalation()
        {
            if (!ContractsBootstrap.BlacksiteRetrievalEnabled.Value)
                return;

            if (_landingInProgress && Time.time - _landingStartTime >= BlacksiteExtractionSequence.ShipLandingTimeoutSeconds)
            {
                AbortBlacksiteLanding("ship landing timed out");
                return;
            }

            if (_landingInProgress || _active)
            {
                // #1283 S7a: the squad is stowed aboard during the flight and released at the door.
                if (_soldierDeployment != null)
                {
                    long deployStart = System.Diagnostics.Stopwatch.GetTimestamp();
                    BlacksiteHitchScope scope = _landingInProgress ? BlacksiteHitchScope.AircraftInbound : BlacksiteHitchScope.SoldierDeploySlice;
                    bool ok;
                    using (BlacksiteHitchTracer.Scope(scope))
                        ok = DrainSoldierDeployment();
                    RecordRuntimeStep(BlacksiteHitchLedger.Name(scope), deployStart, subStep: true);
                    if (!ok)
                    {
                        AbortBlacksiteLanding("native soldier deployment failed mid-slice");
                        return;
                    }
                }

                if (_active)
                    UpdateBlacksiteExtraction();
                return;
            }

            if (_complete)
                return;

            if (_rollFailed)
                return;

            if (_active)
                return;

            // #1305 C1 (D6): a Payload raid is scheduled only on a cart that exists; the wait ends when
            // the cart network-spawns or at the round reset.
            if (!_scheduled && BlacksiteRetrievalRules.WaitsForPayloadCart(
                    MoonContractState.GetCurrentContract(), Y4NGZCompany.Contracts.Payload.PayloadContract.CartSpawned))
                return;

            if (!_scheduled)
                ScheduleBlacksiteRetrieval(skipChance: true);

            if (_scheduled && Time.time >= _triggerTime)
                TryStartBlacksiteRetrieval();
        }

        internal static List<MoonContractObjective> GetActiveObjectives()
        {
            if (_strangerActive)
            {
                // #725: the Stranger hunts one specific player. That player is told so; the
                // rest of the crew reads a plain survive line. The keys differ so a retarget
                // registers as a semantic change on the card.
                bool hunted = IsLocalPlayerStrangerTarget();
                return new List<MoonContractObjective>
                {
                    new MoonContractObjective(
                        hunted ? "IT IS HUNTING YOU" : "SURVIVE THE HUNT",
                        _strangerComplete ? 1 : 0,
                        1,
                        kind: MoonContractObjectiveKind.Status,
                        key: hunted ? "stranger.hunted" : "stranger.hunt",
                        lethal: true)
                };
            }

            BlacksiteRetrievalMissionController mission =
                BlacksiteHardDriveObjectiveService.ActiveMission;
            if (mission != null)
                return new List<MoonContractObjective> { mission.BuildObjective() };

            if (!_active)
                return CongregationEscalationState.GetActiveObjectives();

            return new List<MoonContractObjective>
            {
                new MoonContractObjective("Locate the blacksite hard drive", 0, 1, key: "blacksite.locate")
            };
        }

        private static void RollRuntimeEscalation()
        {
            if (!CanCurrentLevelReceiveEscalation())
            {
                _runtimeEscalation = RuntimeEscalationKind.None;
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.Escalation] Runtime escalation skipped; current moon is not eligible.");
                SendStateToAll();
                return;
            }

            MoonContractType currentContract = MoonContractState.GetCurrentContract();
            // #1075: a no-contract round now raids, so the gate needs the pair, not the value. This
            // runs host-side and only while the round is underway (UpdateRuntimeCore's IsServer and
            // IsRoundRuntimeActive guards), which is after StartGame primed and locked the round's
            // contract - the resolved flag is what proves that rather than assuming it, and refuses
            // a None that only means the table has not been rolled or the snapshot has not landed.
            bool blacksiteContractResolved = MoonContractState.IsCurrentContractResolved();
            bool blacksiteContractEligible =
                BlacksiteRetrievalRules.IsContractReadyToSchedule(currentContract, blacksiteContractResolved);

            if (ContractsBootstrap.StrangerEscalationEnabled.Value && ContractsBootstrap.StrangerEscalationForce.Value)
            {
                SelectRuntimeEscalation(RuntimeEscalationKind.Stranger, "forced");
                return;
            }

            if (ContractsBootstrap.CultEscalationEnabled.Value && ContractsBootstrap.CultEscalationForce.Value)
            {
                SelectRuntimeEscalation(RuntimeEscalationKind.Congregation, "forced");
                return;
            }

            if (ContractsBootstrap.BlacksiteRetrievalEnabled.Value
                && ContractsBootstrap.BlacksiteRetrievalForce.Value
                && blacksiteContractEligible)
            {
                SelectRuntimeEscalation(RuntimeEscalationKind.BlacksiteRetrieval, "forced");
                return;
            }

            if (ContractsBootstrap.BlacksiteRetrievalEnabled.Value && !blacksiteContractEligible)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Selection gate rejected contract {currentContract}; " +
                    $"force={ContractsBootstrap.BlacksiteRetrievalForce.Value}, " +
                    // #1075: the rejection reason, since None can now be rejected for either of two
                    // reasons. resolved=False on a None line means "not told yet", not "no contract".
                    $"resolved={blacksiteContractResolved}, weight=0.");
            }

            float noneWeight = Mathf.Clamp01(ContractsBootstrap.EscalationNoneChance.Value);
            float strangerWeight = ContractsBootstrap.StrangerEscalationEnabled.Value ? Mathf.Clamp01(ContractsBootstrap.StrangerEscalationChance.Value) : 0f;
            float congregationWeight = ContractsBootstrap.CultEscalationEnabled.Value ? Mathf.Clamp01(ContractsBootstrap.CultEscalationChance.Value) : 0f;
            float blacksiteWeight = ContractsBootstrap.BlacksiteRetrievalEnabled.Value && blacksiteContractEligible
                ? Mathf.Clamp01(ContractsBootstrap.BlacksiteRetrievalChance.Value)
                : 0f;
            float totalWeight = noneWeight + strangerWeight + congregationWeight + blacksiteWeight;
            if (totalWeight <= 0f)
            {
                SelectRuntimeEscalation(RuntimeEscalationKind.None, "all weights zero");
                return;
            }

            float originalRoll = UnityEngine.Random.value * totalWeight;
            float roll = originalRoll;
            if (roll < noneWeight)
            {
                SelectRuntimeEscalation(RuntimeEscalationKind.None, $"roll={originalRoll:0.000}/{totalWeight:0.000}");
                return;
            }

            roll -= noneWeight;
            if (roll < strangerWeight)
            {
                SelectRuntimeEscalation(RuntimeEscalationKind.Stranger, $"roll={originalRoll:0.000}/{totalWeight:0.000}");
                return;
            }

            roll -= strangerWeight;
            if (roll < congregationWeight)
            {
                SelectRuntimeEscalation(RuntimeEscalationKind.Congregation, $"roll={originalRoll:0.000}/{totalWeight:0.000}");
                return;
            }

            if (blacksiteWeight > 0f)
                SelectRuntimeEscalation(RuntimeEscalationKind.BlacksiteRetrieval, $"roll={originalRoll:0.000}/{totalWeight:0.000}");
            else
                SelectRuntimeEscalation(RuntimeEscalationKind.None, "Blacksite contract gate");
        }

        private static void SelectRuntimeEscalation(RuntimeEscalationKind kind, string reason)
        {
            _runtimeEscalation = kind;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Escalation] Runtime escalation selected: {FormatRuntimeEscalation(kind)} ({reason}).");
            SendStateToAll();
        }

        private static string FormatRuntimeEscalation(RuntimeEscalationKind kind)
        {
            switch (kind)
            {
                case RuntimeEscalationKind.None:
                    return "none";
                case RuntimeEscalationKind.Stranger:
                    return "The Stranger";
                case RuntimeEscalationKind.Congregation:
                    return "The Congregation";
                case RuntimeEscalationKind.BlacksiteRetrieval:
                    return "Blacksite Retrieval";
                default:
                    return "unrolled";
            }
        }

        private static void UpdateStrangerEscalation()
        {
            if (_strangerFailed || _strangerComplete)
                return;

            UpdateStrangerReference();
            if (_strangerActive)
            {
                UpdateStrangerTargetSnapshot();
                return;
            }

            if (_strangerSpawnInProgress)
                return;

            if (!_strangerScheduled)
                ScheduleStrangerEscalation();

            if (_strangerScheduled && Time.time >= _strangerTriggerTime)
                BeginStrangerEscalationSpawn();
        }

        private static void ScheduleStrangerEscalation()
        {
            if (!ContractsBootstrap.StrangerEscalationEnabled.Value || !CanCurrentLevelReceiveEscalation())
            {
                _strangerFailed = true;
                return;
            }

            float minDelay = Mathf.Max(10f, ContractsBootstrap.StrangerEscalationMinDelaySeconds.Value);
            float maxDelay = Mathf.Max(minDelay, ContractsBootstrap.StrangerEscalationMaxDelaySeconds.Value);
            float delay = ContractsBootstrap.StrangerEscalationForce.Value
                ? Mathf.Clamp(minDelay, 10f, maxDelay)
                : UnityEngine.Random.Range(minDelay, maxDelay);
            _strangerTriggerTime = Time.time + delay;
            _strangerScheduled = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Stranger] The Stranger scheduled in {Mathf.Max(0f, _strangerTriggerTime - Time.time):0}s.");
            BeginStrangerSpawnPlacementCache();
            BeginStrangerBundyPrewarm();
        }

        private static void BeginStrangerEscalationSpawn()
        {
            _strangerScheduled = false;
            MonoBehaviour runner = StartOfRound.Instance;
            if (runner == null)
            {
                FailStrangerSpawn("[MoonContracts.Stranger] The Stranger skipped because no round coroutine runner was available.");
                return;
            }

            _strangerSpawnInProgress = true;
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.Timing] Stranger spawn pipeline started: placement selection is capped at " +
                $"{StrangerSpawnCacheFrameBudgetMilliseconds:0.00}ms/frame and the Bundy prefab will use InstantiateAsync before NGO spawn.");
            try
            {
                _strangerSpawnRoutine = runner.StartCoroutine(StrangerEscalationSpawnCoroutine());
            }
            catch (Exception ex)
            {
                _strangerSpawnRoutine = null;
                _strangerSpawnInProgress = false;
                FailStrangerSpawn($"[MoonContracts.Stranger] Could not start the budgeted Stranger spawn pipeline: {ex.Message}");
            }
        }

        private static IEnumerator StrangerEscalationSpawnCoroutine()
        {
            IEnumerator routine = RunStrangerEscalationSpawnRoutine();
            while (true)
            {
                bool moved;
                object current = null;
                try
                {
                    moved = routine.MoveNext();
                    if (moved)
                        current = routine.Current;
                }
                catch (Exception ex)
                {
                    CleanupPendingStrangerInstantiateResults();
                    FailStrangerSpawn($"[MoonContracts.Stranger] Budgeted Stranger spawn pipeline failed: {ex.GetBaseException().Message}");
                    break;
                }

                if (!moved)
                    break;

                yield return current;
            }

            _strangerInstantiateOperation = null;
            _strangerSpawnRoutine = null;
            _strangerSpawnInProgress = false;
        }

        private static IEnumerator RunStrangerEscalationSpawnRoutine()
        {
            // StartCoroutine advances an iterator immediately through its first yield. Deferring the
            // first real step keeps the trigger frame's instrumented UpdateRuntime call lightweight.
            yield return null;

            var total = System.Diagnostics.Stopwatch.StartNew();
            var placementPerf = new StrangerSpawnPerfContext { Source = "cache-budgeted" };
            int cacheWaitFrames = 0;

            if (!TryGetBundyStrangerEnemyType(out EnemyType bundyEnemyType, out string bundyLabel, out string reason))
            {
                FailStrangerSpawn($"[MoonContracts.Stranger] The Stranger skipped: {reason}");
                yield break;
            }

            if (!_strangerBundyPrefabPrewarmed)
                PrewarmStrangerBundyPrefab(bundyEnemyType.enemyPrefab);

            while (_strangerSpawnPlacementCacheRoutine != null)
            {
                cacheWaitFrames++;
                yield return null;
            }

            StrangerSpawnPlacementCache cache = _strangerSpawnPlacementCache;
            if (cache == null || !cache.Complete || cache.Candidates.Count == 0)
            {
                BeginStrangerSpawnPlacementCache();
                while (_strangerSpawnPlacementCacheRoutine != null)
                {
                    cacheWaitFrames++;
                    yield return null;
                }
                cache = _strangerSpawnPlacementCache;
            }

            if (cache == null || !cache.Complete || cache.Candidates.Count == 0)
            {
                FailStrangerSpawn("[MoonContracts.Stranger] Could not build an outside fire-exit placement cache; escalation skipped.");
                yield break;
            }

            var selection = new StrangerSpawnSelection();
            IEnumerator selectionRoutine = TrySelectStrangerSpawnPlacementBudgeted(cache, selection, placementPerf);
            while (selectionRoutine.MoveNext())
                yield return selectionRoutine.Current;

            if (!selection.Succeeded)
            {
                FailStrangerSpawn("[MoonContracts.Stranger] Could not find an outside fire-exit spawn away from the ship; escalation skipped.");
                yield break;
            }

            Vector3 position = selection.Position;
            Quaternion rotation = selection.Rotation;
            RoundManager round = RoundManager.Instance;
            if (round == null || !round.IsServer)
            {
                FailStrangerSpawn("[MoonContracts.Stranger] The Stranger skipped because the server round ended before async spawn.");
                yield break;
            }

            bool synchronousFallback = false;
            int asyncFrames = 0;
            double instantiateKickoffMs = 0d;
            var instantiateWall = System.Diagnostics.Stopwatch.StartNew();
            GameObject obj = null;
            try
            {
                long kickoffStart = System.Diagnostics.Stopwatch.GetTimestamp();
                _strangerInstantiateOperation = UnityEngine.Object.InstantiateAsync(
                    bundyEnemyType.enemyPrefab,
                    position,
                    rotation);
                instantiateKickoffMs = ElapsedMilliseconds(kickoffStart);
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Timing] Stranger InstantiateAsync could not start; using the original synchronous fallback: " +
                    $"{ex.GetBaseException().Message}");
            }

            if (_strangerInstantiateOperation != null)
            {
                while (!_strangerInstantiateOperation.isDone)
                {
                    asyncFrames++;
                    yield return null;
                }

                try
                {
                    GameObject[] results = _strangerInstantiateOperation.Result;
                    if (results != null && results.Length > 0)
                        obj = results[0];
                }
                catch (Exception ex)
                {
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Timing] Stranger InstantiateAsync completed without a usable result; " +
                        $"using the original synchronous fallback: {ex.GetBaseException().Message}");
                }
                finally
                {
                    _strangerInstantiateOperation = null;
                }
            }

            if (obj == null)
            {
                synchronousFallback = true;
                long fallbackStart = System.Diagnostics.Stopwatch.GetTimestamp();
                Exception fallbackFailure = null;
                try
                {
                    obj = UnityEngine.Object.Instantiate(bundyEnemyType.enemyPrefab, position, rotation);
                }
                catch (Exception ex)
                {
                    fallbackFailure = ex;
                }
                if (fallbackFailure != null)
                {
                    FailStrangerSpawn(
                        $"[MoonContracts.Stranger] Bundy prefab instantiation failed: " +
                        $"{fallbackFailure.GetBaseException().Message}");
                    yield break;
                }
                instantiateKickoffMs += ElapsedMilliseconds(fallbackStart);
            }
            double instantiateWallMs = instantiateWall.Elapsed.TotalMilliseconds;

            NetworkObject networkObject = obj != null
                ? obj.GetComponent<NetworkObject>() ?? obj.GetComponentInChildren<NetworkObject>(true)
                : null;
            EnemyAI enemy = obj != null
                ? obj.GetComponent<EnemyAI>() ?? obj.GetComponentInChildren<EnemyAI>(true)
                : null;
            if (networkObject == null || enemy == null)
            {
                CleanupStrangerClone(obj, networkObject, enemy);
                FailStrangerSpawn("[MoonContracts.Stranger] Bundy async spawn returned no NetworkObject or EnemyAI; escalation skipped.");
                yield break;
            }

            double networkSpawnMs = 0d;
            double setupMs = 0d;
            Exception spawnFailure = null;
            try
            {
                long networkStart = System.Diagnostics.Stopwatch.GetTimestamp();
                networkObject.Spawn(destroyWithScene: true);
                RegisterSpawnedObject(obj);
                RegisterSpawnedEnemy(enemy);
                networkSpawnMs = ElapsedMilliseconds(networkStart);

                long setupStart = System.Diagnostics.Stopwatch.GetTimestamp();
                // Every other Y4NGZ enemy spawn routes through this helper; the Stranger did not, so a
                // placement that landed off the baked exterior mesh left the agent unplaced and Unity
                // logged "Failed to create agent because it is not close enough to the NavMesh".
                ContractAssetLoader.TryActivateAgentOnNavMesh(obj, 6f);

                obj.name = string.Equals(bundyLabel, "Chainsaw Bundy", StringComparison.OrdinalIgnoreCase)
                    ? "TheStranger_ChainsawBundy"
                    : "TheStranger_Bundy";
                enemy.enemyType = bundyEnemyType;
                enemy.isOutside = true;
                _strangerEnemy = enemy;
                _strangerActive = true;
                _strangerActiveStartTime = Time.time;
                _strangerComplete = false;
                _strangerTarget = SelectStrangerTarget();
                _strangerTargetPlayerIndex = GetPlayerIndex(_strangerTarget);
                ActivateStrangerBundyTargeting(enemy, _strangerTarget);
                SendStateToAll();
                EscalationAnnouncements.Show(EscalationAnnouncementKind.StrangerHunting);
                setupMs = ElapsedMilliseconds(setupStart);
            }
            catch (Exception ex)
            {
                spawnFailure = ex;
            }
            if (spawnFailure != null)
            {
                CleanupStrangerClone(obj, networkObject, enemy);
                FailStrangerSpawn(
                    $"[MoonContracts.Stranger] Bundy network spawn or setup failed: " +
                    $"{spawnFailure.GetBaseException().Message}");
                yield break;
            }

            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.Timing] Stranger spawn pipeline completed: placementCpu={placementPerf.SelectionCpuMilliseconds:0.00}ms " +
                $"placementFrames={placementPerf.SelectionFrames} cacheWaitFrames={cacheWaitFrames} source={placementPerf.Source} " +
                $"navSamples={placementPerf.NavMeshSamples} clearanceChecks={placementPerf.ClearanceChecks} " +
                $"candidates={placementPerf.CandidatesEvaluated}/{placementPerf.CandidatesTotal} " +
                $"instantiate={(synchronousFallback ? "sync-fallback" : "async")} kickoffCpu={instantiateKickoffMs:0.00}ms " +
                $"asyncFrames={asyncFrames} instantiateWall={instantiateWallMs:0.00}ms networkSpawn={networkSpawnMs:0.00}ms " +
                $"setup={setupMs:0.00}ms totalWall={total.Elapsed.TotalMilliseconds:0.00}ms.");
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Stranger] The Stranger spawned {bundyLabel} at {position}; fire exit nearby.");
        }

        private static void FailStrangerSpawn(string message)
        {
            _strangerFailed = true;
            ContractsBootstrap.Log?.LogWarning(message);
            SendStateToAll();
        }

        private static void CancelStrangerSpawn(string reason)
        {
            bool hadWork = _strangerSpawnInProgress ||
                _strangerSpawnRoutine != null ||
                _strangerInstantiateOperation != null;
            if (!hadWork)
                return;

            MonoBehaviour runner = StartOfRound.Instance;
            if (_strangerSpawnRoutine != null && runner != null)
            {
                try
                {
                    runner.StopCoroutine(_strangerSpawnRoutine);
                }
                catch
                {
                    // The StartOfRound host can already be tearing down during reset.
                }
            }

            if (_strangerInstantiateOperation != null)
            {
                try
                {
                    if (!_strangerInstantiateOperation.isDone)
                        _strangerInstantiateOperation.Cancel();
                }
                catch (Exception ex)
                {
                    ContractsBootstrap.Log?.LogDebug(
                        $"[MoonContracts.Stranger] Async instantiate cancellation during {reason} reported: {ex.Message}");
                }
            }

            CleanupPendingStrangerInstantiateResults();
            _strangerInstantiateOperation = null;
            _strangerSpawnRoutine = null;
            _strangerSpawnInProgress = false;
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.Timing] Stranger async spawn pipeline cancelled during {reason}; " +
                "cancellation was requested and any completed pending clones were cleaned up.");
        }

        private static void CleanupPendingStrangerInstantiateResults()
        {
            AsyncInstantiateOperation<GameObject> operation = _strangerInstantiateOperation;
            if (operation == null)
                return;

            try
            {
                if (!operation.isDone)
                    return;

                GameObject[] results = operation.Result;
                if (results == null)
                    return;

                for (int i = 0; i < results.Length; i++)
                {
                    GameObject obj = results[i];
                    if (obj == null)
                        continue;
                    NetworkObject networkObject = obj.GetComponent<NetworkObject>()
                        ?? obj.GetComponentInChildren<NetworkObject>(true);
                    EnemyAI enemy = obj.GetComponent<EnemyAI>()
                        ?? obj.GetComponentInChildren<EnemyAI>(true);
                    CleanupStrangerClone(obj, networkObject, enemy);
                }
            }
            catch
            {
                // A cancelled native async operation may not expose a Result array.
            }
        }

        private static void CleanupStrangerClone(GameObject obj, NetworkObject networkObject, EnemyAI enemy)
        {
            if (obj == null)
                return;

            SpawnedObjects.Remove(obj);
            if (enemy != null && RoundManager.Instance?.SpawnedEnemies != null)
                RoundManager.Instance.SpawnedEnemies.Remove(enemy);

            try
            {
                if (networkObject != null && networkObject.IsSpawned && IsServer())
                    networkObject.Despawn(destroy: true);
                else
                    UnityEngine.Object.Destroy(obj);
            }
            catch
            {
                UnityEngine.Object.Destroy(obj);
            }
        }

        private static void ActivateStrangerBundyTargeting(EnemyAI enemy, PlayerControllerB target)
        {
            if (enemy == null)
                return;

#if LGUCONTRACTHUD_STANDALONE
            if (!TryResolveStrangerBundyMethod(
                    enemy,
                    "ActivateStrangerTargeting",
                    out _,
                    out _))
#else
            if (!TryGetStrangerBundy(enemy, out BundyAIBase bundy))
#endif
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Stranger] Spawned Stranger enemy does not expose BundyAIBase; perception integration disabled.");
                return;
            }

            if (!IsLivingStrangerTarget(target))
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Stranger] No living target available for initial Stranger assignment.");
                return;
            }

#if LGUCONTRACTHUD_STANDALONE
            if (!TryInvokeStrangerBundy(
                    enemy,
                    "ActivateStrangerTargeting",
                    target,
                    target.transform.position,
                    Time.time))
            {
                return;
            }
            TryInvokeStrangerBundy(enemy, "UpdateStrangerPerceptionEscalation", 0f);
#else
            bundy.ActivateStrangerTargeting(target, target.transform.position, Time.time);
            bundy.UpdateStrangerPerceptionEscalation(0f);
#endif
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Stranger] Targeting {target.playerUsername}; exact position updates now require Bundy sight or hearing.");
        }

        private static void UpdateStrangerTargetSnapshot()
        {
            if (!_strangerActive || _strangerEnemy == null)
                return;

            bool targetChanged = false;
            if (!IsLivingStrangerTarget(_strangerTarget))
            {
                _strangerTarget = SelectStrangerTarget();
                _strangerTargetPlayerIndex = GetPlayerIndex(_strangerTarget);
                targetChanged = true;
                // #725: clients render a different hunt line for the hunted player, so a
                // retarget is a state change they need to hear about.
                SendStateToAll();
            }

            if (!IsLivingStrangerTarget(_strangerTarget))
                return;

#if LGUCONTRACTHUD_STANDALONE
            if (!TryInvokeStrangerBundy(
                    _strangerEnemy,
                    "UpdateStrangerPerceptionEscalation",
                    Mathf.Max(0f, Time.time - _strangerActiveStartTime)))
            {
                return;
            }
#else
            if (!TryGetStrangerBundy(_strangerEnemy, out BundyAIBase bundy))
                return;

            bundy.UpdateStrangerPerceptionEscalation(
                Mathf.Max(0f, Time.time - _strangerActiveStartTime));
#endif
            if (!targetChanged)
                return;

#if LGUCONTRACTHUD_STANDALONE
            TryInvokeStrangerBundy(
                _strangerEnemy,
                "UpdateStrangerTargetSnapshot",
                _strangerTarget,
                _strangerTarget.transform.position,
                Time.time);
#else
            bundy.UpdateStrangerTargetSnapshot(_strangerTarget, _strangerTarget.transform.position, Time.time);
#endif
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Stranger] Primary target changed after the prior target became unavailable: " +
                $"{_strangerTarget.playerUsername}.");
        }

#if LGUCONTRACTHUD_STANDALONE
        private static bool TryInvokeStrangerBundy(EnemyAI enemy, string methodName, params object[] arguments)
        {
            if (!TryResolveStrangerBundyMethod(enemy, methodName, out object target, out MethodInfo method))
                return false;

            try
            {
                method.Invoke(target, arguments);
                return true;
            }
            catch (Exception ex)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Stranger] Standalone Bundy bridge failed invoking {methodName}: {ex.GetBaseException().Message}");
                return false;
            }
        }

        private static bool TryResolveStrangerBundyMethod(
            EnemyAI enemy,
            string methodName,
            out object target,
            out MethodInfo method)
        {
            target = null;
            method = null;
            if (enemy == null || string.IsNullOrWhiteSpace(methodName))
                return false;

            target = enemy;
            method = target.GetType().GetMethod(
                methodName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                MonoBehaviour[] components = enemy.gameObject != null
                    ? enemy.gameObject.GetComponentsInChildren<MonoBehaviour>(true)
                    : Array.Empty<MonoBehaviour>();
                for (int i = 0; i < components.Length; i++)
                {
                    MonoBehaviour component = components[i];
                    if (component == null)
                        continue;

                    MethodInfo candidate = component.GetType().GetMethod(
                        methodName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (candidate == null)
                        continue;

                    target = component;
                    method = candidate;
                    break;
                }
            }

            if (method == null)
                return false;

            return true;
        }
#else
        private static bool TryGetStrangerBundy(EnemyAI enemy, out BundyAIBase bundy)
        {
            bundy = enemy as BundyAIBase;
            if (bundy != null)
            {
                return true;
            }

            GameObject root = enemy != null ? enemy.gameObject : null;
            bundy = root != null
                ? root.GetComponent<BundyAIBase>() ?? root.GetComponentInChildren<BundyAIBase>(true)
                : null;
            return bundy != null;
        }

        /// <summary>
        /// Resolves the live Stranger Bundy for world-event hooks (e.g. the teleporter reveal patch).
        /// Returns false whenever no Stranger escalation is active.
        /// </summary>
        internal static bool TryGetActiveStrangerBundy(out BundyAIBase bundy)
        {
            bundy = null;
            return _strangerActive &&
                _strangerEnemy != null &&
                TryGetStrangerBundy(_strangerEnemy, out bundy);
        }
#endif

        private static PlayerControllerB SelectStrangerTarget()
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || players.Length == 0)
                return null;

            var candidates = new List<PlayerControllerB>();
            foreach (PlayerControllerB player in players)
            {
                if (IsLivingStrangerTarget(player))
                    candidates.Add(player);
            }

            if (candidates.Count == 0)
                return null;

            return candidates[UnityEngine.Random.Range(0, candidates.Count)];
        }

        private static bool IsLivingStrangerTarget(PlayerControllerB player)
        {
            return player != null &&
                player.isPlayerControlled &&
                !player.isPlayerDead &&
                player.gameObject != null &&
                player.gameObject.activeInHierarchy;
        }

        /// <summary>#725. Index into <c>allPlayerScripts</c>, or -1. The index is what the
        /// state payload carries, since it is stable for the session on every machine.</summary>
        private static int GetPlayerIndex(PlayerControllerB player)
        {
            if (player == null)
                return -1;

            return (int)player.playerClientId;
        }

        /// <summary>#725. True on the machine whose local player the Stranger is hunting. The
        /// host reads its live reference; clients read the replicated index.</summary>
        internal static bool IsLocalPlayerStrangerTarget()
        {
            if (!_strangerActive)
                return false;

            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local == null)
                return false;

            if (_strangerTarget != null)
                return ReferenceEquals(_strangerTarget, local);

            return _strangerTargetPlayerIndex >= 0 && _strangerTargetPlayerIndex == (int)local.playerClientId;
        }

        private static bool TryGetBundyStrangerEnemyType(out EnemyType enemyType, out string selectedLabel, out string reason)
        {
            enemyType = null;
            selectedLabel = "Chainsaw Bundy";
            reason = string.Empty;

            // The bundled Bundy is a compile-time reference; the standalone plugin is the
            // reflection case, and is only consulted when the bundled one has not registered
            // an enemy type — which is the same order the two assembly-qualified probes gave.
            if (TryReadBundyEnemyType(
                    typeof(Y4NGZCompany.Bootstrap.BundyPlugin),
                    BundyChainsawEnemyTypeFieldName,
                    out enemyType))
            {
                selectedLabel = "Chainsaw Bundy";
                return true;
            }

            Type pluginType = Type.GetType(BundyStandalonePluginTypeName, throwOnError: false);
            if (pluginType == null)
            {
                reason = "Bundy ChainsawEnemyType is not registered yet and no standalone Bundy " +
                         "plugin is loaded; The Stranger is chainsaw-only and will not fall back " +
                         "to standard Bundy.";
                return false;
            }

            if (TryReadBundyEnemyType(pluginType, BundyChainsawEnemyTypeFieldName, out enemyType))
            {
                selectedLabel = "Chainsaw Bundy";
                return true;
            }

            reason = "Bundy ChainsawEnemyType is not registered yet; The Stranger is chainsaw-only and will not fall back to standard Bundy.";
            return false;
        }

        private static bool TryReadBundyEnemyType(Type pluginType, string fieldName, out EnemyType enemyType)
        {
            enemyType = null;
            FieldInfo field = pluginType?.GetField(
                fieldName,
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (field == null)
                return false;

            enemyType = field.GetValue(null) as EnemyType;
            if (enemyType == null || enemyType.enemyPrefab == null)
            {
                enemyType = null;
                return false;
            }

            return true;
        }

        private static void BeginStrangerBundyPrewarm()
        {
            if (_strangerBundyPrefabPrewarmed)
                return;

            if (!TryGetBundyStrangerEnemyType(out EnemyType enemyType, out _, out _))
                return;

            PrewarmStrangerBundyPrefab(enemyType.enemyPrefab);
        }

        private static void PrewarmStrangerBundyPrefab(GameObject prefab)
        {
            if (_strangerBundyPrefabPrewarmed || prefab == null)
                return;

            var perf = System.Diagnostics.Stopwatch.StartNew();
            int renderers = 0;
            int materials = 0;
            int meshes = 0;

            try
            {
                Renderer[] prefabRenderers = prefab.GetComponentsInChildren<Renderer>(true);
                foreach (Renderer renderer in prefabRenderers)
                {
                    if (renderer == null)
                        continue;

                    renderers++;
                    Material[] sharedMaterials = renderer.sharedMaterials;
                    if (sharedMaterials != null)
                    {
                        materials += sharedMaterials.Length;
                        foreach (Material material in sharedMaterials)
                        {
                            if (material?.shader != null)
                                _ = material.shader.isSupported;
                        }
                    }

                    if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                    {
                        meshes++;
                        _ = skinned.sharedMesh.vertexCount;
                    }
                    else
                    {
                        MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
                        if (meshFilter?.sharedMesh != null)
                        {
                            meshes++;
                            _ = meshFilter.sharedMesh.vertexCount;
                        }
                    }
                }

                _strangerBundyPrefabPrewarmed = true;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Stranger] Stranger spawn perf: prewarmed Bundy prefab render metadata " +
                    $"renderers={renderers} materials={materials} meshes={meshes} elapsed={perf.Elapsed.TotalMilliseconds:0.00}ms.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Stranger] Bundy prefab prewarm skipped: {e.Message}");
            }
        }

        private static void UpdateStrangerReference()
        {
            if (!_strangerActive)
                return;

            if (_strangerEnemy != null && !_strangerEnemy.isEnemyDead)
                return;

            _strangerComplete = true;
            _strangerActive = false;
            _strangerActiveStartTime = 0f;
            _strangerEnemy = null;
            _strangerTarget = null;
            _strangerTargetPlayerIndex = -1;
            SendStateToAll();
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.Stranger] The Stranger is no longer active.");
        }

        internal static void RequestOpenBlacksiteCrate(ShadowRaidCargoCrate crate)
        {
            if (crate == null || crate.Opened)
                return;

            if (IsServer())
            {
                PlayerControllerB opener = GameNetworkManager.Instance?.localPlayerController
                                           ?? StartOfRound.Instance?.localPlayerController;
                crate.OpenFromServer(opener);
                return;
            }

            EnsureNetworkHandlers();
            NetworkManager manager = NetworkManager.Singleton;
            if (manager?.CustomMessagingManager == null || !manager.IsClient || !manager.IsListening)
                return;

            using (var writer = new FastBufferWriter(sizeof(ulong), Allocator.Temp))
            {
                ulong id = crate.NetworkId;
                writer.WriteValueSafe(id);
                manager.CustomMessagingManager.SendNamedMessage(MsgCrateOpenRequest, NetworkManager.ServerClientId, writer);
            }
        }

        internal static void NotifyBlacksiteCrateOpened(ShadowRaidCargoCrate crate)
        {
            BlacksiteRetrievalMissionController mission =
                BlacksiteHardDriveObjectiveService.ActiveMission;
            if (mission == null)
                return;

            SendCrateOpened(crate != null ? crate.NetworkId : 0UL);
            if (mission.IsServer)
                BlacksiteHardDriveObjectiveService.UpdateMissionServer(mission);
        }

        private static void ScheduleBlacksiteRetrieval(bool skipChance)
        {
            MoonContractType contract = MoonContractState.GetCurrentContract();
            if (!BlacksiteRetrievalRules.CanSchedule(
                    contract,
                    CanCurrentLevelReceiveEscalation(),
                    ContractsBootstrap.BlacksiteRetrievalEnabled.Value))
            {
                _rollFailed = true;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Scheduling rejected for contract {contract}.");
                return;
            }

            bool force = ContractsBootstrap.BlacksiteRetrievalForce.Value;
            float chance = Mathf.Clamp01(ContractsBootstrap.BlacksiteRetrievalChance.Value);
            if (!skipChance && !force && UnityEngine.Random.value > chance)
            {
                _rollFailed = true;
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.Blacksite] Blacksite Retrieval roll failed for this moon.");
                return;
            }

            float minDelay = Mathf.Max(10f, ContractsBootstrap.BlacksiteRetrievalMinDelaySeconds.Value);
            float maxDelay = Mathf.Max(minDelay, ContractsBootstrap.BlacksiteRetrievalMaxDelaySeconds.Value);
            float delay = force ? 0f : UnityEngine.Random.Range(minDelay, maxDelay);
            _triggerTime = Time.time + delay;
            _scheduled = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Blacksite Retrieval scheduled in {Mathf.Max(0f, _triggerTime - Time.time):0}s.");
        }

        /// <summary>The landing search is a multi-frame job (WP-A5). <c>_scheduled</c> stays true
        /// while it runs so <c>UpdateBlacksiteRetrievalEscalation</c> keeps re-entering here. #1262
        /// P9: the frame the job resolves only settles the pose; the entrance lookups (step 4), the
        /// interior route (step 5) and the ship spawn with mission start (step 6) then take one frame
        /// each, and <c>_scheduled</c> is cleared in the spawn frame or by an abort. The ship
        /// therefore departs for the same landing site, a few frames later.</summary>
        private static void TryStartBlacksiteRetrieval()
        {
            MoonContractType contract = MoonContractState.GetCurrentContract();
            if (_landingBeginStep == 0)
            {
                if (!BlacksiteRetrievalRules.IsContractEligible(contract))
                {
                    _scheduled = false;
                    _rollFailed = true;
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Blacksite] Start rejected for ineligible contract {contract}.");
                    return;
                }

                if (ContractAssetLoader.BlacksiteShipPrefab == null
                    || ContractAssetLoader.BlacksiteSoldierPrefab == null || ContractAssetLoader.BlacksiteSoldierEnemyType == null)
                {
                    _scheduled = false;
                    _rollFailed = true;
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.Blacksite] Missing ship or soldier prefab; escalation skipped.");
                    return;
                }

                if (BlacksiteEquipmentAssets.Load<RuntimeAnimatorController>("BlacksiteBatonRuntime") == null
                    || BlacksiteEquipmentAssets.Load<Avatar>("BlacksiteBatonAvatar") == null
                    || BlacksiteEquipmentAssets.DroppedBaton == null || ContractAssetLoader.BlacksiteCratePrefab == null)
                {
                    _scheduled = false; _rollFailed = true;
                    ContractsBootstrap.Log?.LogError("[MoonContracts.Blacksite] Required retrieval equipment is unavailable; install the matching Contracted package.");
                    return;
                }
                _landingBeginStep = 1;
            }

            if (_landingBeginStep < 4)
            {
                if (!AdvanceLandingPlacementJob(out bool placed, out BlacksiteLandingPlan plan))
                    return;

                // #874 Q11: the resolving frame (entrances, interior route, ship spawn and mission
                // init) measured 70-87 ms on 23 September. #1262 P9 split it into three later frames,
                // each under this same scope: step 4 the entrances, step 5 the interior route, step 6
                // the ship spawn and mission start. This frame only settles the pose.
                using var resolved = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.AircraftInbound);
                // #1305: no sealed plan is a pre-flight skip, never a post-flight abort; the job logged why.
                if (!placed)
                {
                    _scheduled = false;
                    _rollFailed = true;
                    // #1343 S2: replicated now, so every peer sees the skip, not only the host.
                    MarkBlacksiteAborted(EscalationAnnouncementCopyTable.BlacksiteNoRouteBody);
                    SendStateToAll();
                    EscalationAnnouncements.Show(EscalationAnnouncementKind.BlacksiteAborted);
                    return;
                }

                _pendingPlan = plan;
                _landingBeginStep = 4;
                return;
            }

            using var inbound = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.AircraftInbound);
            if (_landingBeginStep == 4)
            {
                _pendingOutsideMain = FindOutsideMainEntrancePosition();
                _pendingInsideMain = FindInsideMainEntrancePosition();
                _pendingInsideFireExit = FindInsideFireExitPosition(_pendingInsideMain);
                _pendingOutsideFireExit = FindOutsideFireExitPosition(_pendingOutsideMain, _pendingInsideFireExit);
                _landingBeginStep = 5;
                return;
            }

            if (_landingBeginStep == 5)
            {
                _pendingInteriorClearRoute = FindInteriorClearRoute(_pendingInsideMain, _pendingInsideFireExit);
                _landingBeginStep = 6;
                return;
            }

            _scheduled = false;
            _landingBeginStep = 0;
            // #1233 S7: the ship enters at the far end of the corridor the search proved over the pose
            // it is sent to, and every later flight is that corridor.
            Vector3 approachPosition = BlacksiteFlightCorridor.Entry(_pendingPlan.Origin, _pendingPlan.Rotation, _pendingPlan.Access.Altitude);
            GameObject ship = SpawnBlacksiteShip(approachPosition, _pendingPlan.Rotation);
            var controller = ship != null ? ship.GetComponent<BlacksiteShipController>() : null;
            var mission = ship != null
                ? ship.GetComponent<BlacksiteRetrievalMissionController>()
                : null;
            if (controller == null || mission == null)
            {
                RegisterSpawnedObject(ship);
                AbortBlacksiteLanding("ship prefab lacks a required controller");
                return;
            }

            _blacksiteMission = mission;
            if (!mission.InitializeServer(contract))
            {
                AbortBlacksiteLanding("mission context initialization failed");
                return;
            }

            _landingInProgress = true;
            _landingStartTime = Time.time;
            _active = false;
            _complete = false;
            _announcementShown = true;
            _blacksiteShip = controller;
            controller.InitializeServer(_pendingPlan.Origin, _pendingPlan.Rotation, 22f, _pendingPlan, _landingFootToDeck);
            // #1283 S7a: stowed aboard in flight from the next frame on; the landed notification releases it.
            BlacksiteSoldiers.Clear();
            _soldierDeployment = new SoldierDeployment
            {
                OutsideMain = _pendingOutsideMain,
                InsideMain = _pendingInsideMain,
                InsideFireExit = _pendingInsideFireExit,
                OutsideFireExit = _pendingOutsideFireExit,
                InteriorClearRoute = _pendingInteriorClearRoute,
                SoldierCount = Mathf.Clamp(ContractsBootstrap.BlacksiteRetrievalSoldierCount.Value, 1, BlacksiteExtractionSequence.MaxSquadMembers),
                EnqueuedFrame = Time.frameCount,
            };
            SendStateToAll();
            // Blacksite arrival is presented by the contract strip, with contract-owned audio.
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Blacksite Retrieval aircraft inbound. Landing={_pendingPlan.Origin}, access=drop{_pendingPlan.Access.DropHeight:0.00}m ladder={_pendingPlan.Access.LadderSide} footToDeck={_landingFootToDeck:0.00}, outsideMain={_pendingOutsideMain}, insideMain={_pendingInsideMain}, insideFire={_pendingInsideFireExit}, outsideFire={_pendingOutsideFireExit}.");
        }

        internal static void NotifyBlacksiteShipLanded(BlacksiteShipController ship, Vector3 landingPosition, Quaternion landingRotation)
        {
            if (!IsServer() || !_landingInProgress || _active || _complete)
                return;
            using var landed = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.ShipLanded);

            if (HorizontalDistance(landingPosition, _pendingPlan.Origin) > 24f)
            {
                landingPosition = _pendingPlan.Origin;
                landingRotation = _pendingPlan.Rotation;
            }

            // #1305: touchdown publishes the sealed plan; it never re-judges the landing.
            ship.SealAccessServer(_pendingOutsideMain, Mathf.Clamp(ContractsBootstrap.BlacksiteRetrievalSoldierCount.Value, 1, BlacksiteExtractionSequence.MaxSquadMembers));
            _blacksiteMission?.MarkDeployingServer();
            // #1283 S7a: this frame only turns the stowed squad over to its release; an incomplete stow
            // is a failed deployment. #874 Q11: the route and the releases run on later frames.
            SoldierDeployment deployment = _soldierDeployment;
            if (deployment == null || deployment.Stowed.Count != deployment.SoldierCount)
            {
                AbortBlacksiteLanding($"native soldier deployment failed: {deployment?.Stowed.Count ?? 0}/{deployment?.SoldierCount ?? 0} stowed at touchdown");
                return;
            }
            deployment.LandingPosition = landingPosition;
            deployment.LandingRotation = landingRotation;
            deployment.Cursor = 0;
            deployment.EnqueuedFrame = Time.frameCount;
            _landingInProgress = false;
            _active = true;
            _complete = false;
            _blacksiteActiveStartTime = Time.time;
            SendStateToAll();
            // Deployment is a contract-strip status update, without a vanilla notification cue.
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Blacksite Retrieval active at {landingPosition}.");
        }

        private static void AbortBlacksiteLanding(string reason)
        {
            ResetSoldierDeployment();
            ResetLandingPlacementJob();
            _landingInProgress = false;
            _active = false;
            _complete = false;
            _rollFailed = true;
            MarkBlacksiteAborted(reason);
            if (_blacksiteMission != null)
            {
                BlacksiteHardDriveObjectiveService.CleanupMissionServer(
                    _blacksiteMission,
                    "landing abort");
                _blacksiteMission.ResolveServer(BlacksiteRetrievalOutcome.Aborted);
            }
            SendStateToAll();
            DespawnSpawnedObjects();
            _blacksiteMission = null;
            EscalationAnnouncements.Show(EscalationAnnouncementKind.BlacksiteAborted);
            ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Blacksite Retrieval aborted: {reason}.");
        }

        /// <summary>#1343 S2, host: records the abort for the state payload. Call before the send.</summary>
        private static void MarkBlacksiteAborted(string reason)
        {
            _blacksiteAborted = true;
            _blacksiteAbortServerTime = ServerNow();
            _blacksiteAbortReason = reason ?? string.Empty;
        }

        internal static void NotifyBlacksiteShipDeparted(BlacksiteShipController ship)
        {
            if (ship == null || ship != _blacksiteShip)
                return;

            if (_blacksiteMission != null && _blacksiteMission.Outcome == BlacksiteRetrievalOutcome.Pending)
                _blacksiteMission.Objectives.ConfirmDepartureServer();
            // #874 (2026-09-20): THIS is where the squad leaves the world - the transport has
            // actually completed its departure, and the men it carried go with it. They were
            // visible passengers for the whole climb; despawning them on the first frame of it
            // is the defect this replaces, and it is what broke the witness's custody in round 3.
            // The witness himself is not here: ConfirmDepartureServer above turns his Loaded
            // record into Extracted and despawns him on the contract's own terminal.
            DespawnBlacksitePassengers();
            TryDespawnObject(ship.gameObject);
            _blacksiteShip = null;
            _blacksiteMission = null;
            _active = false;
            _complete = true;
            SendStateToAll();
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.Blacksite] Blacksite Retrieval ship despawned after departure.");
        }

        /// <summary>
        /// #874 (2026-09-20): every member the transport carried home, retired with it. A member
        /// who was left on the moon is untouched: he is a survivor and stays contestable.
        /// </summary>
        private static void DespawnBlacksitePassengers()
        {
            for (int i = BlacksiteSoldiers.Count - 1; i >= 0; i--)
            {
                BlacksiteSoldierAI soldier = BlacksiteSoldiers[i];
                if (soldier == null || soldier.isEnemyDead || !soldier.HasBoardedShip) continue;
                _blacksiteShip?.ReleasePassengerServer(soldier.NetworkObjectId);
                TryDespawnObject(soldier.gameObject);
            }
        }

        /// <summary>
        /// #1219 A1: the executor of the one departure authority. The ship decides
        /// (<see cref="BlacksiteShipController.DepartureVerdict"/>, from
        /// <see cref="BlacksiteExtractionSequence.Departure"/>); this lifts off and, on a Stranded
        /// verdict (#1262 P2), leaves every living unboarded member behind as a survivor.
        /// </summary>
        private static void UpdateBlacksiteExtraction()
        {
            if (Time.time < _nextExtractionCheckTime)
                return;

            _nextExtractionCheckTime = Time.time + 0.5f;
            PruneBlacksiteSoldiers();
            if (_blacksiteShip == null || _blacksiteMission == null)
                return;
            // #874 (2026-09-20): the passenger manifest is reconciled before anything else, and
            // on every tick the escalation runs, so a member that boards is riding the hull from
            // the next half second and a member that dies or is put back on the moon stops being
            // a passenger at the same cadence. It is idempotent: the seat is measured once.
            SeatBoardedSoldiers();
            BlacksiteDepartureVerdict verdict = _blacksiteShip.DepartureVerdict;
            if (verdict == BlacksiteDepartureVerdict.Hold)
                return; // not-a-wait: ends on every living member aboard or on every unboarded member stranded (no route, no session)
            // False while the doorway or ladder veto holds (#1293 R2: a fold with no clock) and on
            // every tick after the lift-off, which is what makes this arm run once.
            if (!_blacksiteShip.TryBeginDepartureServer(18f))
                return; // not-a-wait: the lift-off veto holds only on terms that end on their own event, and on a member only until the progress ledger sees him miss
            int living = 0, boarded = 0;
            foreach (var soldier in BlacksiteSoldiers)
            {
                if (soldier == null || soldier.isEnemyDead) continue;
                living++;
                if (soldier.HasBoardedShip) boarded++;
            }
            BlacksiteDiagnostics.Log("Extraction",
                $"ship departing; reason={BlacksiteExtractionSequence.Token(verdict)}, boarded={boarded}/{living}, aboard={_blacksiteShip.PassengerCount}"
                + $", survivors={(BlacksiteDiagnostics.Enabled ? BlacksiteShipController.UnboardedMemberList(BlacksiteSoldiers) : "none")}");
            // #874 (2026-09-20): NOBODY is despawned here. The squad that boarded rides the hull
            // visibly for the whole eighteen-second climb and is despawned with the transport in
            // NotifyBlacksiteShipDeparted, which is what "completed departure" means. Despawning
            // them on the first frame is what killed round 3's witness: his escort stopped
            // existing, and the custody chain resolved that as escortDead.
            //
            // #1219 A1 / #1262 P2: the detach arm is the Stranded departure's: a transport leaving
            // without a man IS a transport loss for that man, whatever kept him off it.
            for(int i=BlacksiteSoldiers.Count-1;i>=0;i--)
            {
                var soldier=BlacksiteSoldiers[i];
                if (soldier == null || soldier.isEnemyDead || soldier.HasBoardedShip) continue;
                _blacksiteShip.ReleasePassengerServer(soldier.NetworkObjectId);
                _blacksiteMission.Objectives.DropCarrier(soldier, "transport departing without carrier");
                soldier.DetachFromTransportServer(_pendingOutsideMain);
            }
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.Blacksite] Independent ship departing; cargo loss resolves at confirmed departure.");
        }

        /// <summary>
        /// #874 (2026-09-20): every boarded member is a passenger of the transport, and nobody
        /// else is. <c>HasBoardedShip</c> is <c>isEnemyDead || _boardedShip</c>, so the casualty
        /// term is tested first: a corpse is not cargo.
        /// </summary>
        private static void SeatBoardedSoldiers()
        {
            for (int i = 0; i < BlacksiteSoldiers.Count; i++)
            {
                BlacksiteSoldierAI soldier = BlacksiteSoldiers[i];
                if (soldier == null) continue;
                if (!soldier.isEnemyDead && soldier.HasBoardedShip)
                    _blacksiteShip.SeatPassengerServer(soldier.NetworkObjectId, soldier, soldier.transform.position);
                else
                    _blacksiteShip.ReleasePassengerServer(soldier.NetworkObjectId);
            }
        }

        internal static void CollectActiveBlacksiteSoldiers(List<BlacksiteSoldierAI> buffer)
        {
            if (buffer == null)
                return;

            buffer.Clear();
            PruneBlacksiteSoldiers();
            for (int i = 0; i < BlacksiteSoldiers.Count; i++)
            {
                BlacksiteSoldierAI soldier = BlacksiteSoldiers[i];
                if (soldier != null && !soldier.isEnemyDead && soldier.gameObject != null && soldier.gameObject.activeInHierarchy)
                    buffer.Add(soldier);
            }
        }

        private static void PruneBlacksiteSoldiers()
        {
            for (int i = BlacksiteSoldiers.Count - 1; i >= 0; i--)
            {
                if (BlacksiteSoldiers[i] == null)
                    BlacksiteSoldiers.RemoveAt(i);
            }
        }

        private static GameObject SpawnBlacksiteShip(Vector3 position, Quaternion rotation)
        {
            GameObject obj = UnityEngine.Object.Instantiate(ContractAssetLoader.BlacksiteShipPrefab, position, rotation);
            obj.name = "BlacksiteRetrieval_Ship";
            RegisterSpawnedObject(obj);
            SpawnNetworkObject(obj);
            return obj;
        }

        private sealed class SoldierDeployment : BlacksiteDeploySlice
        {
            // Set by the landed notification, which turns the stow into the release.
            internal Vector3 LandingPosition;
            internal Quaternion LandingRotation;
            internal Vector3 OutsideMain;
            internal Vector3 InsideMain;
            internal Vector3 InsideFireExit;
            internal Vector3 OutsideFireExit;
            internal Vector3[] InteriorClearRoute;
            internal HashSet<int> KnownSoldierIds;
            // #1283 S7a: stowed members by index, with the moveTowardsDestination the stow suspended.
            internal readonly List<(BlacksiteSoldierAI Soldier, bool MoveTowardsDestination)> Stowed =
                new List<(BlacksiteSoldierAI, bool)>();

            protected override void ConfigureSquad() => _blacksiteMission.ConfigureSquadServer(
                LandingPosition, OutsideMain, InsideMain, InsideFireExit, OutsideFireExit, SoldierCount);
            protected override bool Stow(int member) => StowSoldier(RoundManager.Instance, this, member);
            protected override bool Release(int member) => ReleaseStowedSoldier(this, member);
            protected override void PublishInfiltrating() => _blacksiteMission.MarkInfiltratingServer();
        }

        private static SoldierDeployment _soldierDeployment;

        /// <summary>
        /// #1072: is the squad still being handed out by the deploy slice? #1283 S7a: from the ship's
        /// spawn (the stow) to the last member released at the door the roster is incomplete, and no
        /// rule may read a casualty out of it. The drain's own state, NOT a count:
        /// <see cref="ResetSoldierDeployment"/> clears it on completion, on a member failing
        /// mid-slice and on an abort, so it can never stick pending.
        /// </summary>
        internal static bool BlacksiteSoldierDeploymentPending => _soldierDeployment != null;

        private static void ResetSoldierDeployment()
        {
            _soldierDeployment = null;
        }

        /// <summary>One frame of the deployment under <see cref="BlacksiteDeploySlice"/>: one member
        /// is stowed per frame while the ship is inbound; once it is active, the squad's route, then
        /// one release per frame. Returns false when a member failed and the deployment must be
        /// aborted, or when the finished deployment is short of its squad.</summary>
        private static bool DrainSoldierDeployment()
        {
            SoldierDeployment deployment = _soldierDeployment;
            if (deployment == null)
                return true;

            if (RoundManager.Instance == null || _blacksiteMission == null)
            {
                ResetSoldierDeployment();
                return false;
            }

            switch (deployment.Drain(Time.frameCount, _active))
            {
                case BlacksiteDeployResult.Failed:
                    ResetSoldierDeployment();
                    return false;
                case BlacksiteDeployResult.Complete:
                    bool complete = BlacksiteSoldiers.Count == deployment.SoldierCount;
                    ResetSoldierDeployment();
                    return complete;
                default:
                    return true;
            }
        }

        // #1283 S7a: member i is spawned in the air on its ground-row deck slot, inside the closed hull in
        // every mode, and seated there, so the hull carries it on every peer. Its brain starts at the door.
        private static bool StowSoldier(RoundManager round, SoldierDeployment deployment, int i)
        {
            BlacksiteShipController ship = _blacksiteShip;
            if (ship == null || !ship.IsSpawned)
                return false;

            // WP-A5: one known-soldier scan per deployment, kept current with each new instance id.
            HashSet<int> soldiersBeforeSpawn = deployment.KnownSoldierIds ??= CaptureBlacksiteSoldierInstanceIds();
            Vector3 seat = ship.transform.TransformPoint(DeckSlotLocal(i, hovering: false));
            NetworkObjectReference spawnedReference;
            try
            {
                spawnedReference = round.SpawnEnemyGameObject(
                    seat,
                    ship.transform.eulerAngles.y,
                    -1,
                    ContractAssetLoader.BlacksiteSoldierEnemyType);
            }
            catch (Exception e)
            {
                CleanupNewBlacksiteSoldiers(round, soldiersBeforeSpawn, $"spawn {i} exception");
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] RoundManager soldier spawn {i} failed: {e.Message}");
                return false;
            }

            if (!spawnedReference.TryGet(out NetworkObject networkObject) || networkObject == null)
            {
                CleanupNewBlacksiteSoldiers(round, soldiersBeforeSpawn, $"spawn {i} returned no NetworkObject");
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] RoundManager soldier spawn {i} returned no NetworkObject.");
                return false;
            }

            GameObject obj = networkObject.gameObject;
            // The role is not known before the squad is configured; the spawn state renames it at the door.
            obj.name = $"BlacksiteRetrieval_Soldier_{i + 1}";
            RegisterSpawnedObject(obj);
            var ai = obj.GetComponent<BlacksiteSoldierAI>()
                ?? obj.GetComponentInChildren<BlacksiteSoldierAI>(true);
            if (ai == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Native soldier prefab '{obj.name}' has no BlacksiteSoldierAI.");
                return false;
            }

            int roundOwnershipCount = round.SpawnedEnemies.Count(enemy => enemy == ai);
            if (roundOwnershipCount != 1)
            {
                ContractsBootstrap.Log?.LogError($"[MoonContracts.Blacksite] Soldier {i} has invalid RoundManager ownership count {roundOwnershipCount}; aborting deployment.");
                return false;
            }

            // Vanilla EnemyAI.Update enables the owner's agent every frame and its AI interval steers
            // toward `destination`: nothing may steer in flight, and the hold leaves the body to the seat.
            deployment.Stowed.Add((ai, ai.moveTowardsDestination));
            ai.moveTowardsDestination = false;
            ai.Navigator.SetPassengerHold(true);
            ship.SeatPassengerServer(ai.NetworkObjectId, ai, seat);
            soldiersBeforeSpawn.Add(ai.GetInstanceID());
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Stowed native soldier {i + 1}/{deployment.SoldierCount} aboard the inbound aircraft, seat={seat}.");
            return true;
        }

        // #1283 S7a: member i leaves its seat at the door, is placed and bound on its validated slot
        // (before the hold hands the body back, so the navigator has nothing to rebind), then initialised.
        private static bool ReleaseStowedSoldier(SoldierDeployment deployment, int i)
        {
            Vector3 landingPosition = deployment.LandingPosition;
            (BlacksiteSoldierAI ai, bool moveTowardsDestination) = deployment.Stowed[i];
            if (ai == null || ai.isEnemyDead || _blacksiteShip == null)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Stowed soldier {i} is gone or dead at the door; deployment aborted.");
                return false;
            }

            _blacksiteShip.ReleasePassengerServer(ai.NetworkObjectId);
            ai.moveTowardsDestination = moveTowardsDestination;
            Vector3 offset = GetSoldierDeploymentOffset(deployment, i);
            Vector3 desiredPosition = landingPosition + offset;
            if (!TryResolveDeploymentPosition(desiredPosition, i, out var position)) return false;
            Vector3 look = (deployment.OutsideMain - position).WithY(0f);
            if (look.sqrMagnitude < 0.01f)
                look = Vector3.forward;
            ai.transform.SetPositionAndRotation(position, Quaternion.LookRotation(look.normalized, Vector3.up));

            var spawnAgent = ai.GetComponent<UnityEngine.AI.NavMeshAgent>() ?? ai.GetComponentInChildren<UnityEngine.AI.NavMeshAgent>(true);
            if (!BlacksiteShipNavigation.TryBindDeploymentAgent(spawnAgent, position, StartOfRound.Instance.collidersAndRoomMaskAndDefault))
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Soldier {i} could not bind to its validated deck slot; refusing a ground-under-ship release.");
                return false;
            }
            ai.Navigator.SetPassengerHold(false);

            if (!ai.InitializeServer(
                    _blacksiteMission,
                    landingPosition,
                    deployment.OutsideMain,
                    deployment.InsideMain,
                    deployment.OutsideFireExit,
                    i,
                    i,
                    offset,
                    deployment.InteriorClearRoute))
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Native soldier {i} rejected replicated mission initialization.");
                return false;
            }

            BlacksiteSoldiers.Add(ai);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Released native {ai.TacticalRole} {i + 1}/{deployment.SoldierCount} member={ai.MemberIndex}, squad={ai.SquadId}, formation={ai.FormationSlot}, seed={ai.AnimationSeed}, health={ai.ReplicatedHealth}, position={position}, desired={desiredPosition}, ship={landingPosition}, outsideMain={deployment.OutsideMain}, insideMain={deployment.InsideMain}.");
            return true;
        }

        private static HashSet<int> CaptureBlacksiteSoldierInstanceIds()
        {
            // #944 S11 U6: this is the call LethalPerformance names in
            // "BlacksiteSoldierAI search called with inactive objects" (run 1 line 33317 one frame
            // after "Mission state Inbound -> Deploying", run 2 lines 30688 and 53217, one per
            // deployment). LethalPerformance names the SEARCHED type, and these two methods are the
            // only FindObjectsOfType<BlacksiteSoldierAI> in the repo. Excluding inactive objects is
            // sound here: the only soldier that is ever deactivated is one CleanupNewBlacksiteSoldiers
            // below has already removed from BlacksiteSoldiers, SpawnedObjects and SpawnedEnemies and
            // handed to Destroy, so leaving it out of the "known before this deployment" set only
            // means a later cleanup may destroy it a second time, which is a no-op.
            BlacksiteSoldierAI[] soldiers = UnityEngine.Object.FindObjectsByType<BlacksiteSoldierAI>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            var instanceIds = new HashSet<int>();
            for (int i = 0; i < soldiers.Length; i++)
            {
                if (soldiers[i] != null)
                    instanceIds.Add(soldiers[i].GetInstanceID());
            }
            return instanceIds;
        }

        private static void CleanupNewBlacksiteSoldiers(
            RoundManager round,
            HashSet<int> soldiersBeforeSpawn,
            string context)
        {
            // #944 S11 U6: same search, same reason as CaptureBlacksiteSoldierInstanceIds. Nothing
            // deactivates a soldier between SpawnEnemyGameObject and this cleanup except this method,
            // so a newly spawned soldier is always active when the sweep runs.
            BlacksiteSoldierAI[] soldiers = UnityEngine.Object.FindObjectsByType<BlacksiteSoldierAI>(
                FindObjectsInactive.Exclude,
                FindObjectsSortMode.None);
            int cleaned = 0;

            for (int i = 0; i < soldiers.Length; i++)
            {
                BlacksiteSoldierAI soldier = soldiers[i];
                if (soldier == null
                    || (soldiersBeforeSpawn != null
                        && soldiersBeforeSpawn.Contains(soldier.GetInstanceID())))
                {
                    continue;
                }

                GameObject obj = soldier.gameObject;
                NetworkObject networkObject = obj.GetComponent<NetworkObject>()
                    ?? obj.GetComponentInChildren<NetworkObject>(includeInactive: true);

                BlacksiteSoldiers.Remove(soldier);
                SpawnedObjects.Remove(obj);
                round?.SpawnedEnemies?.Remove(soldier);

                try
                {
                    if (networkObject != null && networkObject.IsSpawned && IsServer())
                        networkObject.Despawn(destroy: true);
                    else
                    {
                        obj.SetActive(false);
                        UnityEngine.Object.Destroy(obj);
                    }
                }
                catch (Exception exception)
                {
                    obj.SetActive(false);
                    UnityEngine.Object.Destroy(obj);
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Blacksite] Fallback cleanup for incomplete soldier after {context}: {exception.Message}");
                }

                cleaned++;
            }

            if (cleaned > 0)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Blacksite] Cleaned {cleaned} incomplete soldier object(s) after {context}.");
            }
        }

        private static void RegisterSpawnedEnemy(EnemyAI enemy)
        {
            if (enemy == null || RoundManager.Instance == null || RoundManager.Instance.SpawnedEnemies == null)
                return;

            if (!RoundManager.Instance.SpawnedEnemies.Contains(enemy))
                RoundManager.Instance.SpawnedEnemies.Add(enemy);
        }

        private static bool TryResolveDeploymentPosition(Vector3 desiredPosition, int index, out Vector3 position)
        {
            position = Vector3.zero;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            var occupied = BlacksiteSoldiers.Where(s => s != null && !s.isEnemyDead).Select(s => s.transform.position).ToArray();
            var ship = _blacksiteShip; if (ship == null) return false;
            if (BlacksiteShipNavigation.TryDeploymentSlot(ship.transform, ContractAssetLoader.BlacksiteSoldierPrefab.transform,
                desiredPosition, ship.ApproachPosition, occupied, mask, out position)) return true;
            ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] No collision-clear deployment slot with a route to the door for member {index}; deployment aborted.");
            return false;
        }

        private static Vector3 GetSoldierDeploymentOffset(SoldierDeployment deployment, int index)
        {
            return deployment.LandingRotation * DeckSlotLocal(index, _blacksiteShip != null && _blacksiteShip.IsHovering);
        }

        // The vanilla hull occupies the old ship's rear deployment ring. Deploy inside its cleared aisle
        // so every member reaches the exterior through the actual ship door. #1283 S7a: the ground row
        // is every mode's stow seat, behind the door plane.
        private static Vector3 DeckSlotLocal(int index, bool hovering)
            => new Vector3((index%2==0?-.65f:.65f),.1f,(hovering ? 10.6f : 5.3f)-(index/2)*1.15f);

        private static bool TryGetStrangerSpawnPlacement(out Vector3 position, out Quaternion rotation, StrangerSpawnPerfContext perf = null)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (TryGetCachedStrangerSpawnPlacement(out position, out rotation, perf))
                return true;

            if (!TryBuildStrangerSpawnPlacementCacheImmediate(out StrangerSpawnPlacementCache cache, perf))
                return false;

            return TrySelectStrangerSpawnPlacement(cache, out position, out rotation, perf, "sync");
        }

        private static bool TryGetCachedStrangerSpawnPlacement(out Vector3 position, out Quaternion rotation, StrangerSpawnPerfContext perf)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            StrangerSpawnPlacementCache cache = _strangerSpawnPlacementCache;
            if (cache == null || !cache.Complete || cache.Candidates.Count == 0)
                return false;

            if (!IsCurrentStrangerSpawnCache(cache))
                return false;

            return TrySelectStrangerSpawnPlacement(cache, out position, out rotation, perf, "cache");
        }

        private static bool TrySelectStrangerSpawnPlacement(
            StrangerSpawnPlacementCache cache,
            out Vector3 position,
            out Quaternion rotation,
            StrangerSpawnPerfContext perf,
            string source)
        {
            position = Vector3.zero;
            rotation = Quaternion.identity;

            if (cache == null || cache.Candidates.Count == 0)
                return false;

            if (perf != null)
            {
                perf.Source = source;
                perf.CandidatesTotal = cache.Candidates.Count;
            }

            Vector3 best = Vector3.zero;
            float bestScore = float.MinValue;
            foreach (Vector3 candidate in cache.Candidates)
            {
                if (perf != null)
                    perf.CandidatesEvaluated++;

                float shipDistance = HorizontalDistance(candidate, cache.Ship);
                float fireDistance = HorizontalDistance(candidate, cache.FireExit);
                if (shipDistance < 48f || fireDistance < 12f || fireDistance > 44f)
                    continue;
                if (!HasStrangerSpawnClearanceOnNavMesh(candidate, perf))
                    continue;

                float firePenalty = Mathf.Abs(fireDistance - 28f) * 2.2f;
                float score = Mathf.Min(shipDistance, 130f) * 0.35f - firePenalty + UnityEngine.Random.Range(0f, 10f);
                if (score > bestScore)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best == Vector3.zero)
                return false;

            position = best + Vector3.up * 0.05f;
            rotation = GetStrangerRotation(position, cache.Ship);
            return true;
        }

        private static IEnumerator TrySelectStrangerSpawnPlacementBudgeted(
            StrangerSpawnPlacementCache cache,
            StrangerSpawnSelection selection,
            StrangerSpawnPerfContext perf)
        {
            if (selection == null)
                yield break;

            selection.Succeeded = false;
            selection.Position = Vector3.zero;
            selection.Rotation = Quaternion.identity;
            if (cache == null || cache.Candidates.Count == 0)
                yield break;

            if (perf != null)
            {
                perf.Source = "cache-budgeted";
                perf.CandidatesTotal = cache.Candidates.Count;
                perf.SelectionFrames = 1;
            }

            Vector3 best = Vector3.zero;
            float bestScore = float.MinValue;
            var frame = System.Diagnostics.Stopwatch.StartNew();
            for (int i = 0; i < cache.Candidates.Count; i++)
            {
                Vector3 candidate = cache.Candidates[i];
                if (perf != null)
                    perf.CandidatesEvaluated++;

                float shipDistance = HorizontalDistance(candidate, cache.Ship);
                float fireDistance = HorizontalDistance(candidate, cache.FireExit);
                if (shipDistance >= 48f &&
                    fireDistance >= 12f &&
                    fireDistance <= 44f &&
                    HasStrangerSpawnClearanceOnNavMesh(candidate, perf))
                {
                    float firePenalty = Mathf.Abs(fireDistance - 28f) * 2.2f;
                    float score = Mathf.Min(shipDistance, 130f) * 0.35f -
                        firePenalty +
                        UnityEngine.Random.Range(0f, 10f);
                    if (score > bestScore)
                    {
                        bestScore = score;
                        best = candidate;
                    }
                }

                if (!ShouldYieldStrangerSpawnCacheFrame(frame))
                    continue;

                if (perf != null)
                {
                    perf.SelectionCpuMilliseconds += frame.Elapsed.TotalMilliseconds;
                    perf.SelectionFrames++;
                }
                yield return null;
                frame.Restart();
            }

            if (perf != null)
                perf.SelectionCpuMilliseconds += frame.Elapsed.TotalMilliseconds;
            if (best == Vector3.zero)
                yield break;

            selection.Succeeded = true;
            selection.Position = best + Vector3.up * 0.05f;
            selection.Rotation = GetStrangerRotation(selection.Position, cache.Ship);
        }

        private static bool TryFindOutsideFireExitForStranger(out Vector3 position)
        {
            position = Vector3.zero;

            Vector3 ship = StartOfRound.Instance?.elevatorTransform != null
                ? StartOfRound.Instance.elevatorTransform.position
                : Vector3.zero;

            EntranceTeleport best = GetEntrances()
                .Where(entrance => entrance != null)
                .Where(entrance => entrance.entranceId != 0 && entrance.isEntranceToBuilding)
                .OrderByDescending(entrance => HorizontalDistance(GetEntrancePosition(entrance), ship))
                .FirstOrDefault();
            if (best == null)
                return false;

            position = GetEntrancePosition(best);
            return true;
        }

        private static void BeginStrangerSpawnPlacementCache()
        {
            if (_strangerSpawnPlacementCacheRoutine != null)
                return;

            if (_strangerSpawnPlacementCache != null && _strangerSpawnPlacementCache.Complete && IsCurrentStrangerSpawnCache(_strangerSpawnPlacementCache))
                return;

            MonoBehaviour runner = StartOfRound.Instance;
            if (runner == null)
                return;

            _strangerSpawnPlacementCacheRoutine = runner.StartCoroutine(BuildStrangerSpawnPlacementCacheCoroutine());
        }

        private static void StopStrangerSpawnPlacementCache(string reason)
        {
            if (_strangerSpawnPlacementCacheRoutine == null)
                return;

            MonoBehaviour runner = StartOfRound.Instance;
            if (runner != null)
            {
                try
                {
                    runner.StopCoroutine(_strangerSpawnPlacementCacheRoutine);
                }
                catch
                {
                    // The round object can be mid-teardown during run reset.
                }
            }

            _strangerSpawnPlacementCacheRoutine = null;
            ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Stranger] Stranger spawn placement cache stopped: {reason}.");
        }

        private static IEnumerator BuildStrangerSpawnPlacementCacheCoroutine()
        {
            var total = System.Diagnostics.Stopwatch.StartNew();
            var frame = System.Diagnostics.Stopwatch.StartNew();
            int frames = 1;
            var perf = new StrangerSpawnPerfContext { Source = "cache-build" };

            if (!TryInitializeStrangerSpawnPlacementCache(out StrangerSpawnPlacementCache cache))
            {
                _strangerSpawnPlacementCache = null;
                _strangerSpawnPlacementCacheRoutine = null;
                yield break;
            }

            _strangerSpawnPlacementCache = cache;

            GameObject[] nodes = GetOutsideAINodesForStrangerSpawn();
            if (nodes != null)
            {
                for (int i = 0; i < nodes.Length; i++)
                {
                    GameObject node = nodes[i];
                    if (node != null)
                        TryAddStrangerNodeCandidate(cache, node.transform.position, perf);

                    if (ShouldYieldStrangerSpawnCacheFrame(frame))
                    {
                        frames++;
                        yield return null;
                        frame.Restart();
                    }
                }
            }

            float[] radii = { 18f, 24f, 30f, 36f, 42f };
            Vector3 awayFromShip = GetStrangerFireExitDirectionAwayFromShip(cache.FireExit, cache.Ship);
            float baseAngle = Mathf.Atan2(awayFromShip.z, awayFromShip.x);
            for (int radiusIndex = 0; radiusIndex < radii.Length; radiusIndex++)
            {
                float radius = radii[radiusIndex];
                for (int i = 0; i < 12; i++)
                {
                    float angle = baseAngle + (i * 30f * Mathf.Deg2Rad);
                    Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                    TryAddStrangerRadialCandidate(cache, cache.FireExit + offset, perf);

                    if (ShouldYieldStrangerSpawnCacheFrame(frame))
                    {
                        frames++;
                        yield return null;
                        frame.Restart();
                    }
                }
            }

            cache.Complete = cache.Candidates.Count > 0;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Stranger] Stranger spawn perf: cache built complete={cache.Complete} " +
                $"candidates={cache.Candidates.Count} navSamples={perf.NavMeshSamples} frames={frames} " +
                $"elapsed={total.Elapsed.TotalMilliseconds:0.00}ms fireExit={cache.FireExit}.");
            _strangerSpawnPlacementCacheRoutine = null;
        }

        private static bool TryBuildStrangerSpawnPlacementCacheImmediate(out StrangerSpawnPlacementCache cache, StrangerSpawnPerfContext perf)
        {
            if (!TryInitializeStrangerSpawnPlacementCache(out cache))
                return false;

            GameObject[] nodes = GetOutsideAINodesForStrangerSpawn();
            if (nodes != null)
            {
                foreach (GameObject node in nodes)
                {
                    if (node != null)
                        TryAddStrangerNodeCandidate(cache, node.transform.position, perf);
                }
            }

            AddStrangerRadialCandidates(cache, perf);
            cache.Complete = cache.Candidates.Count > 0;
            _strangerSpawnPlacementCache = cache.Complete ? cache : null;
            return cache.Complete;
        }

        private static bool TryInitializeStrangerSpawnPlacementCache(out StrangerSpawnPlacementCache cache)
        {
            cache = null;
            if (!TryFindOutsideFireExitForStranger(out Vector3 fireExit))
                return false;

            Vector3 ship = StartOfRound.Instance?.elevatorTransform != null
                ? StartOfRound.Instance.elevatorTransform.position
                : Vector3.zero;

            cache = new StrangerSpawnPlacementCache
            {
                FireExit = fireExit,
                Ship = ship
            };
            return true;
        }

        private static bool IsCurrentStrangerSpawnCache(StrangerSpawnPlacementCache cache)
        {
            if (cache == null)
                return false;

            if (!TryFindOutsideFireExitForStranger(out Vector3 fireExit))
                return false;

            Vector3 ship = StartOfRound.Instance?.elevatorTransform != null
                ? StartOfRound.Instance.elevatorTransform.position
                : Vector3.zero;

            return HorizontalDistance(cache.FireExit, fireExit) <= 1f &&
                HorizontalDistance(cache.Ship, ship) <= 2f;
        }

        private static GameObject[] GetOutsideAINodesForStrangerSpawn()
        {
            GameObject[] nodes = RoundManager.Instance?.outsideAINodes;
            if (nodes == null || nodes.Length == 0)
                nodes = GameObject.FindGameObjectsWithTag("OutsideAINode");
            return nodes;
        }

        private static bool ShouldYieldStrangerSpawnCacheFrame(System.Diagnostics.Stopwatch frame)
        {
            return frame != null && frame.Elapsed.TotalMilliseconds >= StrangerSpawnCacheFrameBudgetMilliseconds;
        }

        private static void TryAddStrangerNodeCandidate(StrangerSpawnPlacementCache cache, Vector3 nodePosition, StrangerSpawnPerfContext perf)
        {
            if (cache == null)
                return;

            if (HorizontalDistance(nodePosition, cache.FireExit) > 54f)
                return;

            if (!TryResolveNavMeshPosition(nodePosition, 12f, out Vector3 resolved, perf))
                return;

            if (HorizontalDistance(resolved, cache.FireExit) <= 44f)
                AddUniqueStrangerSpawnCandidate(cache.Candidates, resolved);
        }

        private static void AddStrangerRadialCandidates(StrangerSpawnPlacementCache cache, StrangerSpawnPerfContext perf)
        {
            if (cache == null)
                return;

            Vector3 awayFromShip = GetStrangerFireExitDirectionAwayFromShip(cache.FireExit, cache.Ship);
            float baseAngle = Mathf.Atan2(awayFromShip.z, awayFromShip.x);
            foreach (float radius in new[] { 18f, 24f, 30f, 36f, 42f })
            {
                for (int i = 0; i < 12; i++)
                {
                    float angle = baseAngle + (i * 30f * Mathf.Deg2Rad);
                    Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                    TryAddStrangerRadialCandidate(cache, cache.FireExit + offset, perf);
                }
            }
        }

        private static void TryAddStrangerRadialCandidate(StrangerSpawnPlacementCache cache, Vector3 origin, StrangerSpawnPerfContext perf)
        {
            if (cache == null)
                return;

            if (TryResolveNavMeshPosition(origin, 18f, out Vector3 candidate, perf))
                AddUniqueStrangerSpawnCandidate(cache.Candidates, candidate);
        }

        private static Vector3 GetStrangerFireExitDirectionAwayFromShip(Vector3 fireExit, Vector3 ship)
        {
            Vector3 awayFromShip = (fireExit - ship).WithY(0f);
            if (awayFromShip.sqrMagnitude < 4f)
                awayFromShip = Vector3.forward;
            awayFromShip.Normalize();
            return awayFromShip;
        }

        private static void AddUniqueStrangerSpawnCandidate(List<Vector3> candidates, Vector3 candidate)
        {
            if (candidates != null && candidates.All(existing => HorizontalDistance(existing, candidate) > 6f))
                candidates.Add(candidate);
        }

        private static bool HasStrangerSpawnClearance(Vector3 position, StrangerSpawnPerfContext perf = null)
        {
            if (perf != null)
                perf.NavMeshSamples++;

            if (!NavMesh.SamplePosition(position, out NavMeshHit hit, 4f, NavMesh.AllAreas))
                return false;
            if (Mathf.Abs(hit.position.y - position.y) > 3f)
                return false;

            return HasStrangerSpawnClearanceOnNavMesh(hit.position, perf);
        }

        private static bool HasStrangerSpawnClearanceOnNavMesh(Vector3 position, StrangerSpawnPerfContext perf = null)
        {
            if (perf != null)
                perf.ClearanceChecks++;

            Collider[] hits = Physics.OverlapSphere(position + Vector3.up * 1.25f, 1.15f, ~0, QueryTriggerInteraction.Ignore);
            foreach (Collider collider in hits)
            {
                if (collider == null || collider.isTrigger || collider is TerrainCollider)
                    continue;
                Bounds bounds = collider.bounds;
                if (bounds.max.y <= position.y + 0.8f)
                    continue;

                string name = FormatColliderName(collider).ToLowerInvariant();
                if (name.Contains("terrain") || name.Contains("ground") || name.Contains("floor"))
                    continue;

                return false;
            }

            return true;
        }

        private static Quaternion GetStrangerRotation(Vector3 position, Vector3 ship)
        {
            Vector3 look = (ship - position).WithY(0f);
            return look.sqrMagnitude > 0.01f
                ? Quaternion.LookRotation(look.normalized, Vector3.up)
                : Quaternion.identity;
        }

        // Sliced physical landing search; also exercised directly by the extracted-moon checks.
        // #874 S6: the next begin step, one per frame until the search exists: 0 no job, 1 scene
        // sweeps (ladders, entrance, nodes), 2 entrance approach, 3 crew-ship datum and the search.
        // #1262 P9: 4-6 follow the resolved job in TryStartBlacksiteRetrieval (entrances, interior
        // route, ship spawn); the job itself is gone by then.
        private static int _landingBeginStep;
        private static Vector3 _landingShip, _landingMain;
        // #874 plan B: the job's one entrance approach, resolved once before the search exists and
        // handed to it, so no pose, node or phase re-derives the door. It lives exactly as long as the job.
        private static BlacksiteEntranceApproach _landingApproach;
        private static float _landingFootToDeck;
        private static BlacksiteLandingSearch _landingSearch;
        private static float _landingSearchStarted, _landingSearchReportAt;
        // The job's scene sweep (step 1), read by the entrance approach (step 2) and the search (step 3).
        private static Vector3[] _landingNodes = Array.Empty<Vector3>();
        private static int _landingMask = ~0;
        private static void ResetLandingPlacementJob()
        {
            if (_landingSearch != null && !_landingSearch.Complete)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Landing search cancelled by round/escalation reset after {Time.realtimeSinceStartup - _landingSearchStarted:0.0}s: {_landingSearch.Progress}, {_landingSearch.Summary}.");
            _landingBeginStep = 0;
            _landingSearch = null;
            _landingNodes = Array.Empty<Vector3>();
            _landingApproach = null;
        }

        private static void BeginLandingPlacementStep()
        {
            // #874 Q11: 42-70 ms on 23 September as one frame; #874 S6 split it into these steps.
            using var begin = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.BeginLandingJob);
            if (_landingBeginStep == 1)
            {
                ResetLandingPlacementJob();
                BlacksiteWorldLadder.All.Clear();
                foreach (var trigger in UnityEngine.Object.FindObjectsOfType<InteractTrigger>())
                    if (trigger.isLadder && trigger.bottomOfLadderPosition != null && trigger.topOfLadderPosition != null && trigger.ladderPlayerPositionNode != null)
                        BlacksiteWorldLadder.All.Add(new BlacksiteWorldLadder(trigger.bottomOfLadderPosition, trigger.topOfLadderPosition, trigger.ladderPlayerPositionNode, () => trigger == null || trigger.usingLadder));
                _landingSearchStarted = Time.realtimeSinceStartup;
                _landingSearchReportAt = _landingSearchStarted + 10f;
                _landingShip = StartOfRound.Instance?.elevatorTransform != null
                    ? StartOfRound.Instance.elevatorTransform.position
                    : Vector3.zero;
                _landingMain = FindOutsideMainEntrancePosition();
                var landingNodes = RoundManager.Instance?.outsideAINodes;
                if (landingNodes == null || landingNodes.Length == 0)
                    landingNodes = GameObject.FindGameObjectsWithTag("OutsideAINode");
                _landingNodes = landingNodes.Where(n => n != null).Select(n => n.transform.position).ToArray();
                _landingMask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
                _landingBeginStep = 2;
                return;
            }
            if (_landingBeginStep == 2)
            {
                // #874 plan B: every candidate route ends at the same entrance approach, so it is
                // resolved here, once, and a door that has none refuses the whole job before any hull
                // or cable work.
                _landingApproach = BlacksiteLandingGeometry.ResolveEntranceApproach(_landingMain, _landingMask,
                    () => BlacksiteLandingGeometry.OutsideReach(_landingNodes, _landingMain, _landingMask));
                _landingBeginStep = 3;
                return;
            }
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Landing search begins: outsideMain={_landingMain}, entranceNav={_landingApproach.Nav}, entranceReachable={_landingApproach.Resolved} ({_landingApproach}), crewShip={_landingShip}, nodes={_landingNodes.Length}.");
            LogCrewShipDatum(_landingMask);
            _landingFootToDeck = BlacksiteLandingGearPolicy.ResolveFootToDeck(_datumDrops[0], _datumDrops[1], _datumDrops[2], out string footNote);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Landing gear target: footToDeck={_landingFootToDeck:0.00} ({footNote}).");
            int members = Mathf.Clamp(ContractsBootstrap.BlacksiteRetrievalSoldierCount.Value, 1, BlacksiteExtractionSequence.MaxSquadMembers);
            _landingSearch = new BlacksiteLandingSearch(_landingApproach, _landingShip,
                _landingNodes, _landingMask,
                _landingFootToDeck, members);
        }

        private static readonly RaycastHit[] DatumHits = new RaycastHit[16];
        private static readonly float[] _datumDrops = { float.NaN, float.NaN, float.NaN };
        /// <summary>#873: measures how far the crew ship's deck floats above the moon terrain (three
        /// rays, ship-local x -2, 0, +2) so the landing-gear target is measured, not guessed. NaN
        /// where a ray finds no moon ground.</summary>
        private static bool TryMeasureCrewShipDatum(int mask, out float a, out float b, out float c, out string deckName)
        {
            a = b = c = float.NaN; deckName = "none";
            Transform deck = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
            if (deck == null) return false;
            Transform hull = CrewHull(deck); deckName = deck.name;
            a = TerrainBelow(deck.position + deck.right * -2f, hull, mask);
            b = TerrainBelow(deck.position, hull, mask);
            c = TerrainBelow(deck.position + deck.right * 2f, hull, mask);
            return true;
        }
        // Exclude the crew ship's own floor and belly colliders: only moon terrain is the datum.
        private static Transform CrewHull(Transform deck)
        {
            Transform hull = deck;
            for (Transform node = deck; node != null; node = node.parent)
                if (node.name.StartsWith("HangarShip", StringComparison.Ordinal)) hull = node;
            return hull;
        }
        /// <summary>One-shot per landing search: the datum plus how far the crew hull hangs below
        /// its deck (renderer sweep), and the deck transform's name so a log proves which node
        /// the datum was taken from.</summary>
        private static void LogCrewShipDatum(int mask)
        {
            _datumDrops[0] = _datumDrops[1] = _datumDrops[2] = float.NaN;
            if (!TryMeasureCrewShipDatum(mask, out _datumDrops[0], out _datumDrops[1], out _datumDrops[2], out string deckName)) return;
            Transform deck = StartOfRound.Instance.elevatorTransform; Transform hull = CrewHull(deck);
            string drops = string.Empty;
            for (int i = 0; i < 3; i++) drops += (i > 0 ? ", " : string.Empty) + (float.IsNaN(_datumDrops[i]) ? "none" : _datumDrops[i].ToString("0.00"));
            float lowest = float.PositiveInfinity; string lowestName = "none";
            var renderers = hull.GetComponentsInChildren<Renderer>(true);
            if (renderers != null)
                foreach (var renderer in renderers)
                {
                    if (renderer == null) continue;
                    float bottom = renderer.bounds.min.y;
                    if (bottom < lowest) { lowest = bottom; lowestName = renderer.name; }
                }
            string hullText = float.IsPositiveInfinity(lowest) ? "none" : (lowest - deck.position.y).ToString("0.00") + " m";
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Crew ship datum: deckY={deck.position.y:0.00}, terrainBelowDeck=[{drops}] m, lowestHull={hullText} ({lowestName}), deck={deckName}.");
        }

        private static float TerrainBelow(Vector3 origin, Transform hull, int mask)
        {
            int count = Physics.RaycastNonAlloc(origin + Vector3.up * .5f, Vector3.down, DatumHits, 60f, mask, QueryTriggerInteraction.Ignore);
            float best = float.NaN;
            for (int i = 0; i < count; i++)
            {
                var hit = DatumHits[i];
                if (hit.collider == null || (hull != null && hit.collider.transform.IsChildOf(hull))) continue;
                float drop = origin.y - hit.point.y;
                if (drop < -.01f) continue;
                if (float.IsNaN(best) || drop < best) best = drop;
            }
            return best;
        }

        /// <summary>Returns false while the job still owes work. When it returns true the job is
        /// finished and reset; <paramref name="placed"/> says whether the search sealed a plan.</summary>
        private static bool AdvanceLandingPlacementJob(out bool placed, out BlacksiteLandingPlan plan)
        {
            placed = false; plan = default;
            if (_landingSearch == null) { BeginLandingPlacementStep(); return false; }
            _landingSearch.Advance();
            if (!_landingSearch.Complete)
            {
                if (Time.realtimeSinceStartup >= _landingSearchReportAt)
                {
                    _landingSearchReportAt = Time.realtimeSinceStartup + 10f;
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Landing search progress after {Time.realtimeSinceStartup - _landingSearchStarted:0.0}s: {_landingSearch.Progress}.");
                }
                return false;
            }
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Landing placement evaluated {_landingSearch.Summary}.");
            placed = _landingSearch.Found;
            if (placed)
            {
                plan = _landingSearch.Plan;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Landing accepted origin={plan.Origin} yaw={plan.Rotation.eulerAngles.y:0.0} {_landingSearch.Detail} drop={plan.Access.DropHeight:0.00}m ladder={plan.Access.LadderSide}, walkingRoute={_landingSearch.RouteMetres:0.0}m (both directions).");
            }
            else
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Blacksite] Could not find a valid ship landing point ({_landingSearch.FailureReason ?? "no reason recorded"}); escalation skipped.");
            // #873 S12 U5 (4): one line per landing job, whatever the job decided. outcome= and
            // reason= come first; maxSliceMs= is the same number Summary prints and is what the
            // S11H U3 live gate reads its slice measurement from. drop= is the plan's deployment
            // foot and station= the pose the ship holds; both none when no plan was sealed.
            BlacksiteDiagnostics.Log("Landing", BlacksiteLandingSearch.FormatLandingLine(
                placed ? (plan.Hover ? "hover" : "ground") : "none", _landingSearch.FailureReason,
                Time.realtimeSinceStartup - _landingSearchStarted, _landingSearch.Slices,
                _landingSearch.MaxSliceMilliseconds, _landingSearch.Approaches,
                placed ? plan.Foot : (Vector3?)null, placed ? plan.Origin : (Vector3?)null, _landingSearch.LipNavGap));
            ResetLandingPlacementJob();
            return true;
        }

        private static string FormatColliderName(Collider collider)
        {
            if (collider == null)
                return "unknown";
            string objectName = collider.gameObject != null ? collider.gameObject.name : collider.name;
            string parentName = collider.transform != null && collider.transform.parent != null ? collider.transform.parent.name : string.Empty;
            return string.IsNullOrWhiteSpace(parentName) ? objectName : $"{parentName}/{objectName}";
        }

        private static Vector3 FindOutsideMainEntrancePosition()
        {
            EntranceTeleport best = FindEntrance(entrance => entrance.entranceId == 0 && entrance.isEntranceToBuilding)
                ?? FindEntrance(entrance => entrance.entranceId == 0);
            if (best != null)
                return best.entrancePoint != null ? best.entrancePoint.position : best.transform.position;

            try
            {
                return RoundManager.FindMainEntrancePosition();
            }
            catch
            {
                return StartOfRound.Instance?.elevatorTransform != null ? StartOfRound.Instance.elevatorTransform.position : Vector3.zero;
            }
        }

        // #1305 C1 (D6): also the Payload placer's main door (PayloadCartPlacer), which names the main-entrance room from it.
        internal static Vector3 FindInsideMainEntrancePosition()
        {
            Vector3 outsideMain = FindOutsideMainEntrancePosition();
            EntranceTeleport best = FindEntrance(entrance => entrance.entranceId == 0 && !entrance.isEntranceToBuilding)
                ?? FindEntrance(entrance => entrance.entranceId == 0);
            if (best != null)
            {
                Vector3 position = best.entrancePoint != null ? best.entrancePoint.position : best.transform.position;
                if (IsLikelyInteriorPosition(position, outsideMain))
                    return position;
            }

            GameObject[] nodes = RoundManager.Instance?.insideAINodes;
            if (nodes != null && nodes.Length > 0)
            {
                GameObject nearest = nodes
                    .Where(node => node != null)
                    .OrderBy(node => HorizontalDistance(node.transform.position, outsideMain))
                    .FirstOrDefault();
                if (nearest != null)
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Main interior entrance teleport unavailable; using nearest inside AI node {nearest.transform.position}.");
                    return nearest.transform.position;
                }
            }

            ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Main interior entrance could not be resolved; falling back to outside main {outsideMain}.");
            return outsideMain;
        }

        private static Vector3 FindInsideFireExitPosition(Vector3 fallback)
        {
            EntranceTeleport best = FindEntrance(entrance => entrance.entranceId != 0 && !entrance.isEntranceToBuilding, fallback, farthest: true);
            if (best != null)
                return best.entrancePoint != null ? best.entrancePoint.position : best.transform.position;

            GameObject[] nodes = RoundManager.Instance?.insideAINodes;
            if (nodes != null && nodes.Length > 0)
            {
                GameObject farthest = nodes
                    .Where(node => node != null)
                    .OrderByDescending(node => HorizontalDistance(node.transform.position, fallback))
                    .FirstOrDefault();
                if (farthest != null)
                    return farthest.transform.position;
            }

            return fallback;
        }

        private static bool IsLikelyInteriorPosition(Vector3 position, Vector3 outsideReference)
        {
            if (position.y < -40f)
                return true;
            if (outsideReference.y < -40f)
                return position.y < outsideReference.y + 8f;
            return position.y < outsideReference.y - 25f;
        }

        private static Vector3[] FindInteriorClearRoute(Vector3 insideMain, Vector3 insideFireExit)
        {
            var route = new List<Vector3>();
            GameObject[] nodes = RoundManager.Instance?.insideAINodes;
            if (nodes != null && nodes.Length > 0)
            {
                Vector3 mainToFire = (insideFireExit - insideMain).WithY(0f);
                float routeLength = Mathf.Max(1f, mainToFire.magnitude);
                Vector3 routeDirection = routeLength > 0.01f ? mainToFire.normalized : Vector3.forward;

                var candidates = nodes
                    .Where(node => node != null)
                    .Select(node => node.transform.position)
                    .Where(pos => HorizontalDistance(pos, insideMain) > 16f)
                    .Select(pos =>
                    {
                        Vector3 fromMain = (pos - insideMain).WithY(0f);
                        float projected = Vector3.Dot(fromMain, routeDirection);
                        float progress = Mathf.Clamp01(projected / routeLength);
                        float fireDistance = HorizontalDistance(pos, insideFireExit);
                        return new { Position = pos, Progress = progress, FireDistance = fireDistance };
                    })
                    .Where(candidate => candidate.Progress > 0.18f && candidate.Progress < 0.95f)
                    .OrderBy(candidate => candidate.Progress)
                    .ToList();

                foreach (float targetProgress in new[] { 0.28f, 0.52f, 0.76f })
                {
                    var selected = candidates
                        .Where(candidate => route.All(existing => HorizontalDistance(existing, candidate.Position) > 12f))
                        .OrderBy(candidate => Mathf.Abs(candidate.Progress - targetProgress))
                        .ThenBy(candidate => candidate.FireDistance)
                        .FirstOrDefault();
                    if (selected != null)
                        route.Add(ResolveNavMeshPosition(selected.Position, 10f));
                }
            }

            if (route.Count == 0 || HorizontalDistance(route[route.Count - 1], insideFireExit) > 5f)
                route.Add(ResolveNavMeshPosition(insideFireExit, 10f));

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Blacksite] Interior clear route has {route.Count} point(s): {string.Join(" -> ", route.Select(p => p.ToString()))}.");
            return route.ToArray();
        }

        private static Vector3 FindOutsideFireExitPosition(Vector3 fallback, Vector3 interiorFireExit)
        {
            EntranceTeleport best = FindEntrance(entrance => entrance.entranceId != 0 && entrance.isEntranceToBuilding, interiorFireExit, farthest: false);
            if (best != null)
                return best.entrancePoint != null ? best.entrancePoint.position : best.transform.position;
            return fallback;
        }

        /// <summary>Entrance-teleport cache (WP-A5). The Blacksite trigger frame resolved four
        /// entrance positions, each with its own <c>FindObjectsOfType&lt;EntranceTeleport&gt;</c>.
        /// Entrances do not move once the dungeon is generated, so one scan serves the whole
        /// resolution; the array is re-scanned if a cached entry has been destroyed, and dropped by
        /// <see cref="ResetRunState"/>.</summary>
        private static EntranceTeleport[] _cachedEntrances;

        private static EntranceTeleport[] GetEntrances()
        {
            if (_cachedEntrances != null)
            {
                bool stale = _cachedEntrances.Length == 0;
                for (int i = 0; !stale && i < _cachedEntrances.Length; i++)
                {
                    if (_cachedEntrances[i] == null)
                        stale = true;
                }

                if (!stale)
                    return _cachedEntrances;
            }

            _cachedEntrances = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
            return _cachedEntrances;
        }

        private static EntranceTeleport FindEntrance(Func<EntranceTeleport, bool> predicate)
        {
            return GetEntrances()
                .Where(entrance => entrance != null)
                .Where(predicate)
                .OrderBy(entrance => entrance.entranceId)
                .FirstOrDefault();
        }

        private static EntranceTeleport FindEntrance(Func<EntranceTeleport, bool> predicate, Vector3 reference, bool farthest)
        {
            var ordered = GetEntrances()
                .Where(entrance => entrance != null)
                .Where(predicate);

            return farthest
                ? ordered.OrderByDescending(entrance => HorizontalDistance(GetEntrancePosition(entrance), reference)).FirstOrDefault()
                : ordered.OrderBy(entrance => HorizontalDistance(GetEntrancePosition(entrance), reference)).FirstOrDefault();
        }

        private static Vector3 GetEntrancePosition(EntranceTeleport entrance)
        {
            if (entrance == null)
                return Vector3.zero;
            return entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;
        }

        private static void SpawnNetworkObject(GameObject obj)
        {
            if (obj == null || !IsServer())
                return;

            NetworkObject networkObject = obj.GetComponent<NetworkObject>();
            if (networkObject == null || networkObject.IsSpawned)
                return;

            try
            {
                networkObject.Spawn();
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] Network spawn failed for {obj.name}: {e.Message}");
            }
        }

        internal static void RegisterSpawnedObject(GameObject obj)
        {
            if (obj != null && !SpawnedObjects.Contains(obj))
                SpawnedObjects.Add(obj);
        }

        private static void TryDespawnObject(GameObject obj)
        {
            if (obj == null)
                return;

            if (TryDespawnNativeBlacksiteSoldier(obj))
                return;

            SpawnedObjects.Remove(obj);
            UnregisterSpawnedEnemy(obj);
            RetireSpawnedObject(obj);
        }

        /// <summary>Retires one spawned object. A live <see cref="NetworkObject"/> may only be
        /// retired by the server: destroying one on a client tears it out from under Netcode and
        /// desyncs the session, and the server's despawn is what removes it on that client anyway.
        /// Anything that is not a live network object - a client-local prop, an object whose spawn
        /// failed - is destroyed directly.</summary>
        private static void RetireSpawnedObject(GameObject obj)
        {
            if (obj == null)
                return;

            NetworkObject networkObject = obj.GetComponent<NetworkObject>();
            bool networkOwned = networkObject != null && networkObject.IsSpawned;
            if (networkOwned && !IsServer())
                return;

            try
            {
                if (networkOwned)
                    networkObject.Despawn(destroy: true);
                else
                    UnityEngine.Object.Destroy(obj);
            }
            catch
            {
                UnityEngine.Object.Destroy(obj);
            }
        }

        private static void DespawnSpawnedObjects()
        {
            for (int i = SpawnedObjects.Count - 1; i >= 0; i--)
            {
                GameObject obj = SpawnedObjects[i];
                if (obj == null)
                    continue;

                if (TryDespawnNativeBlacksiteSoldier(obj))
                    continue;

                UnregisterSpawnedEnemy(obj);
                RetireSpawnedObject(obj);
            }

            SpawnedObjects.Clear();
            BlacksiteSoldiers.Clear();
            _blacksiteShip = null;
        }

        private static bool TryDespawnNativeBlacksiteSoldier(GameObject obj)
        {
            if (obj == null || !IsServer() || RoundManager.Instance == null)
                return false;

            BlacksiteSoldierAI soldier = obj.GetComponent<BlacksiteSoldierAI>()
                ?? obj.GetComponentInChildren<BlacksiteSoldierAI>(true);
            NetworkObject networkObject = obj.GetComponent<NetworkObject>()
                ?? obj.GetComponentInChildren<NetworkObject>(true);
            if (soldier == null || networkObject == null || !networkObject.IsSpawned)
                return false;

            SpawnedObjects.Remove(obj);
            BlacksiteSoldiers.Remove(soldier);
            RoundManager.Instance.DespawnEnemyOnServer(networkObject);
            return true;
        }

        private static void UnregisterSpawnedEnemy(GameObject obj)
        {
            if (obj == null || RoundManager.Instance == null || RoundManager.Instance.SpawnedEnemies == null)
                return;

            EnemyAI enemy = obj.GetComponent<EnemyAI>() ?? obj.GetComponentInChildren<EnemyAI>();
            if (enemy != null)
                RoundManager.Instance.SpawnedEnemies.Remove(enemy);
        }

        private static Vector3 ResolveNavMeshPosition(Vector3 origin, float maxDistance)
        {
            if (TryResolveNavMeshPosition(origin, maxDistance, out Vector3 position))
                return position;
            return origin;
        }

        private static bool TryResolveNavMeshPosition(Vector3 origin, float maxDistance, out Vector3 position, StrangerSpawnPerfContext perf = null)
        {
            if (perf != null)
                perf.NavMeshSamples++;

            if (NavMesh.SamplePosition(origin, out NavMeshHit hit, maxDistance, NavMesh.AllAreas))
            {
                position = hit.position;
                return true;
            }

            position = origin;
            return false;
        }

        private sealed class StrangerSpawnPlacementCache
        {
            internal Vector3 FireExit;
            internal Vector3 Ship;
            internal bool Complete;
            internal readonly List<Vector3> Candidates = new List<Vector3>();
        }

        private sealed class StrangerSpawnPerfContext
        {
            internal string Source = "sync";
            internal int NavMeshSamples;
            internal int ClearanceChecks;
            internal int CandidatesTotal;
            internal int CandidatesEvaluated;
            internal int SelectionFrames;
            internal double SelectionCpuMilliseconds;
        }

        private sealed class StrangerSpawnSelection
        {
            internal bool Succeeded;
            internal Vector3 Position;
            internal Quaternion Rotation = Quaternion.identity;
        }

        private static void EnsureNetworkHandlers()
        {
            NetworkManager manager = NetworkManager.Singleton;

            // #716/G2: attach/detach and flush before the registration latch's early return, so the
            // late-join subscription tracks the singleton every tick rather than only on the pass
            // that (re)registers the handlers.
            _lateJoinResync.Sync(manager);

            if (manager == null || manager.CustomMessagingManager == null || !manager.IsListening)
                return;

            if (_networkHandlersRegistered && _registeredNetworkManager == manager)
                return;

            if (_networkHandlersRegistered && _registeredNetworkManager != null && _registeredNetworkManager.CustomMessagingManager != null)
            {
                TryUnregister(_registeredNetworkManager, MsgState);
                TryUnregister(_registeredNetworkManager, MsgCrateOpenRequest);
                TryUnregister(_registeredNetworkManager, MsgCrateOpened);
            }

            try
            {
                manager.CustomMessagingManager.RegisterNamedMessageHandler(MsgState, OnReceiveState);
                manager.CustomMessagingManager.RegisterNamedMessageHandler(MsgCrateOpenRequest, OnReceiveCrateOpenRequest);
                manager.CustomMessagingManager.RegisterNamedMessageHandler(MsgCrateOpened, OnReceiveCrateOpened);
                _registeredNetworkManager = manager;
                _networkHandlersRegistered = true;
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.Blacksite] Network message handlers registered.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Network handler registration skipped: {e.Message}");
            }
        }

        private static void TryUnregister(NetworkManager manager, string message)
        {
            try
            {
                manager.CustomMessagingManager.UnregisterNamedMessageHandler(message);
            }
            catch
            {
                // Transport can already be torn down during disconnects.
            }
        }

        private static void SendStateToAll()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager?.CustomMessagingManager == null || !manager.IsServer)
                return;

            // #1343 S2: the abort reason is a variable-length tail, so the writer may grow.
            var writer = new FastBufferWriter(128, Allocator.Temp, 1024);
            try
            {
                WriteState(ref writer);
                manager.CustomMessagingManager.SendNamedMessageToAll(MsgState, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>#716/G2: the same payload aimed at one late joiner. See
        /// <see cref="EscalationLateJoinResync"/> for why this is targeted and retried.</summary>
        private static void SendStateToClient(ulong clientId)
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager?.CustomMessagingManager == null || !manager.IsServer)
                return;

            var writer = new FastBufferWriter(128, Allocator.Temp, 1024);
            try
            {
                WriteState(ref writer);
                manager.CustomMessagingManager.SendNamedMessage(MsgState, clientId, writer);
            }
            finally
            {
                writer.Dispose();
            }
        }

        /// <summary>Single definition of the state payload so the broadcast and the late-join
        /// resend can never drift apart from <see cref="OnReceiveState"/>'s read order.</summary>
        private static void WriteState(ref FastBufferWriter writer)
        {
            writer.WriteValueSafe((int)_runtimeEscalation);
            writer.WriteValueSafe(_strangerActive);
            writer.WriteValueSafe(_strangerComplete);
            writer.WriteValueSafe(_strangerScheduled);
            writer.WriteValueSafe(_active);
            writer.WriteValueSafe(_landingInProgress);
            writer.WriteValueSafe(_complete);
            writer.WriteValueSafe(_announcementShown);
            writer.WriteValueSafe(_cratesOpened);
            writer.WriteValueSafe(_cratesTotal);
            writer.WriteValueSafe(_strangerTargetPlayerIndex);
            // #1343 S2, appended after #725's tail so the earlier fields keep their order.
            writer.WriteValueSafe(_blacksiteAborted);
            writer.WriteValueSafe(_blacksiteAbortServerTime);
            writer.WriteValueSafe(_blacksiteAbortReason ?? string.Empty, oneByteChars: false);
        }

        private static void SendCrateOpened(ulong networkObjectId)
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager?.CustomMessagingManager == null || !manager.IsServer)
                return;

            using (var writer = new FastBufferWriter(sizeof(ulong) + sizeof(int) * 2, Allocator.Temp))
            {
                writer.WriteValueSafe(networkObjectId);
                writer.WriteValueSafe(_cratesOpened);
                writer.WriteValueSafe(_cratesTotal);
                manager.CustomMessagingManager.SendNamedMessageToAll(MsgCrateOpened, writer);
            }
        }

        private static void OnReceiveState(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerMessage(senderClientId))
                return;

            try
            {
                bool wasStrangerActive = _strangerActive;
                bool wasAborted = _blacksiteAborted;
                reader.ReadValueSafe(out int runtimeEscalation);
                reader.ReadValueSafe(out _strangerActive);
                reader.ReadValueSafe(out _strangerComplete);
                reader.ReadValueSafe(out _strangerScheduled);
                reader.ReadValueSafe(out _active);
                reader.ReadValueSafe(out _landingInProgress);
                reader.ReadValueSafe(out _complete);
                reader.ReadValueSafe(out _announcementShown);
                reader.ReadValueSafe(out _cratesOpened);
                reader.ReadValueSafe(out _cratesTotal);
                // #725: appended last so an older host's shorter payload still parses above.
                _strangerTargetPlayerIndex = -1;
                if (reader.TryBeginRead(sizeof(int)))
                    reader.ReadValue(out _strangerTargetPlayerIndex);
                // #1343 S2: the same appended-tail pattern; an older host's payload leaves the defaults.
                _blacksiteAborted = false;
                _blacksiteAbortServerTime = 0d;
                _blacksiteAbortReason = string.Empty;
                if (reader.TryBeginRead(sizeof(bool) + sizeof(double)))
                {
                    reader.ReadValue(out _blacksiteAborted);
                    reader.ReadValue(out _blacksiteAbortServerTime);
                    reader.ReadValueSafe(out _blacksiteAbortReason, oneByteChars: false);
                }
                _runtimeEscalation = (RuntimeEscalationKind)runtimeEscalation;

                // #1343 S2: bands fire on rising edges only, so a duplicate or a late-join resend
                // never re-announces; an abort older than TerminalFreshSeconds is never replayed.
                if (EscalationAnnouncementEdges.ShouldAnnounce(
                        EscalationAnnouncementKind.StrangerHunting, wasStrangerActive, _strangerActive, 0d))
                    EscalationAnnouncements.Show(EscalationAnnouncementKind.StrangerHunting);
                if (EscalationAnnouncementEdges.ShouldAnnounce(
                        EscalationAnnouncementKind.BlacksiteAborted, wasAborted, _blacksiteAborted,
                        EscalationAnnouncementEdges.EventAge(_blacksiteAbortServerTime, ServerNow())))
                    EscalationAnnouncements.Show(EscalationAnnouncementKind.BlacksiteAborted);
                // Every other Blacksite lifecycle change is shown by the contract strip on every peer.
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Failed reading state message: {e.Message}");
            }
        }

        private static void OnReceiveCrateOpenRequest(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServer())
                return;

            try
            {
                reader.ReadValueSafe(out ulong networkObjectId);
                ShadowRaidCargoCrate crate = ShadowRaidCargoCrate.FindByNetworkId(networkObjectId);
                if (crate == null || crate.Opened)
                    return;

                PlayerControllerB opener = FindPlayerByClientId(senderClientId);
                if (opener == null)
                    return;

                crate.OpenFromServer(opener);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Failed reading crate request: {e.Message}");
            }
        }

        private static void OnReceiveCrateOpened(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerMessage(senderClientId))
                return;

            try
            {
                reader.ReadValueSafe(out ulong networkObjectId);
                reader.ReadValueSafe(out _cratesOpened);
                reader.ReadValueSafe(out _cratesTotal);
                _active = true;
                _landingInProgress = false;
                _complete = _cratesTotal > 0 && _cratesOpened >= _cratesTotal;

                ShadowRaidCargoCrate crate = ShadowRaidCargoCrate.FindByNetworkId(networkObjectId);
                if (crate != null)
                    crate.ApplyOpenedLocal();
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Failed reading crate-opened message: {e.Message}");
            }
        }

        private static PlayerControllerB FindPlayerByClientId(ulong clientId)
        {
            var players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            foreach (PlayerControllerB player in players)
            {
                if (player == null)
                    continue;
                if (player.actualClientId == clientId)
                    return player;
            }

            return null;
        }

        private static bool CanCurrentLevelReceiveEscalation()
        {
            var level = StartOfRound.Instance?.currentLevel;
            if (level == null || !level.spawnEnemiesAndScrap)
                return false;

            string name = level.PlanetName ?? string.Empty;
            return name.IndexOf("Gordion", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("Company", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("Shutter", StringComparison.OrdinalIgnoreCase) < 0
                && name.IndexOf("Liquidation", StringComparison.OrdinalIgnoreCase) < 0;
        }

        private static bool IsRoundRuntimeActive()
        {
            var start = StartOfRound.Instance;
            return start != null
                && !start.inShipPhase
                && !start.shipIsLeaving
                && start.shipHasLanded;
        }

        private static bool IsServer()
        {
            return RoundManager.Instance != null && RoundManager.Instance.IsServer;
        }

        private static bool IsServerMessage(ulong senderClientId)
        {
            return senderClientId == NetworkManager.ServerClientId;
        }

        /// <summary>#1343 S2: the clock both the host's abort stamp and a client's age check read,
        /// the same source the Congregation stamps its ceremony with.</summary>
        private static double ServerNow()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening ? manager.ServerTime.Time : Time.timeAsDouble;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        private static Vector3 WithY(this Vector3 value, float y)
        {
            value.y = y;
            return value;
        }
    }
}
