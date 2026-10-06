using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed class BlacksiteSquadDirector
    {
        // #1016 S2: the reservation itself is now a Doorway/ObjectiveLock lane holder and
        // this window is BlacksiteResourceLeasePolicy.StaleSeconds, the ONE copy of what used
        // to be three private constants. The name survives because it is the value the queue
        // types' own comments cite as the thing they equal, and the headless suites assert the
        // two still agree.
        internal const float InteractionReservationSeconds =
            BlacksiteResourceLeasePolicy.StaleSeconds;
        private const float SharedDestinationThreshold = 1.1f;
        // #874 S4.3: crowd separation geometry. The claimant radius is the soldier agent radius
        // BlacksiteNavigator.ConfigureAgent writes; two standing bodies just clear each other at
        // twice it, so the first ring is 2 x 0.45 = 0.90 m, and a second claimant already on the
        // point widens it by one body radius to 1.35 m. #874 Q1: the ring numbers are the arrival
        // table's (BlacksiteArrivalPolicy.ClaimSeparationBase / ClaimSeparationStep) and the ring
        // stops growing at MaxClaimOffset, the widest claim the table proves still fits inside the
        // entrance hand-off. There is no facing term at all; the offset runs along the claimant's
        // own world approach bearing, which is the direction it is already walking in from.
        private const float ClaimantRadius = BlacksiteCargoLayout.AgentRadius;
        // A bearing shorter than one body radius means the member is standing on the point it is
        // claiming and has no approach direction; it falls back to a bearing owned by its member
        // index. Six slots is the crew size, so no two members of one crew share a fallback bearing.
        private const float ClaimBearingMinimum = ClaimantRadius;
        private const int ClaimBearingSlots = 6;
        // How far the requested point may move before a latched offset is re-anchored: two agent
        // radii plus the navigator's one-metre destination re-issue hysteresis
        // (BlacksiteNavigator.SetDestinationThrottled). #874 S4.2 introduces
        // BlacksiteNavigator.MinDestinationChange with the same derivation; unify the two when both
        // land rather than keeping two names for one distance.
        private const float ClaimReanchorDistance = 2f * ClaimantRadius + 1f;
        private const float StimulusShareRadius = 20f;
        private const float ContactBarkCooldownSeconds = 10f;

        private static readonly BlacksiteSoldierTacticalRole[] InteractionRolePreference =
        {
            BlacksiteSoldierTacticalRole.Pointman,
            BlacksiteSoldierTacticalRole.Support,
            BlacksiteSoldierTacticalRole.Leader,
            BlacksiteSoldierTacticalRole.Rifleman,
            BlacksiteSoldierTacticalRole.RearGuard
        };

        private sealed class MemberRecord
        {
            internal BlacksiteSoldierAI Soldier;
            internal BlacksiteSoldierTacticalRole Role;
            internal int Slot = -1;
        }

        // #1016 S2: InteractionReservation is DELETED. Its four fields are the lease the lane
        // hands out - Target and Kind are the key, OwnerToken is the holder and ExpiresAt is the
        // lease's own. A grep across src/ and tests/ found no reference to the type or to
        // _interactionReservations outside this file, and no fixture reflects on either by name.

        // #874 S4.3: a claim carries the geometry its adjusted point was computed from, so the same
        // point can be handed back for as long as nothing about the crowd or the goal has changed.
        // #1293 R3: a claim is held until an event lets it go (a new claim, release, death, unregister, Reset).
        private sealed class DestinationClaim
        {
            internal Vector3 Destination;
            internal Vector3 Requested;
            internal Vector3 Basis;
            internal ulong Claimants;
            internal int Overlaps;
            internal bool HasOffset;
            // #874 Q2: an exact claim is a holder's validated reach. It publishes the keep-out an
            // escort's claim and follow slot stay out of: the reach radius, and the approach line
            // from where the holder is (or its route's last corner, whichever is nearer) to the point.
            internal bool Exact;
            internal float ReachRadius;
            internal Vector3 ApproachCorner;
            internal Vector3 ApproachFrom;
        }

        private readonly BlacksiteRetrievalMissionController _mission;
        private readonly List<MemberRecord> _members = new List<MemberRecord>();
        private readonly Dictionary<ulong, DestinationClaim> _destinationClaims =
            new Dictionary<ulong, DestinationClaim>();
        private readonly List<MemberRecord> _survivorScratch = new List<MemberRecord>();
        // #874 S4: the one progress judge, host-only like this object, and the living-member list it
        // is ticked over (reused, never reallocated). #1293 R1: the perception pass refills and walks
        // the same list on its own clock.
        private readonly BlacksiteMotionMonitor _monitor;
        private readonly List<BlacksiteSoldierAI> _monitorMembers = new List<BlacksiteSoldierAI>();
        /// <summary>#1293 R2: the progress ledger, for device events (<see cref="BlacksiteMotionMonitor.NoteProgress"/>).</summary>
        internal BlacksiteMotionMonitor Ledger => _monitor;
        /// <summary>#1305 H (C3): the living members as of this director's last pass or tick (the fire lane's squadmates).</summary>
        internal IReadOnlyList<BlacksiteSoldierAI> LivingMembers => _monitorMembers;
        // #874 S5.4 (#873): one waiting column per paired doorway, keyed exactly as its reservation
        // is so the two never disagree about which doorway a member is asking for.
        private readonly Dictionary<int, BlacksiteEntranceColumn> _interactionColumns =
            new Dictionary<int, BlacksiteEntranceColumn>();
        // #1016 S2 review batch 2, F4: the idle-column prune's scratch list, restored with the prune.
        private readonly List<int> _interactionColumnScratch = new List<int>();
        // #1293 R1: the squad's ONE world model - what every member knows - written by the
        // perception pass (UpdatePerceptionServer) and by event writers. Server only, like this object.
        internal BlacksiteWorldModel World { get; } = new BlacksiteWorldModel();
        // The perception slots held by registered members, one bit per slot.
        private uint _perceptionSlots;
        private float _nextPerceptionAt = float.NegativeInfinity;
        // Set when the mission despawned and left living members on the moon (ReleaseMission): the
        // pass then runs on their calls, without the mission.
        private bool _missionReleased;
        // World.ChangeCount when the pass last re-decided the squad.
        private int _perceivedChangeCount;
        private readonly List<BlacksiteWorldRow> _expiredRows = new List<BlacksiteWorldRow>();
        private readonly BlacksiteSquadVoiceDirector _voiceDirector;

        /// <summary>
        /// #1066 U8: how many times this squad has actually achieved something this deployment.
        /// A monotonic token, not a clock: the voice director compares it against the value it held
        /// when it last let a member claim the sector was secure, and refuses to let that claim be
        /// repeated until it has moved. On 2026-09-15 the squad was wedged at the entrance for the
        /// whole mission and the ambient Patrol line went on announcing "sector is secure, no
        /// visual" on its 30-55 s timer, which reads as a squad that is fine.
        ///
        /// It is incremented only by facts that cannot happen to a squad that is going nowhere: a
        /// member clearing the facility threshold, and an objective attempt closing Completed.
        /// </summary>
        internal int ProgressToken { get; private set; }

        internal void NoteSquadProgress()
        {
            ProgressToken++;
        }

        private Vector3 _shipPosition;
        private Vector3 _outsideMainEntrance;
        private Vector3 _insideMainEntrance;
        private Vector3 _insideFireExit;
        private Vector3 _outsideFireExit;
        private int _expectedMemberCount = 6;
        private BlacksiteSquadOrder _currentOrder;
        private bool _deploymentObserved;
        private bool _voiceDriveStateInitialized;
        private BlacksiteHardDriveState _lastVoiceDriveState;
        private float _nextPlayerContactBarkAt = float.NegativeInfinity;
        private float _nextMonsterContactBarkAt = float.NegativeInfinity;
        // #988 round 2, SHOULD-FIX 3: the per-recipient order clock. A contact seen every pass by
        // four members must not reconsider a third member's order four times a pass.
        private readonly Dictionary<ulong, float> _nextMonsterOrderRecipientAt = new Dictionary<ulong, float>();
        private int _voiceTransitionSequence;

        internal BlacksiteSquadDirector(BlacksiteRetrievalMissionController mission)
        {
            _mission = mission;
            _voiceDirector = new BlacksiteSquadVoiceDirector(this);
            _monitor = new BlacksiteMotionMonitor(mission);
            _currentOrder = new BlacksiteSquadOrder(
                0,
                BlacksiteSquadOrderKind.Deploy,
                BlacksiteFormationKind.LooseWedge,
                Vector3.zero,
                Vector3.forward);
        }

        internal bool CarrierAboard => _mission.Objectives.LoadedCount > 0;
        internal BlacksiteSquadOrderKind CurrentOrderKind => _currentOrder.Kind;
        internal Vector3 ShipPosition => _mission.Objectives.Ship != null ? _mission.Objectives.Ship.MissionRallyPosition : _shipPosition;
        internal Vector3 OutsideMainEntrance => _outsideMainEntrance;
        internal Vector3 InsideMainEntrance => _insideMainEntrance;
        internal Vector3 InsideFireExit => _insideFireExit;
        internal Vector3 OutsideFireExit => _outsideFireExit;
        internal bool IsServerVoiceAuthority => _mission != null && _mission.IsServer && _mission.IsInitialized;

        internal void ConfigureRouteServer(
            Vector3 shipPosition,
            Vector3 outsideMainEntrance,
            Vector3 insideMainEntrance,
            Vector3 insideFireExit,
            Vector3 outsideFireExit,
            int expectedMemberCount)
        {
            _shipPosition = shipPosition;
            _outsideMainEntrance = outsideMainEntrance;
            _insideMainEntrance = insideMainEntrance;
            _insideFireExit = insideFireExit;
            _outsideFireExit = outsideFireExit;
            _expectedMemberCount = Mathf.Max(1, expectedMemberCount);
        }

        // #874 S8.2: the squad's CHOSEN door. _outsideMainEntrance / _insideMainEntrance and the
        // OutsideMainEntrance / InsideMainEntrance accessors keep their names, but from here on they
        // mean "the door this squad is using", not "the map's main entrance". #1305 G2 (R16): the
        // planner's door goals name its far side and the route takes it; ChosenEntranceId is the
        // door the link reads and the survivor exit prefers.
        //
        // EntranceGeneration is the only signal members need. Each member caches the two points at
        // Initialize, and compares this counter on every order it executes rather than re-reading the
        // director on every field access; the counter is what makes a cache follow a republish.
        internal int EntranceGeneration { get; private set; }
        internal int ChosenEntranceId { get; private set; }

        /// <summary>
        /// Republish the door the whole squad walks to. Called only by the mission's up-front door
        /// choice, which has already proven the candidate's walking route with the same
        /// <c>BlacksiteLandingGeometry.RouteCheck</c> the landing validation uses; the caller owns
        /// the IsServerAuthority gate, exactly as every other server-side director entry does.
        /// </summary>
        internal void RetargetEntranceServer(int entranceId, Vector3 outsideEntrance, Vector3 insideEntrance)
        {
            if (entranceId == ChosenEntranceId
                && (outsideEntrance - _outsideMainEntrance).sqrMagnitude < .0001f)
                return;

            _outsideMainEntrance = outsideEntrance;
            _insideMainEntrance = insideEntrance;
            ChosenEntranceId = entranceId;
            unchecked { EntranceGeneration++; }
            BlacksiteDiagnostics.Log(
                "Entrance",
                $"squad door republished; id={entranceId}, outside={outsideEntrance}, inside={insideEntrance}, generation={EntranceGeneration}.");
        }

        internal BlacksiteSoldierTacticalRole GetRoleForMember(int memberIndex)
        {
            return BlacksiteFormationPlanner.ResolveRole(memberIndex, _expectedMemberCount);
        }

        internal void RegisterMember(BlacksiteSoldierAI soldier)
        {
            if (soldier == null)
                return;
            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i].Soldier == soldier)
                    return;
            }

            // #1293 R1: the member's stable bit in every world row's sight and announce masks.
            int slot = TakePerceptionSlot();
            soldier.JoinPerception(this, slot);
            _members.Add(new MemberRecord
            {
                Soldier = soldier,
                Role = soldier.TacticalRole,
                Slot = slot
            });
            _deploymentObserved = true;
            _members.Sort((left, right) => left.Soldier.MemberIndex.CompareTo(right.Soldier.MemberIndex));

            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Squad",
                    $"member registered id={GetToken(soldier)}, index={soldier.MemberIndex}, role={soldier.TacticalRole}, slot={soldier.FormationSlot}, count={_members.Count}/{_expectedMemberCount}.");
            }
        }

        internal void UnregisterMember(BlacksiteSoldierAI soldier)
        {
            if (soldier == null)
                return;
            ulong token = GetToken(soldier);
            for (int i = _members.Count - 1; i >= 0; i--)
            {
                MemberRecord member = _members[i];
                if (member.Soldier != null && member.Soldier != soldier)
                    continue;
                if (ReferenceEquals(member.Soldier, soldier))
                    soldier.JoinPerception(null, -1);
                ReleasePerceptionSlot(member.Slot);
                _members.RemoveAt(i);
            }
            _destinationClaims.Remove(token);
            _nextMonsterOrderRecipientAt.Remove(token);
            ReleaseInteractionsOwnedBy(token);
            _monitor.Forget(soldier);
            // #1018 S4 (design note C.2): ONE release path. A member that leaves the squad has no
            // assignment, no plan state, no broker lease and no blackboard row, and Release is
            // idempotent - so a member that dies while boarding is released twice and nothing
            // notices. It takes the token and never the soldier, because this arrival can have the
            // Unity object already destroyed (design note G10 e).
            if (soldier.TryGetMission(out var releaseMission) && releaseMission.Objectives != null)
                releaseMission.Objectives.Planner.Release(token, "unregister");
        }

        internal void Reset()
        {
            foreach (var member in _members)
                if (member.Soldier != null) member.Soldier.JoinPerception(null, -1);
            _members.Clear();
            _perceptionSlots = 0u;
            // #1016 S2: the interaction reservations are lane holders now, and the lanes belong
            // to the mission rather than to this director - the mission controller clears the
            // whole ledger in the same breath it discards this object, so clearing a subset
            // here would only hide half of it.
            _destinationClaims.Clear();
            // #1293 R1: the squad's whole knowledge goes with its deployment.
            World.Reset();
            _perceivedChangeCount = World.ChangeCount;
            _nextPerceptionAt = float.NegativeInfinity;
            _missionReleased = false;
            _expiredRows.Clear();
            _deploymentObserved = false;
            _voiceDriveStateInitialized = false;
            _lastVoiceDriveState = default;
            _nextPlayerContactBarkAt = float.NegativeInfinity;
            _nextMonsterContactBarkAt = float.NegativeInfinity;
            _nextMonsterOrderRecipientAt.Clear();
            _voiceTransitionSequence = 0;
            ProgressToken = 0;                                                            // #1066 U8
            _voiceDirector.Reset();
            _survivorScratch.Clear();
            _monitor.Reset();
        }

        /// <summary>
        /// #1293 R1: the mission is despawning - at confirmed departure the ship takes it away. A
        /// living member it leaves on the moon keeps his slot and this table, and the survivors run
        /// the pass themselves (BlacksiteSoldierAI.RefreshTransportLifecycleServer), so a survivor
        /// still sees, selects and fires. With nobody left it is <see cref="Reset"/>. Nothing else
        /// here runs again: the members reach this object only through their own perception.
        /// </summary>
        internal void ReleaseMission()
        {
            for (int i = 0; i < _members.Count; i++)
            {
                BlacksiteSoldierAI soldier = _members[i].Soldier;
                if (soldier != null && !soldier.isEnemyDead && !soldier.HasBoardedShip)
                {
                    _missionReleased = true;
                    return;
                }
            }
            Reset();
        }

        internal void UpdateServer()
        {
            if (_mission == null || !_mission.IsServer || !_mission.IsInitialized)
                return;

            PruneMembers();
            if (_deploymentObserved && LivingMemberCount() == 0
                && _mission.Outcome != BlacksiteRetrievalOutcome.Pending)
                return;
            CleanupClaims();
            UpdateVoiceDriveState();
            _voiceDirector.UpdateServer();
            CollectLivingMembers(_monitorMembers);
            _monitor.Tick(Time.time, _monitorMembers);

            // Objective reservations and per-member duties own mission movement. Combat memory
            // and interaction leases above still use the established squad arbitration.
            foreach (var member in _members)
                if (member.Soldier != null && !member.Soldier.isEnemyDead && !member.Soldier.IsShipGuard)
                { _currentOrder = _mission.Objectives.TryGetOrder(member.Soldier, out var order) ? order.SquadOrder : new BlacksiteSquadOrder(0, BlacksiteSquadOrderKind.Hold, BlacksiteFormationKind.SpreadSecurity, Vector3.zero, Vector3.forward); break; }
        }

        /// <summary>
        /// #1293 R1: the squad's ONE perception pass, every
        /// <see cref="BlacksiteSightPolicy.VisualTickSeconds"/> on its own clock. The mission
        /// controller calls this every server frame because <see cref="UpdateServer"/> runs on the
        /// coarser 0.25 s planner refresh, and so do stranded members, who are its only callers once
        /// the mission has gone (<see cref="ReleaseMission"/>). Game facts first (witness, cart (#1293
        /// R2) and hazard Live rows), then every living member's eyes and ears over the same table,
        /// then the adapter ledger's pass (#1293 R3), then what the squad does with the result.
        /// </summary>
        internal void UpdatePerceptionServer()
        {
            float now = Time.time;
            if (now < _nextPerceptionAt
                || !_missionReleased && (_mission == null || !_mission.IsServer || !_mission.IsInitialized))
                return;
            _nextPerceptionAt = now + BlacksiteSightPolicy.VisualTickSeconds;
            CollectLivingMembers(_monitorMembers);
            if (_deploymentObserved && _monitorMembers.Count == 0
                && (_missionReleased || _mission.Outcome != BlacksiteRetrievalOutcome.Pending))
                return;
            if (!_missionReleased)
            {
                _mission.Objectives.PublishWitnessRowsServer(World, now);
                _mission.Objectives.PublishCargoRowsServer(World, now);
            }
            BlacksiteHazardAwareness.PublishRows(World, _monitorMembers, now);
            // Dead or destroyed eyes see nothing: such a member's slot keeps no row in sight.
            for (int i = 0; i < _members.Count; i++)
                if (_members[i].Soldier == null || _members[i].Soldier.isEnemyDead)
                    World.ReleaseSlot(_members[i].Slot);
            // #1305 H (C4): each member's monster reaction is computed here, once per pass and never on a clock
            // of its own, after every member's look and (#1305 E4, D13) the squad's Coil-Head watcher election.
            for (int i = 0; i < _monitorMembers.Count; i++)
                _monitorMembers[i].PerceiveServer(World, now);
            BlacksiteVanillaMonsters.ElectWatchers(World, _monitorMembers);
            for (int i = 0; i < _monitorMembers.Count; i++)
                _monitorMembers[i].ObserveMonsterReactionServer();
            ForgetLostSubjects();
            OrderMonsterResponses();
            BlacksiteMonsterCombatAdapterRegistry.OnPerceptionPass(now);
            _expiredRows.Clear();
            World.Expire(now, _expiredRows);
            if (BlacksiteDiagnostics.Enabled)
                for (int i = 0; i < _expiredRows.Count; i++)
                {
                    BlacksiteWorldRow row = _expiredRows[i];
                    BlacksiteSoldierAI seenBy = FindMemberByNetworkId(row.SeenBy);
                    BlacksiteDiagnostics.Log(
                        "World",
                        $"expired entity={row.Key} class={row.Class} source={row.Source} age={row.Age(now):0.00} seenBy={(seenBy != null ? seenBy.MemberIndex.ToString() : "none")}");
                }
            _expiredRows.Clear();
            // #1293 R3: squad-table news (ChangeCount moved) is a World decision for every living member.
            if (World.ChangeCount == _perceivedChangeCount)
                return;
            _perceivedChangeCount = World.ChangeCount;
            for (int i = 0; i < _monitorMembers.Count; i++)
                _monitorMembers[i].RequestDecision(BlacksiteDecisionCause.World);
        }

        // #988 B3: an order follows sight - ONE per reporter per pass, about the contact its own
        // eyes would fight, exactly the one contact a reporter's sighting used to report.
        private void OrderMonsterResponses()
        {
            for (int i = 0; i < _monitorMembers.Count; i++)
            {
                BlacksiteSoldierAI reporter = _monitorMembers[i];
                EnemyAI contact = ReportedMonster(reporter);
                if (contact != null) PropagateMonsterAlertOrder(reporter, contact);
            }
        }

        // The base sight scan's choice over the Monster rows this member's own
        // eyes hold: the nearest engageable contact, else (#874 N8) the nearest one its warning fire
        // is open on, else the nearest refused or not-engageable sighting. The weapons gate is
        // CanConsiderEnemy's (IsAttackableBy); its reach half was already passed by the sight bit.
        private EnemyAI ReportedMonster(BlacksiteSoldierAI reporter)
        {
            int slot = reporter.PerceptionSlot;
            Vector3 here = reporter.transform.position;
            EnemyAI best = null, warning = null, refused = null;
            float bestDistance = float.PositiveInfinity, warningDistance = float.PositiveInfinity,
                refusedDistance = float.PositiveInfinity;
            IReadOnlyList<BlacksiteWorldRow> rows = World.Rows;
            for (int r = 0; r < rows.Count; r++)
            {
                BlacksiteWorldRow row = rows[r];
                if (row.Class != BlacksiteWorldClass.Monster || !row.SeenByMember(slot)
                    || !(row.Subject is EnemyAI enemy) || enemy == null)
                    continue;
                bool considered = BlacksiteThreatAdapterRegistry.IsAttackableBy(enemy, reporter);
                bool engagementRefused = BlacksiteThreatAdapterRegistry.EngagementRefusedFor(enemy);
                if (!considered && !engagementRefused)
                    continue;
                float distance = Vector3.Distance(here, enemy.transform.position);
                if (engagementRefused || !reporter.ContactEngageable(enemy))
                {
                    if (distance < refusedDistance) { refused = enemy; refusedDistance = distance; }
                    if (engagementRefused && considered && distance < warningDistance) { warning = enemy; warningDistance = distance; }
                }
                else if (distance < bestDistance) { best = enemy; bestDistance = distance; }
            }
            return best ?? warning ?? refused;
        }

        // A subject that can no longer be known is forgotten by the whole squad at once: a player
        // who is dead, disconnected, ignored or despawned (with no living member nobody knows
        // anyone), and a monster that is dead or destroyed.
        private void ForgetLostSubjects()
        {
            IReadOnlyList<BlacksiteWorldRow> rows = World.Rows;
            for (int i = rows.Count - 1; i >= 0; i--)
            {
                BlacksiteWorldRow row = rows[i];
                string cause = null;
                if (row.Class == BlacksiteWorldClass.Player)
                    cause = _monitorMembers.Count == 0
                        ? "no living observer"
                        : BlacksiteSoldierAI.PlayerKnowledgeIneligibility(row.Subject as PlayerControllerB);
                else if ((row.Class == BlacksiteWorldClass.Monster || row.Class == BlacksiteWorldClass.Unknown)
                    && row.Subject is EnemyAI enemy)
                    cause = enemy == null ? "destroyed" : enemy.isEnemyDead ? "dead" : null;
                if (cause == null)
                    continue;
                ulong key = row.Key;
                World.Forget(key);
                BlacksiteDiagnostics.Log("World", $"forgot entity={key} cause={cause}");
            }
        }

        internal void CollectLivingMembers(List<BlacksiteSoldierAI> destination)
        {
            destination.Clear();
            foreach(var member in _members) if(member.Soldier!=null&&!member.Soldier.isEnemyDead)destination.Add(member.Soldier);
        }

        internal bool TryGetMemberOrder(
            BlacksiteSoldierAI soldier,
            out BlacksiteMemberOrder memberOrder)
        {
            memberOrder = default;
            return soldier != null && _mission != null && _mission.Objectives.TryGetOrder(soldier, out memberOrder);
        }

        internal bool CanMemberBoard(BlacksiteSoldierAI soldier)
        {
            return soldier != null && (_mission.Objectives.Returning || soldier.IsRaidWithdrawing)
                && _mission.Objectives.Ship.CargoReadyForDeparture;
        }

        // #1233 S2: the manifest's one answer (the mission world, which is the objective's, or the
        // member's duty world) - the predicate the entrance gate admits by.
        internal bool MemberRequiresInteriorTraversal(BlacksiteSoldierAI soldier)
        {
            return soldier != null && soldier.isOutside && _mission.Objectives.RequiresInterior(soldier);
        }

        /// <summary>
        /// #1219 A3/C2: the boarding gate, asked at the link start before the board crossing begins
        /// and again when it completes. <paramref name="refusal"/> names the term that refused, in
        /// the order they are tested - <c>notExtracting</c> (neither returning nor withdrawing),
        /// <c>boardingClosed</c> (the climb has not opened), <c>committedMotion</c> - and is null on
        /// acceptance. Where the member stands is not a term: boarded is the completed board
        /// crossing. The boarded fact itself is the soldier's own (HasBoardedShip), so nothing is
        /// recorded here.
        /// </summary>
        internal bool NotifyMemberBoarded(BlacksiteSoldierAI soldier, out string refusal)
        {
            refusal = soldier == null || !(_mission.Objectives.Returning || soldier.IsRaidWithdrawing) ? BlacksiteBoardingRefusal.NotExtracting
                : !_mission.Objectives.Ship.CargoReadyForDeparture ? BlacksiteBoardingRefusal.BoardingClosed
                : soldier.Navigator.HasCommittedMotion ? BlacksiteBoardingRefusal.CommittedMotion
                : null;
            return refusal == null;
        }

        /// <summary>
        /// #1293 R1: the squad's contact bark. The member whose own eyes made a Player or Monster
        /// row New or Reacquired in the perception pass (<c>BlacksiteSoldierAI.PerceiveServer</c>)
        /// raises it; one <see cref="ContactBarkCooldownSeconds"/> clock per class, so a contact
        /// ducking in and out of sight is not re-announced every pass. Who knows about the contact
        /// is not decided here: every member reads the same row.
        /// </summary>
        internal void AnnounceContactServer(BlacksiteSoldierAI observer, BlacksiteWorldRow row)
        {
            if (!CanReport(observer) || row == null)
                return;
            float now = Time.time;
            if (row.Class == BlacksiteWorldClass.Player && row.Subject is PlayerControllerB player)
            {
                if (now < _nextPlayerContactBarkAt)
                    return;
                _nextPlayerContactBarkAt = now + ContactBarkCooldownSeconds;
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log(
                        "Contact",
                        $"exact player visual from member={observer.MemberIndex}, targetClient={player.actualClientId}, point={row.Position}, confidence={row.Confidence(now):0.00}.");
                RequestVoice(
                    BlacksiteVoiceEvent.PlayerContact,
                    observer,
                    critical: true,
                    transition: true,
                    transitionToken: NextVoiceTransitionToken(),
                    volume: 0.82f);
            }
            else if (row.Class == BlacksiteWorldClass.Monster && row.Subject is EnemyAI enemy)
            {
                if (now < _nextMonsterContactBarkAt)
                    return;
                _nextMonsterContactBarkAt = now + ContactBarkCooldownSeconds;
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log(
                        "Contact",
                        $"exact monster visual from member={observer.MemberIndex}, target={enemy.GetType().Name}/{enemy.GetInstanceID()}, point={row.Position}, confidence={row.Confidence(now):0.00}.");
                RequestVoice(
                    BlacksiteVoiceEvent.MonsterContact,
                    observer,
                    critical: true,
                    transition: true,
                    transitionToken: NextVoiceTransitionToken(),
                    volume: 0.82f);
            }
        }

        /// <summary>
        /// #988 B3: a monster alert is an ORDER, not a notification.
        ///
        /// The 2026-09-12 run is the whole argument. One squad alert at t=393.61 reached members 1,
        /// 2 and 3 at 18 to 20 m (`[UAT][Alert] ... recipients=3`); member 1 answered with
        /// `latency=4.19` and members 2 and 3 with `respondedAt=none, response=none`. They engaged
        /// only when the crawler personally walked up to them at t=408.79 and t=409.71, so a 4v1
        /// was fought as four sequential 1v1s and every one of them was lost.
        ///
        /// What was missing is exactly this reach. #1293 R1: what a member KNOWS about the contact is
        /// the squad's one world model and needs no reach at all; this is the one reach left, and
        /// it is an instruction. It is measured from the CONTACT, and only when the reporter is
        /// itself in the fight (<see cref="BlacksiteCombatPolicy.MonsterAlertOrders"/>: reporter
        /// inside ThreatRadius, recipient inside MonsterAlertOrderRadius), so a distant sighting
        /// still only informs. The perception pass issues one per reporter per pass, about the
        /// contact that reporter's own eyes would fight (<see cref="OrderMonsterResponses"/>).
        ///
        /// It orders raiders. A ship guard keeps its post and its leash, and a protected carrier is
        /// ordered but keeps its 800-point travel priority in the selector, so the carrier rule is
        /// unchanged: the cargo still leaves first and only a contact inside melee reach makes it
        /// fight.
        /// </summary>
        private void PropagateMonsterAlertOrder(BlacksiteSoldierAI reporter, EnemyAI enemy)
        {
            if (reporter == null || enemy == null || enemy.isEnemyDead) return;
            // #874 WP F: an order is an instruction to close, and nobody may close on a contact the
            // squad cannot hurt. Refused HERE, before the per-recipient throttle below is spent: an
            // Old Bird reported every pass would otherwise re-arm every member's cooldown and cool
            // out a real crawler order another member reports in the same second. The receipt keeps
            // its own guard for any other caller.
            if (!BlacksiteThreatAdapterRegistry.IsAttackable(enemy)) return;
            Vector3 contact = enemy.transform.position;
            float toReporter = Vector3.Distance(reporter.transform.position, contact);
            if (toReporter > BlacksiteCombatPolicy.ThreatRadius) return;
            int ordered = 0, skipped = 0, cooled = 0;
            for (int i = 0; i < _members.Count; i++)
            {
                BlacksiteSoldierAI teammate = _members[i].Soldier;
                if (teammate == null || teammate.isEnemyDead) continue;
                float toMember = Vector3.Distance(teammate.transform.position, contact);
                if (!BlacksiteCombatPolicy.MonsterAlertOrders(toReporter, toMember)) { skipped++; continue; }
                // A guard that has not been called in holds its ring; the backup dispatch is what
                // moves it, and that rule is older than this one.
                if (teammate != reporter && teammate.IsShipGuard) { skipped++; continue; }
                // #874 Plan F: a captor another member is committed to rescuing is no order for any
                // other baton member. ResolveThreatIntent would answer the order with Ignore, and a
                // held order refreshed by every report of the captor would keep that member off
                // every other ordered contact (ReceiveMonsterAlertOrder keeps a held order unless
                // the new one is closer) for the whole capture. Riflemen are ordered as before, and
                // so is everyone while the capture has no rescuer, so a member may still walk in.
                if (teammate.UsesBaton && BlacksiteVanillaMonsters.RescuedByAnother(enemy, teammate)) continue;
                // #988 round 2: the recipient's own clock. A member already holding this exact order
                // is refreshed by it and costs nothing, so the throttle is only ever spent on
                // reconsidering one. #1293 R1: a refresh no longer re-arms the clock, or a contact
                // seen every pass would cool every rival out and ReceiveMonsterAlertOrder's
                // arbitration (MonsterAlertOrderReplaces) would never run.
                ulong recipientToken = GetToken(teammate);
                if (!teammate.HasMonsterAlertOrderFor(enemy))
                {
                    if (_nextMonsterOrderRecipientAt.TryGetValue(recipientToken, out float nextOrderAt)
                        && Time.time < nextOrderAt)
                    { cooled++; continue; }
                    _nextMonsterOrderRecipientAt[recipientToken] = Time.time + BlacksiteCombatPolicy.MonsterAlertOrderCooldownSeconds;
                }
                teammate.ReceiveMonsterAlertOrder(enemy);
                ordered++;
            }
            if (ordered > 0 && BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.LogRateLimited(
                    "alert.order." + reporter.MemberIndex, 1f, "Alert",
                    $"monster order reporter={reporter.MemberIndex}, target={BlacksiteDiagnosticFormat.Target(enemy)}, "
                    + $"contactRange={toReporter:0.00}, ordered={ordered}, outOfRange={skipped}, cooled={cooled}, "
                    + $"radius={BlacksiteCombatPolicy.MonsterAlertOrderRadius:0.0}m, holds={BlacksiteCombatPolicy.MonsterAlertOrderSeconds:0.0}s.");
        }

        internal void RequestVoice(
            BlacksiteVoiceEvent voiceEvent,
            BlacksiteSoldierAI preferredSpeaker = null,
            bool critical = false,
            bool transition = false,
            int transitionToken = 0,
            float volume = 0.76f,
            bool ambient = false,
            bool immediate = false)
        {
            // #1271 S8: a transition with no token of its own is one line per occurrence. The
            // generator never returns 0 and no keyed transition caller passes 0, so the soldier
            // wrappers can keep the one-line RequestSquadVoice shape and still dedupe per call.
            if (transition && transitionToken == 0)
                transitionToken = NextVoiceTransitionToken();
            _voiceDirector.Request(
                voiceEvent,
                preferredSpeaker,
                critical,
                transition,
                transitionToken,
                volume,
                ambient,
                immediate);
        }

        internal void ReportInitialEntry(BlacksiteSoldierAI reporter)
        {
            RequestVoice(
                BlacksiteVoiceEvent.InitialEntry,
                reporter,
                transition: true,
                transitionToken: 1,
                volume: 0.78f);
        }

        // #1271 S8: the departure call, on the one-shot edge to Departing. The squad is aboard by
        // then, so the event is spoken aboard (IsSpokenAboard) or no member could say it.
        internal void ReportDeparture()
        {
            RequestVoice(
                BlacksiteVoiceEvent.ShipDeparting,
                preferredSpeaker: null,
                transition: true,
                volume: 0.78f);
        }

        internal void ReportCasualty(BlacksiteSoldierAI casualty)
        {
            int memberToken = casualty != null ? casualty.MemberIndex + 1 : NextVoiceTransitionToken();
            RequestVoice(
                BlacksiteVoiceEvent.CasualtyEscalation,
                preferredSpeaker: null,
                critical: true,
                transition: true,
                transitionToken: memberToken,
                volume: 0.84f);
            ReassignRolesOverSurvivors();
            // #1293 R3: a body wants no point; every living squadmate (not him) decides on a casualty.
            ReleaseDestinationClaim(casualty);
            for (int i = 0; i < _members.Count; i++)
            {
                BlacksiteSoldierAI member = _members[i].Soldier;
                if (member != null && !member.isEnemyDead)
                    member.RequestDecision(BlacksiteDecisionCause.Casualty);
            }
        }

        internal void ReportKillClear(BlacksiteSoldierAI reporter)
        {
            RequestVoice(
                BlacksiteVoiceEvent.KillClear,
                reporter,
                transition: true,
                transitionToken: NextVoiceTransitionToken(),
                volume: 0.72f);
        }

        internal bool HasLivingMemberWithRole(BlacksiteSoldierTacticalRole role)
        {
            for (int i = 0; i < _members.Count; i++)
            {
                MemberRecord member = _members[i];
                if (member.Role == role && member.Soldier != null && !member.Soldier.isEnemyDead)
                    return true;
            }
            return false;
        }

        // #314 suggestion 6: the same role order applied to whoever is left -- first Leader, second
        // Pointman, last RearGuard, second-to-last Support -- so a dead Leader is replaced rather
        // than simply missing. Member index order is preserved because _members stays sorted.
        private void ReassignRolesOverSurvivors()
        {
            _survivorScratch.Clear();
            for (int i = 0; i < _members.Count; i++)
            {
                MemberRecord member = _members[i];
                if (member.Soldier != null && !member.Soldier.isEnemyDead && !member.Soldier.ReplicatedDead)
                    _survivorScratch.Add(member);
            }

            if (_survivorScratch.Count == 0)
                return;

            for (int i = 0; i < _survivorScratch.Count; i++)
            {
                MemberRecord member = _survivorScratch[i];
                BlacksiteSoldierTacticalRole role = BlacksiteFormationPlanner.ResolveRole(
                    i,
                    _survivorScratch.Count);
                if (member.Role == role)
                    continue;
                // The record only follows a role the soldier actually took; a refused reassignment
                // would otherwise leave the director planning around a role that never applied.
                if (member.Soldier.ApplyReassignedTacticalRoleServer(role))
                    member.Role = role;
            }
        }

        // #1271 S8: includeBoarded asks for the members still able to speak once aboard - boarded
        // members ride the hull visible with a live voice source until the ship has departed.
        internal void CollectEligibleVoiceSpeakers(List<BlacksiteSoldierAI> destination, bool includeBoarded = false)
        {
            if (destination == null)
                return;
            for (int i = 0; i < _members.Count; i++)
            {
                BlacksiteSoldierAI soldier = _members[i].Soldier;
                if (soldier != null && (includeBoarded ? soldier.IsVoiceCapable : soldier.IsEligibleForSquadVoice))
                    destination.Add(soldier);
            }
        }

        // #1293 R1: a kind-less stimulus (explosion, unattributed noise or damage) is the squad's one
        // Unknown row, heard by the source. Members inside StimulusShareRadius still turn toward an
        // approximate point of their own; what they know is the row.
        internal void ReportStimulus(
            BlacksiteSoldierAI source,
            BlacksiteSoldierReactionKind reaction,
            Vector3 position)
        {
            if (!CanReport(source))
                return;

            string kind;
            float uncertainty;
            bool sound = false;
            switch (reaction)
            {
                case BlacksiteSoldierReactionKind.Explosion:
                    kind = "Explosion";
                    uncertainty = 2.5f;
                    break;
                case BlacksiteSoldierReactionKind.DirectDamage:
                case BlacksiteSoldierReactionKind.Stun:
                case BlacksiteSoldierReactionKind.EnemyCollision:
                    kind = "DamageDirection";
                    uncertainty = 3.5f;
                    break;
                default:
                    kind = "Sound";
                    sound = true;
                    uncertainty = reaction == BlacksiteSoldierReactionKind.LoudNoise ? 4f : 7f;
                    break;
            }

            World.Heard(BlacksiteWorldModel.StimulusKey, null, BlacksiteWorldClass.Unknown, position,
                source.isOutside, source.NetworkObjectId, Time.time);
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.LogRateLimited(
                    $"contact.stimulus.{kind}.{source.MemberIndex}",
                    1.5f,
                    "Contact",
                    $"stimulus sourceMember={source.MemberIndex}, reaction={reaction}, kind={kind}, point={position}, uncertainty={uncertainty:0.0}m.");
            }
            for (int i = 0; i < _members.Count; i++)
            {
                BlacksiteSoldierAI teammate = _members[i].Soldier;
                // #874 S2.4: a boarded, captured, dead or consumed member is not a combat
                // participant, so the squad does not hand it contacts it cannot act on.
                if (teammate == null
                    || teammate == source
                    || !teammate.AllowsCombatLifecycle
                    || Vector3.Distance(teammate.transform.position, source.transform.position) > StimulusShareRadius)
                {
                    continue;
                }

                Vector3 approximate = BlacksiteFormationPlanner.BuildUncertainSearchPoint(
                    position,
                    uncertainty,
                    teammate.MemberIndex,
                    teammate.AnimationSeed);
                teammate.ReceiveSquadStimulusFromDirector(reaction, approximate);
                // #874 S4.0b: a stimulus is a contact registration for the member that received it.
                // Damage-shaped kinds and sound-shaped kinds are separated so the 450 sound stimuli
                // of the last run can be told apart from a hit.
                if (BlacksiteDiagnostics.Enabled)
                    BlacksitePerceptionDiagnostics.NoteContact(
                        teammate.MemberIndex, teammate.NetworkObjectId, "stimulus:" + kind,
                        sound ? BlacksitePerceptionDiagnostics.SourceAudio : BlacksitePerceptionDiagnostics.SourceDamage,
                        Vector3.Distance(teammate.transform.position, approximate), exact: false);
            }
            // The source itself sensed the stimulus first hand.
            if (BlacksiteDiagnostics.Enabled)
                BlacksitePerceptionDiagnostics.NoteContact(
                    source.MemberIndex, source.NetworkObjectId, "stimulus:" + kind,
                    sound ? BlacksitePerceptionDiagnostics.SourceAudio : BlacksitePerceptionDiagnostics.SourceDamage,
                    Vector3.Distance(source.transform.position, position), exact: false);
        }

        internal bool TryAcquireInteraction(
            BlacksiteSoldierAI soldier,
            BlacksiteInteractionTask task,
            ulong ownerToken,
            out Vector3 queuePosition)
        {
            queuePosition = task != null ? task.ApproachPosition : soldier.transform.position;
            if (soldier == null || task == null || task.Target == null)
                return false;

            CleanupClaims();
            bool objective = task.Kind == BlacksiteInteractionKind.ContainerOverride
                || task.Kind == BlacksiteInteractionKind.CacheOverride;
            // #874 S5.4 (#873): every requester registers with the doorway's column, refused or
            // not, so the waiting order is first-asked-first-served and a member keeps its place
            // across the frames it spends walking to it.
            int key = GetInteractionKey(task.Kind, task.Target);
            BlacksiteEntranceColumn column = ColumnFor(key, task.Kind);
            int columnPlace = column?.Place(ownerToken, Time.time) ?? -1;
            // #1016 S2: the lane answers "is somebody else holding this" with a holder, a heldFor
            // and a deadline, which is what the free-text line could never say. Serve BEFORE the
            // request, so a preferred operator leads the column it is about to walk through - the
            // call site's own order, unchanged, since Serve is reached only once both refusal
            // branches have declined to return.
            //
            // #1062 WP1: resolved BEFORE the preferred-operator branch, because that branch now has
            // to know whether this requester is the holder. LaneFor registers the lane, which for
            // an Entrance is the very lane ColumnFor registered one line above; for an
            // ObjectiveLock it is registered one deferral earlier than it used to be, and the
            // preferred operator's own pass - which is never deferred to itself - registered it
            // anyway.
            BlacksiteResourceLane lane = LaneFor(key, task.Kind);
            // #1062 WP1 (#1016 S2 regression): HOLDER FIRST. The two refusal branches below must
            // not be able to refuse each other, and before this they could: the preferred-operator
            // branch turned the HOLDER away before it ever reached lane.Request, so its lease was
            // never renewed, while the standing-holder branch turned the preferred operator away
            // because that dead lease was still in the lane. Nothing released it and nothing swept
            // it, and the doorway stayed wedged until the coordinator was disposed at round end
            // (2026-09-15 host log: member 0 "reservation deferred ... preferred=1" and member 1
            // "reservation occupied owner=396" on every tick for 407 s, tasks at phase=Queued).
            //
            // A member that already holds the resource renews and proceeds. The preferred-operator
            // rule decides who gets it NEXT; it is not a licence to pre-empt a live holder in the
            // middle of a threshold transfer, which is a teleport that cannot be half done. The
            // holder's claim is still bounded - it is the 2 s renewing lease, and the holder that
            // stops asking loses it on the one sweep - so "holder first" cannot outlive the holder.
            //
            // The test is a LIVE lease of this member's own, not bare membership of the holder
            // list: only a column kind reaches column.Place above, so an ObjectiveLock is read here
            // without a sweep having run this frame, and a member
            // that stopped asking for longer than the stale window must not be able to skip the
            // preferred-operator deferral for the one pass before the broker's own sweep catches
            // it. ExpiresIn is the same liveness the foreign-holder branch below tests.
            bool holdsLane = lane != null
                && lane.TryGetHolder(ownerToken, out BlacksiteResourceLease own)
                && own.ExpiresIn(Time.time) > 0f;
            BlacksiteSoldierAI preferred = FindPreferredOperator(objective);
            if (!holdsLane
                && preferred != null
                && preferred != soldier
                && (objective || (preferred.isOutside == soldier.isOutside && preferred.IsRequestingWorldInteraction(task)))
                && !preferred.IsCombatEngaged
                && Vector3.Distance(preferred.transform.position, task.ApproachPosition) <= 15f)
            {
                queuePosition = BuildInteractionQueuePosition(soldier, task, 1, columnPlace);
                if (BlacksiteDiagnostics.Enabled)
                {
                    BlacksiteDiagnostics.LogRateLimited(
                        $"interaction.preferred.{task.Kind}.{soldier.MemberIndex}",
                        2f,
                        "Interaction",
                        $"reservation deferred kind={task.Kind}, requester={soldier.MemberIndex}, preferred={preferred.MemberIndex}, target={task.Label}, {DescribeColumn(column, columnPlace)}"
                        + $", waitFor={DescribeWaitFor(lane, ownerToken)}.");
                }
                return false;
            }

            // #1062 WP1: and the foreign-holder test asks whether that holder is still LIVE. The
            // pre-#1016 code compared `reservation.ExpiresAt > Time.time`; S2 dropped the deadline
            // and left a pure read of BlacksiteResourceLane's holder list, so an expired holder the
            // sweep had not reached yet still refused everybody. Request and Serve both sweep, so a
            // holder that is past its deadline here is gone by the time the grant is decided.
            if (!holdsLane
                && lane != null && lane.TryGetHolder(out BlacksiteResourceLease standing)
                && standing.Holder != ownerToken
                && standing.ExpiresIn(Time.time) > 0f)
            {
                queuePosition = BuildInteractionQueuePosition(soldier, task, 2, columnPlace);
                if (BlacksiteDiagnostics.Enabled)
                {
                    BlacksiteDiagnostics.LogRateLimited(
                        $"interaction.occupied.{task.Kind}.{soldier.MemberIndex}",
                        2f,
                        "Interaction",
                        $"reservation occupied kind={task.Kind}, requester={soldier.MemberIndex}, owner={standing.Holder}, target={task.Label}, {DescribeColumn(column, columnPlace)}"
                        // #1016 S2 (design note E.3): appended at the END. The wording above is
                        // unchanged; these are the two numbers the line never had.
                        + $", waitFor={DescribeWaitFor(lane, ownerToken)}, expiresIn={standing.ExpiresIn(Time.time):0.00}.");
                }
                return false;
            }

            column?.Serve(ownerToken, Time.time);
            if (lane == null)
                return true;
            bool heldBefore = lane.Holds(ownerToken);
            BlacksiteBrokerVerdict verdict = lane.Request(ownerToken, Time.time, "reservation:" + task.Kind);
            if (!verdict.Admitted)
            {
                queuePosition = BuildInteractionQueuePosition(soldier, task, 2, columnPlace);
                if (BlacksiteDiagnostics.Enabled)
                {
                    BlacksiteDiagnostics.LogRateLimited(
                        $"interaction.occupied.{task.Kind}.{soldier.MemberIndex}",
                        2f,
                        "Interaction",
                        $"reservation occupied kind={task.Kind}, requester={soldier.MemberIndex}, owner={(lane.TryGetHolder(out BlacksiteResourceLease held) ? held.Holder : 0UL)}, target={task.Label}, {DescribeColumn(column, columnPlace)}"
                        // #1293 R4: expiresIn= is the holder's lease, as on the branch above; a
                        // verdict carries no wait deadline any more.
                        + $", waitFor={verdict.WaitedFor:0.00}, expiresIn={held.ExpiresIn(Time.time):0.00}.");
                }
                return false;
            }
            if (!heldBefore && BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Interaction",
                    // expiresIn= keeps its name and its position; it is now the LEASE's own
                    // remaining rather than the constant, which on a fresh grant is the same 2.0.
                    $"reservation acquired kind={task.Kind}, owner={ownerToken}, member={soldier.MemberIndex}, target={task.Label}, expiresIn={verdict.Lease.ExpiresIn(Time.time):0.0}s.");
            }
            return true;
        }

        internal void RenewInteraction(BlacksiteInteractionTask task, ulong ownerToken)
        {
            if (task == null || task.Target == null)
                return;
            int key = GetInteractionKey(task.Kind, task.Target);
            // #1016 S2: a renewal is the holder re-requesting its own lane. The lane's Renews rule
            // for Doorway/ObjectiveLock restarts the 2 s window on every re-assert, which is
            // exactly what pushing ExpiresAt out did - and a holder that stops calling this still
            // loses the claim on the same schedule, now through the one sweep.
            //
            // #1016 S2 review batch 2, F14: a renewal may not ACQUIRE. The base was
            // `if (reservation exists && owner == ownerToken) push ExpiresAt` - a no-op for anyone
            // who was not already the holder - whereas Request enters the order and can be granted,
            // so a member whose claim had lapsed could take the doorway back through a path whose
            // whole name says it only renews, bypassing TryAcquireInteraction's preferred-operator
            // deferral and its foreign-reservation branch. The holder test restores the base.
            BlacksiteResourceLane renewing = LaneFor(key, task.Kind);
            if (renewing == null || !renewing.TryGetHolder(ownerToken, out _)) return;
            renewing.Request(ownerToken, Time.time, "reservation:" + task.Kind);
        }

        internal void ReleaseInteraction(BlacksiteInteractionTask task, ulong ownerToken)
        {
            if (task == null || task.Target == null)
                return;
            int key = GetInteractionKey(task.Kind, task.Target);
            // #874 S5.4 (#873): the member has stopped asking for this doorway, so it leaves the
            // waiting order and everyone behind it moves up one place. Removal keeps their order.
            if (_interactionColumns.TryGetValue(key, out BlacksiteEntranceColumn column))
                column.Leave(ownerToken);
            // #1016 S2: one Release, which drops the claim AND the place in the order. The column
            // half above is kept because a column may exist for a doorway whose lane the broker has
            // already pruned; both calls are idempotent.
            BlacksiteResourceLane lane = LaneFor(key, task.Kind);
            bool wasHolder = lane != null && lane.Holds(ownerToken);
            lane?.Release(ownerToken);
            if (wasHolder && BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Interaction",
                    $"reservation released kind={task.Kind}, owner={ownerToken}, target={task.Label}.");
            }
        }

        // #874 S1.2: an exact claim is still published so peers space away from the lane, but the
        // owner keeps the spot its action's reach contract was validated against.
        // #874 S4.3: the separation offset is world-anchored and latched for the life of the claim.
        // It used to be built from soldier.transform.right/forward on every issue, so the adjusted
        // point rotated with the member walking to it and flipped sides by 1.5 to 2 m whenever the
        // overlap count changed parity: 84 of 88 guard legs in the 2026-09-09 run were re-issued at
        // about 2 s spacing alternating between two posts with the distance left unchanged.
        // #874 Q1: <paramref name="acceptanceRadius"/> is how far from <paramref name="destination"/>
        // the caller's goal lets a standing point move and still pass its own completion test. No
        // point handed back - a fresh ring, a latched one, a cleared landing - is ever further than
        // that; a displacement that would be is refused, logged once as substitutionRefused, and
        // the requested point is handed back instead. The claim is still published, so peers keep
        // spacing away from it. On 2026-09-23 round 2 a latched 1.35 m offset taken for an escort
        // leg was handed back for the door the member then had to reach, 3.76 m away (log 40594).
        // #874 Q2: an escort's point that sits in a holder's keep-out and cannot be moved out of it
        // within its own goal's test is REFUSED: false, the claim withdrawn, never published or
        // handed back. The caller holds off or re-plans; <paramref name="claimed"/> is then the
        // requested point for diagnostics only.
        internal bool TryClaimDestination(
            BlacksiteSoldierAI soldier,
            ulong ownerToken,
            Vector3 destination,
            bool exact,
            float acceptanceRadius,
            out Vector3 claimed)
        {
            claimed = destination;
            int overlaps = 0;
            ulong claimants = 0UL;
            foreach (KeyValuePair<ulong, DestinationClaim> entry in _destinationClaims)
            {
                // The claimant set is who wants this point, not where each of them was pushed to, so
                // a member already nudged out of the shared window still counts as a claimant and the
                // set stays stable while the crowd is being spaced out.
                if (entry.Key == ownerToken
                    || HorizontalDistance(entry.Value.Requested, destination) > SharedDestinationThreshold)
                {
                    continue;
                }
                overlaps++;
                claimants ^= ClaimantSignature(entry.Key);
            }

            _destinationClaims.TryGetValue(ownerToken, out DestinationClaim claim);
            bool anchored = claim != null
                && HorizontalDistance(claim.Requested, destination) < ClaimReanchorDistance;
            bool exactAnchored = exact && anchored && claim.Exact;
            // Latched: while the same members want the same point, the point handed back is the one
            // already handed back. Nothing here follows the member walking to it - and, #874 Q1,
            // nothing latched for another goal's test is handed back past this one's, nor (#874 Q2)
            // into a holder's keep-out that opened since.
            if (claim != null && claim.HasOffset && anchored
                && claim.Overlaps == overlaps && claim.Claimants == claimants
                && HorizontalDistance(claim.Destination, destination) <= acceptanceRadius + BlacksiteArrivalPolicy.SubstituteEpsilon
                && !TryKeepOutPush(ownerToken, claim.Destination, out _))
            {
                claimed = claim.Destination;
                return true;
            }

            Vector3 adjusted = destination;
            Vector3 basis = Vector3.zero;
            bool displaced = false;
            if (overlaps > 0 && soldier != null && !exact)
            {
                // A recompute may widen the ring, never swap sides: a live claim keeps its bearing.
                basis = claim != null && claim.HasOffset && anchored
                    ? claim.Basis
                    : SeparationBearing(soldier, destination);
                float ring = Mathf.Min(BlacksiteArrivalPolicy.MaxClaimOffset,
                    BlacksiteArrivalPolicy.ClaimSeparationBase + (overlaps - 1) * BlacksiteArrivalPolicy.ClaimSeparationStep);
                if (NavMesh.SamplePosition(destination + basis * ring, out NavMeshHit hit, 2.5f, NavMesh.AllAreas)
                    && AcceptsClaim(soldier, destination, hit.position, acceptanceRadius))
                {
                    adjusted = hit.position;
                    displaced = true;
                }
            }
            // #874 Q2: an escort's crowd point keeps out of every holder's reach radius and approach
            // line, as far as its own goal's test lets it move (#874 Q1). A point that cannot be
            // moved out - no mesh there, past the goal's test, or into another holder's keep-out -
            // is refused, never handed back where it stands. Round 1: the escorts' claims around
            // the apparatus displaced the holder's reach point 0.9 m and he walked to the far
            // corner (log 11720-11892).
            if (!exact && soldier != null)
            {
                bool inKeepOut = TryKeepOutPush(ownerToken, adjusted, out Vector3 pushed);
                bool stands = false, withinTest = false, pushedIn = false;
                NavMeshHit outside = default;
                if (inKeepOut)
                {
                    stands = NavMesh.SamplePosition(pushed, out outside, BlacksiteArrivalPolicy.AgentRadius, NavMesh.AllAreas);
                    withinTest = stands && AcceptsClaim(soldier, destination, outside.position, acceptanceRadius);
                    pushedIn = stands && TryKeepOutPush(ownerToken, outside.position, out _);
                }
                switch (BlacksiteMotionPolicy.KeepOutClaim(inKeepOut, stands, withinTest, pushedIn))
                {
                    case BlacksiteKeepOutClaim.Refused:
                        _destinationClaims.Remove(ownerToken);
                        return false;
                    case BlacksiteKeepOutClaim.Pushed:
                        adjusted = outside.position;
                        displaced = true;
                        break;
                }
            }

            if (claim == null)
            {
                claim = new DestinationClaim();
                _destinationClaims[ownerToken] = claim;
            }
            claim.Destination = adjusted;
            claim.Requested = destination;
            claim.Basis = basis;
            claim.Claimants = claimants;
            claim.Overlaps = overlaps;
            claim.HasOffset = displaced;
            // #874 Q2: an exact claim for an objective action's reach publishes its keep-out (a
            // combat lane or a cabin slot is exact too, and keeps its point, but is no holder). The
            // route corner is read when the claim is (re)anchored; the line starts at the holder
            // itself once it is on the final leg.
            claim.Exact = exact && soldier != null && soldier.Navigator != null && soldier.Navigator.Motion != null
                && soldier.Navigator.Motion.HoldsObjectiveReach;
            if (claim.Exact)
            {
                var navigator = soldier.Navigator;
                Vector3 here = soldier.transform.position;
                claim.ReachRadius = navigator != null ? navigator.ArrivalContract.ReachRadius : 0f;
                if (!exactAnchored) claim.ApproachCorner = navigator != null && navigator.Motion != null
                    ? navigator.Motion.LastRouteCorner() : here;
                claim.ApproachFrom = HorizontalDistance(here, destination) < HorizontalDistance(claim.ApproachCorner, destination)
                    ? here : claim.ApproachCorner;
            }
            claimed = adjusted;
            return true;
        }

        /// <summary>
        /// The world-interaction queue places (entrance queue places) that have no refusal path of
        /// their own. A refusal there hands back the requested point, as #874 Q1 does.
        /// </summary>
        internal Vector3 ClaimDestination(BlacksiteSoldierAI soldier, ulong ownerToken, Vector3 destination)
        {
            TryClaimDestination(soldier, ownerToken, destination, false, BlacksiteArrivalPolicy.MaxClaimOffset,
                out Vector3 claimed);
            return claimed;
        }

        /// <summary>#1293 R3: the member no longer wants the point it claimed. Idempotent.</summary>
        internal void ReleaseDestinationClaim(BlacksiteSoldierAI member)
        {
            if (member != null) _destinationClaims.Remove(GetToken(member));
        }

        // #874 Q2: bodies and claims on another member's storey.
        private const float PeerStoreyMetres = 1.8f;

        /// <summary>
        /// #874 Q2: the first point of <paramref name="point"/>'s nearest escape from a held exact
        /// claim's keep-out (BlacksiteMotionPolicy.InHolderKeepOut) that is not
        /// <paramref name="ownerToken"/>'s own. False when the point is in no keep-out.
        /// </summary>
        private bool TryKeepOutPush(ulong ownerToken, Vector3 point, out Vector3 pushed)
        {
            pushed = point;
            foreach (KeyValuePair<ulong, DestinationClaim> entry in _destinationClaims)
            {
                DestinationClaim held = entry.Value;
                if (entry.Key == ownerToken || !held.Exact
                    || Mathf.Abs(held.Destination.y - point.y) >= PeerStoreyMetres) continue;
                if (!BlacksiteMotionPolicy.InHolderKeepOut(point.x, point.z, held.Destination.x, held.Destination.z,
                        held.ApproachFrom.x, held.ApproachFrom.z, held.ReachRadius)) continue;
                BlacksiteMotionPolicy.KeepOutPush(point.x, point.z, held.Destination.x, held.Destination.z,
                    held.ApproachFrom.x, held.ApproachFrom.z, held.ReachRadius, out float x, out float z);
                pushed = new Vector3(x, point.y, z);
                return true;
            }
            return false;
        }

        /// <summary>
        /// #874 Q1: a displaced standing point is inside the caller's acceptance radius, or it is
        /// refused and the member's navigator logs the refusal against the goal it serves.
        /// </summary>
        private static bool AcceptsClaim(BlacksiteSoldierAI soldier, Vector3 requested, Vector3 substitute, float acceptanceRadius)
        {
            if (HorizontalDistance(substitute, requested) <= acceptanceRadius + BlacksiteArrivalPolicy.SubstituteEpsilon)
                return true;
            var navigator = soldier != null ? soldier.Navigator : null;
            navigator?.ReportSubstitutionRefused("claim", requested, substitute, acceptanceRadius);
            return false;
        }

        // #874 S4.3: the world-anchored lateral basis. The horizontal bearing from the shared point
        // to the claimant is fixed in the world, where transform.right rotated with the member.
        private static Vector3 SeparationBearing(BlacksiteSoldierAI soldier, Vector3 destination)
        {
            Vector3 bearing = soldier.transform.position - destination;
            bearing.y = 0f;
            if (bearing.sqrMagnitude > ClaimBearingMinimum * ClaimBearingMinimum)
                return bearing.normalized;
            return Quaternion.Euler(0f, soldier.MemberIndex * (360f / ClaimBearingSlots), 0f) * Vector3.forward;
        }

        // Order-independent identity of the overlapping claimant set: a murmur3 finalizer folded by
        // xor, so a claim can tell "the same members" from "one joined or one expired".
        private static ulong ClaimantSignature(ulong token)
        {
            unchecked
            {
                ulong mixed = token ^ (token >> 33);
                mixed *= 0xFF51AFD7ED558CCDUL;
                mixed ^= mixed >> 33;
                mixed *= 0xC4CEB9FE1A85EC53UL;
                mixed ^= mixed >> 33;
                return mixed | 1UL;
            }
        }

        internal bool IsPreferredInteractionOperator(BlacksiteSoldierAI soldier, bool objective)
        {
            return soldier != null && FindPreferredOperator(objective) == soldier;
        }

        private void UpdateVoiceDriveState()
        {
            if (_mission == null)
                return;

            BlacksiteHardDriveState current = _mission.DriveState;
            if (!_voiceDriveStateInitialized)
            {
                _voiceDriveStateInitialized = true;
                _lastVoiceDriveState = current;
                return;
            }
            if (current == _lastVoiceDriveState)
                return;

            _lastVoiceDriveState = current;
            int transitionToken = NextVoiceTransitionToken();
            if (current == BlacksiteHardDriveState.SoldierHeld)
            {
                RequestVoice(
                    BlacksiteVoiceEvent.DriveAcquired,
                    preferredSpeaker: FindMemberByNetworkId(_mission.CarrierNetworkId),
                    critical: true,
                    transition: true,
                    transitionToken: transitionToken,
                    volume: 0.84f);
            }
            else if (current == BlacksiteHardDriveState.Loose
                || current == BlacksiteHardDriveState.PlayerHeld
                || current == BlacksiteHardDriveState.InShip
                || current == BlacksiteHardDriveState.Missing)
            {
                RequestVoice(
                    BlacksiteVoiceEvent.DriveMissing,
                    preferredSpeaker: null,
                    critical: true,
                    transition: true,
                    transitionToken: transitionToken,
                    volume: 0.84f);
            }
        }

        private int NextVoiceTransitionToken()
        {
            unchecked
            {
                _voiceTransitionSequence++;
                if (_voiceTransitionSequence == 0)
                    _voiceTransitionSequence = 1;
                return _voiceTransitionSequence;
            }
        }

        // #874 S5.4 (#873): column= is the member's place in the doorway's waiting order, 0 being
        // the member holding the threshold; headway= is the separation one place represents,
        // BlacksiteEntranceColumn.HeadwaySeconds. Kinds that do not queue as a column print none
        // for both rather than a number that means nothing.
        private static string DescribeColumn(BlacksiteEntranceColumn column, int place)
            => column == null
                ? "column=none, headway=none"
                : $"column={place}, headway={BlacksiteEntranceColumn.HeadwaySeconds:0.00}";

        // #874 S5.4 (#873): only the paired doorways queue as a column. An objective override keeps
        // the fan it has always used; its contention shape belongs to its own unit.
        // (#1233 S5: a facility door never asks this director for anything; #1293 R4: the
        // elevator reserves its car on the car's own capacity-1 lane, never here.)
        private static bool IsColumnKind(BlacksiteInteractionKind kind)
            => kind == BlacksiteInteractionKind.Entrance;

        private BlacksiteEntranceColumn ColumnFor(int key, BlacksiteInteractionKind kind)
        {
            if (!IsColumnKind(kind))
                return null;
            if (!_interactionColumns.TryGetValue(key, out BlacksiteEntranceColumn column))
            {
                column = new BlacksiteEntranceColumn();
                _interactionColumns[key] = column;
            }
            // #1016 S2: the column's order IS the doorway lane. Registering it here is what puts
            // this doorway under the one sweep - before S2 CleanupClaims:1625-1633 pruned the
            // column dictionary on the director's own cadence and the reservation beside it on
            // another, and neither knew about the other.
            column.Bind(Broker, BlacksiteResourceKey.Doorway(key));
            return column;
        }

        /// <summary>#1016 S2: the mission's one claim ledger, or null off the server.</summary>
        private BlacksiteResourceBroker Broker
            => _mission != null && _mission.IsServer ? _mission.Blackboard.Broker : null;

        /// <summary>
        /// #1016 S2: which of the resources an interaction kind contends for. Closed and total, so
        /// a new interaction kind cannot quietly acquire no claim at all.
        ///
        /// #1293 R4: an elevator task holds no claim here. The car is the link's own capacity-1
        /// <see cref="BlacksiteResourceKind.Elevator"/> lane, which it requests and releases itself.
        ///
        /// #1233 S5 (D3): a facility door is no resource at all. Bodies crowd it and the hand
        /// operates it in reach; the capacity-one Door lane, its 2 s renewal and its queue fan are
        /// deleted, and a Door task never asks this director for anything.
        /// </summary>
        private static BlacksiteResourceKind ResourceKindFor(BlacksiteInteractionKind kind)
        {
            switch (kind)
            {
                case BlacksiteInteractionKind.Entrance: return BlacksiteResourceKind.Doorway;
                case BlacksiteInteractionKind.ContainerOverride:
                case BlacksiteInteractionKind.CacheOverride: return BlacksiteResourceKind.ObjectiveLock;
                default: return BlacksiteResourceKind.None;
            }
        }

        /// <summary>
        /// #1016 S2: the lane one interaction reservation is held in.
        ///
        /// This replaces <c>_interactionReservations</c> outright. That dictionary carried
        /// <c>{Target, Kind, OwnerToken, ExpiresAt}</c> and was expired by <c>CleanupClaims</c> on
        /// the director's own cadence; the lane carries the same claim with the same 2 s renewal
        /// window and is expired by the one sweep, which is also what finally gives a refused
        /// requester a place, a holder and a deadline to print.
        /// </summary>
        private BlacksiteResourceLane LaneFor(int key, BlacksiteInteractionKind kind)
        {
            BlacksiteResourceBroker broker = Broker;
            BlacksiteResourceKind resource = ResourceKindFor(kind);
            if (broker == null || resource == BlacksiteResourceKind.None) return null;
            return broker.Lane(
                new BlacksiteResourceKey(resource, key),
                BlacksiteResourceLeasePolicy.Capacity(resource),
                BlacksiteResourceLeasePolicy.HeadwaySeconds(resource));
        }

        /// <summary>
        /// Seconds this member has been queued for THIS resource, or none. For the waitFor= key.
        ///
        /// #1062 WP1: it takes the lane the refusal is about. It used to ask the broker for "the
        /// first lane this token is waiting in", which scans <c>_lanes</c> in dictionary order, so
        /// a member queued for the doorway AND enrolled behind the objective board printed the
        /// board's wait - a 30 s bound - on a doorway refusal. A refusal line that names a doorway
        /// has to report the doorway's own wait or the number is worse than absent.
        /// </summary>
        private string DescribeWaitFor(BlacksiteResourceLane lane, ulong token)
        {
            if (lane == null || !lane.IsWaiting(token))
                return BlacksiteDiagnosticFormat.None;
            float waitedFor = Time.time - lane.WaitStartedAt(token);
            return (waitedFor > 0f ? waitedFor : 0f).ToString("0.00");
        }

        // #874 S5.4 (#873): a member has transferred and the threshold is physically free. The
        // spread is measured from the first clear of the column still being counted, so one grep of
        // event=cleared answers the question RC1 of findings-elevator.md could not: how far apart
        // did this squad actually arrive on the far side. #1219 B4: columnSize counts the distinct
        // members that cleared in this direction (the soldier's world after the transfer) in this
        // column, and the line names the direction.
        internal void NoteEntranceCleared(
            BlacksiteSoldierAI soldier,
            BlacksiteInteractionTask task,
            ulong ownerToken)
        {
            if (soldier == null || task == null || task.Target == null)
                return;
            BlacksiteEntranceColumn column = ColumnFor(GetInteractionKey(task.Kind, task.Target), task.Kind);
            if (column == null)
                return;

            bool towardOutside = BlacksiteWorld.Outside(soldier);
            column.NoteCleared(Time.time, soldier.NetworkObjectId, towardOutside, out float spread, out int columnSize);
            column.Leave(ownerToken);
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Entrance",
                    $"event=cleared, member={soldier.MemberIndex}, spread={spread:0.00}, columnSize={columnSize}"
                    + $", direction={(towardOutside ? "out" : "in")}.");
            }
        }

        private BlacksiteSoldierAI FindPreferredOperator(bool objective)
        {
            if (objective) return _mission.Objectives.ReservedOperator;
            for (int p = 0; p < InteractionRolePreference.Length; p++)
            {
                for (int i = 0; i < _members.Count; i++)
                {
                    MemberRecord member = _members[i];
                    if (member.Role == InteractionRolePreference[p]
                        && member.Soldier != null
                        && !member.Soldier.isEnemyDead
                        && !member.Soldier.ReplicatedStunned
                        // #1293 R2: a stranded member cannot reach the door, so nobody defers to him.
                        && !member.Soldier.Navigator.IsStranded)
                    {
                        return member.Soldier;
                    }
                }
            }
            return null;
        }

        // #874 S5.4 (#873): queueIndex is the reason the member was refused (1 preferred operator,
        // 2 occupied) and is what the fan below still keys on; columnPlace is the member's real
        // place in the doorway's waiting order, or -1 for a kind that does not queue as a column.
        //
        // The fan put two members whose indices differ by an even number on the same point (its
        // parity term is (MemberIndex + queueIndex) & 1), and made an advance from the second depth
        // to the first a 0.85 m step forward with a 3.35 m sideways crossing of the doorway mouth,
        // of which only 0.85 m is along the approach direction: below S4.2's MinDestinationChange
        // 1.45 m, so a straightened fan would have latched the member at the back instead.
        private Vector3 BuildInteractionQueuePosition(
            BlacksiteSoldierAI soldier,
            BlacksiteInteractionTask task,
            int queueIndex,
            int columnPlace = -1)
        {
            Vector3 forward = task.AlignPosition - task.ApproachPosition;
            forward.y = 0f;
            if (forward.sqrMagnitude < 0.01f)
                forward = soldier.transform.forward;
            forward.Normalize();
            if (columnPlace >= 0)
            {
                Vector3 slot = task.ApproachPosition
                    - forward * BlacksiteEntranceColumn.BackMetres(Mathf.Max(1, columnPlace));
                return NavMesh.SamplePosition(slot, out NavMeshHit slotHit, 3f, NavMesh.AllAreas)
                    ? slotHit.position
                    : slot;
            }
            Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
            float sign = ((soldier.MemberIndex + queueIndex) & 1) == 0 ? 1f : -1f;
            Vector3 candidate = task.ApproachPosition
                - forward * (1.5f + queueIndex * 0.85f)
                + right * sign * (1.15f + queueIndex * 0.35f);
            return NavMesh.SamplePosition(candidate, out NavMeshHit hit, 3f, NavMesh.AllAreas)
                ? hit.position
                : candidate;
        }

        private bool CanReport(BlacksiteSoldierAI soldier)
        {
            return soldier != null
                && !soldier.isEnemyDead
                && soldier.SquadId == _mission.MissionNetworkId;
        }

        private int LivingMemberCount()
        {
            int count = 0;
            for (int i = 0; i < _members.Count; i++)
            {
                if (_members[i].Soldier != null && !_members[i].Soldier.isEnemyDead)
                    count++;
            }
            return count;
        }

        private BlacksiteSoldierAI FindMemberByNetworkId(ulong networkId)
        {
            if (networkId == 0UL)
                return null;
            for (int i = 0; i < _members.Count; i++)
            {
                BlacksiteSoldierAI soldier = _members[i].Soldier;
                if (soldier != null && GetToken(soldier) == networkId)
                    return soldier;
            }
            return null;
        }

        private void PruneMembers()
        {
            for (int i = _members.Count - 1; i >= 0; i--)
            {
                BlacksiteSoldierAI soldier = _members[i].Soldier;
                if (soldier == null)
                {
                    ReleasePerceptionSlot(_members[i].Slot);
                    _members.RemoveAt(i);
                    continue;
                }
            }
        }

        // #1293 R1: a stable perception slot (0..31) per registered member - the bit it owns in
        // every world row's SightMask and AnnouncedMask. A freed slot is reused; -1 when all are taken.
        private int TakePerceptionSlot()
        {
            for (int slot = 0; slot < BlacksiteWorldRow.SlotCount; slot++)
            {
                uint bit = 1u << slot;
                if ((_perceptionSlots & bit) != 0)
                    continue;
                _perceptionSlots |= bit;
                return slot;
            }
            return -1;
        }

        private void ReleasePerceptionSlot(int slot)
        {
            if ((uint)slot >= BlacksiteWorldRow.SlotCount)
                return;
            _perceptionSlots &= ~(1u << slot);
            World.ReleaseSlot(slot);
        }

        private void CleanupClaims()
        {
            // #1016 S2: the RESERVATION half of this method is deleted. It expired
            // _interactionReservations on this director's own cadence while the column beside it was
            // pruned on the same pass and neither knew what the other had done; a reservation is now
            // a lane holder expired by BlacksiteResourceLane.Sweep at the same 2 s window, from the
            // one broker sweep.
            //
            // #1016 S2 review batch 2, F4: the COLUMN half is NOT deleted, and the claim that the
            // sweep prunes it was wrong. BlacksiteResourceBroker.Tick removes an idle lane only when
            // it is UNREGISTERED, and ColumnFor registers every doorway lane it creates - it has to,
            // or the column facade would be left holding a lane the broker had dropped. So the
            // dictionary this director owns has to be pruned by this director, exactly as it was
            // before S2 and on the same IsIdle threshold, and Unbind is what tells the broker the
            // registration is over with it. Without both halves a squad that uses a dozen doorways
            // accumulates a dozen columns AND a dozen permanent lanes.
            //
            // Destination claims are crowd spacing, not swept here (design note A.3; #1293 R3: event-released).
            _interactionColumnScratch.Clear();
            foreach (KeyValuePair<int, BlacksiteEntranceColumn> entry in _interactionColumns)
                if (entry.Value.IsIdle(Time.time)) _interactionColumnScratch.Add(entry.Key);
            for (int i = 0; i < _interactionColumnScratch.Count; i++)
            {
                if (_interactionColumns.TryGetValue(_interactionColumnScratch[i], out BlacksiteEntranceColumn idle))
                    idle.Unbind();
                _interactionColumns.Remove(_interactionColumnScratch[i]);
            }
            _interactionColumnScratch.Clear();
        }

        /// <summary>
        /// #1016 S2 (design note C.1 mechanism 2): "no claim outlives its holder", in one call.
        ///
        /// This used to walk <c>_interactionReservations</c> and the columns and nothing else, so a
        /// member that died holding the ship aisle, the ship ladder, a world ladder rung, a cabin
        /// lip token or an objective record kept all five until something else happened to notice.
        /// <c>ReleaseAllHeldBy</c> covers every kind, and this method keeps its name because it is
        /// the roster-leave site's own vocabulary.
        /// </summary>
        private void ReleaseInteractionsOwnedBy(ulong token)
        {
            Broker?.ReleaseAllHeldBy(token);
            // A lost member leaves every waiting order it was standing in. Kept beside the broker
            // call because a column can outlive the lane the sweep pruned under it.
            foreach (KeyValuePair<int, BlacksiteEntranceColumn> entry in _interactionColumns)
                entry.Value.Leave(token);
        }

        private static int GetInteractionKey(BlacksiteInteractionKind kind, object target)
        {
            unchecked
            {
                return ((int)kind * 397) ^ RuntimeHelpers.GetHashCode(target);
            }
        }

        private static ulong GetToken(BlacksiteSoldierAI soldier)
        {
            NetworkObject networkObject = soldier != null ? soldier.NetworkObject : null;
            return networkObject != null && networkObject.IsSpawned
                ? networkObject.NetworkObjectId
                : (ulong)(uint)(soldier != null ? soldier.GetInstanceID() : 0);
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }

    }
}
