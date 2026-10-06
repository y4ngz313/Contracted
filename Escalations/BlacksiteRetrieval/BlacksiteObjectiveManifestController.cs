using System;
using System.Linq;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts.Survey;
using Y4NGZCompany.Contracts.Payload;
using Y4NGZCompany.Contracts.Whistleblower;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // One authoritative manifest owns existing object identities. Loaded cargo is still contestable.
    internal sealed partial class BlacksiteObjectiveManifestController
    {
        internal const ulong NoCarrier = ulong.MaxValue;
        private readonly BlacksiteRetrievalMissionController _mission;
        private readonly List<BlacksiteSoldierAI> _members = new List<BlacksiteSoldierAI>();
        private readonly Dictionary<ulong, BlacksiteObjectivePhase> _applied = new Dictionary<ulong, BlacksiteObjectivePhase>();
        // #951 S11H U3 (a): what the last EnableItemMeshes call on this record actually said, which
        // anchor it was bound to at the time, and when the record is next reconciled anyway. Test 3
        // called vanilla EnableItemMeshes on every Carried/Loaded record on every frame, outside the
        // `changed` guard: 49,894 "DISABLING/ENABLING SKINNEDMESH: TongueMesh" lines, 62 % of an
        // 80,479-line file, and eight GetComponentsInChildren allocations per frame at 91 fps.
        // It is one dictionary rather than three parallel ones because the three facts are read and
        // written together on exactly one code path. Cleared wherever _applied is cleared, which is
        // nowhere today: both live for the life of the mission controller.
        private readonly struct MeshApplication
        {
            internal MeshApplication(bool enabled, int anchor, float reconcileAt)
            { Enabled = enabled; Anchor = anchor; ReconcileAt = reconcileAt; }
            internal bool Enabled { get; }
            internal int Anchor { get; }
            internal float ReconcileAt { get; }
        }
        private readonly Dictionary<ulong, MeshApplication> _meshApplied = new Dictionary<ulong, MeshApplication>();
        /// <summary>
        /// #951 S11H U3 (a): how many times the replica has actually called the vanilla mesh toggle
        /// since the process started, counted only while diagnostics are on. Nothing reads it to
        /// make a decision; it exists so a scored run and the native fixture can report the same
        /// number, which is what the defect was measured in.
        /// </summary>
        internal static int MeshToggleCallsForDiagnostics;
        /// <summary>
        /// #951 S11H U3 (a): how long a record may go without the toggle being re-asserted.
        ///
        /// Derivation: the vanilla toggle is idempotent (it writes `enabled` on every child
        /// renderer and nothing else), and the only external writer this replica competes with is a
        /// player grab, which the PlayerHeld guard above already skips entirely. So the gate needs
        /// no reconcile for correctness; one second bounds the damage of an external flip nobody
        /// has predicted to a single second, at a cost of one call per record per second instead of
        /// one per record per frame - at Test 3's 91 fps and eight records, 8 calls/s against 728.
        /// </summary>
        private const float MeshReconcileSeconds = 1f;
        // #874 (2026-09-20): _claimAt is gone with the 8 s "cart route blocked" watchdog that was
        // its only reader. PayloadCoupling owns the cart's block, recovery and release now, so a
        // second clock over the same coupling could only disagree with it.
        // #874 (2026-09-20): the dwell belongs to the PAIR, not to the record. It is still keyed
        // by the object - every existing Remove(objectId) site is unchanged - but it now carries
        // the operator whose continuous contact it is measuring, so a reservation that changes
        // hands starts its 1.1 s again instead of inheriting the last man's.
        private readonly Dictionary<ulong, (ulong Carrier, float Ready)> _pickupAt = new Dictionary<ulong, (ulong, float)>();
        private readonly Dictionary<ulong, float> _retryAt = new Dictionary<ulong, float>();
        private float _nextDiscovery, _startedAt = -1f;
        // #1271 S8: the mission's first Discover runs at InitializeServer, while the aircraft is
        // still inbound and no member exists to say ObjectiveSighted, so the call is owed until the
        // raid is under way (UpdateServer, the Infiltrating gate).
        private bool _objectiveCallOwed;
        private bool _departureConfirmed, _lootSpawned;
        internal BlacksiteObjectiveManifestController(BlacksiteRetrievalMissionController mission)
        {
            _mission = mission;
            // #1293 R3: the list is the mission's own and lives as long as this board: never unsubscribed.
            if (mission != null && mission.ObjectiveSnapshots != null)
                mission.ObjectiveSnapshots.OnListChanged += NoteRecordChanged;
        }

        /// <summary>#1293 R3: the host's record-change choke point: a new record, or a world change,
        /// marks every duty; a phase or holder change is also a Phase decision for both holders; a
        /// move raises nothing.</summary>
        private void NoteRecordChanged(NetworkListEvent<BlacksiteObjectiveSnapshot> change)
        {
            if (_mission == null || !_mission.IsServer)
                return;
            if (change.Type == NetworkListEvent<BlacksiteObjectiveSnapshot>.EventType.Add)
            {
                Planner.MarkAllDirty();
                return;
            }
            if (change.Type != NetworkListEvent<BlacksiteObjectiveSnapshot>.EventType.Value)
                return;
            BlacksiteObjectiveSnapshot was = change.PreviousValue, now = change.Value;
            bool held = now.Phase != was.Phase || now.CarrierId != was.CarrierId;
            if (!held && now.Outside == was.Outside)
                return;
            if (held)
            {
                RequestPhaseDecision(was.CarrierId);
                if (now.CarrierId != was.CarrierId)
                    RequestPhaseDecision(now.CarrierId);
            }
            Planner.MarkAllDirty();
        }

        private void RequestPhaseDecision(ulong carrierId)
        {
            if (carrierId == NoCarrier) return;
            BlacksiteSoldierAI member = Soldier(carrierId);
            if (member != null) member.RequestDecision(BlacksiteDecisionCause.Phase);
        }

        /// <summary>
        /// #1018 S4: the mission's one squad planner - the ONE writer of a member's assignment.
        /// Lazy on the same terms every other host-side service on this type is, and the property
        /// the native fixtures reach the planner's state through (design note F.3.2).
        /// </summary>
        private BlacksiteSquadPlanner _planner;
        internal BlacksiteSquadPlanner Planner => _planner ?? (_planner = new BlacksiteSquadPlanner(this));
        private NetworkList<BlacksiteObjectiveSnapshot> Records => _mission.ObjectiveSnapshots;
        internal BlacksiteShipController Ship => _mission.GetComponent<BlacksiteShipController>();
        /// <summary>#1016 S2: the mission's one claim ledger, or null off the server.</summary>
        private BlacksiteResourceBroker Broker
            => _mission != null && _mission.IsServer ? _mission.Blackboard.Broker : null;
        internal bool Returning { get; private set; }
        internal int LoadedCount => Count(BlacksiteObjectivePhase.Loaded);
        internal int ExtractedCount => Count(BlacksiteObjectivePhase.Extracted);
        private int Count(BlacksiteObjectivePhase phase) { int n = 0; foreach (var r in Records) if (r.Kind != BlacksiteObjectiveKind.Loot && r.Phase == phase) n++; return n; }
        internal bool ContainsObject(ulong id) => Existing(id);
        /// <summary>
        /// #1219 B3: the manifest's ShadowDrive record is the one custody truth for the drive, and
        /// this is the ONLY writer of the mission's (drive state, carrier) pair for it while it is
        /// SoldierHeld: Carried is SoldierHeld with the record's carrier, and a record that is no
        /// longer Carried while the mission still reads SoldierHeld is Loose with no carrier. So
        /// <c>[Drive] state ... carrier=</c> goes A -> 0 with the Drop's <c>[Objective] ... dropped:</c>
        /// line and 0 -> B on the next acquisition; it never jumps A -> B. The other drive states
        /// (AtExpectedTarget, PlayerHeld, InShip, Extracted, Missing) keep their existing owners.
        /// </summary>
        private void ReconcileDriveCarriers()
        {
            for (int i = 0; i < Records.Count; i++)
            {
                var record = Records[i];
                if (record.Kind != BlacksiteObjectiveKind.ShadowDrive) continue;
                bool carried = record.Phase == BlacksiteObjectivePhase.Carried;
                ulong carrier = carried ? record.CarrierId : 0UL;
                Resolve(record.ObjectId)?.GetComponent<BreachHardDriveItem>()?.ReconcileManifestCarrierServer(carrier);
                if (carried)
                {
                    if (_mission.DriveState != BlacksiteHardDriveState.SoldierHeld || _mission.CarrierNetworkId != carrier)
                        _mission.SetDriveStateServer(BlacksiteHardDriveState.SoldierHeld, carrier);
                }
                else if (_mission.DriveState == BlacksiteHardDriveState.SoldierHeld)
                    _mission.SetDriveStateServer(BlacksiteHardDriveState.Loose, 0UL);
            }
        }
        internal GrabbableObject CarriedItem(ulong id)
        { int index = FindCarrier(id); return index >= 0 ? Resolve(Records[index].ObjectId)?.GetComponent<GrabbableObject>() : null; }
        internal bool IsCarrier(ulong id) => FindCarrier(id) >= 0;
        /// <summary>
        /// #874 (2026-09-20): whether this member's HANDS are full of mission cargo, which is a
        /// different question from whether he holds an objective.
        ///
        /// <see cref="IsCarrier"/> is custody and duty: the board's protected carrier, the
        /// navigator's priority lane, the routing that sends a holder to the ship. A witness
        /// escort is all of those. He is NOT carrying anything in his hands: he walks a live man
        /// out at gunpoint, and the round 3 defect is
        /// exactly that the two questions shared one property - Equipment resolved
        /// CurrentHandOwner=Cargo for him, so his rifle left his hands and UpdateCarryAnimation
        /// drove a two-handed carry over an empty pair of them.
        ///
        /// Every Carried kind but Witness occupies hands: a drive, an apparatus, a survey drone
        /// and a cart handle are all held. Witness is the one agent in the manifest.
        /// </summary>
        internal bool OccupiesHands(ulong id)
        { int i = FindCarrier(id); return i >= 0 && Records[i].Kind != BlacksiteObjectiveKind.Witness; }
        /// <summary>
        /// #874 (2026-09-20): a Witness, a Payload or a ShadowDrive is the round's reason for the
        /// raid; Loot, Salvage, Survey and Apparatus are work the squad does while it is here.
        /// </summary>
        internal static bool IsPrimaryObjective(BlacksiteObjectiveKind kind)
            => kind == BlacksiteObjectiveKind.Witness
               || kind == BlacksiteObjectiveKind.Payload
               || kind == BlacksiteObjectiveKind.ShadowDrive;
        /// <summary>
        /// #874 (2026-09-20): the round's primary objective is physically on the transport.
        /// Extracted counts as well as Loaded so the answer cannot flicker on the one tick
        /// ConfirmDepartureServer rewrites the phase.
        /// </summary>
        internal bool PrimarySecured
        {
            get
            {
                foreach (var r in Records)
                    if (IsPrimaryObjective(r.Kind)
                        && (r.Phase == BlacksiteObjectivePhase.Loaded || r.Phase == BlacksiteObjectivePhase.Extracted))
                        return true;
                return false;
            }
        }
        internal BlacksiteSoldierAI ReservedOperator
        {
            get { foreach (var r in Records) if (r.Kind == BlacksiteObjectiveKind.ShadowDrive && r.Phase == BlacksiteObjectivePhase.Reserved) return Soldier(r.CarrierId); return null; }
        }
        internal bool IsPushing(ulong id) { int i = FindCarrier(id); return i >= 0 && Records[i].Kind == BlacksiteObjectiveKind.Payload; }
        /// <summary>
        /// #964 S14 U2: this member is marching the witness. The board's answer, from the record,
        /// beside IsPushing and asked the same way; Stage 14 U3 reads it at the entrance, the
        /// internal teleport, the ship passage, the elevator and the speed clamp, so those five
        /// seams all ask ONE question instead of each resolving the witness themselves.
        /// It is deliberately the record and not WhistleblowerEnemyAI.IsEscortedBy: custody is the
        /// witness's truth and the record is the board's, and a seam that refuses a crossing has
        /// to refuse it for the board's reason or the two can disagree for a tick.
        /// </summary>
        internal bool IsEscortingWitness(ulong id) { int i = FindCarrier(id); return i >= 0 && Records[i].Kind == BlacksiteObjectiveKind.Witness; }
        /// <summary>
        /// #874 F02: the member the board has put on this witness, or null.
        ///
        /// The witness's own recognition walks to THIS man rather than to whichever soldier
        /// happens to be nearest, and that is what makes "two soldiers do not alternate custody"
        /// a fact rather than a hope: the reservation is the only thing that can take him -
        /// Acquire only ever offers TryBeginCustodyServer the record's own carrier - so a witness
        /// walking to anyone else is a witness walking to a man who will refuse him. The board is
        /// the existing authority for who owns an objective and it stays the only one; this is a
        /// read of its answer, beside IsEscortingWitness which reads the same record for the
        /// carry.
        ///
        /// Reserved and Carried both answer, so a prisoner whose custody slipped for a tick keeps
        /// walking to the same man instead of re-choosing one.
        /// </summary>
        internal BlacksiteSoldierAI OperatorForWitness(ulong witnessObjectId)
        {
            for (int i = 0; i < Records.Count; i++)
            {
                BlacksiteObjectiveSnapshot r = Records[i];
                if (r.Kind != BlacksiteObjectiveKind.Witness || r.ObjectId != witnessObjectId) continue;
                return r.Phase == BlacksiteObjectivePhase.Reserved || r.Phase == BlacksiteObjectivePhase.Carried
                    ? Soldier(r.CarrierId)
                    : null;
            }
            return null;
        }
        /// <summary>#964 S14 U2: the witness behind a record, or null.</summary>
        private static WhistleblowerEnemyAI WitnessOf(BlacksiteObjectiveSnapshot r)
            => r.Kind == BlacksiteObjectiveKind.Witness ? Resolve(r.ObjectId)?.GetComponent<WhistleblowerEnemyAI>() : null;
        private int FindCarrier(ulong id) { for (int i = 0; i < Records.Count; i++) if (Records[i].CarrierId == id && Records[i].Phase == BlacksiteObjectivePhase.Carried) return i; return -1; }
        internal static NetworkObject Resolve(ulong id) => NetworkManager.Singleton?.SpawnManager != null && NetworkManager.Singleton.SpawnManager.SpawnedObjects.TryGetValue(id, out var obj) ? obj : null;
        private BlacksiteSoldierAI Soldier(ulong id) => Resolve(id)?.GetComponent<BlacksiteSoldierAI>();
        private static bool PlayerHeld(GrabbableObject prop) => prop != null && (prop.playerHeldBy != null || prop.heldByPlayerOnServer || prop.isHeld || prop.isPocketed);
        private static bool CrewSecured(GrabbableObject prop) => prop != null && (prop.isInShipRoom || prop.isInElevator);
        private bool Existing(ulong id) { foreach (var r in Records) if (r.ObjectId == id) return true; return false; }
        private bool HasRecordOf(BlacksiteObjectiveKind kind) { foreach (var r in Records) if (r.Kind == kind) return true; return false; }
        // #1219 B1: the record's first world is the object's own, through the one world function.
        private void Add(NetworkObject obj, BlacksiteObjectiveKind kind)
        {
            if (obj == null || !obj.IsSpawned || Existing(obj.NetworkObjectId)) return;
            Records.Add(new BlacksiteObjectiveSnapshot { ObjectId = obj.NetworkObjectId, CarrierId = NoCarrier, Kind = kind, Phase = BlacksiteObjectivePhase.Available, Slot = kind == BlacksiteObjectiveKind.ShadowDrive ? 4 : Records.Count, Position = obj.transform.position, Outside = BlacksiteWorld.Outside(obj, false) });
            // #949 S11H U1: round= appended last, so a log can say which round an objective
            // identity belongs to without correlating spawn lines by timestamp.
            BlacksiteDiagnostics.Log("Objective", $"registered {kind} object={obj.NetworkObjectId}; existing identity retained, round={ShadowRaidRoundRegistry.CurrentRound}");
            // #1271 S8: once per objective (the Existing test above). Any living member asks; the
            // voice director gives the call to the Leader. Salvage speaks once per registering pass
            // (DiscoverFallbackObjectives), the witness at his first sighting.
            if (kind == BlacksiteObjectiveKind.ShadowDrive || kind == BlacksiteObjectiveKind.Payload || kind == BlacksiteObjectiveKind.Apparatus)
            {
                BlacksiteSoldierAI caller = _members.FirstOrDefault(s => s != null);
                if (caller != null) caller.AnnounceObjectiveSighted();
                else _objectiveCallOwed = true;
            }
        }
        internal void UpdateServer()
        {
            if (!_mission.IsServer || !_mission.IsInitialized || _departureConfirmed) return;
            // #1075: same rule as the drive service's mismatch abort - a contract reading is only
            // evidence of a CHANGE once it is resolved. None is now a real contract (the salvage
            // round), so an unresolved None must not be mistaken for one and abort a running raid.
            if (MoonContractState.IsCurrentContractResolved() && MoonContractState.GetCurrentContract() != _mission.Contract) { CleanupServer(); _mission.ResolveServer(BlacksiteRetrievalOutcome.Aborted); Returning = true; return; }
            _mission.SquadDirector.CollectLivingMembers(_members);
            TickGuardBackup();
            if (Time.time >= _nextDiscovery) { _nextDiscovery = Time.time + 3f; Discover(); }
            if (Ship != null && Ship.IsGrounded && !_lootSpawned) { _lootSpawned = true; SpawnStorageLoot(); }
            // #1305: the mission runs once the touchdown seal has put the squad on the moon.
            if (Ship == null || !Ship.SquadOnMoon) return;
            // #874 (2026-09-20): the board's hold on a boarded body runs BEFORE the containment
            // refresh, not after it. Refresh asks a Loaded record whether its object is still
            // inside the hold; the hold is what puts it there, and running the question first
            // meant every frame of the ascent was decided against a body the hull had already
            // moved away from. Round 3 t=1362.11 is the whole failure: the record left Loaded
            // 0.13 s into an 18 s departure, the witness was handed back to the world, and his
            // custody broke against an escort the departure had already despawned.
            //
            // A record invalidated this tick is still never boarded: the boarding transition
            // itself refuses a dead, neutralized or uncustodied witness, which is a fact about
            // HIM rather than an ordering between two calls.
            TickWitnessBoardingServer();
            for (int i = 0; i < Records.Count; i++) Refresh(i);
            ReconcileDriveCarriers();
            TickRendezvousLoading();
            if (_mission.MissionState < BlacksiteRetrievalMissionState.Infiltrating) { Planner.Tick(Time.time); return; }
            if (_startedAt < 0) _startedAt = Time.time;
            if (_objectiveCallOwed)
            {
                BlacksiteSoldierAI owedCaller = _members.FirstOrDefault(s => s != null);
                if (owedCaller != null) { _objectiveCallOwed = false; owedCaller.AnnounceObjectiveSighted(); }
            }
            int raiders = _members.Count(s => !s.IsShipGuard);
            bool outstanding = false;
            bool discoveredObjective = BlacksiteContractModes.Mode(_mission.Contract) == BlacksiteContractMode.PayloadCart; // #1305 P (D1): raided only once its cart exists
            foreach (var r in Records) if (r.Kind != BlacksiteObjectiveKind.Loot) discoveredObjective = true;
            // #1078 (2026-09-15): `outstanding` is held by the live RECORD. Every writer that takes a
            // Payload record out of Available/Reserved/Carried is a fact about the cart or the world:
            //   Refresh                  - the object gone, the cart delivered or failed; Staged within the lift
            //                              reach (#1293 R2); #1305 G4 a latched stuck verdict (AnswerStuckCartAgain);
            //   ReleaseJob (Jobs.cs)     - Unavailable only through the holder's own Unsupported route at the
            //                              second failure (#1305 C4, G1) or six failures without haul
            //                              progress (#1219 A4), both written off as lost (event=lost);
            //   Drop                     - the same budget arm, and #1219 D3's CartImmovable;
            //   AbandonCargo (Cargo.cs)  - the stuck verdict (NoCartRoute), and after
            //                              the recall owed cargo no living member can load or be reserved
            //                              to haul (#1305 G4: a held lease ends on its own progress fact);
            //   TickRendezvousLoading    - Loaded, which is the cart aboard the ship.
            // Boarding is deliberately NOT blocked on a cart: the Clock and the return rule are the
            // backstop, and a raid that cannot reach its cart must still leave.
            foreach (var r in Records)
                if (BlacksiteObjectivePolicy.RecordOutstanding(r.Kind != BlacksiteObjectiveKind.Loot,
                    r.Phase == BlacksiteObjectivePhase.Available, r.Phase == BlacksiteObjectivePhase.Reserved,
                    r.Phase == BlacksiteObjectivePhase.Carried))
                { outstanding = true; break; }
            // Return empty if no retrieval members remain, the real object is absent, or the raid times out.
            UpdateRaidState(raiders);
            // #874 S6.5a: name the rule that ordered the extraction. On 2026-09-09 the line said
            // only outstanding=False, one line after the sixth failed attempt; that is a symptom.
            // #1066 U2 / #1305 C4 (R12) / #1305 E2: the branch names objectivesDone only with mission
            // cargo Loaded (the line's loaded=) or Extracted aboard - salvage hauled after a written-off
            // apparatus included - and objectivesLost with nothing aboard: lost is never done, and a
            // write-off (event=lost) never outranks cargo the squad brought home.
            // #874 Q11: a squad the deploy slice is still spawning is not "raiders gone".
            var returnRule = BlacksiteObjectivePolicy.ReturnRule(raiders, outstanding, Time.time - _startedAt, discoveredObjective, _withdrawalCommitted, PrimarySecured, PrimaryAwaitingPickup, _mission.SoldierDeploymentPending, LoadedCount + ExtractedCount > 0);
            if (returnRule != BlacksiteReturnRule.None)
            {
                // #942 S11 U4: AnnouncePickup() moves here from the ship's recall branch, which is reachable only from Offsite: a ground ship that now holds station never leaves, so it would otherwise never announce the pickup it is committing to.
                if (!Returning) { BlacksiteDiagnostics.Log("Extraction", $"Boarding ordered: raiders={raiders}, outstanding={outstanding}, discovered={discoveredObjective}, elapsed={Time.time - _startedAt:0}s, loaded={LoadedCount}, rule={BlacksiteObjectivePolicy.ReturnRuleToken(returnRule)}."); _members.FirstOrDefault(s => s != null)?.AnnouncePickup(); }
                Returning = true; Ship.BeginBoardingServer(); _mission.MarkExtractingServer();
            }
            // #874 (2026-09-20): there is no un-committing arm: once the recall is ordered the squad comes
            // home, #1293 R2 with its owed cargo. After it the board reserves owed cargo that is not staged
            // (#1305 G4: to a withdrawing member too), and owed cargo nobody can haul is written off.
            AssignReservations();
            if (Returning) WriteOffUnhauledCargo();
            Planner.Tick(Time.time);
        }
        /// <summary>
        /// #1293 R1: a witness is a game fact, not a sighting. Step 1 of the squad director's
        /// perception pass: every witness whose record is still in play (not Extracted, Unavailable or
        /// Loaded), alive and not held aboard, is a Live Witness row at his own transform; any other
        /// witness has no row. The pass sets the row's sight bits and announces the first sighting.
        /// The boarding invariant (a witness stowed aboard is not "seen" by the men on the deck) is
        /// asked of the witness himself, which is stronger than an ordering between two calls.
        /// </summary>
        internal void PublishWitnessRowsServer(BlacksiteWorldModel world, float now)
        {
            for (int i = 0; i < Records.Count; i++)
            {
                BlacksiteObjectiveSnapshot record = Records[i];
                if (record.Kind != BlacksiteObjectiveKind.Witness) continue;
                WhistleblowerEnemyAI witness = record.Phase == BlacksiteObjectivePhase.Extracted
                    || record.Phase == BlacksiteObjectivePhase.Unavailable
                    || record.Phase == BlacksiteObjectivePhase.Loaded
                        ? null : Resolve(record.ObjectId)?.GetComponent<WhistleblowerEnemyAI>();
                if (witness == null || witness.isEnemyDead || witness.IsHeldAboard) world.Forget(record.ObjectId);
                else world.Live(record.ObjectId, witness, BlacksiteWorldClass.Witness, witness.transform.position,
                    BlacksiteWorld.Outside(witness), now);
            }
        }
        /// <summary>
        /// #1293 R2: the cart is a game fact too, beside the witness and in the same pass: every
        /// Payload record still in play (not Extracted, Unavailable or Loaded) whose cart exists is a
        /// Live Objective row at the cart's transform; any other has no row. Read by Refresh's
        /// lift-reach measurement (Staged) and, as presence only, by TryPickupApproach (#1305 G4). Host only.
        /// </summary>
        internal void PublishCargoRowsServer(BlacksiteWorldModel world, float now)
        {
            for (int i = 0; i < Records.Count; i++)
            {
                BlacksiteObjectiveSnapshot record = Records[i];
                if (record.Kind != BlacksiteObjectiveKind.Payload) continue;
                PayloadCartController cart = record.Phase == BlacksiteObjectivePhase.Extracted
                    || record.Phase == BlacksiteObjectivePhase.Unavailable
                    || record.Phase == BlacksiteObjectivePhase.Loaded
                        ? null : Resolve(record.ObjectId)?.GetComponent<PayloadCartController>();
                if (cart == null) world.Forget(record.ObjectId);
                else world.Live(record.ObjectId, cart, BlacksiteWorldClass.Objective, cart.transform.position,
                    BlacksiteWorld.Outside(cart), now);
            }
        }
        /// <summary>
        /// #990: this member holds the Witness reservation and the squad has him in sight: his Live
        /// row's <c>InSight</c>, the pass's own sight bits (#1293 R1). The pursuit floor is asked exactly
        /// this question and nothing else, so an escort walking a witness OUT (a Carried record) never
        /// qualifies: #1293 R2 he walks at his role speed with the witness attached.
        /// </summary>
        internal bool IsPursuingWitnessInSight(ulong id)
        {
            foreach (var record in Records)
            {
                if (record.Kind != BlacksiteObjectiveKind.Witness
                    || record.Phase != BlacksiteObjectivePhase.Reserved
                    || record.CarrierId != id) continue;
                return TryLiveRow(record.ObjectId, out var row) && row.InSight;
            }
            return false;
        }
        // #1293 R1: the object's Live row in the squad's world model (a witness's: PublishWitnessRowsServer).
        // Host only: a client has no squad director, so no row.
        private bool TryLiveRow(ulong objectId, out BlacksiteWorldRow row)
        {
            row = null;
            var world = _mission.SquadDirector?.World;
            return world != null && world.TryGet(objectId, out row) && row.Source == BlacksiteWorldSource.Live;
        }
        /// <summary>
        /// #1066 U2: has the round itself ended under the mission. The same signal every contract in
        /// this repo tests (<c>StartOfRound.inShipPhase</c> / <c>shipIsLeaving</c>), read here so a
        /// record whose object was destroyed by the round-end purge can be named RoundEnded rather
        /// than sharing Invalidated with a mid-raid theft or despawn.
        /// </summary>
        private static bool RoundHasEnded()
        {
            var start = StartOfRound.Instance;
            return start == null || start.inShipPhase || start.shipIsLeaving;
        }
        /// <summary>
        /// #1066 U2: an objective disappeared. This ALWAYS writes its [Objective] line - the old path
        /// only logged through CloseAttempt, which is silent when no attempt happened to be open, so
        /// the 2026-09-15 purge left no trace at all. Loot is excluded from the squad's call: a loot
        /// record going away is routine and is not what "the raid's objective was taken" means.
        /// </summary>
        private void NoteObjectiveLost(BlacksiteObjectiveSnapshot record, BlacksiteObjectiveCloseCause cause)
        {
            BlacksiteDiagnostics.Log("Objective",
                $"event=lost, object={record.ObjectId}, kind={record.Kind}, "
                + $"cause={cause}, "
                + $"phase=Unavailable, round={ShadowRaidRoundRegistry.CurrentRound}");
            // #1271 S8: the round-end purge is not the squad losing anything, and loot is routine.
            if (record.Kind != BlacksiteObjectiveKind.Loot && cause != BlacksiteObjectiveCloseCause.RoundEnded)
            {
                BlacksiteSoldierAI caller = _members.FirstOrDefault(s => s != null);
                if (caller != null) caller.AnnounceObjectiveLost();
            }
        }
        // #941 S11 U3: the adapter is chosen by the contract mode table, not by the contract, so
        // BlacksiteContractModes.Mode is the single source for both this branch and the scheduling
        // gate. Excluded contracts never reach here (BlacksiteRetrievalRules.IsContractEligible
        // stops the mission being initialized), and the arm exists only so an excluded contract
        // that somehow arrives discovers nothing rather than raiding.
        private void Discover()
        {
            switch (BlacksiteContractModes.Mode(_mission.Contract))
            {
                case BlacksiteContractMode.HardDrive:
                    // #1219 B3: the target, the drive's objective configuration and the mission's
                    // drive id are set by the FIRST discovery only. Re-running them every 3 s printed
                    // the [Mission] target / [Drive] network object lines each time and rewrote the
                    // drive's _extracted flag under a live raid (ConfigureObjectiveServer).
                    if (HasRecordOf(BlacksiteObjectiveKind.ShadowDrive)) break;
                    if (ShadowRaidContract.TryGetBlacksiteRetrievalTarget(out var target))
                    {
                        _mission.SetTargetServer(target);
                        var drive = target.Drive != null ? target.Drive.GetComponent<BreachHardDriveItem>() : null;
                        if (drive != null) { if (_mission.DriveState == BlacksiteHardDriveState.Missing) _mission.SetDriveStateServer(BlacksiteHardDriveState.AtExpectedTarget, 0); drive.ConfigureObjectiveServer(_mission.Contract, _mission.MissionNetworkId, false); _mission.SetDriveServer(drive); Add(drive.NetworkObject, BlacksiteObjectiveKind.ShadowDrive); }
                    }
                    break;
                case BlacksiteContractMode.PayloadCart:
                    var cart = PayloadContract.ActiveCart; if (cart != null && !cart.IsDelivered && !cart.IsFailed) Add(cart.NetworkObject, BlacksiteObjectiveKind.Payload);
                    break;
                // #964 S14 U2: the witness escort adapter #941 reserved this arm for. The witness
                // is the objective while he is alive and the escort is switched on. #874 Q10: he
                // stays the contract's objective after that too - a dead or Unavailable witness
                // gives the squad no substitute work; only with the escort switched off does the
                // fallback raid run (DiscoverFallbackObjectives' own primary test decides).
                case BlacksiteContractMode.WitnessEscort:
                    if (!DiscoverWitnessObjective()) DiscoverFallbackObjectives();
                    break;
                case BlacksiteContractMode.Default:
                    DiscoverFallbackObjectives();
                    break;
                case BlacksiteContractMode.Excluded:
                    break;
            }
            if (PrimaryHeld && !Returning) DiscoverSalvage(); // #1305 G5 (D17): work while held, not a substitute mission (#874 Q10's Fallback table unchanged).
        }
        /// <summary>
        /// #964 S14 U2: register the live witness as the round's objective, once. Idempotent the
        /// way the drive's arm is: Add refuses an identity the board already holds, so the 3 s
        /// discovery tick re-enters this without producing a second record, and Refresh is what
        /// keeps his position current.
        ///
        /// Returns false when there is nothing to escort - the setting is off, no witness has
        /// spawned yet, or the crew has already neutralized him. The caller then asks the fallback,
        /// which runs only when the setting is off (#874 Q10). Once a record exists it is the
        /// record's own phase, not this test, that decides what the squad does: a dead witness is
        /// closed Completed by Refresh and stays Unavailable, and this returns false from then on.
        /// </summary>
        private bool DiscoverWitnessObjective()
        {
            if (!BlacksiteCustodySettings.EscortEnabled) return false;
            var witness = MoonContractState.ActiveWhistleblower;
            if (witness == null || witness.NetworkObject == null || !witness.NetworkObject.IsSpawned) return false;
            if (Existing(witness.NetworkObjectId))
            {
                foreach (var r in Records)
                    if (r.ObjectId == witness.NetworkObjectId)
                        return r.Phase != BlacksiteObjectivePhase.Unavailable;
                return false;
            }
            // Neutralized covers both the kill and the Neutralized behaviour state, which is the
            // same test GetWhistleblowerObjectives completes the contract row on.
            if (witness.isEnemyDead || witness.Neutralized) return false;
            Add(witness.NetworkObject, BlacksiteObjectiveKind.Witness);
            return true;
        }
        private void Refresh(int index)
        {
            var r = Records[index]; if (r.Phase == BlacksiteObjectivePhase.Extracted || r.Phase == BlacksiteObjectivePhase.Unavailable) return;
            // #874 S6.1a: every direct close names its cause. These six sites closed silently on
            // 2026-09-09, so an [UAT][Objective] grep could not pair a reservation with anything.
            // #1066 U2: the object is GONE. Before this unit that closed with Invalidated and, if no
            // attempt happened to be open, CloseAttempt logged nothing at all - the 2026-09-15
            // round-end ShadowRaid purge destroyed the drive and the only trace was `outstanding`
            // silently going false one tick later, which ReturnRule then read as objectivesDone.
            // Name the cause, always write the line, and remember that this objective was LOST so
            // the return rule cannot report a purge as a completed raid.
            var obj = Resolve(r.ObjectId);
            if (obj == null)
            {
                var lostCause = RoundHasEnded()
                    ? BlacksiteObjectiveCloseCause.RoundEnded
                    : BlacksiteObjectiveCloseCause.Invalidated;
                CloseAttempt(r, BlacksiteAttemptOutcome.Interrupted, lostCause);
                r.Phase = BlacksiteObjectivePhase.Unavailable; r.CarrierId = NoCarrier; Records[index] = r;
                NoteObjectiveLost(r, lostCause);
                return;
            }
            var prop = obj.GetComponent<GrabbableObject>(); var cart = obj.GetComponent<PayloadCartController>();
            if (cart != null && (cart.IsDelivered || cart.IsFailed)) { if (cart.IsDelivered) CompleteHaul(r); else CloseAttempt(r, BlacksiteAttemptOutcome.Interrupted, BlacksiteObjectiveCloseCause.Invalidated); r.Phase = BlacksiteObjectivePhase.Unavailable; r.CarrierId = NoCarrier; Records[index] = r; if (!cart.IsDelivered) NoteObjectiveLost(r, BlacksiteObjectiveCloseCause.Invalidated); /* #1066 U2 */ return; }
            // #964 S14 U2: the witness's own terminal, beside the cart's and read the same way.
            // A dead witness is the CONTRACT completing - the crew won - so the attempt closes
            // Completed, and the record goes Unavailable because there is nothing left to escort.
            // Killing him in an escort's hands is the intended climax, so this fires while he is
            // Carried as readily as while he is Available; the custody release that goes with it
            // is the witness's own (U1 releases in KillEnemy), and the record's Drop is skipped
            // entirely because a terminal record is never handed back to the board.
            var witness = WitnessOf(r);
            if (witness != null && (witness.isEnemyDead || witness.Neutralized))
            {
                CompleteHaul(r);
                r.Phase = BlacksiteObjectivePhase.Unavailable; r.CarrierId = NoCarrier;
                r.Position = obj.transform.position; Records[index] = r; return;
            }
            // #1293 R2: Staged is measured every tick, not remembered (WithinLiftReach): within the
            // ship's lift reach it stays Staged whoever moves it - a player on the handle or in whose
            // hands it is included - and out of reach it is Available, still owed. #1305 H1: the ship's
            // own device holding a cart (the magnet's rise and dock, the cable trip) lifts it out of reach.
            if (r.Phase == BlacksiteObjectivePhase.Staged)
            {
                if (cart != null && cart.IsUsingBlacksiteMagnet(Ship.NetworkObjectId)) return;
                if (!WithinLiftReach(r, obj, out Vector3 at))
                {
                    r.Phase = BlacksiteObjectivePhase.Available; r.CarrierId = NoCarrier; r.ContainerCargo = false; r.Position = at; Records[index] = r;
                    BlacksiteDiagnostics.Log("Cargo", $"unstaged {r.Kind} object={r.ObjectId}: out of the ship's lift reach at {at}.");
                }
                else if (Vector3.Distance(r.Position, at) > BlacksiteArrivalPolicy.MinDestinationChange)
                { r.Position = at; Records[index] = r; }
                return;
            }
            if (r.Phase == BlacksiteObjectivePhase.Loaded)
            {
                // #874 (2026-09-20): the departure is a CUTOFF. While the transport is still on
                // the ground a loaded objective stays contestable - a player may take it out of
                // the hold, and the cart may be re-attached - but from the frame the hull commits
                // to leaving, "is it still inside the hold" is a question about a volume that is
                // climbing, and the only honest answer is the one the board already has. Round 3
                // t=1362.11 lost the round's whole objective to it: 0.13 s into an 18 s departure
                // the witness fell out of Loaded, was handed back to the world in mid-air, and
                // broke custody against an escort that no longer existed.
                if (Ship.IsDeparting) return;
                // #874 (2026-09-22, Rend): a release that is still IN FLIGHT is not an object out
                // of the hold. The hold test measures the object's live transform, and between the
                // frame a carrier is granted the load and the frame the authored release reaches
                // the slot the object is in his hands, up to the gate's own 1.5 m plus the 1.25 m
                // loading reach away from the box. Reading that gap as contestation closed the
                // attempt and handed the record straight back - and the retained-crate delivery,
                // whose loader releases beside a box on the deck, would have looped on it. The
                // release is host-authored, its target IS the slot, it is bounded by the
                // presentation's own 6 m/s and it ends the moment its carrier dies or is stunned,
                // after which this asks again. A player grab still wins immediately, on the term
                // that measures the player rather than the object.
                if (PlayerHeld(prop) || (cart != null && cart.IsAttached)
                    || (!BlacksiteCarryPresentation.IsReleasing(prop) && !CargoContains(r, obj.transform.position)))
                { CloseAttempt(r, BlacksiteAttemptOutcome.Interrupted, BlacksiteObjectiveCloseCause.Interrupted); r.Phase = BlacksiteObjectivePhase.Available; r.CarrierId = NoCarrier; r.Position = obj.transform.position; Records[index] = r; }
                return;
            }
            if (r.Phase == BlacksiteObjectivePhase.Carried)
            {
                var carrier = Soldier(r.CarrierId);
                // #964 S14 U2: the witness's per-kind interruption term. The manifest OBSERVES
                // custody, it never decides it: the witness has already released him on a real
                // event (the knockdown, the stun, the escort's death) with a named cause on the
                // [UAT][Custody] line, so the board's only job is to notice that this member no
                // longer holds him and hand the record back. #1293 R2: the cargo-threat term is the
                // Payload's alone; an escort in combat keeps his hold.
                bool custodyLost = witness != null && (carrier == null || !witness.IsEscortedBy(carrier));
                bool cargoThreat = carrier != null && carrier.HasImmediateCargoThreat
                    && r.Kind == BlacksiteObjectiveKind.Payload;
                if (custodyLost || BlacksiteObjectivePolicy.CarrierInterrupted(carrier != null, carrier != null && carrier.isEnemyDead, carrier != null && carrier.ReplicatedStunned, cargoThreat, PlayerHeld(prop)))
                { Drop(index, carrier != null ? carrier.transform.position : obj.transform.position, $"carrier interrupted (dead={carrier == null || carrier.isEnemyDead}, stunned={carrier != null && carrier.ReplicatedStunned}, combat={carrier != null && carrier.IsObjectiveCombatActive}, playerHeld={PlayerHeld(prop)}, custodyLost={custodyLost})", BlacksiteAttemptOutcome.Interrupted, CustodyCauseFor(carrier, custodyLost)); return; }
                if (carrier.isOutside && TryStageOrLoad(index, r, carrier, obj, prop, cart)) return;
                // #1219 B1: a carried record's world IS its carrier's world, for every kind. The
                // record republishes the carrier's world and the object's point whenever the world
                // flips or the point has moved past BlacksiteArrivalPolicy.MinDestinationChange
                // 1.45 m - the navigator's own re-issue distance, so a carry costs the replication
                // traffic an Available record does. Before this only a witness republished, and a
                // drive carried out of the facility kept reading "inside" for the whole carry: the
                // round 3 escorts alternated Infiltrate/Escort on that frozen world, and the claim's
                // old ceiling (deleted by #1293 R2) teleported the drive back to its reservation-time point.
                bool carrierOutside = BlacksiteWorld.Outside(carrier);
                if (r.Outside != carrierOutside
                    || Vector3.Distance(r.Position, obj.transform.position) > BlacksiteArrivalPolicy.MinDestinationChange)
                { r.Outside = carrierOutside; r.Position = obj.transform.position; Records[index] = r; }
                // #964 S14 U2: a witness is walked INSIDE the ship, never to the cargo loading
                // position, because there is nothing to load him into: the Carried-to-Loaded
                // transition is his escort's completed board crossing (#1219 C2), not a staging
                // release at the container. Everything else on the carry leg is shared: the same
                // watermark, the same goal-identity restart at the world boundary.
                Vector3 carryTarget = r.Kind == BlacksiteObjectiveKind.Witness ? Ship.BoardingPosition : CargoDeliveryPoint(r);
                var carryPhase = carrier.IsWaitingForTraversal ? BlacksiteJobPhase.Waiting : carrier.isOutside && Vector3.Distance(carrier.transform.position, carryTarget) < 5f ? BlacksiteJobPhase.Load : BlacksiteJobPhase.Carry;
                // #873: a cart stages on the ground beyond the drop; it stays Carried until #839 loads it.
                Vector3 carryGoal = !carrier.isOutside ? _mission.SquadDirector.InsideMainEntrance : carryTarget;
                // #874 S6.1: the carry leg observes through the same watermark as the reserved leg;
                // its goal swaps outright at the world boundary, from the inside entrance to the
                // ship's cargo loading position, which no distance baseline can survive undeclared.
                bool coupled = cart != null && cart.TryFollowNpcServer(carrier);
                // #1305 R18/R23: the cart's plan verdict is the carry lease's progress fact. A cart that
                // reports no plan lets its holder go this tick (no push pose without a plan); a pair
                // with a plan that does not move is the Push ledger's (AnswerStalledRecord).
                string refusal = coupled ? cart.NpcPlanRefusal : null;
                if (refusal != null)
                {
                    cart.ReleaseNpcPusherServer("blocked");
                    Drop(index, carrier.transform.position, "cart has no plan, planRefusal=" + refusal, failedCause: BlacksiteObjectiveCloseCause.NoCartRoute);
                    return;
                }
                if (ObserveNow(r.ObjectId)) ObserveCarryJob(r, carrier, carryPhase, carryGoal);
                // #874 (2026-09-20): the manifest observes the coupling; TryFollowNpcServer is false
                // only once the cart has actually let the pusher go.
                // #1305 H1: this ship's magnet taking the cart off its carrier is no failed haul.
                if (cart != null && !coupled)
                    Drop(index, carrier.transform.position, "cart released this carrier",
                        cart.IsUsingBlacksiteMagnet(Ship.NetworkObjectId) ? BlacksiteAttemptOutcome.Interrupted : BlacksiteAttemptOutcome.Failed);
                return;
            }
            // #1293 R2: the other half of the measurement - an owed record no NPC hauler holds that
            // stands within the lift reach is Staged, whoever pushed it there.
            if (r.Phase == BlacksiteObjectivePhase.Available && WithinLiftReach(r, obj, out Vector3 reached))
            {
                r.Phase = BlacksiteObjectivePhase.Staged; r.CarrierId = NoCarrier; r.ContainerCargo = false;
                r.Position = reached; r.Outside = BlacksiteWorld.Outside(obj, r.Outside); Records[index] = r;
                BlacksiteDiagnostics.Log("Cargo", $"staged {r.Kind} object={r.ObjectId} within the ship's lift reach at {reached}.");
                return;
            }
            if (PlayerHeld(prop) || CrewSecured(prop) || (cart != null && cart.IsAttached))
            { if (r.Phase == BlacksiteObjectivePhase.Reserved) { CloseAttempt(r, BlacksiteAttemptOutcome.Interrupted, BlacksiteObjectiveCloseCause.Interrupted); r.Phase = BlacksiteObjectivePhase.Available; r.CarrierId = NoCarrier; Records[index] = r; } return; }
            // #1219 B1: an unheld object's world is its own, through the one world function (the
            // cart's replicated flag, the witness's own isOutside, a prop's isInFactory).
            r.Position = obj.transform.position; r.Outside = BlacksiteWorld.Outside(obj, r.Outside);
            // #1219 D3: a stuck cart is never offered to a soldier again (CanReserveNpc), so one left
            // Available - a player moved it out of the lift reach (or back inside) and let go, or #1305 G4
            // its lease ended at the pose that latched the verdict - is written off where it stands.
            if (r.Phase == BlacksiteObjectivePhase.Available && AnswerStuckCartAgain(index, r, cart)) return;
            if (r.Phase == BlacksiteObjectivePhase.Reserved)
            {
                var carrier = Soldier(r.CarrierId);
                // A guard - posted or called in to a report - cannot hold a reservation. #1293 R2: nor can
                // a witness whose agent went unbound after the board reserved him: one Interrupted close.
                if (carrier == null || carrier.isEnemyDead || ((carrier.IsShipGuard || GuardCalledIn(r.CarrierId)) && !r.ContainerCargo)
                    || (witness != null && witness.IsAgentUnbound)) { CloseAttempt(r, BlacksiteAttemptOutcome.Interrupted, BlacksiteObjectiveCloseCause.Interrupted); r.Phase = BlacksiteObjectivePhase.Available; r.CarrierId = NoCarrier; }
                else if (HasPlayerResponse(carrier))
                {
                    _pickupAt.Remove(r.ObjectId);
                    return;
                }
                else if (ShouldTransfer(r, carrier)) ReleaseJob(ref r, carrier, false, "reachable operator already in target world", BlacksiteObjectiveCloseCause.Reassigned, BlacksiteAttemptOutcome.Interrupted);
                else
                {
                    Vector3 approach;
                    HandleApproach handle = HandleApproach.Pose; bool approachValid;
                    if (cart != null) approachValid = (handle = TryHandleApproach(r, cart, carrier, out approach)) == HandleApproach.Pose;
                    else approachValid = TryPickupApproach(r, carrier, out approach);
                    if (!approachValid && carrier.isOutside == ObjectiveWorld(r) && Vector3.Distance(carrier.transform.position, r.Position) < 6f
                        && _pickupProbeFailures.TryGetValue(r.ObjectId, out int failedProbes) && failedProbes >= 3)
                    // #874 S6.2 / #1078: a prop resolver refusing every ring is a placement fact: Interrupted, no mission
                    // withdrawal (WithdrawalCause has no probe arm). #1305 G4: a cart charges no probe (EndStalledCartLease).
                    { ReleaseJob(ref r, carrier, true, "no reachable standing point after three bounded probes", BlacksiteObjectiveCloseCause.ApproachUnreachable, BlacksiteAttemptOutcome.Interrupted); Records[index] = r; return; }
                    bool atAccess = TryAcquireOnDwellServer(ref r, carrier, obj, prop, cart, approach, approachValid);
                    if (r.Phase == BlacksiteObjectivePhase.Reserved && ObserveJob(r, carrier, approach, atAccess) && cart != null) EndStalledCartLease(ref r, carrier, cart, handle);
                }
            }
            Records[index] = r;
        }
        /// <summary>
        /// The dwell gate on the action's own access, and the acquisition that gate admits.
        ///
        /// A method of its own for the reason <c>TickWitnessRecordServer</c> is one: a non-empty
        /// NetworkList cannot be enumerated headlessly, so a fixture that wants to drive the REAL
        /// capture - rather than seed a captured state and call that proof - has to be able to
        /// hand this a record of its own. Returns whether the operator stands in the action's
        /// access this tick, which is what the job observer reports; <paramref name="record"/>
        /// becomes Carried only once the sustained dwell has run AND the real acquisition
        /// succeeded.
        ///
        /// #874 S1.2: the gate is the action's own, measured from the action's anchor - the
        /// cart's production attach test, not a nearby destination. Dwell may not accumulate
        /// while the operator is still travelling or turned away from it.
        ///
        /// #964 S14 U2: the witness's gate is the ACTION's own, exactly as the cart's is:
        /// BlacksiteCustodyPolicy.CanBegin measures body to body against U1's fixed
        /// CustodyTakeDistance 1.2, so the board's dwell has to be measured against the same
        /// thing and not against the 0.80 m item reach it would otherwise use. The measurement is
        /// to his live transform, never to the published approach point, because he walks: an
        /// approach point a second old is a place he has left. Facing is not part of it - a man
        /// is taken from behind as readily as from in front.
        ///
        /// #964 recovery (2026-09-20): and neither is the operator's own GROUND SPEED, which is
        /// the term that made a moving witness uncapturable. IsSettledForInteraction asks "is
        /// this body still travelling toward the thing it is about to operate", and it answers
        /// that by reading the agent's absolute velocity against 0.35 m/s. For a prop, a cart or
        /// a crate - things that do not move - absolute velocity IS the velocity relative to the
        /// thing, so the term is exactly right and every one of those kinds keeps it unchanged.
        /// For a man walking at about 1.7 m/s it is a contradiction: the only escort who can be
        /// inside a 1.2 m body gate on him is one matching his pace, and that escort's agent is
        /// never under 0.35. The Toystore run is the whole proof - every [UAT][Objective]
        /// event=arrived line for goalKind=Witness carries settled=False (whole-log 33082, 33788,
        /// 33896, 36470, 37167, 40772) and the 15 MB log holds no [UAT][Custody] line at all.
        ///
        /// What replaces it for a witness is not nothing; it is the DWELL, which is the same
        /// question asked about the pair instead of about one body. The 1.1 s clock below is
        /// removed on any tick the gate fails, so it only ever completes when the escort has held
        /// himself inside the 1.2 m body gate, in the same world, out of combat, with an
        /// unobstructed line to the body, continuously, for 1.1 s. An escort sprinting past a
        /// standing witness holds that for 0.34 s and is refused; an escort who lets a walking
        /// witness open the gap resets it on the tick the gap opens; an escort who paces him is
        /// doing precisely the thing the escort is for. Nothing else is relaxed: the range, the
        /// world, the combat term, the real line of sight in HasPhysicalAccess and the custody
        /// policy's own re-check inside TryBeginCustodyServer all still have to pass, so capture
        /// stays neither remote nor instantaneous.
        /// </summary>
        private bool TryAcquireOnDwellServer(ref BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI carrier,
            NetworkObject obj, GrabbableObject prop, PayloadCartController cart, Vector3 approach, bool approachValid)
        {
            bool witnessTake = record.Kind == BlacksiteObjectiveKind.Witness;
            bool atAccess = !carrier.IsObjectiveCombatActive && approachValid && carrier.isOutside == ObjectiveWorld(record)
                && (cart != null
                    ? cart.IsWithinNpcAttachDistance(carrier.transform.position) && carrier.IsFacing(cart.transform.position, 45f)
                    : witnessTake
                    ? Vector3.Distance(carrier.transform.position, obj.transform.position) <= BlacksiteCustodySettings.CustodyTakeDistance
                    : Vector3.Distance(carrier.transform.position, approach) < PickupReachDistance)
                && (witnessTake || carrier.IsSettledForInteraction)
                && HasPhysicalAccess(record, prop, carrier);
            if (!atAccess) { _pickupAt.Remove(record.ObjectId); return false; }
            if (!_pickupAt.TryGetValue(record.ObjectId, out var dwell) || dwell.Carrier != carrier.NetworkObjectId)
            { _pickupAt[record.ObjectId] = (carrier.NetworkObjectId, Time.time + 1.1f); return true; }
            if (Time.time < dwell.Ready || !Acquire(record, carrier, prop, cart)) return true;
            // Acquisition completes the pickup attempt. #1219 A4: it no longer clears the
            // objective's failure history - only haul progress does (ObserveCarryJob, CompleteHaul).
            // The carry to the rendezvous is a different goal, so it opens its own attempt for
            // the same member rather than reusing the pickup clock.
            CloseAttempt(record, BlacksiteAttemptOutcome.Completed, BlacksiteObjectiveCloseCause.Completed);
            BeginAttempt(record, carrier.NetworkObjectId);
            BlacksiteDiagnostics.Log("Objective", $"acquired {record.Kind} object={record.ObjectId}, member={carrier.MemberIndex}.");
            // #1271 S8: the taker reports it. The drive already speaks DriveAcquired from
            // BlacksiteSquadDirector.UpdateVoiceDriveState.
            if (record.Kind == BlacksiteObjectiveKind.Witness) carrier.AnnounceWitnessTaken();
            else if (record.Kind != BlacksiteObjectiveKind.ShadowDrive) carrier.AnnounceObjectiveTaken();
            record.Phase = BlacksiteObjectivePhase.Carried; record.Acquired = true;
            _pickupAt.Remove(record.ObjectId);
            return true;
        }
        private bool HasPhysicalAccess(BlacksiteObjectiveSnapshot r, GrabbableObject prop, BlacksiteSoldierAI carrier)
        {
            if (carrier.isOutside != ObjectiveWorld(r)) return PickupRejected(r, "different world space", carrier);
            // #948 S12 U3: an egg is never picked up, whatever put it in the manifest.
            if (ProtectedEggPickup(r, prop, carrier)) return false;
            var target = Resolve(r.ObjectId);
            if (target == null) return PickupRejected(r, "object missing", carrier);
            ShadowRaidCargoCrate cache = null;
            if (r.Kind == BlacksiteObjectiveKind.ShadowDrive)
            {
                var container = BreachContainerController.FindByNetworkId(_mission.ExpectedTargetNetworkId);
                cache = Resolve(_mission.SecondaryTargetNetworkId)?.GetComponent<ShadowRaidCargoCrate>();
                if ((container != null && !container.IsOpen) || (cache != null && !cache.Opened))
                    return PickupRejected(r, "container or cache closed", carrier);
            }
            Vector3 origin = carrier.transform.position + Vector3.up * 1.2f;
            Vector3 end = target.transform.position + Vector3.up * .15f;
            if (Vector3.Distance(origin, end) > 2.2f) return PickupRejected(r, "item beyond reach", carrier);
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            foreach (var hit in Physics.RaycastAll(origin, (end - origin).normalized, Vector3.Distance(origin, end), mask, QueryTriggerInteraction.Ignore))
            {
                if (hit.transform.IsChildOf(target.transform) || hit.transform.IsChildOf(carrier.transform)) continue;
                // An opened case's simple collision shell still encloses its item. Match its
                // player reach contract, but never ignore shipping-container or room walls.
                if (cache != null && cache.Opened && hit.transform.IsChildOf(cache.transform)) continue;
                return PickupRejected(r, $"blocked by {hit.collider.name} layer={hit.collider.gameObject.layer}", carrier);
            }
            // #964 S14 U2: Witness is admitted EXPLICITLY rather than by the prop == null accident.
            // A witness would pass today's first term because he carries no GrabbableObject, and a
            // rule that only works because a component happens to be missing is one component away
            // from breaking. Everything above this line still applies to him unchanged, and it is
            // the part that matters: the same-world test, the 2.2 m reach and the line of sight
            // from the escort's chest to his body are exactly the checks "can this man be taken"
            // needs, and a wall between the two bodies refuses custody the same way it refuses a
            // pickup.
            return prop == null || prop.grabbable
                || r.Kind == BlacksiteObjectiveKind.Survey || r.Kind == BlacksiteObjectiveKind.Witness
                || PickupRejected(r, "item not grabbable", carrier);
        }
        private bool Acquire(BlacksiteObjectiveSnapshot r, BlacksiteSoldierAI carrier, GrabbableObject prop, PayloadCartController cart)
        {
            // #964 S14 U2: a witness is taken into CUSTODY, not into a pair of hands. U1's
            // TryBeginCustodyServer is the whole acquisition and it re-checks its own gate (the
            // take distance, both bodies alive and standing, the 15 s re-take backoff), so a
            // refusal here is a refusal the custody policy made and the board simply keeps the
            // reservation and tries again on the next dwell.
            //
            // It deliberately sits BEFORE PrepareHandsForCargo. That call exists to empty a
            // carrier's hands for a prop, and there is no prop: an escort marching a prisoner out
            // of a facility the crew is in keeps his weapon (#1293 R2: and his hold while he uses it).
            if (r.Kind == BlacksiteObjectiveKind.Witness)
            {
                var witness = WitnessOf(r);
                return witness != null && witness.TryBeginCustodyServer(carrier);
            }
            if (!carrier.PrepareHandsForCargo()) return false;
            if (cart != null) return cart.TryAttachNpcServer(carrier);
            if (prop == null || PlayerHeld(prop) || CrewSecured(prop)) return false;
            if (r.Kind == BlacksiteObjectiveKind.ShadowDrive) return prop.GetComponent<BreachHardDriveItem>().SetSoldierCarrierServer(carrier, "objective reservation acquired");
            if (r.Kind == BlacksiteObjectiveKind.Apparatus) _mission.DisconnectApparatusServer(r.ObjectId);
            prop.GetComponent<SurveyDroneItem>()?.ReleaseForBlacksiteCarry();
            carrier.PlayWorldInteractionGesture(); return true;
        }
        /// <summary>
        /// #950 A4: a member who is fighting for his own footing is not a candidate for an attempt.
        ///
        /// The reservation filter already excluded the ship guard, a called-in guard, a withdrawing
        /// member, an objective fight, a stun, a boarded member and a player response, and it did
        /// NOT exclude the two states in which the member's own body is not his to command:
        /// Hazard (he is walking out of a slime footprint) and capture (a vanilla monster has him
        /// in a special animation). Reserving to one of those spends an attempt and its whole travel
        /// budget on a member who cannot start walking until the state ends, which is one of the
        /// ways the 2026-09-12 ShadowRaid board burned six attempts on one crate. The member is not
        /// blacklisted: he is simply not free THIS dwell, exactly like the other terms.
        /// </summary>
        private static bool IsOccupiedByOwnSurvival(BlacksiteSoldierAI soldier)
            => soldier == null
                || soldier.SelectedBehaviour == BlacksiteBehaviour.Evade
                || BlacksiteVanillaMonsters.VictimCaptured(soldier);

        private void AssignReservations()
        {
            // #1293 R2: a stranded member is handed no work. #1305 G4: after the recall a withdrawing one hauls owed cargo.
            var free = _members.Where(s => !s.IsShipGuard && !GuardCalledIn(s.NetworkObjectId) && (Returning || !s.IsRaidWithdrawing) && !s.IsObjectiveCombatActive && !s.ReplicatedStunned && !s.HasBoardedShip && !HasPlayerResponse(s) && !IsOccupiedByOwnSurvival(s) && !s.Navigator.IsStranded).ToList();
            foreach (var r in Records) if (r.CarrierId != NoCarrier) free.RemoveAll(s => s.NetworkObjectId == r.CarrierId);
            // #1305 G5 (D17): the board holds the primary's holder only. While it is held the one escort
            // (!Sweeps) takes no salvage and every other free raider reserves his own in the same pass.
            BlacksiteResourceBroker broker = Broker;
            bool primaryHeld = PrimaryHeld, primaryWaits = false;
            if (primaryHeld) free.RemoveAll(s => !Sweeps(s));
            else if (!Returning) for (int i = 0; i < Records.Count; i++) primaryWaits |= ReArms(Records[i].Kind) && Records[i].Phase == BlacksiteObjectivePhase.Available;
            for (int i = 0; i < Records.Count && free.Count > 0; i++)
            {
                var r = Records[i]; if (r.Kind == BlacksiteObjectiveKind.Loot || r.Phase != BlacksiteObjectivePhase.Available) continue;
                if (primaryHeld && ReArms(r.Kind)) continue; // capacity one: no second mission objective while one is held
                if (primaryWaits && r.Kind == BlacksiteObjectiveKind.Salvage) continue; // #1305 G5 (D17): a released primary keeps its holder and escort free for its retry
                // #1293 R2: after the recall the board reserves only owed cargo (outside, not aboard);
                // a staged record is never Available, so it is the lift's, not a hauler's.
                if (Returning && !CargoOwed(r)) continue;
                if (_retryAt.TryGetValue(r.ObjectId, out float retry) && Time.time < retry) continue;
                var obj = Resolve(r.ObjectId); var prop = obj != null ? obj.GetComponent<GrabbableObject>() : null;
                // #1293 R2: an unbound witness (no navmesh at his release) is offered to nobody, silently,
                // until his own retry binds him (event=bound, bound=True).
                if (obj != null && r.Kind == BlacksiteObjectiveKind.Witness && (obj.GetComponent<WhistleblowerEnemyAI>()?.IsAgentUnbound ?? false)) continue;
                if (!BlacksiteObjectivePolicy.CanReserve(r.Phase == BlacksiteObjectivePhase.Available, obj != null, PlayerHeld(prop), CrewSecured(prop), obj != null && (obj.GetComponent<PayloadCartController>()?.IsAttached ?? false), true)) continue;
                var s = SelectOperator(r, free);
                if (s == null) continue;
                free.Remove(s);
                // #1016 S2 (design note C.5): the RECORD stays the claim and stays on the wire -
                // CarrierId and Phase are serialized fields of a NetworkList, and moving them off
                // it is forbidden. The broker lease is a host-side shadow granted in the same
                // statement, so the two can never be created apart. The board is taken by the
                // SELECTED operator, which is why it is requested here and not before the loop:
                // before the loop there is no holder to name.
                r.CarrierId = s.NetworkObjectId; r.Phase = BlacksiteObjectivePhase.Reserved; Records[i] = r;
                BlacksiteBrokerVerdict claim = broker != null
                    ? broker.Request(BlacksiteResourceKey.Objective(r.ObjectId), s.NetworkObjectId, Time.time, "carrier")
                    : default;
                if (broker != null && ReArms(r.Kind))
                    broker.Request(BlacksiteResourceKey.ObjectiveBoard(), s.NetworkObjectId, Time.time, "carrier of " + r.Kind);
                BeginAttempt(r, s.NetworkObjectId);
                BlacksiteDiagnostics.Log("Objective", $"reserved {r.Kind} object={r.ObjectId}, member={s.MemberIndex}, attempt={_jobs[r.ObjectId].Generation}, outside={r.Outside}, point={r.Position}"
                    // #1016 S2 (design note E.3): appended at the END, so every key score.ps1
                    // already parses keeps its name and its position.
                    + $", expiresIn={claim.Lease.ExpiresIn(Time.time):0.00}"
                    // #1153: and WHERE the operator was standing when the board picked him, on the
                    // same line and last, so "the ring was never resolved from the ship" is one
                    // grep against the reservation rather than an inference from missing lines.
                    + $", operatorOutside={s.isOutside}.");
                if (ReArms(r.Kind)) break;
            }
        }
        /// <summary>#1305 G5 (D17): a mission objective (ReArms) is Reserved or Carried.</summary>
        internal bool PrimaryHeld
        {
            get
            {
                for (int i = 0; i < Records.Count; i++)
                    if (ReArms(Records[i].Kind) && (Records[i].Phase == BlacksiteObjectivePhase.Reserved || Records[i].Phase == BlacksiteObjectivePhase.Carried)) return true;
                return false;
            }
        }
        private bool EscortCandidate(BlacksiteSoldierAI s)
            => s != null && !s.isEnemyDead && !s.IsRifleGuard && !s.IsRaidWithdrawing
                && !s.HasBoardedShip && !s.Navigator.IsStranded && !HoldsAnyRecord(s.NetworkObjectId);
        /// <summary>#1305 G5 (D17): a record-less member sweeps while a primary is held unless he is its one escort, the first eligible record-less raider by (MemberIndex, NetworkObjectId), read live.</summary>
        internal bool Sweeps(BlacksiteSoldierAI soldier)
        {
            if (!PrimaryHeld || HoldsAnyRecord(soldier.NetworkObjectId)) return false;
            for (int i = 0; i < _members.Count; i++)
            {
                var other = _members[i];
                if (other == soldier || !EscortCandidate(other)) continue;
                if (other.MemberIndex < soldier.MemberIndex || other.MemberIndex == soldier.MemberIndex && other.NetworkObjectId < soldier.NetworkObjectId) return true;
            }
            return false;
        }
        /// <summary>#1305 G5 (D17): a record holder or the one escort while a primary is held.</summary>
        internal bool HasOwnJob(BlacksiteSoldierAI s) => PrimaryHeld && !Sweeps(s);
        /// <summary>
        /// #1219 B1: which world the objective behind this record is in, on the host. A Carried
        /// record with a live carrier is in its carrier's world (the record republishes it, but only
        /// on the next Refresh); every other record is in the world it last published
        /// (<see cref="BlacksiteMissionWorld.ObjectiveOutside"/>). The one answer every host reader
        /// asking "which world is the objective in" uses.
        /// </summary>
        internal bool ObjectiveWorld(in BlacksiteObjectiveSnapshot record)
        {
            var carrier = record.Phase == BlacksiteObjectivePhase.Carried ? Soldier(record.CarrierId) : null;
            bool live = carrier != null && !carrier.isEnemyDead;
            return BlacksiteMissionWorld.ObjectiveOutside(record.Phase, live, live && BlacksiteWorld.Outside(carrier), record.Outside);
        }
        /// <summary>
        /// #1233 S2 (rule 1): the ONE answer to "does this member intend to be inside the facility".
        /// The entrance gate (<c>BlacksiteSquadPlanner.AdmitsWorldCrossing</c>), <c>ResolveIntent</c>,
        /// <c>PlanDutyOrder</c> and <c>MemberRequiresInteriorTraversal</c> all read it. Duty facts
        /// first: the recall and a withdrawal go out, a carrier takes its load to the ship, a
        /// called-in guard goes to the report's world, a ship guard stays out, and a holder goes to its
        /// record. Every other member works in the mission world, which is the primary focus
        /// objective's world (<c>TryFocusRecord</c>, <see cref="ObjectiveWorld"/>) and, with no
        /// objective, the baton raiders' inside clear.
        /// </summary>
        internal bool RequiresInterior(BlacksiteSoldierAI soldier)
        {
            if (Returning || soldier.IsRaidWithdrawing || IsCarrier(soldier.NetworkObjectId)) return false;
            // _backupOutside is the REPORT's world (a memo), which is where a called-in guard goes.
            if (GuardCalledIn(soldier.NetworkObjectId)) return !_backupOutside;
            if (soldier.IsShipGuard) return false;
            for (int i = 0; i < Records.Count; i++) if (Records[i].CarrierId == soldier.NetworkObjectId) return !ObjectiveWorld(Records[i]);
            bool hasFocus = TryFocusRecord(out var focus, out _);
            return BlacksiteMissionWorld.Interior(hasFocus, hasFocus && ObjectiveWorld(focus), soldier.UsesBaton);
        }
        // External interruptions only: a casualty, a stun or a departing transport, never a squad failure.
        // #964 S14 U2: the two production callers are BlacksiteSoldierAI (its own casualty or stun)
        // and MoonEscalationState (a transport departing without its carrier). Both are facts about
        // the ESCORT, and the escort being gone is what the witness sees, so the custody cause the
        // release carries is resolved from the carrier's own state rather than from the reason text.
        internal void DropCarrier(BlacksiteSoldierAI soldier, string reason)
        { int i = FindCarrier(soldier.NetworkObjectId); if (i >= 0) Drop(i, soldier.transform.position, reason, BlacksiteAttemptOutcome.Interrupted, CustodyCauseFor(soldier, false)); }
        /// <summary>
        /// #964 S14 U2: the manifest's reason for letting go of a witness, in U1's vocabulary.
        /// Only the two facts about the escort that custody itself names are mapped; anything
        /// else is the BOARD deciding, which is what Cleanup is for. alreadyReleased is the case
        /// where the witness let go first (a knockdown, his own death): the cause is
        /// already on the [UAT][Custody] line U1 wrote, and Drop's guard means nothing is passed on
        /// at all, so the value is only a placeholder.
        /// </summary>
        private static BlacksiteCustodyBreak CustodyCauseFor(BlacksiteSoldierAI carrier, bool alreadyReleased)
        {
            if (alreadyReleased) return BlacksiteCustodyBreak.Cleanup;
            if (carrier == null || carrier.isEnemyDead) return BlacksiteCustodyBreak.EscortDead;
            if (carrier.ReplicatedStunned) return BlacksiteCustodyBreak.EscortStunned;
            return BlacksiteCustodyBreak.Cleanup;
        }
        private void Drop(int index, Vector3 origin, string reason, BlacksiteAttemptOutcome outcome = BlacksiteAttemptOutcome.Failed,
            BlacksiteCustodyBreak custodyCause = BlacksiteCustodyBreak.Cleanup, BlacksiteObjectiveCloseCause failedCause = BlacksiteObjectiveCloseCause.ProgressBudget)
        {
            var r = Records[index]; var obj = Resolve(r.ObjectId);
            var formerCarrier = Soldier(r.CarrierId);
            var witness = WitnessOf(r);
            Vector3 point;
            if (witness != null)
            {
                // #964 S14 U2: an escorted man is RELEASED, not dropped. There is no prop to hand
                // back, no ground drop point to resolve and no carry presentation to unbind; what
                // ends is custody, and it ends with a cause on U1's own [UAT][Custody] line
                // carrying this attempt's generation.
                //
                // The IsInCustody guard is the recursion guard the design asks for: a break that
                // ORIGINATED in the witness (a knockdown, his death, a stunned or dead escort
                // noticed by his own tick) has already cleared the escort and logged its
                // cause, and re-entering ReleaseCustodyServer would either write a second, wrong
                // event=broken line or, once Stage 14 U3 wires the witness back into DropCarrier,
                // loop between the two. The record is handed back either way: the board's job here
                // is the record, and custody is only released if it is still held.
                if (witness.IsInCustody)
                    witness.ReleaseCustodyServer(custodyCause,
                        _jobs.TryGetValue(r.ObjectId, out var custodyJob) ? custodyJob.Generation : 0);
                point = obj != null ? obj.transform.position : r.Position;
            }
            else
            {
                obj?.GetComponent<PayloadCartController>()?.ReleaseNpcPusherServer();
                point = BlacksiteDriveCarrier.ResolveReachableDropPoint(formerCarrier != null ? formerCarrier.transform.position : origin);
                obj?.GetComponent<BreachHardDriveItem>()?.DropFromSoldierServer(point, reason);
                BlacksiteCarryPresentation.BeginRelease(obj != null ? obj.GetComponent<GrabbableObject>() : null, point);
            }
            // A drop ends the attempt before the record is assignable again.
            int generation = _jobs.TryGetValue(r.ObjectId, out var job) ? job.Generation : 0;
            bool wasOpen = job != null && job.AttemptOpen;
            // #1219 D3: a cart whose stuck verdict latched gets the mission's one answer ahead of A4's
            // budget - left to the ship's lift (Available here; #1293 R2: Refresh's lift-reach
            // measurement stages it), or withdrawn as CartImmovable - and #1233 S1 makes it terminal:
            // no attempt charged, no retry, a close named Abandoned. Any other spent mission
            // objective is withdrawn and written off; loot and salvage as ReleaseJob (#1153).
            var stuckCart = witness == null && obj != null ? obj.GetComponent<PayloadCartController>() : null;
            var stuck = StuckCartAnswer(stuckCart);
            bool answered = stuck != BlacksiteCartStuckAnswer.None;
            outcome = BlacksiteObjectiveAttempts.DropOutcome(outcome, answered);
            var history = CloseAttempt(r.ObjectId, outcome);
            var withdrawal = BlacksiteObjectivePolicy.DropWithdrawal(ReArms(r.Kind), history.Exhausted, stuck);
            r.Phase = BlacksiteObjectivePolicy.AvailabilityAfter(withdrawal)
                == BlacksiteObjectiveAvailability.Unavailable ? BlacksiteObjectivePhase.Unavailable : BlacksiteObjectivePhase.Available;
            r.CarrierId = NoCarrier; r.Position = point;
            // #1219 B1: the former carrier was resolved before any record write. #964 S14 U2: a
            // released witness's world is his own, not the escort's - they are the same the instant
            // custody breaks and not one tick later. Every other kind lands in its former carrier's world,
            // which the carry arm has already been republishing; no carrier (a Loaded record in
            // cleanup) keeps the record's own.
            r.Outside = witness != null ? BlacksiteWorld.Outside(witness)
                : formerCarrier != null ? BlacksiteWorld.Outside(formerCarrier) : r.Outside;
            Records[index] = r;
            if (answered) _retryAt.Remove(r.ObjectId);
            else _retryAt[r.ObjectId] = outcome == BlacksiteAttemptOutcome.Failed ? Mathf.Max(Time.time + 2f, history.RetryAt(Time.time)) : Time.time + 2f;
            // #874 S6.1a: the drop's own outcome names the close; #1233 S1: a cart answer names its own.
            if (wasOpen) LogObjectiveClosed(r, formerCarrier, job, generation, outcome,
                answered ? BlacksiteObjectiveCloseCause.Abandoned
                : outcome == BlacksiteAttemptOutcome.Completed ? BlacksiteObjectiveCloseCause.Completed
                : outcome == BlacksiteAttemptOutcome.Interrupted ? BlacksiteObjectiveCloseCause.Interrupted
                : failedCause, history, withdrawal: withdrawal);
            ReconcileDriveCarriers();
            BlacksiteDiagnostics.Log("Objective", $"{r.Kind} object={r.ObjectId} dropped: {reason}; outcome={outcome}, objectiveFailures={history.Failures}/{BlacksiteObjectiveAttempts.MaxFailures}, result={r.Phase}, withdrawal={BlacksiteObjectivePolicy.WithdrawalToken(withdrawal)}.");
            LogStuckCartAnswer(r, stuckCart, stuck);
            WriteOffWithdrawn(r, withdrawal);
        }
        internal void LateUpdateReplica()
        {
            if (Ship == null) return;
            foreach (var r in Records)
            {
                // #964 S14 U2: the replica has nothing to do for a witness, and the skip is
                // explicit rather than left to the prop == null continue below. He is an AGENT:
                // he holds his own transform, his pose comes from the replicated
                // currentBehaviourStateIndex, and there is no mesh to toggle, no anchor to parent
                // to and no cargo slot to place him in. Ship.CargoAnchor(slot, kind) has no name
                // for Witness and must never be reached for one; this line is why it cannot be.
                //
                // #874 (2026-09-20): he IS a passenger while his record is Loaded, and that is
                // owned entirely by the transport - the seat is authored by the host into the
                // ship's replicated passenger list and applied on every peer by
                // BlacksiteShipController.ApplyPassengers. Nothing about it belongs here: a
                // client deriving his seat from the phase and his own interpolated position is
                // exactly the error an authored seat exists to remove.
                if (r.Kind == BlacksiteObjectiveKind.Witness) continue;
                var obj = Resolve(r.ObjectId); if (obj == null) continue;
                var cart = obj.GetComponent<PayloadCartController>();
                if (cart != null && r.Phase == BlacksiteObjectivePhase.Loaded && !cart.IsAttached && !cart.IsNpcAttached)
                { if (_mission.IsServer && !cart.IsOnBlacksiteMagnet(Ship.NetworkObjectId)) cart.transform.SetPositionAndRotation(Ship.transform.TransformPoint(r.CargoLocalPosition), Ship.transform.rotation * Quaternion.Euler(0, r.CargoLocalYaw, 0)); continue; }
                var prop = obj.GetComponent<GrabbableObject>(); if (prop == null) continue;
                if(r.Acquired && prop is LungProp apparatus) apparatus.isLungDocked=false;
                bool changed = !_applied.TryGetValue(r.ObjectId, out var previous) || previous != r.Phase;
                if (r.Phase == BlacksiteObjectivePhase.Carried || r.Phase == BlacksiteObjectivePhase.Loaded)
                {
                    if (PlayerHeld(prop)) continue;
                    var carrier = Soldier(r.CarrierId); Transform anchor = r.Phase == BlacksiteObjectivePhase.Loaded ? (r.ContainerCargo ? Ship.ContainerSlot(r.Slot) : Ship.CargoAnchor(r.Slot, r.Kind)) : carrier?.ItemCarryHolder;
                    if (anchor == null) continue;
                    if (changed) { prop.GetComponent<SurveyDroneItem>()?.ReleaseForBlacksiteCarry(); prop.EnablePhysics(r.Phase == BlacksiteObjectivePhase.Loaded); if (prop.propBody != null) prop.propBody.isKinematic = true; }
                    prop.parentObject = anchor; prop.isHeldByEnemy = r.Phase == BlacksiteObjectivePhase.Carried; prop.grabbable = r.Phase == BlacksiteObjectivePhase.Loaded && (!r.ContainerCargo || (Ship.CargoContainer != null && Ship.CargoContainer.Opened));
                    // #951 S11H U3 (a): the desired mesh state is computed every frame, the vanilla
                    // toggle is called only when the answer is not already applied: on a phase
                    // change, on a carrier or anchor change (the anchor's instance id covers both,
                    // because a Carried record's anchor IS its carrier's hand), when the answer
                    // itself flips, and on the MeshReconcileSeconds sweep.
                    bool meshEnabled = r.Phase != BlacksiteObjectivePhase.Loaded || !r.ContainerCargo || (Ship.CargoContainer != null && Ship.CargoContainer.Opened);
                    int meshAnchor = anchor.GetInstanceID();
                    if (changed || !_meshApplied.TryGetValue(r.ObjectId, out var meshState)
                        || meshState.Enabled != meshEnabled || meshState.Anchor != meshAnchor || Time.time >= meshState.ReconcileAt)
                    {
                        prop.EnableItemMeshes(meshEnabled);
                        _meshApplied[r.ObjectId] = new MeshApplication(meshEnabled, meshAnchor, Time.time + MeshReconcileSeconds);
                        if (BlacksiteDiagnostics.Enabled) MeshToggleCallsForDiagnostics++;
                    }
                    // #1219 B1: the replicated record's world, never the carrier's isOutside, which is
                    // host-local and reads its default on every client. A carried record republishes
                    // its carrier's world, and a loaded one is aboard (outside).
                    prop.isInShipRoom = false; prop.isInElevator = false; prop.isInFactory = !r.Outside;
                    if (r.Phase == BlacksiteObjectivePhase.Carried) BlacksiteCarryPresentation.Bind(prop, anchor);
                    else
                    {
                        if (changed && previous == BlacksiteObjectivePhase.Carried) BlacksiteCarryPresentation.BeginRelease(prop, anchor.position);
                        if (!BlacksiteCarryPresentation.IsReleasing(prop)) prop.transform.SetPositionAndRotation(anchor.position, anchor.rotation);
                    }
                    prop.hasHitGround = true; prop.reachedFloorTarget = true;
                }
                else if (changed && (r.Phase == BlacksiteObjectivePhase.Staged || previous == BlacksiteObjectivePhase.Carried || previous == BlacksiteObjectivePhase.Loaded) && !PlayerHeld(prop))
                {
                    // #951 S11H U3 (a): the release keeps its unconditional call, which is already
                    // inside `changed`; the record is written so the map never claims a state the
                    // meshes are not in.
                    prop.parentObject = null; prop.isHeldByEnemy = false; prop.grabbable = true; prop.EnableItemMeshes(true); prop.EnablePhysics(true); prop.transform.position = r.Position;
                    _meshApplied[r.ObjectId] = new MeshApplication(true, 0, Time.time + MeshReconcileSeconds);
                    if (BlacksiteDiagnostics.Enabled) MeshToggleCallsForDiagnostics++;
                    prop.isInFactory = !r.Outside; prop.hasHitGround = false; prop.reachedFloorTarget = false; prop.fallTime = 0;
                    var parent = prop.transform.parent; prop.startFallingPosition = parent != null ? parent.InverseTransformPoint(r.Position) : r.Position; prop.targetFloorPosition = prop.startFallingPosition;
                    BlacksiteCarryPresentation.BeginRelease(prop, r.Position);
                }
                _applied[r.ObjectId] = r.Phase;
            }
        }
        internal void ConfirmDepartureServer()
        {
            if (!_mission.IsServer || _departureConfirmed) return; _departureConfirmed = true;
            // Departure is terminal for every open attempt; loaded and staged cargo closes as done.
            foreach (var r in Snapshots)
            {
                bool done = r.Phase == BlacksiteObjectivePhase.Loaded || r.Phase == BlacksiteObjectivePhase.Staged;
                if (done) CompleteHaul(r);
                else CloseAttempt(r, BlacksiteAttemptOutcome.Interrupted, BlacksiteObjectiveCloseCause.Interrupted);
            }
            ClearDutyJobs(); int extracted = 0;
            // #874 Q9 (F6): everything the ship carries departs with it. A Loaded record IS in its
            // slot - LateUpdateReplica pins it there every frame and Refresh's Loaded arm has
            // already handed back anything a player took out while the ship was grounded - so the
            // departure asks no bounds question at the instant. It used to ask CargoContains here,
            // on a hull already climbing, and on 2026-09-23 round 2 the four rack items failed it,
            // were never despawned, and hung at ship altitude after the ship had gone. Only a
            // player's hands (the item or the cart handle) keep a Loaded record behind.
            for (int i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                if (r.Phase == BlacksiteObjectivePhase.Extracted || r.Phase == BlacksiteObjectivePhase.Unavailable) continue;
                var obj = Resolve(r.ObjectId); var prop = obj != null ? obj.GetComponent<GrabbableObject>() : null;
                var cart = obj != null ? obj.GetComponent<PayloadCartController>() : null;
                bool playerHeld = PlayerHeld(prop), cartAttached = cart != null && cart.IsAttached;
                if (!BlacksiteObjectivePolicy.CanExtract(r.Phase == BlacksiteObjectivePhase.Loaded, obj != null, playerHeld, cartAttached, _departureConfirmed))
                {
                    NoteLeftBehind(r, obj, obj == null ? "objectMissing" : playerHeld ? "playerHeld" : cartAttached ? "cartPlayerAttached" : "phase=" + r.Phase);
                    continue;
                }
                if (cart != null && !cart.MarkBlacksiteExtractedServer()) { NoteLeftBehind(r, obj, "cartRefusedExtraction"); continue; }
                // #964 S14 U2: the witness's terminal, beside the cart's and for the same reason.
                // The cart calls PayloadContract.NotifyFailed here; the Whistleblower contract has
                // no contract class and no NotifyFailed, so the equivalent is one latch, and Stage
                // 14 U4 turns that latch into the failed card row IsCurrentContractFailed scans
                // for. This finally implements the catalog's standing promise that the witness
                // escaping voids the contract.
                if (r.Kind == BlacksiteObjectiveKind.Witness) MoonContractState.NotifyWhistleblowerExtractedServer(WitnessOf(r));
                // #1076: the replicated half of the same terminal, for all three contracts. NOTHING
                // on an object may carry it: the Extracted phase written on the next line, the
                // cart's _failed above and any NetworkVariable on this mission all die unsent,
                // because this whole departure is one synchronous chain that ends in
                // MoonEscalationState.NotifyBlacksiteShipDeparted -> TryDespawnObject on the ship -
                // and the mission is a component of that same ship object. It goes on the
                // progression channel instead, which is a named message with a round-keyed replay
                // ledger and no object behind it.
                MoonContractState.NotifyBlacksiteObjectiveDepartedServer(r.Kind);
                r.Phase = BlacksiteObjectivePhase.Extracted; Records[i] = r; if (r.Kind != BlacksiteObjectiveKind.Loot) extracted++;
                BlacksiteDiagnostics.Log("Extraction", $"departed object={r.ObjectId} kind={r.Kind} slot={r.Slot}");
                obj.Despawn(true);
            }
            _mission.ResolveServer(extracted > 0 ? BlacksiteRetrievalOutcome.Recovered : BlacksiteRetrievalOutcome.Denied);
            BlacksiteDiagnostics.Log("Extraction", $"Departure confirmed: {extracted} actual objectives extracted; bomb and earned Survey progress remain independent.");
        }
        // #874 Q9: the one line for a record the ship leaves behind, written once because the
        // departure is latched by _departureConfirmed.
        private static void NoteLeftBehind(BlacksiteObjectiveSnapshot r, NetworkObject obj, string reason)
            => BlacksiteDiagnostics.Log("Extraction", $"notDeparted object={r.ObjectId} kind={r.Kind} phase={r.Phase} slot={r.Slot}, "
                + $"position={(obj != null ? BlacksiteDiagnosticFormat.V(obj.transform.position) : "none")}, reason={reason}");
        internal void CleanupServer()
        {
            if (!_mission.IsServer) return;
            for (int i = 0; i < Records.Count; i++)
            { var r = Records[i]; if (r.Phase == BlacksiteObjectivePhase.Carried || r.Phase == BlacksiteObjectivePhase.Loaded) { var obj = Resolve(r.ObjectId); if (obj != null) Drop(i, obj.transform.position, "mission cleanup", BlacksiteAttemptOutcome.Interrupted); } }
            // #874 (2026-09-20): a cleanup ends every ride. The drop above already put each
            // record back, and this is the body half of it: the transport stops writing his
            // transform and his own agent takes it again.
            ReleaseWitnessHoldsServer();
            ClearDutyJobs();
            ReconcileDriveCarriers();
            LateUpdateReplica();
        }
        private void SpawnStorageLoot()
        {
            // Existing vanilla scrap prefabs, already network-registered by the game. No loot boxes.
            var choices = StartOfRound.Instance?.allItemsList?.itemsList?.Where(i => i != null && i.isScrap && i.spawnPrefab != null && !i.twoHanded && i.spawnPrefab.GetComponent<LungProp>() == null && i.spawnPrefab.GetComponent<SurveyDroneItem>() == null && i.spawnPrefab.GetComponent<BreachHardDriveItem>() == null).ToArray();
            if (choices == null || choices.Length == 0) return;
            for (int i = 0; i < 4; i++)
            {
                var item = choices[UnityEngine.Random.Range(0, choices.Length)]; var anchor = Ship.CargoAnchor(i, BlacksiteObjectiveKind.Loot);
                var go = UnityEngine.Object.Instantiate(item.spawnPrefab, anchor.position, anchor.rotation); var obj = go.GetComponent<NetworkObject>(); var prop = go.GetComponent<GrabbableObject>();
                if (obj == null || prop == null) { UnityEngine.Object.Destroy(go); continue; }
                prop.itemProperties = item; obj.Spawn(); prop.SetScrapValue(Mathf.Max(1, UnityEngine.Random.Range(item.minValue, item.maxValue + 1))); _mission.SyncCargoValueClientRpc(new NetworkObjectReference(obj), prop.scrapValue);
                // #1219 B1: the hold is outside, so the prop says so before its record reads it
                // (a freshly spawned GrabbableObject carries vanilla's isInFactory default).
                prop.isInFactory = false;
                Add(obj, BlacksiteObjectiveKind.Loot); int n = Records.Count - 1; var record = Records[n]; record.Phase = BlacksiteObjectivePhase.Loaded; record.Slot = i; Records[n] = record;
            }
        }
    }
}
