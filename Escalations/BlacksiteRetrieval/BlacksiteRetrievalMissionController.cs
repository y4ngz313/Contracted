using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts.Whistleblower;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    [DefaultExecutionOrder(300)]
    internal sealed class BlacksiteRetrievalMissionController : NetworkBehaviour
    {
        // #1219 E1: the planner pass cadence.
        private const float RefreshInterval = BlacksitePatrolPolicy.PlannerTickSeconds;
        internal NetworkList<BlacksiteObjectiveSnapshot> ObjectiveSnapshots;
        private readonly NetworkVariable<bool> _initialized = new NetworkVariable<bool>();
        private readonly NetworkVariable<int> _memberCount = new NetworkVariable<int>(6);
        private BlacksiteObjectiveManifestController _objectives;
        internal BlacksiteObjectiveManifestController Objectives => _objectives ?? (_objectives = new BlacksiteObjectiveManifestController(this));
        // #954 S12A U1: the one committed room route the squad follows. Host only: every entry
        // point on the planner is a no-op off the server, and the graph itself is built lazily on
        // first use inside a level and rebuilt when the dungeon instance changes.
        private BlacksiteSquadRoutePlanner _routePlanner;
        internal BlacksiteSquadRoutePlanner RoutePlanner => _routePlanner ?? (_routePlanner = new BlacksiteSquadRoutePlanner(this));
        // #1016 S2: the squad's one claim ledger and read facade, hung off the mission exactly as
        // RoutePlanner is and created on the same lazy terms. Host only: every lane is host-side
        // state and the replicated objective record is untouched by it (design note C.5).
        private BlacksiteSquadBlackboard _blackboard;
        internal BlacksiteSquadBlackboard Blackboard => _blackboard ?? (_blackboard = new BlacksiteSquadBlackboard(this));
        internal int MemberCount => _memberCount.Value;
        /// <summary>
        /// #1072: the squad is still being spawned. MemberCount is the PLANNED squad size, written
        /// by ConfigureSquadServer before the first soldier exists; this is the deployment drain's
        /// own pending flag, so a host rule can tell "four raiders are expected" from "four raiders
        /// are standing on the moon". Host-side only, and false off the server, which is correct:
        /// every consumer of it is a server-only rule and no client ever runs the drain.
        /// </summary>
        internal bool SoldierDeploymentPending
            => IsServer && _Shared.MoonEscalationState.BlacksiteSoldierDeploymentPending;
        private readonly System.Collections.Generic.HashSet<ulong> _apparatusDisconnected = new System.Collections.Generic.HashSet<ulong>();
        private readonly System.Collections.Generic.HashSet<ulong> _apparatusEffectsApplied = new System.Collections.Generic.HashSet<ulong>();
        private void Awake() { ObjectiveSnapshots = new NetworkList<BlacksiteObjectiveSnapshot>(); }
        private void LateUpdate() { if(IsSpawned) Objectives.LateUpdateReplica(); }
        internal void DisconnectApparatusServer(ulong id)
        {
            var lung=BlacksiteObjectiveManifestController.Resolve(id)?.GetComponent<LungProp>();
            if(IsServer && lung!=null && lung.isLungDocked && _apparatusDisconnected.Add(id)) DisconnectApparatusClientRpc(id);
        }
        [ClientRpc] private void DisconnectApparatusClientRpc(ulong id)
        {
            var lung=BlacksiteObjectiveManifestController.Resolve(id)?.GetComponent<LungProp>();
            if(lung==null || !_apparatusEffectsApplied.Add(id))return;
            lung.isLungDocked=false;
            var method=HarmonyLib.AccessTools.Method(typeof(LungProp), "DisconnectFromMachinery");
            var animation=method?.Invoke(lung,null) as System.Collections.IEnumerator;
            if(animation!=null)lung.StartCoroutine(animation);
        }

        // #314 D12/D13: a juggled drive used to flip the derived mission state at the 4 Hz refresh
        // rate, and one dropped carrier tick un-latched a running extraction.
        private const float DriveStateFlipBackHoldSeconds = 1.5f;
        private const float ExtractionDriveLossGraceSeconds = 2f;
        private readonly NetworkVariable<int> _contract = new NetworkVariable<int>((int)MoonContractType.None);
        private readonly NetworkVariable<int> _missionState = new NetworkVariable<int>((int)BlacksiteRetrievalMissionState.Scheduled);
        private readonly NetworkVariable<int> _driveState = new NetworkVariable<int>((int)BlacksiteHardDriveState.Missing);
        private readonly NetworkVariable<int> _outcome = new NetworkVariable<int>((int)BlacksiteRetrievalOutcome.Pending);
        private readonly NetworkVariable<ulong> _expectedTargetId = new NetworkVariable<ulong>(0UL);
        private readonly NetworkVariable<ulong> _secondaryTargetId = new NetworkVariable<ulong>(0UL);
        private readonly NetworkVariable<ulong> _driveId = new NetworkVariable<ulong>(0UL);
        private readonly NetworkVariable<ulong> _carrierId = new NetworkVariable<ulong>(0UL);
        private readonly NetworkVariable<Vector3> _targetPosition = new NetworkVariable<Vector3>(Vector3.zero);
        private readonly NetworkVariable<float> _targetYaw = new NetworkVariable<float>(0f);
        // #959 S13: what the squad is doing, as one byte the alert belt prints. Host-computed on
        // the existing 4 Hz tick and written ONLY on an accepted change, so the wire cost is one
        // byte per transition rather than four writes a second. The host is free to fold in its
        // private signals (the squad order kind, the manifest's Returning latch) because only the
        // answer crosses the wire, not the inputs.
        private readonly NetworkVariable<byte> _activityStatus =
            new NetworkVariable<byte>((byte)BlacksiteActivityStatus.None);
        private BlacksiteActivityDebounce _activityDebounce;
        // #966 S14 U4: the witness escort's two HUD signals.
        //
        // The slip window is host state, read on the same 4 Hz tick that already folds the
        // snapshots: a witness who leaves an escort's hands alive cannot be taken again until
        // U1's re-take backoff has run, and that backoff IS the window in which WITNESS SLIPPED is
        // the true reading. The stamp is derived from BlacksiteCustodySettings, not chosen: no new
        // constant is introduced here.
        private bool _witnessHeldServer;
        private float _witnessSlippedUntil = float.NegativeInfinity;
        // The alert latch is per-peer, because the one-shot belt is a local store with no host
        // path (see RefreshWitnessAlerts).
        private bool _witnessHeldLocal;
        // Alert lifetimes, review-whistleblower-escort.md 3.9 and the plan's Stage 14 U4
        // paragraph: 8 s for the take and 6 s for the break. Both sit well under the 18 s the only
        // other one-shot in the mod uses (WeatherChangeAlerts.AlertSeconds), because a weather
        // change is a standing condition and these two are beats in a fight.
        private const float WitnessTakenAlertSeconds = 8f;
        private const float WitnessFreeAlertSeconds = 6f;
        private float _nextRefreshAt;
        private BlacksiteSquadDirector _squadDirector;
        private BlacksiteRetrievalMissionState _stateBeforeCurrent = BlacksiteRetrievalMissionState.Scheduled;
        private float _missionStateEnteredAt;
        private float _extractionDriveLostSince;

        internal MoonContractType Contract => (MoonContractType)_contract.Value;
        internal BlacksiteRetrievalMissionState MissionState => (BlacksiteRetrievalMissionState)_missionState.Value;
        internal BlacksiteHardDriveState DriveState => (BlacksiteHardDriveState)_driveState.Value;
        internal BlacksiteRetrievalOutcome Outcome => (BlacksiteRetrievalOutcome)_outcome.Value;
        // #959 S13: readable on every peer, which is the whole point - the belt runs on the client.
        internal BlacksiteActivityStatus ActivityStatus => (BlacksiteActivityStatus)_activityStatus.Value;
        internal ulong MissionNetworkId => NetworkObject != null && NetworkObject.IsSpawned ? NetworkObject.NetworkObjectId : 0UL;
        internal ulong ExpectedTargetNetworkId => _expectedTargetId.Value;
        internal ulong SecondaryTargetNetworkId => _secondaryTargetId.Value;
        internal ulong DriveNetworkId => _driveId.Value;
        internal ulong CarrierNetworkId => _carrierId.Value;
        internal Vector3 TargetPosition => _targetPosition.Value;
        internal Quaternion TargetRotation => Quaternion.Euler(0f, _targetYaw.Value, 0f);
        internal bool HasTarget => _expectedTargetId.Value != 0UL || _secondaryTargetId.Value != 0UL;
        // #1075: read on every peer, so it is worth saying which half does what now that None is a
        // raidable contract. _initialized is the readiness signal: it is host-written in
        // InitializeServer, defaults false, and reaches a client in the same NetworkVariable
        // delivery as _contract, so a peer that has not been told the mission's contract reads
        // false here without the contract value being consulted at all. A client's "not synced yet"
        // is therefore keyed on this flag and never on None. The mode-table test is kept for the
        // other reason it existed: a malformed or future contract value off the wire is Excluded
        // and must not present as a live mission.
        internal bool IsInitialized => _initialized.Value && BlacksiteRetrievalRules.IsContractEligible(Contract);
        internal bool IsTerminal => Outcome != BlacksiteRetrievalOutcome.Pending;
        internal BlacksiteSquadDirector SquadDirector => IsServer
            ? _squadDirector ?? (_squadDirector = new BlacksiteSquadDirector(this))
            : null;
        // #1293 R3: which dirty squad members decide on this frame. Host only, cleared with the
        // mission; a member without a mission decides whenever it is dirty.
        private readonly BlacksiteDecisionQueue _decisionQueue = new BlacksiteDecisionQueue();
        internal BlacksiteDecisionQueue DecisionQueue => IsServer ? _decisionQueue : null;

        [ClientRpc] internal void SyncCargoValueClientRpc(NetworkObjectReference reference,int value)
        {
            if(reference.TryGet(out var obj)) obj.GetComponent<GrabbableObject>()?.SetScrapValue(value);
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            BlacksiteHardDriveObjectiveService.RegisterMission(this);
        }

        public override void OnNetworkDespawn()
        {
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Mission",
                    $"despawn mission={MissionNetworkId}, state={MissionState}, drive={DriveState}, outcome={Outcome}.");
            }
            if(IsServer) _objectives?.CleanupServer();
            // #1293 R1: members the ship leaves on the moon keep the squad's table (ReleaseMission).
            _squadDirector?.ReleaseMission();
            _decisionQueue.Clear();
            _squadDirector = null;
            // #1016 S2: the mission's claims end with the mission. Every lane, every wait and every
            // motion-lease tie goes here, which is the mission-scoped half of design note G.9 - the
            // elevator statics this replaces were cleared only on ROUND reset, so a second mission
            // inside one round inherited the first's ratcheted capacity.
            // #1016 S2 review batch 2, F3(c): Retire, not Clear. The mission is not merely resetting
            // its ledger - it is gone, so the broker has to leave the static live registry too
            // or it is a dead reference a round reset walks. Clear is the state half and a live mission survives it; Retire is
            // the lifetime half and nothing survives it.
            // #1016 S2 final review F-C: the retired blackboard is KEPT, not nulled. The getter is
            // lazy, so a late touch after despawn would otherwise construct a fresh blackboard
            // whose broker joins the static live registry with nothing left to retire it - one
            // permanent entry per round. A late touch now reaches the retired broker instead,
            // which is out of the registry and owns no lane.
            _blackboard?.Broker.Retire();
            BlacksiteHardDriveObjectiveService.UnregisterMission(this);
            base.OnNetworkDespawn();
        }

        /// <summary>
        /// #1016 S2 review batch 2, F3(c): the other way a mission ends.
        ///
        /// A controller torn down with the scene never runs <see cref="OnNetworkDespawn"/>, so its
        /// broker would stay in the static live registry - one dead reference that
        /// <c>BlacksiteResourceBroker.ClearAll</c> walks on every round reset for the rest of the
        /// session, now that a round reset no longer empties the registry. <see cref="Retire"/> is
        /// idempotent, so the two hooks firing in sequence is the normal case and costs nothing.
        /// </summary>
        public override void OnDestroy()
        {
            _blackboard?.Broker.Retire();
            base.OnDestroy();
        }

        private void Update()
        {
            if (!IsSpawned)
                return;
            using var hitch = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.MissionUpdate);
            // #1293 R1: the squad's one perception pass keeps its own, finer clock
            // (BlacksiteSightPolicy.VisualTickSeconds); the director gates it.
            if (IsServer)
                using (BlacksiteHitchTracer.Scope(BlacksiteHitchScope.SquadPerception))
                    _squadDirector?.UpdatePerceptionServer();
            if (Time.time < _nextRefreshAt)
                return;
            _nextRefreshAt = Time.time + RefreshInterval;
            // #966 S14 U4: the custody one-shots run on every peer, host included, before the
            // server gate. They read only replicated state and write only the local alert belt.
            RefreshWitnessAlerts();
            if (!IsServer)
                return;
            Objectives.UpdateServer();
            SquadDirector?.UpdateServer();
            // #959 S13: last, so the status reads the order the director has just published rather
            // than the one from a quarter of a second ago.
            RefreshActivityStatusServer();
        }

        /// <summary>
        /// #959 S13: fold the replicated mission state, the ship, the objective snapshots and the
        /// host's private squad order into one belt status, then offer it to the debounce. Server
        /// only: Objectives.Returning and SquadDirector do not exist on a client.
        /// </summary>
        private void RefreshActivityStatusServer()
        {
            if (!IsServer || !IsInitialized)
                return;

            // The status belongs to the contract objective, not whichever mission record changed
            // last. Primary records win even when a fallback record was registered first; a raid
            // with no primary uses its first non-Loot record as the fallback kind.
            BlacksiteObjectiveKind contractKind = default(BlacksiteObjectiveKind);
            bool hasContractKind = false;
            foreach (var record in ObjectiveSnapshots)
            {
                if (record.Kind == BlacksiteObjectiveKind.Loot)
                    continue;
                if (BlacksiteObjectiveManifestController.IsPrimaryObjective(record.Kind))
                {
                    contractKind = record.Kind;
                    hasContractKind = true;
                    break;
                }
                if (!hasContractKind)
                {
                    contractKind = record.Kind;
                    hasContractKind = true;
                }
            }

            bool loaded = false, carriedOutside = false, carriedInside = false, reserved = false;
            if (hasContractKind)
            {
                foreach (var record in ObjectiveSnapshots)
                {
                    if (record.Kind != contractKind)
                        continue;
                    switch (record.Phase)
                    {
                        case BlacksiteObjectivePhase.Loaded:
                            loaded = true;
                            break;
                        case BlacksiteObjectivePhase.Carried:
                            // #1219 B1: host-only (IsServer above), so the objective world is the carrier's.
                            if (Objectives.ObjectiveWorld(record)) carriedOutside = true; else carriedInside = true;
                            break;
                        case BlacksiteObjectivePhase.Reserved:
                            reserved = true;
                            break;
                    }
                }
            }

            BlacksiteShipController ship = Objectives.Ship;
            BlacksiteActivityStatus computed = BlacksiteActivityPolicy.Resolve(
                (int)MissionState,
                (int)DriveState,
                (int)Outcome,
                ship != null ? (int)ship.Phase : BlacksiteActivityWire.ShipInbound,
                new BlacksiteActivitySnapshotFlags(loaded, carriedOutside, carriedInside, reserved),
                Objectives.Returning,
                (int)SquadDirector.CurrentOrderKind,
                RefreshWitnessSlipServer());

            // The carrier is "present" while either channel still names one; the sticky rule exists
            // for the frame where a carrier reference is momentarily null between hand-offs.
            bool carrierPresent = CarrierNetworkId != 0UL || carriedInside || carriedOutside;
            BlacksiteActivityStatus previous = ActivityStatus;
            if (!_activityDebounce.Tick(computed, carrierPresent, Time.time, out BlacksiteActivityStatus accepted))
                return;

            _activityStatus.Value = (byte)accepted;
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Activity",
                    $"status={accepted}, mode={BlacksiteContractModes.Mode(Contract)}, from={previous}");
            }
        }

        /// <summary>
        /// #966 S14 U4. Whether the witness has been taken out of an escort's hands and cannot be
        /// taken again yet. Server only, and it reads nothing the host does not already own: the
        /// custody owner is the witness's own replicated variable (or, #1293 R2, his Loaded
        /// record) and the backoff length is U1's fixed <c>CustodyRetakeBackoffSeconds</c>.
        /// </summary>
        private bool RefreshWitnessSlipServer()
        {
            WhistleblowerEnemyAI witness =
                BlacksiteContractModes.Mode(Contract) == BlacksiteContractMode.WitnessEscort
                    ? MoonContractState.ActiveWhistleblower
                    : null;
            bool held = WitnessHeld(witness);
            if (held != _witnessHeldServer)
            {
                _witnessHeldServer = held;
                // Released alive is a slip. Released because he died is the contract completing,
                // and the terminal rung says so on the tick it happens.
                _witnessSlippedUntil = !held && WitnessIsLoose(witness)
                    ? Time.time + BlacksiteCustodySettings.CustodyRetakeBackoffSeconds
                    : float.NegativeInfinity;
            }

            return !held && WitnessIsLoose(witness) && Time.time < _witnessSlippedUntil;
        }

        private static bool WitnessIsLoose(WhistleblowerEnemyAI witness)
            => witness != null && !witness.isEnemyDead && !witness.Neutralized;

        /// <summary>
        /// #1293 R2: held is an escort's hold or the ship's seat. The extraction seat ends custody
        /// (WhistleblowerEnemyAI.EnterShipHoldServer) in the same server step that writes his record
        /// Loaded, and both replicate together, so on every peer the seat reads as still held: no
        /// slip and no "WITNESS FREE" for a man going home.
        /// </summary>
        private bool WitnessHeld(WhistleblowerEnemyAI witness)
        {
            if (witness == null)
                return false;
            if (witness.IsInCustody)
                return true;
            ulong id = witness.NetworkObjectId;
            for (int i = 0; i < ObjectiveSnapshots.Count; i++)
                if (ObjectiveSnapshots[i].ObjectId == id && ObjectiveSnapshots[i].Phase == BlacksiteObjectivePhase.Loaded)
                    return true;
            return false;
        }

        /// <summary>
        /// #966 S14 U4. The two custody one-shots on the alert belt.
        ///
        /// There is no host-originated path to <c>ContractHudAlerts</c> in this mod: the belt is a
        /// process-local static list and its only other producer, <c>WeatherChangeAlerts</c>, is a
        /// per-peer poll of replicated vanilla state that every client runs for itself. This
        /// follows that precedent exactly. The witness's custody owner is a NetworkVariable with
        /// Everyone read permission, so every peer sees the same transition and posts the same
        /// one-shot without an RPC. Re-posting the same text extends it rather than duplicating
        /// it, which is the belt's own rule.
        /// </summary>
        private void RefreshWitnessAlerts()
        {
            if (!IsInitialized || BlacksiteContractModes.Mode(Contract) != BlacksiteContractMode.WitnessEscort)
                return;

            WhistleblowerEnemyAI witness = MoonContractState.ActiveWhistleblower;
            bool held = WitnessHeld(witness);
            if (held == _witnessHeldLocal)
                return;

            _witnessHeldLocal = held;
            if (held)
            {
                ContractHudAlerts.Post("WITNESS TAKEN", WitnessTakenAlertSeconds, lethal: true);
                return;
            }

            // A killed witness is the crew winning, not a rescue: the card and the belt both say
            // so on their own and a "WITNESS FREE" flash over the kill would read as a failure.
            if (WitnessIsLoose(witness))
                ContractHudAlerts.Post("WITNESS FREE", WitnessFreeAlertSeconds);
        }

        internal bool InitializeServer(MoonContractType contract)
        {
            // #1075: the mission latches the round's contract here, so this is where an unresolved
            // None must be refused rather than frozen into a mission for the rest of the round. The
            // selection gate asks the same pair; asking it again at the door costs one dictionary
            // lookup and keeps the invariant local to the object that owns the value.
            if (!IsServer
                || !BlacksiteRetrievalRules.IsContractReadyToSchedule(
                        contract, MoonContractState.IsCurrentContractResolved()))
                return false;

            BlacksiteDiagnostics.Reset();
            BlacksitePerceptionDiagnostics.Reset();
            _contract.Value = (int)contract;
            _initialized.Value = true;
            _missionState.Value = (int)BlacksiteRetrievalMissionState.Inbound;
            _driveState.Value = (int)BlacksiteHardDriveState.Missing;
            _outcome.Value = (int)BlacksiteRetrievalOutcome.Pending;
            _expectedTargetId.Value = 0UL;
            _secondaryTargetId.Value = 0UL;
            _driveId.Value = 0UL;
            _carrierId.Value = 0UL;
            _targetPosition.Value = Vector3.zero;
            _targetYaw.Value = 0f;
            _squadDirector = new BlacksiteSquadDirector(this);
            _decisionQueue.Clear();
            _nextRefreshAt = Time.time;
            Objectives.UpdateServer();
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Mission",
                    $"initialized mission={MissionNetworkId}, contract={contract}, state={MissionState}, drive={DriveState}.");
            }
            return true;
        }

        internal void ConfigureSquadServer(
            Vector3 shipPosition,
            Vector3 outsideMainEntrance,
            Vector3 insideMainEntrance,
            Vector3 insideFireExit,
            Vector3 outsideFireExit,
            int expectedMemberCount)
        {
            if (!IsServer)
                return;
            _memberCount.Value = expectedMemberCount;
            SquadDirector.ConfigureRouteServer(
                shipPosition,
                outsideMainEntrance,
                insideMainEntrance,
                insideFireExit,
                outsideFireExit,
                expectedMemberCount);
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Route",
                    $"configured squad route members={expectedMemberCount}, ship={shipPosition}, outsideMain={outsideMainEntrance}, insideMain={insideMainEntrance}, insideFire={insideFireExit}, outsideFire={outsideFireExit}.");
            }
            ChooseEntranceUpFrontServer(outsideMainEntrance);
        }

        // #1150: an interior whose main entrance has no inside endpoint the paired transfer would
        // accept (no NavMesh within the transfer's own sample of the exit point) is not walked to
        // three times first. The same endpoint resolution the transfer uses decides here, once, at
        // route configuration; a refused main door with a usable fire exit republishes the fire
        // exit as the squad's door before any member lands. A door that only fails later, or an
        // interior with no usable door at all, is the transfer counter's and the mission-level
        // stalled rule's, exactly as before.
        //
        // #874 N6: the same seam also weighs the primary objective. On Vow (seed 86393907) the
        // Payload cart sat in a NavMesh pocket reachable only from the fire exit (10.9 m, log
        // 10428/10542). The squad kept the main door, rode the mineshaft car down and stood 7 m
        // above the cart for four minutes with no complete path (11228, 24786). Each door's reach
        // is measured from the inside endpoint the transfer itself would land on, walking or riding
        // the mineshaft car (BlacksiteReachability.Route). A door that reaches the objective
        // replaces the main door only when the main door cannot reach it
        // (BlacksiteEntrancePolicy.PrefersObjectiveDoor). It must also prove its outside walk from
        // the main door with MoonRoute, because the landing only
        // proved the ship-to-main-door leg. The choice goes out on the existing director republish,
        // so the entry, the extraction and the cart haul all follow the one door.
        private void ChooseEntranceUpFrontServer(Vector3 outsideMainEntrance)
        {
            EntranceTeleport main = BlacksiteEntranceInteraction.FindBestEntrance(
                outsideMainEntrance, isEntranceToBuilding: true, preferredEntranceId: 0, allowAnyId: false);
            if (main == null)
                return;
            bool mainUsable = BlacksiteEntranceInteraction.TryResolveExitEndpoint(main, out _, out Vector3 mainInside, out string refusal);
            bool objectiveKnown = TryGetInteriorObjectivePoint(out Vector3 objective);
            bool mainReaches = false;
            string reach = $"id{main.entranceId}="
                + (!mainUsable ? "refused" : objectiveKnown ? ObjectiveReach(mainInside, objective, out mainReaches) : "unmeasured");
            if (mainUsable && (!objectiveKnown || mainReaches))
            {
                if (objectiveKnown)
                    BlacksiteDiagnostics.Log(
                        "Entrance",
                        $"objective door check; keeping entrance id={main.entranceId}, objective={BlacksiteDiagnosticFormat.V(objective)}, reach=[{reach}]");
                return;
            }

            // #1150's alternate is the nearest non-main door; it is kept as the fallback below. N6
            // walks on past it only to find a door that reaches the objective.
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            List<EntranceTeleport> doors = BlacksiteEntranceInteraction.FindEntrancesNearest(
                outsideMainEntrance, isEntranceToBuilding: true, excludeEntranceId: main.entranceId);
            EntranceTeleport fallback = null, fallbackExit = null, chosen = null, chosenExit = null;
            string alternateRefusal = "no candidate";
            for (int i = 0; i < doors.Count; i++)
            {
                EntranceTeleport door = doors[i];
                bool usable = BlacksiteEntranceInteraction.TryResolveExitEndpoint(door, out EntranceTeleport exit, out Vector3 inside, out string doorRefusal);
                if (i == 0)
                {
                    if (usable) { fallback = door; fallbackExit = exit; }
                    else alternateRefusal = doorRefusal;
                }
                if (!objectiveKnown)
                    break;
                if (!usable)
                {
                    reach += $", id{door.entranceId}=refused";
                    continue;
                }
                reach += $", id{door.entranceId}={ObjectiveReach(inside, objective, out bool reaches)}";
                if (!BlacksiteEntrancePolicy.PrefersObjectiveDoor(mainUsable, mainReaches, reaches))
                    continue;
                if (!BlacksiteLandingGeometry.MoonRoute(outsideMainEntrance, EntrancePoint(door), mask, out string why))
                {
                    reach += $"/moonRoute=rejected:{why}";
                    continue;
                }
                chosen = door;
                chosenExit = exit;
                break;
            }

            if (chosen != null)
            {
                Vector3 chosenOutside = EntrancePoint(chosen);
                Vector3 chosenInside = EntrancePoint(chosenExit);
                SquadDirector.RetargetEntranceServer(chosen.entranceId, chosenOutside, chosenInside);
                BlacksiteDiagnostics.Log(
                    "Entrance",
                    $"objective door check; choosing entrance id={chosen.entranceId} at {chosenOutside}, inside={chosenInside}, objective={BlacksiteDiagnosticFormat.V(objective)}"
                    + $"{(mainUsable ? string.Empty : $". cause={refusal}")}, reach=[{reach}]");
                return;
            }
            if (mainUsable)
            {
                BlacksiteDiagnostics.Log(
                    "Entrance",
                    $"objective door check; keeping entrance id={main.entranceId}, no door reaches the objective, objective={BlacksiteDiagnosticFormat.V(objective)}, reach=[{reach}]");
                return;
            }
            if (fallback == null)
            {
                BlacksiteDiagnostics.Log(
                    "Entrance",
                    $"main entrance inside endpoint refused up front; keeping entrance id={main.entranceId}, no alternate entrance: {alternateRefusal}. cause={refusal}, reach=[{reach}]");
                return;
            }

            Vector3 outsidePoint = EntrancePoint(fallback);
            Vector3 insidePoint = EntrancePoint(fallbackExit);
            SquadDirector.RetargetEntranceServer(fallback.entranceId, outsidePoint, insidePoint);
            BlacksiteDiagnostics.Log(
                "Entrance",
                $"main entrance inside endpoint refused up front; choosing entrance id={fallback.entranceId} at {outsidePoint}, inside={insidePoint}. cause={refusal}, reach=[{reach}]");
        }

        private static Vector3 EntrancePoint(EntranceTeleport entrance)
            => entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;

        // #874 N6: the primary objective's floor point, when it is inside. The 2 m sample is the
        // route planner's own tile-representative radius (a cart or prop origin sits above its
        // floor). No interior objective, no floor within it: the door choice is left to #1150.
        private bool TryGetInteriorObjectivePoint(out Vector3 point)
        {
            point = default;
            // #1219 B1: host-only (ConfigureSquadServer, IsServer), so the host objective-world answer.
            if (!Blackboard.TryGetPrimaryObjective(out BlacksiteObjectiveSnapshot record) || Objectives.ObjectiveWorld(record))
                return false;
            if (!NavMesh.SamplePosition(record.Position, out NavMeshHit hit, 2f, NavMesh.AllAreas))
                return false;
            point = hit.position;
            return true;
        }

        // #874 P-S3: the reach is the route verdict (#874 S2: the navmesh's own answer), walked or
        // ridden on the native car, or none.
        private static string ObjectiveReach(Vector3 inside, Vector3 objective, out bool reaches)
        {
            Y4NGZCompany.Facility.Interior.InteriorAnchorService.RecordNavMeshPathCalculation();
            BlacksiteRouteVerdict route = BlacksiteReachability.Route(BlacksiteTraveller.Soldier(false), inside, objective);
            reaches = route.Reachable;
            return reaches ? $"{(route.Rides ? "ride" : "walk")}:{route.Distance:0.0}m" : "none";
        }

        internal void SetTargetServer(BlacksiteRetrievalTargetPose target)
        {
            if (!IsServer)
                return;

            _expectedTargetId.Value = GetNetworkId(target.ExpectedTarget);
            _secondaryTargetId.Value = GetNetworkId(target.SecondaryTarget);
            _targetPosition.Value = target.Position;
            _targetYaw.Value = target.Rotation.eulerAngles.y;
            if (target.Drive != null)
                _driveId.Value = GetNetworkId(target.Drive);
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Mission",
                    $"target expected={_expectedTargetId.Value}, secondary={_secondaryTargetId.Value}, drive={_driveId.Value}, position={_targetPosition.Value}, yaw={_targetYaw.Value:0.0}.");
            }
        }

        internal void SetDriveServer(BreachHardDriveItem drive)
        {
            if (!IsServer)
                return;
            _driveId.Value = drive != null && drive.NetworkObject != null
                ? drive.NetworkObject.NetworkObjectId
                : 0UL;
            if (BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.Log("Drive", $"network object set to {_driveId.Value}.");
        }

        internal void SetDriveStateServer(BlacksiteHardDriveState state, ulong carrierNetworkId)
        {
            if (!IsServer || IsTerminal)
                return;

            BlacksiteHardDriveState previousDriveState = DriveState;
            BlacksiteRetrievalMissionState previousMissionState = MissionState;
            ulong previousCarrier = _carrierId.Value;
            _driveState.Value = (int)state;
            _carrierId.Value = carrierNetworkId;
            if (MissionState == BlacksiteRetrievalMissionState.Scheduled
                || MissionState == BlacksiteRetrievalMissionState.Inbound
                || MissionState == BlacksiteRetrievalMissionState.Deploying)
            {
                return;
            }

            switch (state)
            {
                case BlacksiteHardDriveState.AtExpectedTarget:
                    if (MissionState != BlacksiteRetrievalMissionState.Overriding
                        && MissionState != BlacksiteRetrievalMissionState.Inspecting)
                    {
                        SetMissionStateServer(BlacksiteRetrievalMissionState.ApproachingExpectedDrive);
                    }
                    break;
                case BlacksiteHardDriveState.Loose:
                case BlacksiteHardDriveState.Missing:
                case BlacksiteHardDriveState.PlayerHeld:
                case BlacksiteHardDriveState.InShip:
                    // Extraction is sticky: it only un-latches once the drive has genuinely been
                    // out of the carrier's hands for a beat, not on a single dropped tick.
                    if (previousMissionState == BlacksiteRetrievalMissionState.Extracting)
                    {
                        if (_extractionDriveLostSince <= 0f)
                            _extractionDriveLostSince = Time.time;
                        if (Time.time - _extractionDriveLostSince < ExtractionDriveLossGraceSeconds)
                            break;
                    }

                    SetMissionStateServer(
                        state == BlacksiteHardDriveState.PlayerHeld
                        || state == BlacksiteHardDriveState.InShip
                        || previousMissionState == BlacksiteRetrievalMissionState.Extracting
                        || previousMissionState == BlacksiteRetrievalMissionState.EscortingCarrier
                            ? BlacksiteRetrievalMissionState.RecoveringDrive
                            : BlacksiteRetrievalMissionState.TrackingMissingDrive);
                    break;
                case BlacksiteHardDriveState.SoldierHeld:
                    _extractionDriveLostSince = 0f;
                    if (MissionState != BlacksiteRetrievalMissionState.Extracting)
                        SetMissionStateServer(BlacksiteRetrievalMissionState.EscortingCarrier);
                    break;
                case BlacksiteHardDriveState.Extracted:
                    _extractionDriveLostSince = 0f;
                    ResolveServer(BlacksiteRetrievalOutcome.Recovered);
                    break;
            }

            if (previousDriveState != state || previousCarrier != carrierNetworkId)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Blacksite] Drive state {previousDriveState} -> {state}; " +
                    $"carrier={carrierNetworkId}, mission={MissionState}.");
                if (BlacksiteDiagnostics.Enabled)
                {
                    BlacksiteDiagnostics.Log(
                        "Drive",
                        $"state {previousDriveState} -> {state}; object={DriveNetworkId}, carrier={previousCarrier} -> {carrierNetworkId}, mission={previousMissionState} -> {MissionState}.");
                }
            }
        }

        internal void MarkDeployingServer()
        {
            SetMissionStateServer(BlacksiteRetrievalMissionState.Deploying);
        }

        internal void MarkInfiltratingServer()
        {
            SetMissionStateServer(BlacksiteRetrievalMissionState.Infiltrating);
        }

        internal void MarkApproachingExpectedDriveServer()
        {
            if (DriveState == BlacksiteHardDriveState.AtExpectedTarget)
                SetMissionStateServer(BlacksiteRetrievalMissionState.ApproachingExpectedDrive);
        }

        internal void MarkOverridingServer()
        {
            if (DriveState == BlacksiteHardDriveState.AtExpectedTarget)
                SetMissionStateServer(BlacksiteRetrievalMissionState.Overriding);
        }

        internal void MarkInspectingServer()
        {
            if (DriveState == BlacksiteHardDriveState.AtExpectedTarget)
                SetMissionStateServer(BlacksiteRetrievalMissionState.Inspecting);
        }

        internal void MarkExtractingServer()
        {
            if (!IsTerminal) SetMissionStateServer(BlacksiteRetrievalMissionState.Extracting);
        }

        internal void ResolveServer(BlacksiteRetrievalOutcome outcome)
        {
            if (!IsServer || outcome == BlacksiteRetrievalOutcome.Pending || IsTerminal)
                return;

            BlacksiteRetrievalMissionState previousState = MissionState;
            BlacksiteRetrievalOutcome previousOutcome = Outcome;
            _outcome.Value = (int)outcome;
            switch (outcome)
            {
                case BlacksiteRetrievalOutcome.Recovered:
                    _missionState.Value = (int)BlacksiteRetrievalMissionState.Recovered;
                    _driveState.Value = (int)BlacksiteHardDriveState.Extracted;
                    break;
                case BlacksiteRetrievalOutcome.Denied:
                    _missionState.Value = (int)BlacksiteRetrievalMissionState.Denied;
                    break;
                default:
                    _missionState.Value = (int)BlacksiteRetrievalMissionState.Aborted;
                    break;
            }
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Mission",
                    $"outcome {previousOutcome} -> {Outcome}; state={previousState} -> {MissionState}, drive={DriveState}, carrier={CarrierNetworkId}.");
            }
        }

        internal MoonContractObjective BuildObjective()
        {
            string label = "Protect the contract objective from the Blacksite team";
            int progress = 0;
            // #959 S13: squad-activity states defer to the belt's debounced line; the action
            // arms below tell the player what to do about the Blacksite team.
            bool activity = false;
            if (IsTerminal)
            {
                activity = true;
                progress = 1;
            }
            else if (MissionState == BlacksiteRetrievalMissionState.Inbound
                || MissionState == BlacksiteRetrievalMissionState.Deploying
                || MissionState == BlacksiteRetrievalMissionState.Infiltrating
                || MissionState == BlacksiteRetrievalMissionState.Overriding
                || MissionState == BlacksiteRetrievalMissionState.Inspecting)
            {
                // #725: "contesting", "overriding" and "inspecting" named internal states, not
                // anything a player could see. The activity line says where the team is.
                activity = true;
            }
            else if (MissionState == BlacksiteRetrievalMissionState.Extracting)
                label = "Stop the Blacksite team extracting the cargo";
            else if (MissionState == BlacksiteRetrievalMissionState.EscortingCarrier
                || DriveState == BlacksiteHardDriveState.SoldierHeld)
            {
                label = "Recover the objective from the Blacksite team";
            }
            else if (DriveState == BlacksiteHardDriveState.PlayerHeld)
                label = "Keep the objective away from the Blacksite team";
            else if (DriveState == BlacksiteHardDriveState.InShip)
            {
                // The ship-leave capture decides payout off objective IsComplete
                // (MoonContractRoundEndState.IsContractComplete), so InShip must report complete or
                // the contract can never resolve as a win no matter how the fight ends.
                label = "Hold the hard drive aboard against the Blacksite team";
                progress = 1;
            }
            else if (DriveState == BlacksiteHardDriveState.Loose)
                label = "Recover the displaced hard drive";
            else if (DriveState == BlacksiteHardDriveState.Missing)
            {
                // Shadow Raid keeps its player instruction; every other contract has no drive to
                // locate, so the line is the squad's activity instead.
                if (Contract == MoonContractType.ShadowRaid)
                    label = "Locate the hard drive";
                else
                    activity = true;
            }
            else if (MissionState == BlacksiteRetrievalMissionState.ApproachingExpectedDrive
                || MissionState == BlacksiteRetrievalMissionState.TrackingMissingDrive)
            {
                activity = true;
            }
            else if (MissionState == BlacksiteRetrievalMissionState.RecoveringDrive)
                label = "Keep the objective away from the Blacksite team";

            if (activity)
            {
                // #959 S13: the key carries the status, so the HUD's change detection sees a new
                // identity on every transition without depending on the display text, and the
                // belt title supplies the source name that the old "RECOVERY TEAM \u00B7 " prefix
                // printed a second time (#520).
                BlacksiteActivityStatus status = ActivityStatus;
                BlacksiteActivityLine line = BlacksiteActivityText.Format(
                    status, BlacksiteContractModes.Mode(Contract));
                return new MoonContractObjective(
                    line.Text,
                    progress,
                    1,
                    kind: MoonContractObjectiveKind.Status,
                    key: BlacksiteActivityText.StatusKey(status),
                    lethal: line.Lethal);
            }

            // Default (text) key on purpose: every player-action label marks a distinct mission
            // state with no per-tick payload, so each transition should read as a semantic
            // change on the HUD (#520).
            return new MoonContractObjective(label, progress, 1);
        }

        private void SetMissionStateServer(BlacksiteRetrievalMissionState state)
        {
            if (!IsServer || IsTerminal || MissionState == state)
                return;

            BlacksiteRetrievalMissionState previous = MissionState;
            // #314 D12 hysteresis: a drive being juggled between hands would otherwise walk the
            // derived state back and forth every refresh. A drive-derived state has to hold for a
            // beat before it may flip straight back to the one it just left. Deployment and
            // terminal transitions are never delayed.
            if (state == _stateBeforeCurrent
                && IsDriveDerivedState(state)
                && IsDriveDerivedState(previous)
                && Time.time - _missionStateEnteredAt < DriveStateFlipBackHoldSeconds)
            {
                return;
            }

            _stateBeforeCurrent = previous;
            _missionStateEnteredAt = Time.time;
            _missionState.Value = (int)state;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Mission state {previous} -> {state}.");
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Mission",
                    $"state {previous} -> {state}; drive={DriveState}, carrier={CarrierNetworkId}, target={TargetPosition}.");
            }
        }

        private static bool IsDriveDerivedState(BlacksiteRetrievalMissionState state)
        {
            return state == BlacksiteRetrievalMissionState.ApproachingExpectedDrive
                || state == BlacksiteRetrievalMissionState.TrackingMissingDrive
                || state == BlacksiteRetrievalMissionState.RecoveringDrive
                || state == BlacksiteRetrievalMissionState.EscortingCarrier;
        }

        private static ulong GetNetworkId(NetworkObject networkObject)
        {
            return networkObject != null && networkObject.IsSpawned
                ? networkObject.NetworkObjectId
                : 0UL;
        }
    }
}
