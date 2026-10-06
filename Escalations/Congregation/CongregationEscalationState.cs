using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Escalations._Shared;
using Y4NGZCompany.Core.Compat;
namespace Y4NGZCompany.Escalations.Congregation
{
    internal static class CongregationEscalationState
    {
        private const string MsgState = "lgu.contracts.escalation.cult.state";
        private const float DetectionSuspenseHoldSeconds = 6f;
        private const float DetectionSuspenseFadeSeconds = 1.35f;
        private const float DetectionSuspenseVolume = 0.78f;
        // #845 D drum placement: behind the fire (the ritual rotation faces the main entrance),
        // outside the 3.65 m ring, +Z turned away from the fire; the drummer kneels beyond it.
        private const float DrumBehindFireMeters = 5.2f;
        private const float DrumSampleRadius = 2f;
        // #845 I+J: all three 2.62 m rigs land the real stick at frame 37 from this shared root.
        // Facing stays drum-local -Z; the drum box clears the body even within 120 mm arrival error.
        private static readonly Vector3 DrummerOffsetInDrumFrame = new Vector3(0.26f, 0f, 0.90f);
        // The spot is always snapped onto the NavMesh so the drummer can path to it; a snap beyond
        // the tolerance shifts the strike by that much and is logged once.
        private const float DrummerSpotSampleRadius = 1f;
        private const float DrummerSpotAlignmentToleranceMeters = 0.10f;
        // #845 C: the drum's footprint for server-side local avoidance and the player blocker.
        private const float DrumObstacleRadius = 0.34f;
        private const float DrumObstacleHeight = 0.6f;
        private static readonly Vector3 DrumBlockerSize = new Vector3(0.62f, 0.56f, 0.62f);
        // #845 D / #1336 ceremony sources: custom distance curves from CongregationAudioMix,
        // priority 64, one OccludeAudio.
        private const int CeremonySourcePriority = 64;
        // Loops due now (or already running, for a late joiner) share one DSP start instant with
        // their offsets computed for that instant.
        private const double LoopScheduleLeadSeconds = 0.1;
        // Schedule a future loop entry once it is this close, so PlayScheduled lands it exactly.
        private const double LoopScheduleWindowSeconds = 0.5;
        private const float DrumPhaseResyncSeconds = 4f;
        // #845 D far spawn: outside AI nodes 60-150 m out, hidden from every living player.
        private const float FarSpawnMinDistance = 60f;
        private const float FarSpawnMaxDistance = 150f;
        private const float NearSpawnMinDistance = 30f;
        private const float SpawnVisibilityRange = 90f;
        private const float SpawnHeadHeight = 2.07f;
        private const int SpawnPathChecksPerTier = 10;
        private const float RitualFireLightBaseIntensity = 9.5f;
        private const float RitualFireLightFlickerIntensity = 2.8f;
        // Fixed at the mean of the former 26 m + Perlin*6 m flicker. Writing Light.range every frame
        // invalidated HDRP's cached light data each frame; the flicker is intensity-only now.
        private const float RitualFireLightRange = 29f;

        private static readonly List<GameObject> SpawnedObjects = new List<GameObject>();
        private static readonly List<CongregantAI> Cultists = new List<CongregantAI>();
        private static readonly Dictionary<ulong, Vector3> LastPlayerPositions = new Dictionary<ulong, Vector3>();
        // #435: keyed on the CustomMessagingManager because NGO builds a new one for every
        // StartHost/StartClient while NetworkManager.Singleton survives the process; a
        // NetworkManager-keyed latch never re-registers in a session's second lobby.
        private static CustomMessagingManager _registeredMessaging;
        private static bool _networkHandlersRegistered;
        /// <summary>#716/G2: resends the ceremony state to a player who joined after the
        /// Congregation started. Without it a late joiner gets no tip, no ritual fire and no
        /// ceremony audio, because the state message is only sent on change. Serviced from
        /// <see cref="EnsureNetworkHandlers"/>, which runs every tick.</summary>
        private static readonly EscalationLateJoinResync _lateJoinResync =
            new EscalationLateJoinResync("[MoonContracts.Cult]", SendStateToClient);
        private static bool _scheduled;
        private static bool _rollFailed;
        private static bool _active;
        private static bool _complete;
        private static bool _ruined;
        private static bool _ceremonyStarted;
        private static bool _detectionTriggered;
        private static bool _aggroReleased;
        private static float _triggerTime;
        private static float _travelStartTime;
        private static float _stareEndTime;
        private static float _nextThinkTime;
        private static float _lastProgressTickTime;
        // Server: per-ceremony arrival timeout from the longest spawn-to-spot path (#845 D).
        private static float _arrivalTimeoutSeconds = CongregationProgress.MinArrivalTimeoutSeconds;
        private static int _lastRuntimeFrame = -1;
        private static int _cultistsTotal;
        private static int _cultistsKilled;
        private static bool _seraphimCultistCleanupActive;
        /// <summary>Ship-leave left the ceremony standing; the drain is owed (#540).</summary>
        private static bool _departureTeardownPending;
        private static bool _seraphimWhiteCultistsCleared;
        private static bool _seraphimFleeReleased;
        private static Vector3 _ritualPoint;
        private static Quaternion _ritualRotation = Quaternion.identity;
        private static CultistRitualPhase _phase = CultistRitualPhase.Traveling;
        private static CultistRitualPhase _pausedRitualPhase = CultistRitualPhase.Phase1Idle;
        private static PlayerControllerB _detectedPlayer;
        private static ulong _detectedClientId;
        private static SeraphimEnemyAI _spawnedSeraphim;
        private static GameObject _localFireObject;
        private static Material _ritualFlameMaterial;
        private static bool _ritualFlameMaterialResolved;

        // ---- #845 D progress ceremony -------------------------------------------------------------
        // Server-owned: the progress model, the drummer and the drum site. Every field below the
        // model is in the named message (appended after the ritual point), so a late joiner gets
        // the drum, the audio offsets and the objective percentage from the existing resend.
        private static CongregationProgress _progress;
        private static CongregantAI _drummer;
        private static Vector3 _drumSpot;
        private static Vector3 _drummerFacePoint;
        private static int _drumBeatsCounted;
        private static int _progressPercent;
        private static Vector3 _drumPosition;
        private static float _drumYaw;
        private static double _ceremonyStartServerTime = -1d;
        // #1343 S2: when the host completed or ruined the ceremony, so a client tells a fresh
        // terminal event from a late-join resend of an old one. 0 = no terminal event.
        private static double _ceremonyEndServerTime;
        private static bool _drumActive;
        private static double _drumLoopStartServerTime = -1d;
        private static double _tempoRampStartServerTime = -1d;
        private static bool _hasDrummer;
        private static ulong _drummerNetworkObjectId;

        // Every peer: the local drum object, its two sources and the playback bookkeeping.
        private static GameObject _localDrumObject;
        private static AudioSource _blowsSource;
        private static AudioSource _drumSource;
        private static bool _localBlowsHandled;
        // Immutable DSP/sample origin for this peer's chants; resumes never re-estimate it from
        // ServerTime (NGO can correct that clock while an existing source keeps playing).
        private static double _localChantRun = -1d;
        private static double _localChantDspOrigin;
        // The drum-loop run (its server start time) this peer has scheduled; -1 when silent.
        private static double _localDrumRun = -1d;
        private static double _localDrumAudibleAt;
        private static CongregantAI _localDrummer;
        private static ulong _localDrummerId;
        private static bool _localDrummerInDrumState;
        private static float _nextDrumPhaseResyncTime;
        private static AudioSource _detectionSuspenseSource;
        private static float _detectionSuspenseFadeStartTime;
        private static float _detectionSuspenseStopTime;
        private static FieldInfo _voicePlayerStateField;
        private static bool _voicePlayerStateFieldResolved;
        // One speaking-flag accessor per concrete voice state type, resolved once; a null entry
        // records a type with no usable member so the lookup is not repeated every tick.
        private static readonly Dictionary<Type, Func<object, bool>> VoiceSpeakingAccessors = new Dictionary<Type, Func<object, bool>>();
        private static readonly string[] VoiceSpeakingMemberNames = { "IsSpeaking", "isSpeaking", "Speaking", "speaking" };

        internal static bool IsActive => _active || (_complete && !_ruined);
        internal static bool IsActiveOrScheduled => _active || _scheduled;
        internal static bool HasRolledOrActive => _scheduled || _rollFailed || _active || _complete || _ruined;
        internal static string ActiveTitle => IsActive ? "THE CONGREGATION" : string.Empty;

        /// <summary>
        /// True from the summon until Seraphim dies or despawns (null or
        /// <c>isEnemyDead</c>). The monitor row's scramble belt runs for exactly
        /// this window.
        /// </summary>
        internal static bool HasSpawnedSeraphim =>
            _spawnedSeraphim != null && !_spawnedSeraphim.isEnemyDead;

        internal static bool ScheduleRuntimeSelection()
        {
            if (_scheduled || _active)
                return true;
            if (_complete || _ruined || _rollFailed)
                return false;
            if (!ContractsBootstrap.CultEscalationEnabled.Value)
            {
                _rollFailed = true;
                return false;
            }

            ScheduleCultEscalation();
            return _scheduled;
        }

        /// <summary>Resets the ceremony. <paramref name="deferEntityTeardown"/> leaves the cultists,
        /// the Seraphim and the ritual fire standing for <see cref="CompleteDeferredTeardown"/> to
        /// retire at vanilla's end-of-round sweep (#540).</summary>
        internal static void ResetRunState(bool deferEntityTeardown = false)
        {
            DiscardSeraphimPrewarm("run reset");
            if (deferEntityTeardown)
            {
                _departureTeardownPending = true;
            }
            else
            {
                _departureTeardownPending = false;
                DespawnSpawnedObjects();
                DestroyLocalFire();
                Cultists.Clear();
            }
            StopDetectionSuspense();
            LastPlayerPositions.Clear();
            PendingCultistSpawns.Clear();
            _lastCultistDrainFrame = -1;
            // Per round, not per session: an overflow is moon-specific evidence, and a session latch
            // would hide it on every moon after the first one that tripped it.
            _ritualClearanceOverflowLogged = false;
            ResetRitualPlacementJob();
            ResetFleeReleaseQueue();
            _cachedOutsideMainValid = false;
            _scheduled = false;
            _rollFailed = false;
            _active = false;
            _complete = false;
            _ruined = false;
            _ceremonyEndServerTime = 0d;
            _ceremonyStarted = false;
            _detectionTriggered = false;
            _aggroReleased = false;
            _triggerTime = 0f;
            _travelStartTime = 0f;
            _stareEndTime = 0f;
            _nextThinkTime = 0f;
            ResetCeremonyProgressState();
            _lastRuntimeFrame = -1;
            _cultistsTotal = 0;
            _cultistsKilled = 0;
            _seraphimCultistCleanupActive = false;
            _seraphimWhiteCultistsCleared = false;
            _seraphimFleeReleased = false;
            _spawnedSeraphim = null;
            _ritualPoint = Vector3.zero;
            _ritualRotation = Quaternion.identity;
            _phase = CultistRitualPhase.Traveling;
            _pausedRitualPhase = CultistRitualPhase.Phase1Idle;
            _detectedPlayer = null;
            _detectedClientId = 0UL;
        }

        /// <summary>Clears the #845 D progress, drummer and drum-audio clock fields. Runs at round
        /// reset and when a ceremony starts; the local drum object goes with the fire.</summary>
        private static void ResetCeremonyProgressState()
        {
            StopLocalChants();
            _progress = null;
            _drummer = null;
            _drumSpot = Vector3.zero;
            _drummerFacePoint = Vector3.zero;
            _drumBeatsCounted = 0;
            _progressPercent = 0;
            _drumPosition = Vector3.zero;
            _drumYaw = 0f;
            _ceremonyStartServerTime = -1d;
            _drumActive = false;
            _drumLoopStartServerTime = -1d;
            _tempoRampStartServerTime = -1d;
            _hasDrummer = false;
            _drummerNetworkObjectId = 0UL;
            _lastProgressTickTime = 0f;
            _arrivalTimeoutSeconds = CongregationProgress.MinArrivalTimeoutSeconds;
        }

        /// <summary>Server: the phase a cultist arriving at its spot takes up (#845 D): the
        /// ceremony's ritual phase once it has started (the paused one during a stare), else none,
        /// so a pre-ceremony arrival waits in Traveling for <see cref="StartCeremony"/>.</summary>
        internal static bool TryGetArrivalRitualPhase(out CultistRitualPhase phase)
        {
            phase = _phase == CultistRitualPhase.Stare ? _pausedRitualPhase : _phase;
            return _active && _ceremonyStarted && !_complete && !_ruined && IsRitualPhase(phase);
        }

        /// <summary>Retires whatever the ship-leave reset left standing (#540). Idempotent, and
        /// null-safe against the cultists vanilla's sweep has already despawned.</summary>
        internal static void CompleteDeferredTeardown(string reason)
        {
            if (!_departureTeardownPending)
                return;

            _departureTeardownPending = false;
            DespawnSpawnedObjects();
            DestroyLocalFire();
            Cultists.Clear();
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Cult] Deferred departure teardown ran ({reason}).");
        }

        /// <summary><see cref="MoonEscalationState"/> calls this twice in a frame when the Congregation
        /// is the rolled escalation: once ungated for the client ceremony audio, once on the server's
        /// Congregation branch with <paramref name="canSchedule"/>. The per-frame work (network
        /// handlers, ceremony audio, flee drain, ceremony tick) runs on the first call only; the
        /// second call only services the schedule, which is all it ever added.</summary>
        internal static void UpdateRuntime(bool canSchedule)
        {
            int frame = Time.frameCount;
            if (_lastRuntimeFrame != frame)
            {
                _lastRuntimeFrame = frame;
                EnsureNetworkHandlers();

                if (!IsRoundRuntimeActive())
                {
                    StopLocalCeremonyAudio();
                    return;
                }

                UpdateLocalCeremonyAudio();

                if (!IsServer())
                    return;

                // The only live part of the old Seraphim cultist cleanup tick: its kill timer and
                // cleanup flag had no reader left, TryGetNextSeraphimCultistTarget owns both.
                DrainFleeReleases();
                PumpSeraphimPrewarm();

                if (_active)
                {
                    DrainPendingCultistSpawn();
                    UpdateActiveCeremony();
                    return;
                }
            }
            else if (_active || !IsRoundRuntimeActive() || !IsServer())
            {
                return;
            }

            if (!canSchedule || _complete || _ruined || _rollFailed)
                return;

            if (!ContractsBootstrap.CultEscalationEnabled.Value)
                return;

            // Scheduling itself happens in ScheduleRuntimeSelection, which MoonEscalationState calls
            // before this whenever nothing is scheduled, rolled or active.
            if (_scheduled && Time.time >= _triggerTime)
                TryStartCultEscalation();
        }

