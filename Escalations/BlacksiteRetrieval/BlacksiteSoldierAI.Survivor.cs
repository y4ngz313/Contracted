using UnityEngine;
using UnityEngine.AI;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        private static float _nextStrandedExitAt;
        private float _nextStrandedExitLog;
        internal static void ResetStrandedExitSpacing() => _nextStrandedExitAt = 0f;
        private bool _transportLost, _defensiveWindow;
        private float _strandedSince, _defendUntil;
        private Vector3 _groundRally;
        // #872: the stranded rally goal is held once resolved, so a stranded member always has a destination.
        private Vector3 _strandedGoal; private bool _hasStrandedGoal, _strandedHoldLogged;
        private readonly BlacksiteEventCooldown _contactCooldown = new BlacksiteEventCooldown();
        private readonly BlacksiteEventCooldown _reactionCooldown = new BlacksiteEventCooldown();
        internal bool HasLostTransport => _transportLost;
        internal bool HasCommittedJobTraversal => _interactionCoordinator != null && _interactionCoordinator.IsCommittedTraversal;
        // #1219 B2: a paired-entrance task that has not yet transferred this member (reserved,
        // queued, approaching, aligning or performing - the phases that still need the doorway).
        // The planner refreshes a member's assignment in place while it is true: a crossing in
        // progress is never re-evaluated.
        internal bool IsCrossingEntrance => _interactionCoordinator != null
            && _interactionCoordinator.HasActiveTask
            && _interactionCoordinator.ActiveKind == BlacksiteInteractionKind.Entrance
            && BlacksiteEntranceInteraction.RequiresDoorway(_interactionCoordinator.ActivePhase);
        // #1293 R2: the planner's stall answer (BlacksiteSquadPlanner.AnswerStalled) withdraws a
        // doorway task that has not yet transferred this member. Nothing else ends one on a replan -
        // the coordinator ticks the active task whatever the new order issues - and the interrupt's
        // Failed outcome releases the reservation to the member queued behind.
        internal void WithdrawEntranceCrossing(string reason)
        {
            if (_interactionCoordinator != null && _interactionCoordinator.IsEntranceTraversalActive
                && BlacksiteEntranceInteraction.RequiresDoorway(_interactionCoordinator.ActivePhase))
                _interactionCoordinator.Interrupt(reason);
        }
        internal bool IsWaitingForTraversal => (_interactionCoordinator != null && _interactionCoordinator.HasActiveTask)
            || (_navigator != null && (_navigator.HasCommittedMotion || _navigator.IsQueuedAtShipLink));

        // #874 S6.1: the traversal milestones. Read-only to everything except the interaction
        // coordinator and the navigator, which write them where they ALREADY report the same
        // events. Nothing here influences a decision this member makes.
        internal int TraversalMilestoneCount { get; private set; }
        internal string LastTraversalMilestone { get; private set; } = "none";
        // A crossing reached a point it can reach at most once: a cabin boarded, a car arrived at
        // the target floor, an entrance threshold cleared, a ladder mounted or dismounted, a ship
        // passage leg completed. Never a queue slot, a staging point, a retry or a new task.
        internal void NoteTraversalMilestone(string milestone)
        {
            TraversalMilestoneCount++;
            LastTraversalMilestone = string.IsNullOrEmpty(milestone) ? "none" : milestone;
        }

        internal void DetachFromTransportServer(Vector3 groundRally)
        {
            if (!IsServerAuthority() || _transportLost || isEnemyDead || _boardedShip) return;
            _transportLost = true; _strandedSince = Time.time; _groundRally = ResolveGroundRally(groundRally);
            _hasStrandedGoal = false; _strandedHoldLogged = false;
            _raidExtracting = false; _extractingToShip = false;
            _squadDirector?.ReleaseDestinationClaim(this); _squadDirector = null;
            _combatController?.ClearFormationSlot();
            if (!HasCommittedJobTraversal) _interactionCoordinator?.Interrupt("transport lost: exit and regroup");
            Navigator.InvalidateDestination(clearPosition: false);
            BlacksiteDiagnostics.Log("Survivor", $"member={MemberIndex}, transport lost, outside={isOutside}, groundRally={_groundRally}; defensive combat remains active.");
            // #1293 R3: a stranded member's whole decision changes (withdrawal to the ground rally).
            RequestDecision(BlacksiteDecisionCause.Outcome);
        }
        // Rallying onto the literal entrance point yields an Entrance smart-path step, and a stranded
        // member has no squad director to reserve it; the queued task then disables its weapon and
        // never completes. Hold the rally back along the ship-to-entrance line.
        private const float GroundRallySetback = 6f;
        private Vector3 ResolveGroundRally(Vector3 entrance)
        {
            Vector3 from = TryGetMission(out var mission) && mission.Objectives.Ship != null
                ? mission.Objectives.Ship.transform.position : transform.position;
            Vector3 direction = Vector3.ProjectOnPlane(entrance - from, Vector3.up);
            if (direction.sqrMagnitude < 1f) direction = Vector3.ProjectOnPlane(entrance - transform.position, Vector3.up);
            if (direction.sqrMagnitude < 1f) return entrance;
            Vector3 rally = entrance - direction.normalized * GroundRallySetback;
            return NavMesh.SamplePosition(rally, out var nav, 4f, NavMesh.AllAreas) ? nav.position : entrance;
        }
        private void RefreshTransportLifecycleServer()
        {
            if (!_initialized || _boardedShip || isEnemyDead) return;
            if (_transportLost)
            {
                // #1293 R1: the ship takes the mission, the pass's driver, with it at confirmed
                // departure, so the members it left drive their squad's pass. The director's own
                // clock runs it once per tick however many call it, the mission included.
                _perceptionDirector?.UpdatePerceptionServer();
                TryReattachTransportServer();
                return;
            }
            if (!TryGetMission(out var mission) || mission.IsTerminal)
                DetachFromTransportServer(_outsideMainEntrance);
        }
        // #872: transport loss is not permanent. A stranded member re-attaches to the squad director
        // whenever the mission is live and the ship is grounded (a departure detach never qualifies:
        // the mission goes terminal at confirmed departure).
        private const float ReattachDelaySeconds = 5f;
        private void TryReattachTransportServer()
        {
            if (Time.time - _strandedSince < ReattachDelaySeconds) return;
            if (!TryGetMission(out var mission, ignoreTransportLoss: true) || mission.IsTerminal
                || mission.Objectives.Ship == null || !mission.Objectives.Ship.IsGrounded || mission.SquadDirector == null) return;
            var ship = mission.Objectives.Ship;
            _transportLost = false; _squadDirector = mission.SquadDirector; _squadDirector.RegisterMember(this);
            Navigator.InvalidateDestination(clearPosition: false);
            _hasStrandedGoal = false; _strandedHoldLogged = false;
            if (_combatController != null) _combatController.DefendingDuringWithdrawal = false;
            BlacksiteDiagnostics.Log("Survivor", $"member={MemberIndex}, transport re-attached: mission live, ship grounded, accessGeneration={ship.AccessGeneration}.");
            RequestDecision(BlacksiteDecisionCause.Outcome);
            if (_agent != null && _agent.enabled && !_agent.isOnNavMesh) BindNavigationInPlace("transport re-attached off navmesh");
        }
        private bool TryDefendDuringWithdrawal()
        {
            if (HasOccupiedCargoHands || _combatController == null || _combatController.AmmunitionExhausted || HasCommittedJobTraversal) return false;
            // #874 G4: the defensive window expired.
            if (_defensiveWindow && Time.time >= _defendUntil)
            {
                _defensiveWindow = false;
                _combatController.DefendingDuringWithdrawal = false;
            }
            // #874 G4, #1293 R3: an active window re-checks range and LOS every decision.
            if (_defensiveWindow)
            {
                if (!_combatController.RevalidateDefensiveWindow(transform.position, 16f))
                {
                    _defensiveWindow = false;
                    _combatController.DefendingDuringWithdrawal = false;
                    return false;
                }
                // #874 G4: re-assert the input. Two writers clear it without closing this window
                // or the target - TryReattachTransportServer and the brain catch-all - and a member
                // executing a defensive window must not read the restraint as lifted. The suspend
                // latch goes with it, so a latch left set by an earlier Combat decision cannot hold
                // DefensiveOnly false for the whole window.
                _combatController.DefendingDuringWithdrawal = true;
                _combatController.CombatSuspendedForThreat = false;
                return true;
            }
            // #874 G4, #1293 R3: target search and LOS, once per decision.
            if (!TryFindCombatTarget(out var player, out var enemy)) return false;
            Vector3 point = player != null ? GetPlayerAimPoint(player) : enemy.transform.position + Vector3.up * 1.1f;
            if (Vector3.Distance(transform.position, point) > 16f
                || !HasCombatLineOfSight(point, player, enemy))
                return false;
            if (!_defensiveWindow) { _defensiveWindow = true; _defendUntil = Time.time + 3f; }
            _combatController.DefendingDuringWithdrawal = true;
            _combatController.CombatSuspendedForThreat = false;
            if (player != null) _combatController.EngagePlayer(player); else _combatController.EngageEnemy(enemy);
            return true;
        }
        // The stranded rally goal always resolves to something: the member's own offset around the
        // rally, the rally itself, the last known good navmesh position, or the raw rally point.
        private Vector3 ResolveStrandedGoal()
        {
            Vector3 offset = Quaternion.Euler(0, MemberIndex * 60f, 0) * Vector3.forward * 4f;
            Vector3 goal = NavMesh.SamplePosition(_groundRally + offset, out var nav, 1.5f, NavMesh.AllAreas) ? nav.position
                : NavMesh.SamplePosition(_groundRally, out nav, 4f, NavMesh.AllAreas) ? nav.position
                : _hasLastGoodNavPosition ? _lastGoodNavPosition : _groundRally;
            _strandedGoal = goal; _hasStrandedGoal = true;
            return goal;
        }
        private bool TryStrandedExitServer()
        {
            if (!IsServerAuthority() || !_transportLost || isOutside || isEnemyDead || _boardedShip
                || HasCommittedJobTraversal || Time.time < _nextStrandedExitAt) return false;
            if (!BlacksiteCombatPolicy.CanBeginEntrance(HorizontalDistance(transform.position, _insideMainEntrance),
                transform.position.y - _insideMainEntrance.y)) return false;
            var entrance = BlacksiteEntranceInteraction.FindBestEntrance(_insideMainEntrance, false, _chosenEntranceId, allowAnyId: true);
            // A fallback doorway must itself be within reach; never cross a remote fire exit.
            if (entrance != null && !BlacksiteCombatPolicy.CanBeginEntrance(
                HorizontalDistance(transform.position, GetEntrancePoint(entrance)),
                transform.position.y - GetEntrancePoint(entrance).y)) return false;
            bool transferred = TransferThroughEntranceServer(entrance, "stranded survivor exit", out string refusal);
            // A transfer may change spaces before reporting a failed agent bind. Still space
            // the next body in that case, so it cannot land on the first one.
            if (transferred || isOutside)
            {
                _nextStrandedExitAt = Time.time + 1.5f;
                _interactionCoordinator?.Interrupt("stranded survivor exited");
                BlacksiteDiagnostics.Log("Survivor", $"member={MemberIndex}, stranded survivor exit: entrance={entrance.entranceId}, strandedFor={Time.time - _strandedSince:0.0}s");
                return true;
            }
            if (Time.time >= _nextStrandedExitLog)
            {
                _nextStrandedExitLog = Time.time + BlacksiteObjectivePolicy.SquadMoveSampleSeconds;
                BlacksiteDiagnostics.Log("Survivor", $"member={MemberIndex}, stranded survivor exit refused: {refusal}");
            }
            return false;
        }

        // Interior exit attempts continue until outside. After two minutes, the outside rally
        // stops re-rolling but remains a held destination when the member is displaced.
        private bool TickStrandedServer()
        {
            if (TryDefendDuringWithdrawal()) return true;
            if (HasCommittedJobTraversal) return false;
            bool exitWindow = Time.time - _strandedSince < 120f;
            if (!isOutside)
            {
                SetDestinationThrottled(_insideMainEntrance, issuer: "order:Survivor");
                TryStrandedExitServer();
            }
            else
            {
                Vector3 goal = exitWindow || !_hasStrandedGoal ? ResolveStrandedGoal() : _strandedGoal;
                if (!exitWindow && !_strandedHoldLogged)
                {
                    _strandedHoldLogged = true;
                    BlacksiteDiagnostics.Log("Survivor", $"member={MemberIndex}, stranded rally held: goal={goal}.");
                }
                if (HorizontalDistance(transform.position, goal) >= 1f) SetDestinationThrottled(goal, issuer: "order:Survivor");
            }
            if (_agent != null && _agent.enabled && !_agent.isOnNavMesh) BindNavigationInPlace("stranded member off navmesh");
            return true;
        }
        internal void ApplySlimeContactServer(BlobAI hazard)
        {
            if (!IsServerAuthority()) return;
            // #1293 R5: the one monster damage path. The native 35 raw per touch accumulates in its
            // remainder at 25 raw per health unit instead of rounding every contact up to a bite.
            // #1305 H (C3): a contact puts the member inside the slime's footprint, so the next
            // perception pass admits its way out (ObserveHazardRowsServer).
            ApplyVanillaMonsterDamage(hazard, 35);
            // The hit has no attacker, so the member turns to the slime that made it.
            FacePoint(hazard.transform.position);
            // #944 S11 U6 (review P12): key and message built before the Enabled check. The force
            // is the [UAT][Hit] line's. #1305 H: its own category; [UAT][Hazard] is the disc transitions only.
            if (BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.LogRateLimited($"slime.hit.{hazard.GetInstanceID()}.{NetworkObjectId}", 1f,
                    "Slime", $"member={MemberIndex}, slime contact, hp={ReplicatedHealth}.");
        }

        /// <summary>
        /// #1305 H (C3): this member's read of the squad's live hazard discs, last in each
        /// PerceiveServer pass - the only place a disc's exit or arrival becomes an event (R2: no
        /// clock, no hold, no lifetime): <see cref="BlacksiteNavigator.BlacksiteMotionOwner.ObserveHazardDiscs"/>.
        /// A disc is a constraint on the job's route, never a behaviour of its own (R1). Withheld only
        /// while a motor owns the body (TraversalForbidsStop, #874 S7.4 M6: a committed task still
        /// walking reads it) or the member is off the navmesh with no route to admit.
        /// </summary>
        internal void ObserveHazardRowsServer(BlacksiteWorldModel world, float now)
        {
            if (world == null || (_interactionCoordinator != null && _interactionCoordinator.TraversalForbidsStop)
                || _agent == null || !_agent.enabled || !_agent.isOnNavMesh || Navigator.BodyWriter != BlacksiteBodyWriter.Agent) return;
            Navigator.Motion.ObserveHazardDiscs(world);
        }
    }
}