        internal static List<MoonContractObjective> GetActiveObjectives()
        {
            if (!IsActive) return new List<MoonContractObjective>();
            bool ascension = _complete && !_ruined;
            // Clients read the percentage from the named message; the key stays stable so each
            // new number updates the strip in place instead of reading as a new objective.
            string ceremonyText = EscalationAnnouncementCopyTable.CongregationStrip(_ceremonyStarted, _progressPercent);
            return new List<MoonContractObjective>
            {
                new MoonContractObjective(
                    ascension ? ContractHudScramble.GetText((int)(Time.unscaledTime * 4f)) : ceremonyText,
                    0, 1, kind: MoonContractObjectiveKind.Status,
                    key: ascension ? "cult.broadcast.ascension" : "cult.broadcast.ceremony",
                    lethal: ascension)
            };
        }

        internal static void NotifyCultistKilled(CongregantAI cultist)
        {
            if (!_active || cultist == null || !IsServer())
                return;

            _cultistsKilled = Mathf.Clamp(_cultistsKilled + 1, 0, Mathf.Max(1, _cultistsTotal));
            if (!_complete && cultist.IsWhiteCultist)
            {
                RuinCeremony("white ritual leader killed before ritual completion");
                return;
            }

            PruneCultists();
            if (!_complete && Cultists.Count == 0 && PendingCultistSpawns.Count == 0)
            {
                RuinCeremony("all cultists killed before ritual completion");
                return;
            }

            if (cultist == _drummer)
            {
                _drummer = null;
                PromoteNextDrummer();
                RefreshDrumActive(ServerNow());
            }

            SendStateToAll();
        }

        private static void ScheduleCultEscalation()
        {
            if (!CanCurrentLevelReceiveEscalation())
            {
                _rollFailed = true;
                return;
            }

            bool force = ContractsBootstrap.CultEscalationForce.Value;

            float minDelay = Mathf.Max(10f, ContractsBootstrap.CultEscalationMinDelaySeconds.Value);
            float maxDelay = Mathf.Max(minDelay, ContractsBootstrap.CultEscalationMaxDelaySeconds.Value);
            float delay = force ? Mathf.Clamp(minDelay, 20f, 45f) : UnityEngine.Random.Range(minDelay, maxDelay);
            _triggerTime = Time.time + delay;
            _scheduled = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] The Congregation scheduled in {Mathf.Max(0f, _triggerTime - Time.time):0}s.");
        }

        /// <summary>The placement search is a multi-frame job (WP-A5): the trigger frame was measured
        /// at 41.71ms, almost all of it inside the candidate sweep. <c>_scheduled</c> stays true while
        /// the job runs so <c>UpdateRuntime</c> keeps re-entering here; it is cleared only when the
        /// job resolves. The ritual therefore starts at the same class of site, a few frames later.</summary>
        private static void TryStartCultEscalation()
        {
            if (!_placementJobActive)
            {
                if (!ContractAssetLoader.CultistAssetsReady)
                {
                    _scheduled = false;
                    _rollFailed = true;
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.Cult] Missing one or more cultist prefabs; escalation skipped.");
                    return;
                }

                PendingRoles.Clear();
                PendingRoles.AddRange(BuildRoleList());
                if (PendingRoles.Count == 0)
                {
                    _scheduled = false;
                    _rollFailed = true;
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.Cult] No cultists configured; escalation skipped.");
                    return;
                }

                BeginRitualPlacementJob();
            }

            if (!AdvanceRitualPlacementJob(out bool placed, out Vector3 ritualPoint, out Quaternion ritualRotation))
                return;

            _scheduled = false;
            if (!placed)
            {
                _rollFailed = true;
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Cult] Could not find a flat open ritual site; escalation skipped.");
                return;
            }

            _ritualPoint = ritualPoint;
            _ritualRotation = ritualRotation;
            List<CultistRole> roles = PendingRoles;

            Cultists.Clear();
            SpawnedObjects.Clear();
            ResetCeremonyProgressState();
            _active = true;
            _complete = false;
            _ruined = false;
            _ceremonyEndServerTime = 0d;
            _ceremonyStarted = false;
            _detectionTriggered = false;
            _aggroReleased = false;
            _phase = CultistRitualPhase.Traveling;
            _pausedRitualPhase = CultistRitualPhase.Phase1Idle;
            _travelStartTime = Time.time;
            _cultistsTotal = roles.Count;
            _cultistsKilled = 0;
            _detectedPlayer = null;
            _detectedClientId = 0UL;
            _progress = new CongregationProgress(BuildProgressSettings());
            ResolveDrumSite();

            SpawnCultists(roles);
            SendStateToAll();
            EscalationAnnouncements.Show(EscalationAnnouncementKind.CongregationGathering);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] The Congregation started. Ritual={_ritualPoint}, cultists={roles.Count}.");
        }

        private static List<CultistRole> BuildRoleList()
        {
            var roles = new List<CultistRole>();
            for (int i = 0; i < Mathf.Clamp(ContractsBootstrap.CultEscalationRedCount.Value, 0, 8); i++)
                roles.Add(CultistRole.Red);
            for (int i = 0; i < Mathf.Clamp(ContractsBootstrap.CultEscalationBlackCount.Value, 0, 8); i++)
                roles.Add(CultistRole.Black);
            for (int i = 0; i < Mathf.Clamp(ContractsBootstrap.CultEscalationWhiteCount.Value, 0, 3); i++)
                roles.Add(CultistRole.White);
            return roles;
        }

        private struct PendingCultistSpawn
        {
            internal CultistRole Role;
            internal Vector3 Spawn;
            internal Vector3 RitualSpot;
            internal bool IsDrummer;
            internal int Index;
            internal int Total;
        }

        private static readonly Queue<PendingCultistSpawn> PendingCultistSpawns = new Queue<PendingCultistSpawn>();

        private static CongregationProgressSettings BuildProgressSettings()
        {
            return new CongregationProgressSettings
            {
                RedPrayThreshold = ContractsBootstrap.CultCeremonyRedPrayProgress.Value,
                AllPrayThreshold = ContractsBootstrap.CultCeremonyAllPrayProgress.Value,
                BeatProgress = ContractsBootstrap.CultCeremonyBeatProgress.Value,
                PrayLoopProgress = ContractsBootstrap.CultCeremonyPrayLoopProgress.Value,
                AllPresentProgressPerSecond = ContractsBootstrap.CultCeremonyAllPresentProgressPerSecond.Value,
                StallRuinSeconds = ContractsBootstrap.CultCeremonyStallRuinSeconds.Value,
                SpeedUpEtaSeconds = ContractsBootstrap.CultCeremonySpeedUpEtaSeconds.Value,
                SpeedUpProgress = ContractsBootstrap.CultCeremonySpeedUpProgress.Value,
            };
        }

        /// <summary>Server, once per ceremony (#845 D): the drum stands behind the fire relative to
        /// the ritual rotation, outside the ring, with its +Z facing away from the fire; the
        /// drummer's spot is the measured offset in the drum frame. Every peer builds the local
        /// drum from the synced position and yaw.</summary>
        private static void ResolveDrumSite()
        {
            Quaternion yawRotation = Quaternion.Euler(0f, _ritualRotation.eulerAngles.y, 0f);
            Quaternion drumRotation = yawRotation * Quaternion.Euler(0f, 180f, 0f);
            _drumYaw = drumRotation.eulerAngles.y;
            _drumPosition = ResolveNavMeshPosition(_ritualPoint + yawRotation * new Vector3(0f, 0f, -DrumBehindFireMeters), DrumSampleRadius);
            Vector3 rawDrumSpot = _drumPosition + drumRotation * DrummerOffsetInDrumFrame;
            if (NavMesh.SamplePosition(rawDrumSpot, out NavMeshHit hit, DrummerSpotSampleRadius, NavMesh.AllAreas))
            {
                _drumSpot = hit.position;
                float error = HorizontalDistance(hit.position, rawDrumSpot);
                if (error > DrummerSpotAlignmentToleranceMeters)
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Drummer spot snapped {error:0.00} m horizontally onto the NavMesh; the drum strike is off by that much.");
            }
            else
            {
                _drumSpot = rawDrumSpot;
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Cult] No NavMesh within {DrummerSpotSampleRadius:0.0} m of the drummer spot {rawDrumSpot}; the drummer walks to the nearest reachable point instead.");
            }
            _drummerFacePoint = _drumSpot + drumRotation * new Vector3(0f, 0f, -2f);
        }

        // #705: cultists spawn one per runtime pass instead of the whole congregation in
        // the trigger frame; each Instantiate + navmesh activation + network spawn is a
        // slice of what was a 40ms+ schedule-branch stall. The first cultist still spawns
        // in the trigger frame so the ceremony is never live with zero cultists, and the
        // travel phase absorbs the sub-second stagger.
        private static void SpawnCultists(List<CultistRole> roles)
        {
            PendingCultistSpawns.Clear();
            // #845 D: the first black congregant by index is the drummer; the ring slots are laid
            // out for everyone else.
            int drummerIndex = roles.IndexOf(CultistRole.Black);
            int ringTotal = drummerIndex >= 0 ? roles.Count - 1 : roles.Count;
            var spots = new Vector3[roles.Count];
            int ringIndex = 0;
            for (int i = 0; i < roles.Count; i++)
            {
                spots[i] = i == drummerIndex
                    ? _drumSpot
                    : ResolveNavMeshPosition(_ritualPoint + GetRitualSpotOffset(_ritualRotation, ringIndex++, ringTotal, roles[i]), 6f);
            }

            var spawnPositions = ResolveCultistSpawnPositions(_ritualPoint, _ritualRotation, spots, out float longestPath);
            _arrivalTimeoutSeconds = CongregationProgress.ArrivalTimeoutSeconds(longestPath);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Longest spawn-to-spot path {longestPath:0.0} m; arrival timeout {_arrivalTimeoutSeconds:0} s; drum={_drumPosition}, drummerSpot={_drumSpot}.");
            for (int i = 0; i < roles.Count; i++)
            {
                PendingCultistSpawns.Enqueue(new PendingCultistSpawn
                {
                    Role = roles[i],
                    Spawn = spawnPositions[i],
                    RitualSpot = spots[i],
                    IsDrummer = i == drummerIndex,
                    Index = i,
                    Total = roles.Count,
                });
            }

            DrainPendingCultistSpawn();
        }

        /// <summary>#705 queues one cultist per runtime pass, but <see cref="MoonEscalationState"/>
        /// calls this state's <c>UpdateRuntime</c> twice in the same frame - once ungated for the
        /// client ceremony audio, once again on the server's Congregation branch - so the queue was
        /// popping two cultists a frame and halving the slice the queue exists to create. The frame
        /// latch restores the intended one-per-frame cadence without changing either call site's
        /// purpose (2026-09-02 audit, WP-A5).</summary>
        private static int _lastCultistDrainFrame = -1;

        private static void DrainPendingCultistSpawn()
        {
            if (PendingCultistSpawns.Count == 0 || !_active)
                return;
            if (_lastCultistDrainFrame == Time.frameCount)
                return;

            _lastCultistDrainFrame = Time.frameCount;
            PendingCultistSpawn pending = PendingCultistSpawns.Dequeue();
            Vector3 ritualSpot = pending.RitualSpot;
            Quaternion rotation = LookRotationToward(pending.Spawn, _ritualPoint);
            GameObject prefab = ContractAssetLoader.GetCultistPrefab(pending.Role);
            GameObject obj = UnityEngine.Object.Instantiate(prefab, pending.Spawn, rotation);
            obj.name = $"TheCongregation_{pending.Role}_Cultist_{pending.Index + 1}";
            ContractAssetLoader.TryActivateAgentOnNavMesh(obj, 6f);
            var ai = obj.GetComponent<CongregantAI>();
            if (ai != null)
            {
                ai.InitializeServer(pending.Role, _ritualPoint, ritualSpot, pending.Index);
                Cultists.Add(ai);
                RegisterSpawnedEnemy(ai);
                // The planned drummer walks straight to the drum; a later black takes the drum
                // instead if the planned one already died mid-drain.
                if (pending.Role == CultistRole.Black && _drummer == null)
                {
                    if (pending.IsDrummer)
                        ai.MarkDrummerServer(_drummerFacePoint);
                    else
                        ai.AssignDrummerServer(_drumSpot, _drummerFacePoint);
                    _drummer = ai;
                }
            }

            RegisterSpawnedObject(obj);
            SpawnNetworkObject(obj);
            ai?.SyncInitialState();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Spawned {pending.Role} cultist {pending.Index + 1}/{pending.Total} at {pending.Spawn}; ritualSpot={ritualSpot}.");
        }

        private static void UpdateActiveCeremony()
        {
            if (Time.time < _nextThinkTime)
                return;

            _nextThinkTime = Time.time + 0.2f;
            PruneCultists();

            // The pending queue guards keep a mid-drain ceremony (#705) from reading as
            // "all cultists killed" or as an early full arrival.
            if (!_complete && Cultists.Count == 0 && PendingCultistSpawns.Count == 0)
            {
                RuinCeremony("all cultists killed before ritual completion");
                return;
            }

            // A drummer despawned without KillEnemy (NotifyCultistKilled handles a death) still
            // hands the drum on.
            if (!ReferenceEquals(_drummer, null) && (_drummer == null || _drummer.isEnemyDead))
            {
                _drummer = null;
                PromoteNextDrummer();
            }

            double now = ServerNow();
            bool drumChanged = RefreshDrumActive(now);

            if (_phase == CultistRitualPhase.Traveling)
            {
                // #845 D: the drummer reaching the drum starts the ceremony (and its audio); the
                // others keep walking to their ring spots. With no black congregant alive the old
                // gate applies: everyone arrived. The arrival timeout covers both.
                bool timedOut = PendingCultistSpawns.Count == 0 && Time.time - _travelStartTime >= _arrivalTimeoutSeconds;
                bool ready = _drummer != null
                    ? _drummer.IsDrummingServer
                    : PendingCultistSpawns.Count == 0 && Cultists.Count > 0 && AllCultistsArrived();
                if (ready || timedOut)
                    StartCeremony();
                else if (drumChanged)
                    SendStateToAll();
                return;
            }

            if (_phase == CultistRitualPhase.Stare)
            {
                // A stare pauses every contribution; the tick still runs so the drum's beats are
                // consumed rather than paid out after the release.
                if (TickProgress(now, staring: true, drumChanged))
                    return;
                if (Time.time >= _stareEndTime)
                    ReleaseAggroAfterStare();
                return;
            }

            if (!IsRitualPhase(_phase))
            {
                if (drumChanged)
                    SendStateToAll();
                return;
            }

            if (!_detectionTriggered && TryDetectPlayer(out PlayerControllerB detected))
            {
                BeginDetectedStare(detected);
                return;
            }

            TickProgress(now, staring: false, drumChanged);
        }

        /// <summary>Server, every 0.2 s ceremony tick: counts drum beats from the drum cadence
        /// (not the animator), praying congregants and the all-present bonus into the progress
        /// model, starts the drum speed-up, ruins a stalled ceremony, advances one ritual phase per
        /// crossed threshold and sends the state when anything a client shows changed. True when
        /// the ceremony ended (ruined or summoned) inside the tick.</summary>
        private static bool TickProgress(double now, bool staring, bool forceSend)
        {
            if (_progress == null)
                return false;

            float dt = _lastProgressTickTime > 0f ? Time.time - _lastProgressTickTime : 0f;
            _lastProgressTickTime = Time.time;

            int beats = 0;
            if (_drumActive && _drumLoopStartServerTime >= 0d)
            {
                int due = CongregationProgress.BeatsDue(_drumLoopStartServerTime, now, _tempoRampStartServerTime,
                    ContractsBootstrap.CultCeremonyDrumSpeedUpTempo.Value, ContractsBootstrap.CultCeremonyDrumSpeedUpRampSeconds.Value);
                beats = Mathf.Max(0, due - _drumBeatsCounted);
                _drumBeatsCounted = Mathf.Max(due, _drumBeatsCounted);
            }

            int praying = 0;
            bool allPresent = PendingCultistSpawns.Count == 0 && Cultists.Count > 0;
            for (int i = 0; i < Cultists.Count; i++)
            {
                CongregantAI cultist = Cultists[i];
                if (cultist == null || cultist.isEnemyDead)
                    continue;
                if (cultist.IsPrayingForProgress)
                    praying++;
                if (!cultist.IsPresentAtRitual)
                    allPresent = false;
            }

            int crossings = _progress.Advance(dt, beats, praying, allPresent, staring);
            bool dirty = forceSend;
            if (_progress.SpeedUp && _tempoRampStartServerTime < 0d)
            {
                _tempoRampStartServerTime = now;
                dirty = true;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Drum speed-up at progress {_progress.Progress:0.0} (ETA {_progress.EtaSeconds:0.0} s).");
            }

            int percent = _progress.Percent;
            if (percent != _progressPercent)
            {
                _progressPercent = percent;
                dirty = true;
            }

            if (_progress.Stalled)
            {
                RuinCeremony($"no ritual progress for {_progress.SecondsWithoutProgress:0} s");
                return true;
            }

            for (int i = 0; i < crossings && _active; i++)
            {
                AdvanceRitualPhase();
                dirty = false; // each phase change sends the full state
            }

            if (!_active)
                return true;
            if (dirty)
                SendStateToAll();
            return false;
        }

        /// <summary>Server: tracks whether the drum is sounding (the drummer is alive and at the
        /// drum) and which object the peers phase-lock. A drum that starts after the ceremony began
        /// restarts its loop now, at the current cadence, without the blows. True when a synced
        /// field changed.</summary>
        private static bool RefreshDrumActive(double now)
        {
            bool changed = false;
            CongregantAI drummer = _drummer != null && !_drummer.isEnemyDead ? _drummer : null;
            NetworkObject drummerObject = drummer != null ? drummer.NetworkObject : null;
            bool hasDrummer = drummerObject != null && drummerObject.IsSpawned;
            ulong drummerId = hasDrummer ? drummerObject.NetworkObjectId : 0UL;
            if (hasDrummer != _hasDrummer || drummerId != _drummerNetworkObjectId)
            {
                _hasDrummer = hasDrummer;
                _drummerNetworkObjectId = drummerId;
                changed = true;
            }

            bool drumming = drummer != null && drummer.IsDrummingServer;
            if (drumming != _drumActive)
            {
                _drumActive = drumming;
                if (drumming && _ceremonyStarted)
                {
                    _drumLoopStartServerTime = Math.Max(now, _ceremonyStartServerTime + CongregationProgress.LoopEntrySeconds);
                    _drumBeatsCounted = 0;
                }
                changed = true;
            }

            return changed;
        }

        /// <summary>Server: the next living black congregant by index takes the drum; with none
        /// left the drum stays silent and progress comes from prayer only.</summary>
        private static void PromoteNextDrummer()
        {
            for (int i = 0; i < Cultists.Count; i++)
            {
                CongregantAI candidate = Cultists[i];
                if (candidate == null || candidate.isEnemyDead || candidate.Role != CultistRole.Black
                    || candidate.IsFleeingFromSeraphim || candidate.IsSeraphimExecutionLocked)
                    continue;

                candidate.AssignDrummerServer(_drumSpot, _drummerFacePoint);
                _drummer = candidate;
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] The drummer fell; {candidate.name} walks to the drum.");
                return;
            }

            ContractsBootstrap.Log?.LogInfo("[MoonContracts.Cult] The drummer fell and no black congregant is left; the drum is silent.");
        }

        /// <summary>Server: the ceremony ended; the drummer stops and its tempo returns to 1.</summary>
        private static void StopDrummerServer()
        {
            if (_drummer != null && !_drummer.isEnemyDead)
            {
                _drummer.SetDrummingServer(false);
                _drummer.SetDrumTempo(1f);
            }

            _drumActive = false;
            _tempoRampStartServerTime = -1d;
        }

        private static void StartCeremony()
        {
            _ceremonyStarted = true;
            double now = ServerNow();
            _ceremonyStartServerTime = now;
            _lastProgressTickTime = Time.time;
            RefreshDrumActive(now);
            if (_drumActive)
            {
                _drumLoopStartServerTime = now + CongregationProgress.LoopEntrySeconds;
                _drumBeatsCounted = 0;
            }

            CreateLocalFire(_ritualPoint);
            SetRitualPhase(CultistRitualPhase.Phase1Idle);
            UpdateLocalCeremonyAudio();
            EscalationAnnouncements.Show(EscalationAnnouncementKind.RitualFireLit);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Ceremony started ({(_drumActive ? "drummer at the drum" : "no drummer at the drum")}); drumming phase.");
        }

        private static void AdvanceRitualPhase()
        {
            switch (_phase)
            {
                case CultistRitualPhase.Phase1Idle:
                    SetRitualPhase(CultistRitualPhase.Phase2RedPray);
                    ContractsBootstrap.Log?.LogInfo("[MoonContracts.Cult] Progress 33: red prayer started.");
                    break;
                case CultistRitualPhase.Phase2RedPray:
                    SetRitualPhase(CultistRitualPhase.Phase3AllPray);
                    // #1336: Phase 3 lasts 15-30 s; the host builds the Seraphim meanwhile.
                    SeraphimPrewarm.Begin();
                    ContractsBootstrap.Log?.LogInfo("[MoonContracts.Cult] Progress 66: all prayer started.");
                    break;
                case CultistRitualPhase.Phase3AllPray:
                    CompleteCeremony();
                    break;
            }
        }

        private static void SetRitualPhase(CultistRitualPhase phase)
        {
            _phase = phase;
            for (int i = 0; i < Cultists.Count; i++)
            {
                CongregantAI cultist = Cultists[i];
                // A cultist still walking takes the phase when it reaches its spot (UpdateTravel).
                if (cultist == null || cultist.isEnemyDead || cultist.IsAggroed || cultist.IsWalkingToRitualSpot)
                    continue;
                cultist.SetRitualPhaseServer(phase);
            }
            SendStateToAll();
        }

        private static void BeginDetectedStare(PlayerControllerB detected)
        {
            _detectionTriggered = true;
            _aggroReleased = false;
            _pausedRitualPhase = _phase;
            _phase = CultistRitualPhase.Stare;
            _stareEndTime = Time.time + Mathf.Max(0.1f, ContractsBootstrap.CultEscalationStareSeconds.Value);
            _detectedPlayer = detected;
            _detectedClientId = detected != null ? detected.actualClientId : 0UL;
            PlayDetectionSuspense();

            for (int i = 0; i < Cultists.Count; i++)
            {
                CongregantAI cultist = Cultists[i];
                // #845 D: the drummer keeps drumming through detection.
                if (cultist != null && !cultist.isEnemyDead && cultist != _drummer)
                    cultist.BeginStareServer(detected);
            }

            SendStateToAll();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Player detected by ritual cultists: {detected?.playerUsername ?? _detectedClientId.ToString()}.");
        }

        private static void ReleaseAggroAfterStare()
        {
            if (_aggroReleased)
                return;

            _aggroReleased = true;
            _phase = _pausedRitualPhase;
            PlayerControllerB target = ResolveDetectedPlayer();

            for (int i = 0; i < Cultists.Count; i++)
            {
                CongregantAI cultist = Cultists[i];
                // #845 D: the drummer is never released into aggro.
                if (cultist == null || cultist.isEnemyDead || cultist == _drummer)
                    continue;

                if (cultist.IsWhiteCultist)
                    cultist.ResumeCeremonyServer(_phase);
                else
                    cultist.BeginAggroServer(target);
            }

            SendStateToAll();
            UpdateLocalCeremonyAudio();
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.Cult] Red and black cultists released into aggro; white cultist resumed ritual.");
        }

        private static void CompleteCeremony()
        {
            _complete = true;
            // Stamped first so every send from here on carries it.
            _ceremonyEndServerTime = ServerNow();
            _active = false;
            _phase = CultistRitualPhase.Phase4Demon;
            _progressPercent = 100;
            StopDrummerServer();
            for (int i = 0; i < Cultists.Count; i++)
            {
                CongregantAI cultist = Cultists[i];
                if (cultist != null && !cultist.isEnemyDead && !cultist.IsAggroed)
                    cultist.SetRitualPhaseServer(CultistRitualPhase.Phase4Demon);
            }

            // The summon's cost is named: the 2026-08-31 audit measured a 76ms
            // Congregation.UpdateRuntime(idle) pass (#705), the 2026-10-04 playtest 81ms here.
            // #1336 moved the instantiate and build into Phase 3 (PumpSeraphimPrewarm); the
            // warning splits what is left of the frame.
            long summonStart = System.Diagnostics.Stopwatch.GetTimestamp();
            SpawnSeraphimSpectacle(out SeraphimSummonTiming summonTiming);
            MoonEscalationState.RecordRuntimeStep("Congregation.SeraphimSummon", summonStart, subStep: true);
            double summonMilliseconds = (System.Diagnostics.Stopwatch.GetTimestamp() - summonStart)
                * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            if (summonMilliseconds >= 10.0)
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Timing] Seraphim summon instantiate took {summonMilliseconds:0.00}ms in one frame ({(summonTiming.UsedDormant ? "activate" : "instantiate")} {summonTiming.CreateMilliseconds:0.00} init {summonTiming.InitMilliseconds:0.00} spawn {summonTiming.SpawnMilliseconds:0.00} sync {summonTiming.SyncMilliseconds:0.00}).");
            // Whatever pre-warm the summon did not take (it bailed before the body) goes now.
            DiscardSeraphimPrewarm("summon did not use it");
            BeginSeraphimCultistCleanup();

            SendStateToAll();
            StopLocalCeremonyAudio();
            DestroyLocalFire();
            EscalationAnnouncements.Show(EscalationAnnouncementKind.CeremonyComplete);
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.Cult] Phase 4 reached. Seraphim enemy summoned.");
        }

        private static void SpawnSeraphimSpectacle(out SeraphimSummonTiming timing)
        {
            timing = default;
            if (!ContractAssetLoader.SeraphimAssetsReady)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Cult] Seraphim assets missing; ceremony completed without the enemy summon.");
                return;
            }

            GameObject prefab = ContractAssetLoader.SeraphimPrefab;
            if (prefab == null)
                return;

            float scale = SeraphimEnemyAI.ResolveVisualScale();
            Vector3 start = ResolveSeraphimRiseStart(scale);
            // #1336: the Phase 3 pre-warm's body, built or not, stands in for the instantiate.
            SeraphimPrewarmStage prewarmed = SeraphimPrewarm.Consume();
            GameObject obj = _dormantSeraphimObject;
            _dormantSeraphimObject = null;
            _dormantSeraphim = null;
            timing.UsedDormant = obj != null;
            try
            {
                long stepStart = System.Diagnostics.Stopwatch.GetTimestamp();
                if (timing.UsedDormant)
                {
                    obj.transform.SetPositionAndRotation(start, _ritualRotation);
                }
                else
                {
                    // Instantiated here rather than through RoundManager.SpawnEnemyGameObject (which
                    // spawns inside the call) so scale, rise position and init are set before Spawn:
                    // NGO's spawn snapshot then hands every client the body already at size, and the
                    // prefab carries no NetworkTransform to override it.
                    obj = UnityEngine.Object.Instantiate(prefab, start, _ritualRotation);
                }
                obj.name = SeraphimObjectName;
                obj.transform.localScale = Vector3.one * scale;
                // The pre-warmed body's Awake and OnEnable run here, before InitializeServer, as an
                // instantiated body's ran inside Instantiate.
                if (timing.UsedDormant)
                    obj.SetActive(true);

                var seraphim = obj.GetComponent<SeraphimEnemyAI>();
                if (seraphim == null)
                    seraphim = obj.AddComponent<SeraphimEnemyAI>();

                if (seraphim.enemyType == null && ContractAssetLoader.SeraphimEnemyType != null)
                    seraphim.enemyType = ContractAssetLoader.SeraphimEnemyType;
                if (seraphim.agent == null)
                    seraphim.agent = obj.GetComponent<NavMeshAgent>();
                if (seraphim.creatureAnimator == null)
                    seraphim.creatureAnimator = obj.GetComponentInChildren<Animator>(true);
                timing.CreateMilliseconds = TakeSummonStepMilliseconds(ref stepStart);

                seraphim.InitializeServer(_ritualPoint, _ritualRotation);
                _spawnedSeraphim = seraphim;
                timing.InitMilliseconds = TakeSummonStepMilliseconds(ref stepStart);

                RegisterSpawnedObject(obj);
                NetworkObject networkObject = obj.GetComponent<NetworkObject>();
                if (IsServer() && networkObject != null && !networkObject.IsSpawned)
                    networkObject.Spawn(destroyWithScene: true);
                RegisterSpawnedEnemy(seraphim);
                timing.SpawnMilliseconds = TakeSummonStepMilliseconds(ref stepStart);

                seraphim.SyncInitialState();
                timing.SyncMilliseconds = TakeSummonStepMilliseconds(ref stepStart);
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Spawned Seraphim enemy at {start}; ritualPoint={_ritualPoint}; scale={scale}; prewarm={prewarmed}.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Cult] Seraphim summon failed after ceremony completion: {e}");
                if (obj != null)
                {
                    try
                    {
                        UnityEngine.Object.Destroy(obj);
                    }
                    catch
                    {
                    }
                }
            }
        }

        private const string SeraphimObjectName = "TheCongregation_Seraphim_Ophanim";

        // #1336: Phase 3 builds the Seraphim one stage per host frame - an inactive, unspawned body
        // and this peer's rise effects - so the summon frame only activates and spawns it. Every
        // teardown discards it; a summon without it instantiates as before.
        private static readonly SeraphimSummonPrewarm SeraphimPrewarm = new SeraphimSummonPrewarm();
        private static GameObject _dormantSeraphimObject;
        private static SeraphimEnemyAI _dormantSeraphim;

        /// <summary>Where the summon frame went (#1336), reported with the slow-summon warning.</summary>
        private struct SeraphimSummonTiming
        {
            internal bool UsedDormant;
            internal double CreateMilliseconds;
            internal double InitMilliseconds;
            internal double SpawnMilliseconds;
            internal double SyncMilliseconds;
        }

        private static Vector3 ResolveSeraphimRiseStart(float scale)
        {
            return _ritualPoint - Vector3.up * Mathf.Max(7f, scale * 1.05f);
        }

        private static double TakeSummonStepMilliseconds(ref long stepStart)
        {
            long now = System.Diagnostics.Stopwatch.GetTimestamp();
            double milliseconds = (now - stepStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
            stepStart = now;
            return milliseconds;
        }

        /// <summary>Host, once per frame from UpdateRuntime: runs one pre-warm stage, and drops the
        /// pre-warm once the ceremony has left Phase 3 without a summon (a stare inside Phase 3
        /// keeps it).</summary>
        private static void PumpSeraphimPrewarm()
        {
            if (!SeraphimPrewarm.IsBegun)
                return;
            if (!IsSeraphimPrewarmPhase())
            {
                DiscardSeraphimPrewarm("ceremony left Phase 3 without a summon");
                return;
            }
            if (!SeraphimPrewarm.IsPending)
                return;

            long stageStart = System.Diagnostics.Stopwatch.GetTimestamp();
            SeraphimPrewarmStage stage = SeraphimPrewarm.NextStage();
            bool kept;
            try
            {
                kept = RunSeraphimPrewarmStage(stage);
            }
            catch (Exception e)
            {
                kept = false;
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Cult] Seraphim prewarm stage {stage} failed; the summon instantiates instead: {e}");
            }

            MoonEscalationState.RecordRuntimeStep("Congregation.SeraphimPrewarm", stageStart, subStep: true);
            double stageMilliseconds = TakeSummonStepMilliseconds(ref stageStart);
            if (stageMilliseconds >= 10.0)
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Timing] Seraphim prewarm stage {stage} took {stageMilliseconds:0.00}ms");
            if (!kept)
                DiscardSeraphimPrewarm("prewarm stage failed");
        }

        private static bool IsSeraphimPrewarmPhase()
        {
            CultistRitualPhase phase = _phase == CultistRitualPhase.Stare ? _pausedRitualPhase : _phase;
            return _active && !_complete && !_ruined && phase == CultistRitualPhase.Phase3AllPray;
        }

        private static bool RunSeraphimPrewarmStage(SeraphimPrewarmStage stage)
        {
            switch (stage)
            {
                case SeraphimPrewarmStage.Instantiate:
                {
                    // One body per ceremony, however often Phase 3 is re-entered.
                    if (_dormantSeraphimObject != null)
                        return true;

                    GameObject prefab = ContractAssetLoader.SeraphimPrefab;
                    if (prefab == null)
                        return false;

                    Vector3 start = ResolveSeraphimRiseStart(SeraphimEnemyAI.ResolveVisualScale());
                    _dormantSeraphimObject = SeraphimEnemyAI.InstantiateInactive(prefab, start, _ritualRotation);
                    _dormantSeraphimObject.name = SeraphimObjectName;
                    _dormantSeraphim = _dormantSeraphimObject.GetComponent<SeraphimEnemyAI>();
                    if (_dormantSeraphim == null)
                        _dormantSeraphim = _dormantSeraphimObject.AddComponent<SeraphimEnemyAI>();
                    return true;
                }
                case SeraphimPrewarmStage.Build:
                    if (_dormantSeraphim == null)
                        return false;
                    _dormantSeraphim.PrepareDormantServer();
                    return true;
                case SeraphimPrewarmStage.Effects:
                    SeraphimEnemyAI.PrewarmRiseEffects(_ritualPoint, SeraphimEnemyAI.ResolveVisualScale(), SeraphimEnemyAI.ResolveRiseDuration());
                    return true;
                default:
                    return true;
            }
        }

        /// <summary>Drops the pre-warm: the never-activated body is destroyed - no Awake ran, so no
        /// OnDestroy runs and NGO never sees it - with this peer's pooled rise effects. Idempotent,
        /// and silent when nothing was pre-warmed.</summary>
        private static void DiscardSeraphimPrewarm(string reason)
        {
            bool hadPrewarm = SeraphimPrewarm.IsBegun || _dormantSeraphimObject != null;
            SeraphimPrewarm.Abort();
            if (_dormantSeraphimObject != null)
                UnityEngine.Object.Destroy(_dormantSeraphimObject);
            _dormantSeraphimObject = null;
            _dormantSeraphim = null;
            SeraphimEnemyAI.DiscardPrewarmedRiseEffects();
            if (hadPrewarm)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Seraphim prewarm discarded ({reason}).");
        }

        private static void BeginSeraphimCultistCleanup()
        {
            _seraphimCultistCleanupActive = false;
            _seraphimWhiteCultistsCleared = false;
            _seraphimFleeReleased = false;
            if (!ContractsBootstrap.CultSeraphimCullCultistsOnSpawn.Value)
                return;

            bool hasLivingCultist = false;
            for (int i = 0; i < Cultists.Count; i++)
            {
                CongregantAI cultist = Cultists[i];
                if (cultist != null && !cultist.isEnemyDead)
                {
                    hasLivingCultist = true;
                }
            }

            if (!hasLivingCultist)
                return;

            _seraphimCultistCleanupActive = true;
        }

        internal static bool TryGetNextSeraphimCultistTarget(out CongregantAI cultist)
        {
            cultist = null;
            if (!_seraphimCultistCleanupActive || !ContractsBootstrap.CultSeraphimCullCultistsOnSpawn.Value)
                return false;

            PruneCultists();
            CongregantAI whiteCultist = FindLivingWhiteCultist();
            if (!_seraphimWhiteCultistsCleared)
            {
                if (whiteCultist != null)
                {
                    cultist = whiteCultist;
                    return true;
                }

                _seraphimWhiteCultistsCleared = true;
                ReleaseSeraphimFleeingCultists();
            }

            if (!_seraphimFleeReleased)
                ReleaseSeraphimFleeingCultists();
            else if (_fleeReleaseCursor < PendingFleeReleases.Count)
                DrainFleeReleases();
            else
                RetrySeraphimFleeingCultists();

            for (int i = 0; i < Cultists.Count; i++)
            {
                CongregantAI candidate = Cultists[i];
                if (candidate == null || candidate.isEnemyDead)
                    continue;

                cultist = candidate;
                return true;
            }

            _seraphimCultistCleanupActive = false;
            return false;
        }

        private static CongregantAI FindLivingWhiteCultist()
        {
            for (int i = 0; i < Cultists.Count; i++)
            {
                CongregantAI cultist = Cultists[i];
                if (cultist != null && !cultist.isEnemyDead && cultist.IsWhiteCultist)
                    return cultist;
            }

            return null;
        }

        // WP-A5: the release used to hand every surviving cultist a flee order in one frame, and each
        // CongregantAI.PickFleeDestination costs up to ten NavMesh.SamplePosition plus
        // NavMesh.CalculatePath pairs. The release is not required to be simultaneous - the cultists
        // are reacting to the Seraphim killing the white cultist and each one visibly startles on its
        // own beat - so the orders are handed out a couple per frame from the drain below. The order
        // of release and the per-cultist direction fan are unchanged.
        private const int FleeReleasePerFrame = 2;
        private static readonly List<CongregantAI> PendingFleeReleases = new List<CongregantAI>();
        private static int _fleeReleaseCursor;
        private static int _fleeReleaseCount;
        private static int _lastFleeDrainFrame = -1;
        private static int _fleeRetryCursor;

        private static void ResetFleeReleaseQueue()
        {
            PendingFleeReleases.Clear();
            _fleeReleaseCursor = 0;
            _fleeReleaseCount = 0;
            _lastFleeDrainFrame = -1;
            _fleeRetryCursor = 0;
        }

        private static void ReleaseSeraphimFleeingCultists()
        {
            if (_seraphimFleeReleased)
                return;

            _seraphimFleeReleased = true;
            ResetFleeReleaseQueue();
            for (int i = 0; i < Cultists.Count; i++)
            {
                CongregantAI cultist = Cultists[i];
                if (cultist != null && !cultist.isEnemyDead && !cultist.IsWhiteCultist)
                    PendingFleeReleases.Add(cultist);
            }

            if (PendingFleeReleases.Count == 0)
                return;

            _fleeReleaseCount = PendingFleeReleases.Count;
            DrainFleeReleases();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Seraphim killed the white cultist; releasing {_fleeReleaseCount} cultists to flee in separate directions, {FleeReleasePerFrame} per frame.");
        }

        /// <summary>Hands out at most <see cref="FleeReleasePerFrame"/> flee orders. Called from the
        /// server runtime tick and from the release itself, so the first pair still reacts on the
        /// frame the white cultist dies.</summary>
        private static void DrainFleeReleases()
        {
            if (_fleeReleaseCursor >= PendingFleeReleases.Count)
                return;
            // One pair per frame: the runtime tick and the Seraphim's target query can both reach here.
            if (_lastFleeDrainFrame == Time.frameCount)
                return;

            _lastFleeDrainFrame = Time.frameCount;
            long releaseStart = MoonEscalationState.RuntimeStepTimestamp();
            int released = 0;
            while (_fleeReleaseCursor < PendingFleeReleases.Count && released < FleeReleasePerFrame)
            {
                int index = _fleeReleaseCursor++;
                CongregantAI cultist = PendingFleeReleases[index];
                if (cultist == null || cultist.isEnemyDead)
                    continue;

                Vector3 direction = ResolveSeraphimFleeDirection(cultist, index, Mathf.Max(1, _fleeReleaseCount));
                cultist.BeginFleeServer(_ritualPoint, direction);
                released++;
            }

            if (released > 0)
                MoonEscalationState.RecordRuntimeStep("Congregation.CultistFleeRelease", releaseStart, subStep: true);

            if (_fleeReleaseCursor >= PendingFleeReleases.Count)
                PendingFleeReleases.Clear();
        }

        private static void RetrySeraphimFleeingCultists()
        {
            // Still bounded per pass: the retry re-issues orders for cultists whose first order did
            // not take, and each one costs the same navmesh work as the initial release. The cursor
            // rotates instead of restarting at zero, so a cultist whose BeginFleeServer never takes
            // (permanently unreachable navmesh, say) consumes one slot per sweep rather than the
            // whole per-pass budget forever, and every other cultist still gets its retry.
            if (Cultists.Count == 0)
            {
                _fleeRetryCursor = 0;
                return;
            }

            int retried = 0;
            int examined = 0;
            while (retried < FleeReleasePerFrame && examined < Cultists.Count)
            {
                if (_fleeRetryCursor >= Cultists.Count)
                    _fleeRetryCursor = 0;

                int index = _fleeRetryCursor++;
                examined++;

                CongregantAI cultist = Cultists[index];
                if (cultist == null || cultist.isEnemyDead || cultist.IsWhiteCultist || cultist.IsSeraphimExecutionLocked || cultist.IsFleeingFromSeraphim)
                    continue;

                Vector3 direction = ResolveSeraphimFleeDirection(cultist, index, Mathf.Max(1, Cultists.Count));
                cultist.BeginFleeServer(_ritualPoint, direction);
                retried++;
            }
        }

        private static Vector3 ResolveSeraphimFleeDirection(CongregantAI cultist, int index, int count)
        {
            float angleStep = count > 0 ? 360f / count : 360f;
            float angle = index * angleStep + UnityEngine.Random.Range(-18f, 18f);
            Vector3 spread = _ritualRotation * (Quaternion.Euler(0f, angle, 0f) * Vector3.forward);
            spread.y = 0f;

            Vector3 outward = cultist != null ? (cultist.transform.position - _ritualPoint).WithCultY(0f) : Vector3.zero;
            if (outward.sqrMagnitude < 0.25f)
                outward = spread;
            if (spread.sqrMagnitude < 0.25f)
                spread = UnityEngine.Random.insideUnitSphere.WithCultY(0f);

            Vector3 blended = Vector3.Slerp(outward.normalized, spread.normalized, 0.8f).WithCultY(0f);
            if (blended.sqrMagnitude < 0.25f)
                blended = spread.WithCultY(0f);
            return blended.sqrMagnitude > 0.25f ? blended.normalized : Vector3.forward;
        }

        private static bool AllCultistsArrived()
        {
            for (int i = 0; i < Cultists.Count; i++)
            {
                CongregantAI cultist = Cultists[i];
                if (cultist != null && !cultist.isEnemyDead && !cultist.HasReachedRitualSpot)
                    return false;
            }

            return true;
        }

        private static void RuinCeremony(string reason)
        {
            _ruined = true;
            _ceremonyEndServerTime = ServerNow();
            _active = false;
            PendingCultistSpawns.Clear();
            _phase = CultistRitualPhase.Ruined;
            StopDrummerServer();
            SendStateToAll();
            StopLocalCeremonyAudio();
            DestroyLocalFire();
            DiscardSeraphimPrewarm("ceremony ruined");
            EscalationAnnouncements.Show(EscalationAnnouncementKind.CeremonyRuined);
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Ceremony ruined: {reason}.");
        }

        private static bool TryDetectPlayer(out PlayerControllerB detected)
        {
            detected = null;
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || players.Length == 0)
                return false;

            float closeRadius = Mathf.Max(0.5f, ContractsBootstrap.CultEscalationCloseDetectionRadius.Value);
            float soundRadius = Mathf.Max(closeRadius, ContractsBootstrap.CultEscalationSoundDetectionRadius.Value);

            foreach (PlayerControllerB player in players)
            {
                if (!IsDetectablePlayer(player))
                    continue;

                for (int i = 0; i < Cultists.Count; i++)
                {
                    CongregantAI cultist = Cultists[i];
                    if (cultist == null || cultist.isEnemyDead)
                        continue;

                    float distance = Vector3.Distance(cultist.transform.position, player.transform.position);
                    if (distance <= closeRadius)
                    {
                        detected = player;
                        return true;
                    }

                    if (distance <= soundRadius && PlayerIsMakingDetectableSound(player, distance, soundRadius))
                    {
                        detected = player;
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool PlayerIsMakingDetectableSound(PlayerControllerB player, float distance, float soundRadius)
        {
            if (player == null)
                return false;

            if (PlayerIsSpeaking(player))
                return true;

            float speed = GetPlayerHorizontalSpeed(player);
            if (speed < 0.55f)
                return false;

            if (player.isCrouching)
                return distance <= Mathf.Min(soundRadius, soundRadius * 0.45f) && speed > 1.25f;

            if (player.isSprinting || speed > 4.8f)
                return true;

            return speed > 1.0f;
        }

        private static float GetPlayerHorizontalSpeed(PlayerControllerB player)
        {
            if (player == null)
                return 0f;

            if (player.thisController != null)
            {
                Vector3 velocity = player.thisController.velocity;
                velocity.y = 0f;
                return velocity.magnitude;
            }

            ulong id = player.actualClientId;
            Vector3 current = player.transform.position;
            if (!LastPlayerPositions.TryGetValue(id, out Vector3 previous))
            {
                LastPlayerPositions[id] = current;
                return 0f;
            }

            LastPlayerPositions[id] = current;
            Vector3 delta = current - previous;
            delta.y = 0f;
            return delta.magnitude / Mathf.Max(Time.deltaTime, 0.001f);
        }

        private static bool PlayerIsSpeaking(PlayerControllerB player)
        {
            if (player == null)
                return false;

            try
            {
                if (!_voicePlayerStateFieldResolved)
                {
                    _voicePlayerStateField = typeof(PlayerControllerB).GetField(
                        "voicePlayerState",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    _voicePlayerStateFieldResolved = true;
                }
                object state = _voicePlayerStateField?.GetValue(player);
                if (state == null)
                    return false;

                Type type = state.GetType();
                if (!VoiceSpeakingAccessors.TryGetValue(type, out Func<object, bool> accessor))
                {
                    accessor = ResolveVoiceSpeakingAccessor(type);
                    VoiceSpeakingAccessors[type] = accessor;
                }
                return accessor != null && accessor(state);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Resolves the voice state's speaking flag once per concrete state type (the
        /// detection tick used to run the member lookup, and allocate its name array, per player per
        /// tick). Same accessor shape as <c>BlacksiteSoldierAI.ResolveVoiceSpeakingAccessor</c>: a
        /// compiled expression, falling back to a reflection read when compilation throws.</summary>
        private static Func<object, bool> ResolveVoiceSpeakingAccessor(Type stateType)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            PropertyInfo property = null;
            FieldInfo field = null;
            for (int i = 0; i < VoiceSpeakingMemberNames.Length; i++)
            {
                string memberName = VoiceSpeakingMemberNames[i];
                property = stateType.GetProperty(memberName, flags);
                if (property != null && property.PropertyType == typeof(bool) && property.GetIndexParameters().Length == 0)
                    break;
                property = null;

                field = stateType.GetField(memberName, flags);
                if (field != null && field.FieldType == typeof(bool))
                    break;
                field = null;
            }

            if (property == null && field == null)
                return null;

            try
            {
                var state = System.Linq.Expressions.Expression.Parameter(typeof(object), "state");
                var typedState = System.Linq.Expressions.Expression.Convert(state, stateType);
                System.Linq.Expressions.Expression read = property != null
                    ? System.Linq.Expressions.Expression.Property(typedState, property)
                    : System.Linq.Expressions.Expression.Field(typedState, field);
                return System.Linq.Expressions.Expression
                    .Lambda<Func<object, bool>>(read, state)
                    .Compile();
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.Cult] Voice speaking accessor compile failed for {stateType.FullName}, using reflection: {e.Message}");
                if (property != null)
                    return s => (bool)property.GetValue(s);
                return s => (bool)field.GetValue(s);
            }
        }

        private static PlayerControllerB ResolveDetectedPlayer()
        {
            if (IsDetectablePlayer(_detectedPlayer))
                return _detectedPlayer;

            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;

            foreach (PlayerControllerB player in players)
            {
                if (player != null && player.actualClientId == _detectedClientId && IsDetectablePlayer(player))
                    return player;
            }

            foreach (PlayerControllerB player in players)
            {
                if (IsDetectablePlayer(player))
                    return player;
            }

            return null;
        }

        /// <summary>Server: the field half of vanilla EnemyAI.PlayerIsTargetable for an outdoor
        /// enemy with cannotBeInShip (no EnemyAI instance here), plus the host's mob-ignore switch.</summary>
        private static bool IsDetectablePlayer(PlayerControllerB player)
        {
            return player != null
                && player.isPlayerControlled
                && !player.isPlayerDead
                && !player.isInsideFactory
                && !player.isInHangarShipRoom
                && player.inAnimationWithEnemy == null
                && !MobIgnoreBridge.IsIgnored(player.actualClientId);
        }

        // ---- Sliced ritual placement job (WP-A5) -------------------------------------------------
        // The trigger frame used to run the whole search: every outside AI node through
        // NavMesh.SamplePosition with an O(n^2) dedupe, 56 radial samples with the same dedupe, then
        // nine footprint samples plus an allocating OverlapSphere and a lower-cased collider name per
        // surviving candidate, with a LogDebug interpolation per rejection. 41.71ms in one frame.
        // The work is unchanged in kind; it is now spread over frames under a millisecond budget,
        // the dedupe is a spatial hash, the overlap query is non-allocating, collider names are
        // compared without allocating, and the per-rejection log is one summary line.
        private const double RitualPlacementSliceBudgetMilliseconds = 2.0;
        private const float RitualCandidateCellSize = 8f;
        private const int RitualRadialSteps = 14;
        private static readonly float[] RitualRadialRadii = { 42f, 55f, 68f, 82f };
        private static readonly List<CultistRole> PendingRoles = new List<CultistRole>();
        private static readonly List<Vector3> PlacementCandidates = new List<Vector3>();
        private static readonly Dictionary<long, List<int>> PlacementGrid = new Dictionary<long, List<int>>();
        private static readonly Collider[] RitualClearanceBuffer = new Collider[512];
        private static bool _ritualClearanceOverflowLogged;
        private static bool _placementJobActive;
        private static int _placementStage;
        private static GameObject[] _placementNodes;
        private static int _placementNodeCursor;
        private static int _placementRadialCursor;
        private static int _placementScoreCursor;
        private static int _placementRejections;
        private static string _placementLastBlocker = "none";
        private static Vector3 _placementShip;
        private static Vector3 _placementMain;
        private static Vector3 _placementBest;
        private static float _placementBestScore;
        private static float _placementBaseAngle;

        private static void ResetRitualPlacementJob()
        {
            _placementJobActive = false;
            _placementStage = 0;
            _placementNodes = null;
            _placementNodeCursor = 0;
            _placementRadialCursor = 0;
            _placementScoreCursor = 0;
            _placementRejections = 0;
            _placementLastBlocker = "none";
            _placementBest = Vector3.zero;
            _placementBestScore = float.MinValue;
            PlacementCandidates.Clear();
            PlacementGrid.Clear();
        }

        private static void BeginRitualPlacementJob()
        {
            ResetRitualPlacementJob();
            _placementJobActive = true;
            _placementShip = StartOfRound.Instance?.elevatorTransform != null
                ? StartOfRound.Instance.elevatorTransform.position
                : Vector3.zero;
            _placementMain = FindOutsideMainEntrancePosition();

            _placementNodes = RoundManager.Instance?.outsideAINodes;
            if (_placementNodes == null || _placementNodes.Length == 0)
                _placementNodes = GameObject.FindGameObjectsWithTag("OutsideAINode");

            Vector3 baseDirection = (_placementShip - _placementMain).WithCultY(0f);
            if (baseDirection.sqrMagnitude < 4f)
                baseDirection = Vector3.forward;
            baseDirection.Normalize();
            _placementBaseAngle = Mathf.Atan2(baseDirection.z, baseDirection.x);
        }

        /// <summary>Returns false while the job still owes work. When it returns true the job is
        /// finished and reset; <paramref name="placed"/> says whether a site was found.</summary>
        private static bool AdvanceRitualPlacementJob(out bool placed, out Vector3 position, out Quaternion rotation)
        {
            placed = false;
            position = Vector3.zero;
            rotation = Quaternion.identity;

            long sliceStart = System.Diagnostics.Stopwatch.GetTimestamp();
            int radialTotal = RitualRadialRadii.Length * RitualRadialSteps;

            while (_placementStage < 3)
            {
                if (_placementStage == 0)
                {
                    int nodeCount = _placementNodes != null ? _placementNodes.Length : 0;
                    while (_placementNodeCursor < nodeCount)
                    {
                        GameObject node = _placementNodes[_placementNodeCursor++];
                        if (node != null)
                            TryAddPlacementCandidate(ResolveNavMeshPosition(node.transform.position, 16f), 6f);
                        if (RitualSliceExhausted(sliceStart))
                            return false;
                    }

                    _placementStage = 1;
                }
                else if (_placementStage == 1)
                {
                    while (_placementRadialCursor < radialTotal)
                    {
                        int index = _placementRadialCursor++;
                        float radius = RitualRadialRadii[index / RitualRadialSteps];
                        int step = index % RitualRadialSteps;
                        float angle = _placementBaseAngle + (step * (360f / RitualRadialSteps) * Mathf.Deg2Rad);
                        Vector3 offset = new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius;
                        TryAddPlacementCandidate(ResolveNavMeshPosition(_placementMain + offset, 28f), 7f);
                        if (RitualSliceExhausted(sliceStart))
                            return false;
                    }

                    _placementStage = 2;
                }
                else
                {
                    while (_placementScoreCursor < PlacementCandidates.Count)
                    {
                        ScoreRitualCandidate(PlacementCandidates[_placementScoreCursor++]);
                        if (RitualSliceExhausted(sliceStart))
                            return false;
                    }

                    _placementStage = 3;
                }
            }

            if (_placementRejections > 0)
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.Cult] Ritual placement rejected {_placementRejections} of {PlacementCandidates.Count} candidate(s); last blocker={_placementLastBlocker}.");
            }

            if (_placementBest != Vector3.zero)
            {
                position = _placementBest + Vector3.up * 0.05f;
                rotation = GetRitualRotation(position, _placementMain, _placementShip);
                placed = true;
            }

            ResetRitualPlacementJob();
            return true;
        }

        private static bool RitualSliceExhausted(long sliceStart)
        {
            double elapsed = (System.Diagnostics.Stopwatch.GetTimestamp() - sliceStart) * 1000.0
                             / System.Diagnostics.Stopwatch.Frequency;
            return elapsed >= RitualPlacementSliceBudgetMilliseconds;
        }

        private static void ScoreRitualCandidate(Vector3 candidate)
        {
            float shipDistance = HorizontalDistance(candidate, _placementShip);
            float mainDistance = HorizontalDistance(candidate, _placementMain);
            if (shipDistance < 28f || mainDistance < 35f || mainDistance > 120f)
                return;

            Quaternion candidateRotation = GetRitualRotation(candidate, _placementMain, _placementShip);
            if (!HasRitualClearance(candidate, candidateRotation, out string blocker))
            {
                _placementRejections++;
                _placementLastBlocker = blocker;
                return;
            }

            const float desiredMainDistance = 62f;
            float score = 100f
                - Mathf.Abs(mainDistance - desiredMainDistance) * 1.5f
                + Mathf.Min(shipDistance, 90f) * 0.18f
                + UnityEngine.Random.Range(0f, 12f);
            if (score > _placementBestScore)
            {
                _placementBestScore = score;
                _placementBest = candidate;
            }
        }

        /// <summary>Spatial-hash replacement for the O(n^2) "further than minDistance from every
        /// existing candidate" scan. The cell size is the largest minimum distance used, so the 3x3
        /// neighbourhood around a point always contains every candidate that could reject it.</summary>
        private static bool TryAddPlacementCandidate(Vector3 candidate, float minDistance)
        {
            int cellX = Mathf.FloorToInt(candidate.x / RitualCandidateCellSize);
            int cellZ = Mathf.FloorToInt(candidate.z / RitualCandidateCellSize);
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dz = -1; dz <= 1; dz++)
                {
                    if (!PlacementGrid.TryGetValue(PlacementCellKey(cellX + dx, cellZ + dz), out List<int> neighbours))
                        continue;
                    for (int i = 0; i < neighbours.Count; i++)
                    {
                        if (HorizontalDistance(PlacementCandidates[neighbours[i]], candidate) <= minDistance)
                            return false;
                    }
                }
            }

            long key = PlacementCellKey(cellX, cellZ);
            if (!PlacementGrid.TryGetValue(key, out List<int> bucket))
            {
                bucket = new List<int>(4);
                PlacementGrid[key] = bucket;
            }

            bucket.Add(PlacementCandidates.Count);
            PlacementCandidates.Add(candidate);
            return true;
        }

        private static long PlacementCellKey(int cellX, int cellZ)
        {
            return ((long)cellX << 32) ^ (uint)cellZ;
        }

        private static bool HasRitualClearance(Vector3 position, Quaternion rotation, out string blocker)
        {
            blocker = "navmesh";
            if (!HasStableRitualFootprint(position, rotation))
                return false;

            int hitCount = Physics.OverlapSphereNonAlloc(
                position + Vector3.up * 1.75f,
                6.2f,
                RitualClearanceBuffer,
                ~0,
                QueryTriggerInteraction.Ignore);
            // A full buffer means the query was truncated and a blocker may be among the hits that
            // were dropped, so the site is rejected rather than cleared on incomplete evidence.
            if (hitCount >= RitualClearanceBuffer.Length)
            {
                if (!_ritualClearanceOverflowLogged)
                {
                    _ritualClearanceOverflowLogged = true;
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Cult] The Congregation ritual clearance overlap filled its "
                        + $"{RitualClearanceBuffer.Length}-collider buffer at {position}; the candidate is "
                        + "rejected as unverifiable. If the escalation fails to place on this moon, this is why.");
                }

                blocker = "overlap buffer full";
                return false;
            }

            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = RitualClearanceBuffer[i];
                if (collider == null || collider.isTrigger || collider is TerrainCollider)
                    continue;
                Bounds bounds = collider.bounds;
                if (bounds.max.y <= position.y + 1.15f)
                    continue;

                if (ColliderNameContains(collider, "terrain", "ground", "floor"))
                    continue;

                if (ColliderNameContains(collider, "tree", "branch", "rock", "wall", "house", "facility", "entrance", "building", "fence", "pipe", "stair", "catwalk"))
                {
                    blocker = FormatColliderName(collider);
                    return false;
                }

                if (bounds.size.y > 2.5f || bounds.size.x > 4.2f || bounds.size.z > 4.2f)
                {
                    blocker = FormatColliderName(collider);
                    return false;
                }
            }

            blocker = "none";
            return true;
        }

        private static bool HasStableRitualFootprint(Vector3 position, Quaternion rotation)
        {
            Vector3[] samples =
            {
                Vector3.zero,
                new Vector3(4f, 0f, 0f),
                new Vector3(-4f, 0f, 0f),
                new Vector3(0f, 0f, 4f),
                new Vector3(0f, 0f, -4f),
                new Vector3(6f, 0f, 5f),
                new Vector3(-6f, 0f, 5f),
                new Vector3(6f, 0f, -5f),
                new Vector3(-6f, 0f, -5f),
            };

            int valid = 0;
            foreach (Vector3 sample in samples)
            {
                Vector3 world = position + rotation * sample;
                if (!NavMesh.SamplePosition(world, out NavMeshHit hit, 4.5f, NavMesh.AllAreas))
                    continue;
                if (Mathf.Abs(hit.position.y - position.y) > 1.8f)
                    continue;
                valid++;
            }

            return valid >= 7;
        }

        private struct SpawnTier
        {
            internal float Min;
            internal float Max;
            internal bool Hidden;

            internal SpawnTier(float min, float max, bool hidden)
            {
                Min = min;
                Max = max;
                Hidden = hidden;
            }
        }

        // #845 D: far and hidden first, then relax visibility, then 30-60 m (hidden first), then the
        // old 17-30 m ring.
        private static readonly SpawnTier[] SpawnTiers =
        {
            new SpawnTier(FarSpawnMinDistance, FarSpawnMaxDistance, true),
            new SpawnTier(FarSpawnMinDistance, FarSpawnMaxDistance, false),
            new SpawnTier(NearSpawnMinDistance, FarSpawnMinDistance, true),
            new SpawnTier(NearSpawnMinDistance, FarSpawnMinDistance, false),
        };

        private static NavMeshPath _spawnPath;
        private static readonly Vector3[] SpawnPathCorners = new Vector3[64];

        /// <summary>Server, trigger frame (#845 D): one spawn per congregant on an outside AI node
        /// 60-150 m (horizontal) from the ritual point, NavMesh-sampled, with a complete path to that
        /// congregant's ritual spot, out of every living player's sight and at least 7.5 m from the
        /// other spawns; the run blend brings them in. Falls back through
        /// <see cref="SpawnTiers"/>, then to the old ring. Path checks are capped per tier so a moon
        /// with many unreachable nodes cannot turn this into the trigger-frame stall #705 removed.
        /// <paramref name="longestPath"/> feeds the per-ceremony arrival timeout.</summary>
        private static Vector3[] ResolveCultistSpawnPositions(Vector3 ritualPoint, Quaternion rotation, Vector3[] spots, out float longestPath)
        {
            int count = spots.Length;
            var results = new Vector3[count];
            var used = new List<Vector3>(count);
            longestPath = 0f;
            if (_spawnPath == null)
                _spawnPath = new NavMeshPath();

            // Candidate nodes, sampled once and shuffled so congregants do not always come from the
            // same side. Visibility is resolved lazily and cached per node for this call.
            var nodes = new List<Vector3>();
            GameObject[] aiNodes = RoundManager.Instance != null ? RoundManager.Instance.outsideAINodes : null;
            if (aiNodes != null)
            {
                for (int i = 0; i < aiNodes.Length; i++)
                {
                    GameObject node = aiNodes[i];
                    if (node == null)
                        continue;
                    float distance = HorizontalDistance(node.transform.position, ritualPoint);
                    if (distance < NearSpawnMinDistance - 4f || distance > FarSpawnMaxDistance + 4f)
                        continue;
                    if (NavMesh.SamplePosition(node.transform.position, out NavMeshHit hit, 4f, NavMesh.AllAreas))
                        nodes.Add(hit.position);
                }
            }
            for (int i = nodes.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                Vector3 swap = nodes[i];
                nodes[i] = nodes[j];
                nodes[j] = swap;
            }
            var visibility = new sbyte[nodes.Count]; // 0 unknown, 1 hidden, 2 visible

            int farHidden = 0;
            for (int i = 0; i < count; i++)
            {
                bool found = false;
                float pathLength = 0f;
                Vector3 resolved = Vector3.zero;
                for (int tier = 0; tier < SpawnTiers.Length && !found; tier++)
                {
                    SpawnTier spawnTier = SpawnTiers[tier];
                    int pathChecks = 0;
                    for (int n = 0; n < nodes.Count && pathChecks < SpawnPathChecksPerTier; n++)
                    {
                        Vector3 candidate = nodes[n];
                        float distance = HorizontalDistance(candidate, ritualPoint);
                        if (distance < spawnTier.Min || distance > spawnTier.Max || IsNearUsedSpawn(used, candidate))
                            continue;
                        if (spawnTier.Hidden)
                        {
                            if (visibility[n] == 0)
                                visibility[n] = IsSpawnVisibleToAnyPlayer(candidate) ? (sbyte)2 : (sbyte)1;
                            if (visibility[n] == 2)
                                continue;
                        }

                        pathChecks++;
                        if (!TryMeasureCompletePath(candidate, spots[i], out float length))
                            continue;

                        resolved = candidate;
                        pathLength = length;
                        found = true;
                        if (tier == 0)
                            farHidden++;
                        break;
                    }
                }

                if (!found)
                {
                    resolved = ResolveRingSpawnPosition(ritualPoint, rotation, i, used);
                    pathLength = TryMeasureCompletePath(resolved, spots[i], out float ringLength)
                        ? ringLength
                        : HorizontalDistance(resolved, spots[i]);
                }

                used.Add(resolved);
                results[i] = resolved;
                longestPath = Mathf.Max(longestPath, pathLength);
            }

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Cult] Spawn placement: {farHidden}/{count} far and hidden from {nodes.Count} candidate node(s).");
            return results;
        }

        /// <summary>The pre-#845 D 17-30 m ring around the ritual, now the last fallback.</summary>
        private static Vector3 ResolveRingSpawnPosition(Vector3 ritualPoint, Quaternion rotation, int index, List<Vector3> used)
        {
            float[] angleOffsets = { 18f, 82f, 146f, 214f, 278f, 334f, 48f, 116f, 244f, 306f };
            for (int attempt = 0; attempt < 14; attempt++)
            {
                float angle = angleOffsets[index % angleOffsets.Length] + attempt * 19f + UnityEngine.Random.Range(-8f, 8f);
                float radius = UnityEngine.Random.Range(17f, 30f);
                Vector3 local = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * radius;
                Vector3 candidate = ritualPoint + rotation * local;
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, 8f, NavMesh.AllAreas))
                    continue;
                if (HorizontalDistance(hit.position, ritualPoint) < 12f || HorizontalDistance(hit.position, ritualPoint) > 38f)
                    continue;
                if (IsNearUsedSpawn(used, hit.position))
                    continue;

                return hit.position;
            }

            return ResolveNavMeshPosition(ritualPoint + rotation * (Quaternion.Euler(0f, angleOffsets[index % angleOffsets.Length], 0f) * Vector3.forward * (16f + index)), 16f);
        }

        private static bool TryMeasureCompletePath(Vector3 from, Vector3 to, out float length)
        {
            length = 0f;
            _spawnPath.ClearCorners();
            if (!NavMesh.CalculatePath(from, to, NavMesh.AllAreas, _spawnPath) || _spawnPath.status != NavMeshPathStatus.PathComplete)
                return false;

            int corners = _spawnPath.GetCornersNonAlloc(SpawnPathCorners);
            Vector3[] points = SpawnPathCorners;
            if (corners >= SpawnPathCorners.Length)
            {
                points = _spawnPath.corners;
                corners = points.Length;
            }
            for (int c = 1; c < corners; c++)
                length += Vector3.Distance(points[c - 1], points[c]);
            return true;
        }

        /// <summary>A living player outdoors within <see cref="SpawnVisibilityRange"/> whose camera
        /// has an unblocked line to the candidate's head height sees it; farther than that, or
        /// behind cover, the congregant can appear there without popping in.</summary>
        private static bool IsSpawnVisibleToAnyPlayer(Vector3 candidate)
        {
            StartOfRound round = StartOfRound.Instance;
            PlayerControllerB[] players = round != null ? round.allPlayerScripts : null;
            if (players == null)
                return false;

            Vector3 head = candidate + Vector3.up * SpawnHeadHeight;
            int mask = round.collidersAndRoomMaskAndDefault;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player == null || !player.isPlayerControlled || player.isPlayerDead || player.isInsideFactory || player.gameplayCamera == null)
                    continue;

                Vector3 eye = player.gameplayCamera.transform.position;
                if (Vector3.Distance(eye, head) > SpawnVisibilityRange)
                    continue;
                if (!Physics.Linecast(eye, head, mask, QueryTriggerInteraction.Ignore))
                    return true;
            }

            return false;
        }

        private static bool IsNearUsedSpawn(List<Vector3> used, Vector3 position)
        {
            for (int i = 0; i < used.Count; i++)
            {
                if (HorizontalDistance(used[i], position) < 7.5f)
                    return true;
            }

            return false;
        }

        private static Vector3 GetRitualSpotOffset(Quaternion rotation, int index, int total, CultistRole role)
        {
            float radius = role == CultistRole.White ? 2.35f : 3.65f;
            float angle = total <= 0 ? 0f : (index * 360f / total) + (role == CultistRole.White ? 0f : 18f);
            Vector3 local = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * radius;
            return rotation * local;
        }

        private static Quaternion GetRitualRotation(Vector3 position, Vector3 main, Vector3 ship)
        {
            Vector3 look = (main - position).WithCultY(0f);
            if (look.sqrMagnitude < 1f)
                look = (ship - position).WithCultY(0f);
            return look.sqrMagnitude > 0.01f
                ? Quaternion.LookRotation(look.normalized, Vector3.up)
                : Quaternion.identity;
        }

        private static Quaternion LookRotationToward(Vector3 from, Vector3 to)
        {
            Vector3 look = (to - from).WithCultY(0f);
            return look.sqrMagnitude > 0.01f
                ? Quaternion.LookRotation(look.normalized, Vector3.up)
                : Quaternion.identity;
        }

        private static void CreateLocalFire(Vector3 position)
        {
            if (_localFireObject != null)
                return;

            _localFireObject = new GameObject("TheCongregation_RitualFire_Local");
            _localFireObject.transform.position = position;

            GameObject pitPrefab = CongregationPropsAssets.RitualFirePit;
            if (pitPrefab != null)
            {
                GameObject pit = UnityEngine.Object.Instantiate(pitPrefab, _localFireObject.transform);
                pit.name = CongregationPropsAssets.RitualFirePitName;
                pit.transform.localPosition = Vector3.zero;
                pit.transform.localRotation = Quaternion.identity;
                pit.transform.localScale = Vector3.one;
            }

            CreateRitualFlames(_localFireObject.transform);

            Light light = EnsureRitualFireLight(_localFireObject.transform);

            CultRitualFireFlicker flicker = _localFireObject.AddComponent<CultRitualFireFlicker>();
            flicker.Initialize(light);
            UpdateLocalCeremonyAudio();
        }

        private static void CreateRitualFlames(Transform parent)
        {
            Material material = ResolveRitualFlameMaterial();
            if (material == null)
                return;

            var obj = new GameObject("RitualFlames");
            obj.SetActive(false);
            obj.transform.SetParent(parent, false);
            obj.transform.localPosition = new Vector3(0f, 0.6f, 0f);
            ParticleSystem flames = obj.AddComponent<ParticleSystem>();
            var main = flames.main;
            main.playOnAwake = false;
            main.loop = true;
            main.prewarm = true;
            main.duration = 2f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(1.45f, 1.65f);
            main.startSpeed = 0f;
            main.startSize3D = true;
            main.startSizeX = new ParticleSystem.MinMaxCurve(0.55f, 0.75f);
            main.startSizeY = new ParticleSystem.MinMaxCurve(0.8f, 1.2f);
            main.startSizeZ = 1f;
            main.startRotation = 0f;
            main.startColor = Color.white;
            main.gravityModifier = 0f;
            main.maxParticles = 3;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;

            var emission = flames.emission;
            emission.rateOverTime = 2f;
            var shape = flames.shape;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 0.22f;
            shape.rotation = new Vector3(-90f, 0f, 0f);

            // Flame03-hollow_16x4 is actually a 16 x 5 sheet; use the fireplace's 50 fps.
            var sheet = flames.textureSheetAnimation;
            sheet.enabled = true;
            sheet.mode = ParticleSystemAnimationMode.Grid;
            sheet.numTilesX = 16;
            sheet.numTilesY = 5;
            sheet.animation = ParticleSystemAnimationType.WholeSheet;
            sheet.timeMode = ParticleSystemAnimationTimeMode.FPS;
            sheet.fps = 50f;

            var renderer = obj.GetComponent<ParticleSystemRenderer>();
            renderer.renderMode = ParticleSystemRenderMode.VerticalBillboard;
            renderer.allowRoll = false;
            renderer.maxParticleSize = 1f;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.sharedMaterial = material;
            obj.SetActive(true);
            flames.Play(withChildren: false);
        }

        private static Material ResolveRitualFlameMaterial()
        {
            if (_ritualFlameMaterialResolved)
                return _ritualFlameMaterial;
            _ritualFlameMaterialResolved = true;

            StartOfRound start = StartOfRound.Instance;
            _ritualFlameMaterial = FindRitualFlameMaterial(start != null ? start.explosionPrefab : null);
            // Read prefab renderers only: instantiating an explosion or landmine can cause damage.
            if (_ritualFlameMaterial == null && start != null && start.levels != null)
            {
                for (int i = 0; i < start.levels.Length && _ritualFlameMaterial == null; i++)
                {
                    SelectableLevel level = start.levels[i];
                    if (level == null || level.spawnableMapObjects == null)
                        continue;
                    for (int j = 0; j < level.spawnableMapObjects.Length && _ritualFlameMaterial == null; j++)
                    {
                        GameObject prefab = level.spawnableMapObjects[j]?.prefabToSpawn;
                        if (prefab != null && prefab.GetComponentInChildren<Landmine>(true) != null)
                            _ritualFlameMaterial = FindRitualFlameMaterial(prefab);
                    }
                }
            }

            if (_ritualFlameMaterial == null)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Cult] Vanilla LingeringFire FlameMaterial is unavailable; ritual flames are skipped, keeping the optional pit and light.");
            return _ritualFlameMaterial;
        }

        private static Material FindRitualFlameMaterial(GameObject prefab)
        {
            if (prefab == null)
                return null;
            foreach (ParticleSystemRenderer renderer in prefab.GetComponentsInChildren<ParticleSystemRenderer>(true))
            {
                if (renderer == null || !string.Equals(renderer.gameObject.name, "LingeringFire", StringComparison.Ordinal))
                    continue;
                Material material = renderer.sharedMaterial;
                if (material != null && string.Equals(material.name, "FlameMaterial", StringComparison.Ordinal))
                    return material;
            }

            return null;
        }

        private static Light EnsureRitualFireLight(Transform parent)
        {
            Transform lightTransform = parent != null ? parent.Find("RitualFireLight") : null;
            GameObject lightObject = lightTransform != null ? lightTransform.gameObject : new GameObject("RitualFireLight");
            if (parent != null && lightObject.transform.parent != parent)
                lightObject.transform.SetParent(parent, false);
            lightObject.transform.localPosition = new Vector3(0f, 1.1f, 0f);
            lightObject.transform.localRotation = Quaternion.identity;
            lightObject.transform.localScale = Vector3.one;

            Light light = lightObject.GetComponent<Light>();
            if (light == null)
                light = lightObject.AddComponent<Light>();
            light.enabled = true;
            light.type = LightType.Point;
            light.range = RitualFireLightRange;
            light.intensity = RitualFireLightBaseIntensity;
            light.color = new Color(1f, 0.42f, 0.12f, 1f);
            // The ritual fire needs no shadow map; a shadowed point light re-renders six cube faces.
            light.shadows = LightShadows.None;
            light.renderMode = LightRenderMode.ForcePixel;
            return light;
        }

        private static void DestroyLocalFire()
        {
            DestroyLocalDrum();
            if (_localFireObject == null)
                return;
            UnityEngine.Object.Destroy(_localFireObject);
            _localFireObject = null;
        }

        private static void UpdateLocalCeremonyAudio()
        {
            UpdateDetectionSuspenseFade();

            if (!CanPlayLocalChants)
                StopLocalChants();
            bool live = _active && !_complete && !_ruined && _ritualPoint != Vector3.zero;
            // #845 D: the drum stands at the site from the moment it is known, before the first
            // congregant arrives; it goes with the fire.
            if (live && _localDrumObject == null)
                CreateLocalDrum();

            if (live && _ceremonyStarted && _localFireObject == null)
            {
                // WP-A5: one of the three candidate owners of the measured 75.98ms idle pass. The
                // sub-step names it (or clears it) in the existing slow-pass breakdown.
                long fireStart = MoonEscalationState.RuntimeStepTimestamp();
                CreateLocalFire(_ritualPoint);
                MoonEscalationState.RecordRuntimeStep("Congregation.CreateLocalFire", fireStart, subStep: true);
            }

            if (!live || !_ceremonyStarted || _ceremonyStartServerTime < 0d || _localDrumObject == null)
                return;

            double now = ServerNow();
            UpdateCeremonyLoops(now);
            UpdateDrummerPhaseLock(now);
        }

        private static void StopLocalCeremonyAudio()
        {
            StopCeremonySources();
            StopDetectionSuspense();
        }

        // ---- #845 D local drum and ceremony audio (every peer, no RPC) ------------------------------

        /// <summary>Builds the local drum from the synced position and yaw: an empty
        /// <c>CongregationDrum</c> with a <c>Visual</c> child for the prop mesh, the blows and drum
        /// sources and one OccludeAudio so facility walls muffle both.</summary>
        private static void CreateLocalDrum()
        {
            _localDrumObject = new GameObject("CongregationDrum");
            _localDrumObject.transform.SetPositionAndRotation(_drumPosition, Quaternion.Euler(0f, _drumYaw, 0f));
            var visual = new GameObject("Visual");
            visual.transform.SetParent(_localDrumObject.transform, false);
            // #845 C: the sidecar drum (pivot at the base centre, +Y up, +Z to the drummer); an
            // absent prop leaves the drum invisible.
            GameObject drumPrefab = CongregationPropsAssets.Drum;
            if (drumPrefab != null)
            {
                GameObject drumMesh = UnityEngine.Object.Instantiate(drumPrefab, visual.transform, false);
                drumMesh.transform.localPosition = Vector3.zero;
                drumMesh.transform.localRotation = Quaternion.identity;
                drumMesh.transform.localScale = Vector3.one;
                foreach (Collider collider in drumMesh.GetComponentsInChildren<Collider>(true))
                    UnityEngine.Object.Destroy(collider);
            }

            // Server agents walk around the drum by local avoidance (no carving; clients run no
            // agents), and players collide with it on every peer.
            NavMeshObstacle obstacle = _localDrumObject.AddComponent<NavMeshObstacle>();
            obstacle.shape = NavMeshObstacleShape.Capsule;
            obstacle.radius = DrumObstacleRadius;
            obstacle.height = DrumObstacleHeight;
            obstacle.center = new Vector3(0f, DrumObstacleHeight * 0.5f, 0f);
            obstacle.carving = false;
            var blocker = new GameObject("Collision");
            int collidersLayer = LayerMask.NameToLayer("Colliders");
            blocker.layer = collidersLayer >= 0 ? collidersLayer : 11;
            blocker.transform.SetParent(_localDrumObject.transform, false);
            BoxCollider blockerCollider = blocker.AddComponent<BoxCollider>();
            blockerCollider.size = DrumBlockerSize;
            blockerCollider.center = new Vector3(0f, DrumBlockerSize.y * 0.5f, 0f);

            _blowsSource = AddCeremonySource(CongregationPropsAssets.Blows, false, CongregationAudioMix.Horn);
            _drumSource = AddCeremonySource(CongregationPropsAssets.DrumLoop, true, CongregationAudioMix.Drum);
            // Added after the sources: OccludeAudio requires an AudioSource on its object.
            OccludeAudio occlude = _localDrumObject.AddComponent<OccludeAudio>();
            occlude.useReverb = true;

            _localBlowsHandled = false;
            _localDrumRun = -1d;
            _localDrumAudibleAt = 0d;
            _localDrummerInDrumState = false;
            _nextDrumPhaseResyncTime = 0f;
        }

        private static AudioSource AddCeremonySource(AudioClip clip, bool loop, CongregationAudioProfile profile)
        {
            AudioSource source = _localDrumObject.AddComponent<AudioSource>();
            ConfigureCeremonySource(source, clip, loop, profile);
            return source;
        }

        /// <summary>Shared spatial mix for the horn, drum and chant (#1336). Custom rolloff ignores
        /// minDistance, so none is set.</summary>
        internal static void ConfigureCeremonySource(AudioSource source, AudioClip clip, bool loop, CongregationAudioProfile profile)
        {
            source.clip = clip;
            source.playOnAwake = false;
            source.loop = loop;
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Custom;
            source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, BuildRolloffCurve(profile));
            source.maxDistance = profile.MaxDistance;
            source.volume = profile.Volume;
            source.priority = CeremonySourcePriority;
            source.dopplerLevel = 0f;
            var group = ContractSpatialAudio.ResolveMixerGroup();
            if (group != null)
                source.outputAudioMixerGroup = group;
        }

        private static AnimationCurve BuildRolloffCurve(CongregationAudioProfile profile)
        {
            var keys = new Keyframe[profile.KeyCount];
            for (int i = 0; i < keys.Length; i++)
                keys[i] = new Keyframe(profile.KeyTime(i), profile.KeyValue(i), profile.InTangent(i), profile.OutTangent(i));
            return new AnimationCurve(keys);
        }

        private static void DestroyLocalDrum()
        {
            StopLocalChants();
            if (_localDrumObject != null)
                UnityEngine.Object.Destroy(_localDrumObject);
            _localDrumObject = null;
            _blowsSource = null;
            _drumSource = null;
            _localBlowsHandled = false;
            _localDrumRun = -1d;
            _localDrummer = null;
            _localDrummerId = 0UL;
            _localDrummerInDrumState = false;
        }

        private static void StopCeremonySources()
        {
            if (_blowsSource != null)
                _blowsSource.Stop();
            if (_drumSource != null)
                _drumSource.Stop();
            StopLocalChants();
        }

        internal static bool CanPlayLocalChants => _active && !_complete && !_ruined && _ceremonyStarted
            && _ceremonyStartServerTime >= 0d && IsRoundRuntimeActive();

        // _isDrummer is server-only until the actor starts drumming. The ceremony message already
        // names its object while it is still walking, on clients as well as the host.
        internal static bool IsLocalChantDrummer(CongregantAI actor)
            => _hasDrummer && actor.NetworkObject != null
                && actor.NetworkObject.NetworkObjectId == _drummerNetworkObjectId;

        private static void StopLocalChants()
        {
            for (CongregantAI actor = CongregantAI.LocalChantActors; actor != null; actor = actor.NextLocalChantActor)
                actor.DestroyLocalChant();
            _localChantRun = -1d;
            _localChantDspOrigin = 0d;
        }

        /// <summary>The ceremony audio sequence from the synced server clock: the blows once at
        /// t = 0 (not after they end), then drum and congregant chants on one DSP instant at
        /// t = 11.6 s. Late joiners use the current offset; subsequent singers on that peer use
        /// the same immutable DSP/sample origin. The drum keeps its tempo-integrated clock.</summary>
        private static void UpdateCeremonyLoops(double now)
        {
            float fastTempo = ContractsBootstrap.CultCeremonyDrumSpeedUpTempo.Value;
            float rampSeconds = ContractsBootstrap.CultCeremonyDrumSpeedUpRampSeconds.Value;
            double elapsed = now - _ceremonyStartServerTime;
            if (!_localBlowsHandled && elapsed >= 0d)
            {
                _localBlowsHandled = true;
                if (elapsed < CongregationProgress.BlowsSeconds && _blowsSource != null && _blowsSource.clip != null)
                {
                    _blowsSource.timeSamples = ClipOffsetSamples(_blowsSource.clip, elapsed);
                    _blowsSource.Play();
                }
            }

            if (_localChantRun >= 0d && _localChantRun != _ceremonyStartServerTime)
                StopLocalChants();
            double chantStart = _ceremonyStartServerTime + CongregationProgress.LoopEntrySeconds;
            bool chantDue = false;
            for (CongregantAI actor = CongregantAI.LocalChantActors; actor != null; actor = actor.NextLocalChantActor)
            {
                actor.StopChantIfIneligible();
                chantDue |= actor.NeedsLocalChant;
            }
            chantDue &= _localChantRun == _ceremonyStartServerTime || now >= chantStart - LoopScheduleWindowSeconds;
            bool drumDue = _drumActive && _drumLoopStartServerTime >= 0d && _localDrumRun != _drumLoopStartServerTime
                && now >= _drumLoopStartServerTime - LoopScheduleWindowSeconds;
            if (chantDue || drumDue)
            {
                // Preserve the drum's scheduled entry. All initial singers use this same DSP
                // instant, even on a late join; later singers join that established sample grid.
                double startAt = now + LoopScheduleLeadSeconds;
                if (chantDue && _localChantRun != _ceremonyStartServerTime)
                    startAt = Math.Max(startAt, chantStart);
                if (drumDue)
                    startAt = Math.Max(startAt, _drumLoopStartServerTime);
                double dspStart = AudioSettings.dspTime + (startAt - now);

                if (chantDue)
                {
                    AudioClip chant = CongregationPropsAssets.TribalLoop;
                    if (chant != null && chant.samples > 0)
                    {
                        double chantDspStart = dspStart;
                        long samples;
                        if (_localChantRun != _ceremonyStartServerTime)
                        {
                            _localChantRun = _ceremonyStartServerTime;
                            samples = (long)Math.Round((startAt - chantStart) * chant.frequency);
                            _localChantDspOrigin = dspStart - samples / (double)chant.frequency;
                        }
                        else
                        {
                            // A singer may arrive while the first batch is still scheduled ahead.
                            // It must wait for that same entry, never seek a negative sample offset.
                            samples = (long)Math.Ceiling(Math.Max(0d, dspStart - _localChantDspOrigin) * chant.frequency);
                            chantDspStart = _localChantDspOrigin + samples / (double)chant.frequency;
                        }
                        int offset = (int)(samples % chant.samples);
                        for (CongregantAI actor = CongregantAI.LocalChantActors; actor != null; actor = actor.NextLocalChantActor)
                            actor.ScheduleLocalChant(chant, chantDspStart, offset);
                    }
                }

                if (drumDue)
                {
                    _localDrumRun = _drumLoopStartServerTime;
                    _localDrumAudibleAt = startAt;
                    _localDrummerInDrumState = false;
                    _nextDrumPhaseResyncTime = 0f;
                    if (_drumSource != null && _drumSource.clip != null)
                    {
                        double content = CongregationProgress.DrumContentSeconds(_drumLoopStartServerTime, startAt, _tempoRampStartServerTime, fastTempo, rampSeconds);
                        _drumSource.pitch = CongregationProgress.DrumTempoAt(startAt, _tempoRampStartServerTime, fastTempo, rampSeconds);
                        _drumSource.timeSamples = ClipOffsetSamples(_drumSource.clip, content);
                        _drumSource.PlayScheduled(dspStart);
                    }
                }
            }

            // #1336 choir cap: coherent singers add, so the per-singer level falls with their count.
            int singers = 0;
            for (CongregantAI actor = CongregantAI.LocalChantActors; actor != null; actor = actor.NextLocalChantActor)
            {
                if (actor.IsChantScheduled)
                    singers++;
            }
            float chantVolume = CongregationAudioMix.ChantVolumeFor(singers);
            for (CongregantAI actor = CongregantAI.LocalChantActors; actor != null; actor = actor.NextLocalChantActor)
            {
                if (actor.IsChantScheduled)
                    actor.SetChantVolume(chantVolume);
            }

            // The drummer died: the drum stops on every peer; the other congregants keep chanting.
            if (!_drumActive && _localDrumRun >= 0d)
            {
                _localDrumRun = -1d;
                if (_drumSource != null)
                    _drumSource.Stop();
            }

            // Speed-up: drum pitch only, ramped the same way on every peer from the synced start.
            if (_drumSource != null && _localDrumRun >= 0d)
                _drumSource.pitch = CongregationProgress.DrumTempoAt(now, _tempoRampStartServerTime, fastTempo, rampSeconds);
        }

        /// <summary>Locks the drummer's DrumSwing to the audible drum: on entering the Drumming
        /// state and every <see cref="DrumPhaseResyncSeconds"/> after, from the drum source's content
        /// time (timeSamples already include the pitch). The tempo scales its AttackSpeed.</summary>
        private static void UpdateDrummerPhaseLock(double now)
        {
            CongregantAI drummer = ResolveLocalDrummer();
            if (drummer == null)
                return;

            drummer.SetDrumTempo(CongregationProgress.DrumTempoAt(now, _tempoRampStartServerTime,
                ContractsBootstrap.CultCeremonyDrumSpeedUpTempo.Value, ContractsBootstrap.CultCeremonyDrumSpeedUpRampSeconds.Value));

            bool inDrumState = drummer.IsInDrummingState;
            bool entered = inDrumState && !_localDrummerInDrumState;
            _localDrummerInDrumState = inDrumState;
            bool audible = _localDrumRun >= 0d && now >= _localDrumAudibleAt
                && _drumSource != null && _drumSource.clip != null && _drumSource.isPlaying;
            if (!audible || (!entered && Time.time < _nextDrumPhaseResyncTime))
                return;

            double audioTime = _drumSource.timeSamples / (double)_drumSource.clip.frequency;
            if (drummer.AlignDrumPhase(audioTime))
                _nextDrumPhaseResyncTime = Time.time + DrumPhaseResyncSeconds;
        }

        /// <summary>The drummer named in the last state message, resolved once per object id.</summary>
        private static CongregantAI ResolveLocalDrummer()
        {
            if (!_hasDrummer)
            {
                _localDrummer = null;
                return null;
            }

            if (_localDrummer != null && _localDrummerId == _drummerNetworkObjectId)
                return _localDrummer.isEnemyDead ? null : _localDrummer;

            _localDrummer = null;
            _localDrummerId = _drummerNetworkObjectId;
            _localDrummerInDrumState = false;
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.SpawnManager != null
                && manager.SpawnManager.SpawnedObjects.TryGetValue(_drummerNetworkObjectId, out NetworkObject networkObject)
                && networkObject != null)
                _localDrummer = networkObject.GetComponent<CongregantAI>();
            return _localDrummer;
        }

        private static int ClipOffsetSamples(AudioClip clip, double seconds)
        {
            if (clip.samples <= 0 || seconds <= 0d)
                return 0;
            long samples = (long)Math.Round(seconds * clip.frequency) % clip.samples;
            return (int)samples;
        }

        private static double ServerNow()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening ? manager.ServerTime.Time : Time.timeAsDouble;
        }

        private static void PlayDetectionSuspense()
        {
            AudioClip clip = ContractAssetLoader.CultistDetectionSuspenseClip;
            if (clip == null)
                return;

            if (_detectionSuspenseSource == null)
            {
                GameObject obj = new GameObject("TheCongregation_DetectionSuspense_Local");
                UnityEngine.Object.DontDestroyOnLoad(obj);
                _detectionSuspenseSource = obj.AddComponent<AudioSource>();
                _detectionSuspenseSource.playOnAwake = false;
                _detectionSuspenseSource.spatialBlend = 0f;
                _detectionSuspenseSource.rolloffMode = AudioRolloffMode.Linear;
                _detectionSuspenseSource.priority = 64;
            }

            _detectionSuspenseSource.Stop();
            _detectionSuspenseSource.clip = clip;
            _detectionSuspenseSource.loop = clip.length < DetectionSuspenseHoldSeconds;
            _detectionSuspenseSource.volume = DetectionSuspenseVolume;
            _detectionSuspenseSource.Play();
            _detectionSuspenseFadeStartTime = Time.time + DetectionSuspenseHoldSeconds;
            _detectionSuspenseStopTime = _detectionSuspenseFadeStartTime + DetectionSuspenseFadeSeconds;
        }

        private static void UpdateDetectionSuspenseFade()
        {
            if (_detectionSuspenseSource == null || !_detectionSuspenseSource.isPlaying || _detectionSuspenseStopTime <= 0f)
                return;

            if (Time.time < _detectionSuspenseFadeStartTime)
                return;

            float fadeProgress = Mathf.InverseLerp(_detectionSuspenseFadeStartTime, _detectionSuspenseStopTime, Time.time);
            _detectionSuspenseSource.volume = Mathf.Lerp(DetectionSuspenseVolume, 0f, fadeProgress);
            if (Time.time >= _detectionSuspenseStopTime)
                StopDetectionSuspense();
        }

        private static void StopDetectionSuspense()
        {
            if (_detectionSuspenseSource != null)
            {
                _detectionSuspenseSource.Stop();
                _detectionSuspenseSource.clip = null;
                _detectionSuspenseSource.volume = 0f;
            }

            _detectionSuspenseFadeStartTime = 0f;
            _detectionSuspenseStopTime = 0f;
        }

        private sealed class CultRitualFireFlicker : MonoBehaviour
        {
            private Light _light;
            private float _seed;

            internal void Initialize(Light light)
            {
                _light = light;
                _seed = UnityEngine.Random.Range(0f, 100f);
            }

            private void Update()
            {
                float time = Time.time + _seed;
                if (_light != null)
                {
                    float noise = Mathf.PerlinNoise(_seed, Time.time * 4.25f);
                    _light.intensity = RitualFireLightBaseIntensity
                        + noise * RitualFireLightFlickerIntensity
                        + Mathf.Sin(time * 8.5f) * 0.45f;
                }
            }
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
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Cult] Network spawn failed for {obj.name}: {e.Message}");
            }
        }

        private static void RegisterSpawnedObject(GameObject obj)
        {
            if (obj != null && !SpawnedObjects.Contains(obj))
                SpawnedObjects.Add(obj);
        }

        private static void RegisterSpawnedEnemy(EnemyAI enemy)
        {
            if (enemy == null || RoundManager.Instance == null || RoundManager.Instance.SpawnedEnemies == null)
                return;

            if (!RoundManager.Instance.SpawnedEnemies.Contains(enemy))
                RoundManager.Instance.SpawnedEnemies.Add(enemy);
        }

        /// <summary>Retires one spawned object. A live <see cref="NetworkObject"/> may only be
        /// retired by the server; destroying one on a client desyncs Netcode, and the server's
        /// despawn already removes it there.</summary>
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

                UnregisterSpawnedEnemy(obj);
                RetireSpawnedObject(obj);
            }

            SpawnedObjects.Clear();
        }

        private static void UnregisterSpawnedEnemy(GameObject obj)
        {
            if (obj == null || RoundManager.Instance == null || RoundManager.Instance.SpawnedEnemies == null)
                return;

            EnemyAI enemy = obj.GetComponent<EnemyAI>() ?? obj.GetComponentInChildren<EnemyAI>();
            if (enemy != null)
                RoundManager.Instance.SpawnedEnemies.Remove(enemy);
        }

        private static void PruneCultists()
        {
            for (int i = Cultists.Count - 1; i >= 0; i--)
            {
                CongregantAI cultist = Cultists[i];
                if (cultist == null)
                {
                    Cultists.RemoveAt(i);
                    continue;
                }
                if (cultist.isEnemyDead)
                    Cultists.RemoveAt(i);
            }
        }

        private static void EnsureNetworkHandlers()
        {
            NetworkManager manager = NetworkManager.Singleton;

            // #716/G2: attach/detach and flush before the registration latch's early return, so the
            // late-join subscription tracks the singleton every tick rather than only on the pass
            // that (re)registers the handler.
            _lateJoinResync.Sync(manager);

            if (manager == null || manager.CustomMessagingManager == null || !manager.IsListening)
                return;

            CustomMessagingManager messaging = manager.CustomMessagingManager;
            if (_networkHandlersRegistered && ReferenceEquals(_registeredMessaging, messaging))
                return;

            if (_networkHandlersRegistered && _registeredMessaging != null)
            {
                try
                {
                    _registeredMessaging.UnregisterNamedMessageHandler(MsgState);
                }
                catch
                {
                }
            }

            try
            {
                messaging.RegisterNamedMessageHandler(MsgState, OnReceiveState);
                _registeredMessaging = messaging;
                _networkHandlersRegistered = true;
                ContractsBootstrap.Log?.LogDebug("[MoonContracts.Cult] Network message handlers registered.");
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Network handler registration skipped: {e.Message}");
            }
        }

        private static void SendStateToAll()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager?.CustomMessagingManager == null || !manager.IsServer)
                return;

            var writer = new FastBufferWriter(128, Allocator.Temp);
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

            // The cultists' own state RPC is sent on change only, so a joiner is brought up to date
            // on this same retried late-join schedule. Every cultist body still in the scene is
            // asked, dead ones included (Cultists holds only the living), so a joiner sees the dead
            // lying dead.
            for (int i = 0; i < SpawnedObjects.Count; i++)
            {
                GameObject obj = SpawnedObjects[i];
                CongregantAI cultist = obj != null ? obj.GetComponent<CongregantAI>() : null;
                if (cultist != null)
                    cultist.RequestStateResend();
            }

            // The Seraphim's rise parameters and locked animation are one-shot RPCs; the joiner gets
            // both again, targeted, on the same schedule.
            if (_spawnedSeraphim != null)
                _spawnedSeraphim.ResendStateToClient(clientId);

            var writer = new FastBufferWriter(128, Allocator.Temp);
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
            writer.WriteValueSafe(_active);
            writer.WriteValueSafe(_complete);
            writer.WriteValueSafe(_ruined);
            writer.WriteValueSafe(_ceremonyStarted);
            writer.WriteValueSafe(_detectionTriggered);
            writer.WriteValueSafe((int)_phase);
            writer.WriteValueSafe(_cultistsKilled);
            writer.WriteValueSafe(_cultistsTotal);
            writer.WriteValueSafe(_ritualPoint);
            // #845 D, appended so the earlier fields keep their order.
            writer.WriteValueSafe(_progressPercent);
            writer.WriteValueSafe(_drumPosition);
            writer.WriteValueSafe(_drumYaw);
            writer.WriteValueSafe(_ceremonyStartServerTime);
            writer.WriteValueSafe(_drumActive);
            writer.WriteValueSafe(_drumLoopStartServerTime);
            writer.WriteValueSafe(_tempoRampStartServerTime);
            writer.WriteValueSafe(_hasDrummer);
            writer.WriteValueSafe(_drummerNetworkObjectId);
            // #1343 S2, appended last so the earlier fields keep their order.
            writer.WriteValueSafe(_ceremonyEndServerTime);
        }

        private static void OnReceiveState(ulong senderClientId, FastBufferReader reader)
        {
            if (!IsServerMessage(senderClientId))
                return;

            try
            {
                bool wasActive = _active;
                bool wasCeremonyStarted = _ceremonyStarted;
                bool wasComplete = _complete;
                bool wasRuined = _ruined;
                CultistRitualPhase previousPhase = _phase;
                reader.ReadValueSafe(out _active);
                reader.ReadValueSafe(out _complete);
                reader.ReadValueSafe(out _ruined);
                reader.ReadValueSafe(out _ceremonyStarted);
                reader.ReadValueSafe(out _detectionTriggered);
                reader.ReadValueSafe(out int phase);
                reader.ReadValueSafe(out _cultistsKilled);
                reader.ReadValueSafe(out _cultistsTotal);
                reader.ReadValueSafe(out _ritualPoint);
                reader.ReadValueSafe(out _progressPercent);
                reader.ReadValueSafe(out _drumPosition);
                reader.ReadValueSafe(out _drumYaw);
                reader.ReadValueSafe(out _ceremonyStartServerTime);
                reader.ReadValueSafe(out _drumActive);
                reader.ReadValueSafe(out _drumLoopStartServerTime);
                reader.ReadValueSafe(out _tempoRampStartServerTime);
                reader.ReadValueSafe(out _hasDrummer);
                reader.ReadValueSafe(out _drummerNetworkObjectId);
                // #1343 S2: appended last so an older host's shorter payload still parses above.
                _ceremonyEndServerTime = 0d;
                if (reader.TryBeginRead(sizeof(double)))
                    reader.ReadValue(out _ceremonyEndServerTime);
                _phase = (CultistRitualPhase)phase;

                // #1343 S2: one band per message, on the rising edge only, so a duplicate or a
                // late-join resend never re-announces and a stale terminal event never replays.
                var previous = new CongregationAnnouncementSnapshot(wasActive, wasCeremonyStarted, wasComplete, wasRuined);
                var current = new CongregationAnnouncementSnapshot(_active, _ceremonyStarted, _complete, _ruined);
                EscalationAnnouncementKind announcement = EscalationAnnouncementEdges.SelectCongregation(
                    previous, current, EscalationAnnouncementEdges.EventAge(_ceremonyEndServerTime, ServerNow()));
                if (_active && _ceremonyStarted && !wasCeremonyStarted)
                    CreateLocalFire(_ritualPoint);
                if (announcement != EscalationAnnouncementKind.None)
                    EscalationAnnouncements.Show(announcement);
                if (_active && _phase == CultistRitualPhase.Stare && previousPhase != CultistRitualPhase.Stare)
                    PlayDetectionSuspense();
                if (!_active)
                    DestroyLocalFire();
                UpdateLocalCeremonyAudio();
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Cult] Failed reading state message: {e.Message}");
            }
        }

        private static bool IsRitualPhase(CultistRitualPhase phase)
        {
            return phase == CultistRitualPhase.Phase1Idle
                || phase == CultistRitualPhase.Phase2RedPray
                || phase == CultistRitualPhase.Phase3AllPray;
        }

        private static Vector3 ResolveNavMeshPosition(Vector3 origin, float maxDistance)
        {
            if (NavMesh.SamplePosition(origin, out NavMeshHit hit, maxDistance, NavMesh.AllAreas))
                return hit.position;
            return origin;
        }

        /// <summary>Cached for the round: entrances do not move once the dungeon is generated, and the
        /// placement job asked for this inside its candidate loop (WP-A5). Cleared by
        /// <see cref="ResetRunState"/>.</summary>
        private static bool _cachedOutsideMainValid;
        private static Vector3 _cachedOutsideMain;

        private static Vector3 FindOutsideMainEntrancePosition()
        {
            if (_cachedOutsideMainValid)
                return _cachedOutsideMain;

            Vector3 resolved = ResolveOutsideMainEntrancePosition();
            _cachedOutsideMain = resolved;
            _cachedOutsideMainValid = true;
            return resolved;
        }

        private static Vector3 ResolveOutsideMainEntrancePosition()
        {
            EntranceTeleport best = null;
            foreach (EntranceTeleport entrance in UnityEngine.Object.FindObjectsOfType<EntranceTeleport>())
            {
                if (entrance != null && entrance.entranceId == 0 && entrance.isEntranceToBuilding)
                {
                    best = entrance;
                    break;
                }
            }
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

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>Allocation-free replacement for the old
        /// <c>FormatColliderName(collider).ToLowerInvariant().Contains(token)</c>: the tokens cannot
        /// span the "parent/object" separator, so testing each name part case-insensitively is the
        /// same predicate without the two strings per collider.</summary>
        private static bool ColliderNameContains(Collider collider, params string[] tokens)
        {
            if (collider == null)
                return false;

            GameObject obj = collider.gameObject;
            string objectName = obj != null ? obj.name : collider.name;
            Transform parent = collider.transform != null ? collider.transform.parent : null;
            string parentName = parent != null ? parent.name : null;

            for (int i = 0; i < tokens.Length; i++)
            {
                string token = tokens[i];
                if (objectName != null && objectName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
                if (parentName != null && parentName.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        private static string FormatColliderName(Collider collider)
        {
            if (collider == null)
                return "unknown";
            string objectName = collider.gameObject != null ? collider.gameObject.name : collider.name;
            string parentName = collider.transform != null && collider.transform.parent != null ? collider.transform.parent.name : string.Empty;
            return string.IsNullOrWhiteSpace(parentName) ? objectName : $"{parentName}/{objectName}";
        }
    }
}
