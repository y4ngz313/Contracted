using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        private bool _monsterCaptured, _monsterConsumed;
        private int _monsterDamageRemainder;
        private float _webSlowUntil, _nextFootstepNoiseAt;
        // #1305 H (C4): the reaction the squad's perception pass last computed for this member
        // (ObserveMonsterReactionServer), and the evade's retreat goal with its re-target event:
        // they replace the 0.2 s reaction poll, the 1 s escape clock and the Jester flee window.
        // _evadeFrom: the contact's distance when the goal was picked.
        private EnemyAI _monsterReaction;
        private bool _watchingMonster, _evadeDue, _hasEvadeGoal;
        private Vector3 _evadeGoal;
        private float _evadeFrom;
        internal bool MonsterVisionBlocked => _monsterCaptured || _spawnState.Value.IsStunned;
        // #1305 E4: this member is facing a Gaze row (a Coil-Head) the squad elected it to watch - SoldierReaction's Watch.
        internal bool IsWatchingMonster => _watchingMonster && _monsterReaction != null
            && !_monsterReaction.isEnemyDead && _monsterReaction.isActiveAndEnabled;
        // #1305 E4 (D13): may hold this Coil-Head's watch - sees it along a clear line in range, no facing cone (it turns to it),
        // not carrying the objective (the better thing to do) and not fighting a player (its combat contact; player combat wins).
        internal bool CanWatch(EnemyAI coil, float range) => BlacksiteVanillaMonsters.SoldierSees(this, coil, range, 181f) && !IsObjectiveCarrier && !(_combatController?.CurrentContact is GameNetcodeStuff.PlayerControllerB);
        // #1271 S3a: the one rule for who turns a watcher's body. The watch faces its Coil-Head exactly
        // while combat is not turning the body onto its aim point (FacesAimPoint) and no crossing or
        // committed traversal carries the body; combat's facing wins exactly as it does for a member
        // that is not watching. #1305 H (C3): a member a live hazard disc blocks faces the disc on
        // the same terms, so a watched Coil-Head stays frozen while the member waits.
        internal bool WatchFaces => (IsWatchingMonster || Navigator.Motion.HazardBlock.HasValue) && !Navigator.HasCommittedMotion
            && (_combatController == null || !_combatController.FacesAimPoint);
        // What a facing watch looks at: the watched Coil-Head, else the blocking disc's centre.
        private Vector3 WatchPoint
            => (IsWatchingMonster ? _monsterReaction.transform.position : Navigator.Motion.HazardBlock.CurrentCentre) + Vector3.up * 1.8f;
        // The agent turns the body unless combat owns facing or the watch does.
        internal bool AgentOwnsRotation => (_combatController == null || !_combatController.OwnsFacing) && !WatchFaces;
        [ClientRpc]
        internal void SyncVanillaMonsterClientRpc(NetworkObjectReference reference, byte action)
        {
            // Server applies before the RPC, so a host cannot double-play an animation.
            if (IsServerAuthority()) return;
            if (reference.TryGet(out var obj)) BlacksiteVanillaMonsters.ApplyEvent(obj.GetComponent<EnemyAI>(), this, (BlacksiteVanillaMonsters.Event)action);
        }
        internal void ApplyVanillaMonsterDamage(EnemyAI attacker, int rawDamage)
        {
            if (!IsServerAuthority() || isEnemyDead || HasBoardedShip || rawDamage <= 0) return;
            if (TryGetMission(out var mission)) mission.Objectives.RequestGuardBackup(this, false);
            NotifyAttackedByEnemy(attacker);
            _monsterDamageRemainder += rawDamage;
            int force = _monsterDamageRemainder / 25;
            _monsterDamageRemainder %= 25;
            // #1293 R5: the one attacker-tagged line every monster hit on a soldier prints, host only.
            if (BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.Log("Hit", $"source={(attacker != null ? attacker.GetType().Name : "none")}, member={MemberIndex}, raw={rawDamage}, force={force}.");
            if (force > 0) HitEnemy(force, null, playHitSFX: true);
        }
        internal void BeginMonsterCapture()
        {
            _monsterCaptured = true;
            inSpecialAnimation = true;
            if (IsServerAuthority())
            {
                _interactionCoordinator?.Interrupt("monster capture");
                _combatController?.TickServer(disabled: true);
                DropCarriedDriveServer("monster capture");
            }
            Navigator.BeginMonsterCapture();
            // #874 S2.2: a carried body is where the monster puts it, not a wall players collide
            // with. S2.4: the applier decides that from the resolved state, here and every frame.
            ApplyLifecyclePresentation("monster capture");
        }
        internal void EndMonsterCapture()
        {
            if (!_monsterCaptured) return;
            _monsterCaptured = false; inSpecialAnimation = false;
            // #1262 P4: a consumed body stays where the monster left it, hidden; the floor cast is for a living release.
            if (!_monsterConsumed) Navigator.EndMonsterCapture();
            ApplyLifecyclePresentation("monster capture released");
        }
        internal void HideConsumedSoldier()
        {
            _monsterConsumed = true;
            EnableEnemyMesh(false, overrideDoNotSet: true, tamperWithMeshes: true);
            ApplyLifecyclePresentation("consumed");
        }
        internal void ApplySpiderWebSlow() => _webSlowUntil = Time.time + .3f;
        private void EmitFootstepNoise()
        {
            // #1283 S7a: a rider (stowed, not yet configured, or boarded) takes no steps for monsters to hear.
            if (!IsServerAuthority() || isEnemyDead || _monsterCaptured || !HasReplicatedIdentity
                || EffectiveLifecycle == BlacksiteLifecycleState.Boarded || Time.time < _nextFootstepNoiseAt) return;
            _nextFootstepNoiseAt = Time.time + .25f;
            // #1233 S8: the noise itself is unchanged - same radius, same loudness, same listeners
            // including every vanilla monster. It is only NAMED for the length of the dispatch, so
            // a squadmate's DetectNoise drops a boot instead of reacting to it.
            using (AnnounceSquadNoise(this))
                RoundManager.Instance?.PlayAudibleNoise(transform.position, _serverCrouched ? 5f : 12f, _serverCrouched ? .25f : .5f);
        }
        /// <summary>
        /// #1305 H (C4): the monster reaction, computed once per squad perception pass and stored on
        /// the member - BlacksiteSquadDirector.UpdatePerceptionServer, after every PerceiveServer and (#1305 E4)
        /// the watcher election - or at once when a refused contact hits it (NotifyAttackedByEnemy). The reaction beginning or its
        /// contact changing is the evade's re-target event.
        /// </summary>
        internal void ObserveMonsterReactionServer()
        {
            EnemyAI contact = BlacksiteVanillaMonsters.SoldierReaction(this, out bool watch);
            if (contact != _monsterReaction || watch != _watchingMonster)
            {
                _evadeDue = contact != null && !watch;
                _hasEvadeGoal = false;
                _evadeGoal = Vector3.positiveInfinity;
            }
            // The evade's other re-target events, read on this pass and never on a clock: the goal
            // reached (the navigator's arrival, not a held verdict) or the contact closed in since the
            // goal was picked. A member with no goal (none found, or the same one again) waits for one.
            else if (contact != null && !watch)
                _evadeDue |= (_hasEvadeGoal && Navigator.HasArrived(_evadeGoal))
                    || HorizontalDistance(transform.position, contact.transform.position) < _evadeFrom - BlacksiteArrivalPolicy.MinDestinationChange;
            _monsterReaction = contact;
            _watchingMonster = watch;
        }

        /// <summary>
        /// #1305 H (C4): actuates the stored reaction every frame; nothing here polls. A watch - and
        /// (C3) a member a live disc blocks - only turns the body while <see cref="WatchFaces"/>,
        /// and returns false, so the job and Combat run after it (combat's aim point wins facing).
        /// An evade owns the body: hands free, face on the contact, and the retreat goal re-targeted
        /// on those events and on its goal being refused (an edge: the refused goal is dropped and
        /// one re-target follows; the same point found again is no goal).
        /// </summary>
        private bool TickMonsterReaction()
        {
            if (_interactionCoordinator != null && _interactionCoordinator.IsCommittedTraversal) return false;
            if (_spawnState.Value.IsStunned || stunNormalizedTimer > 0f) return false;
            // A contact left in the world this member just crossed out of is no reaction (SoldierReaction's SameWorld).
            bool reacting = _monsterReaction != null && !_monsterReaction.isEnemyDead && _monsterReaction.isActiveAndEnabled
                && BlacksiteWorld.Same(_monsterReaction, this);
            // Both arms run before the brain; neither the retreat nor the watcher's duty inherits a guard scan.
            if (reacting) Navigator.EndPatrolAlert();
            if (!reacting || _watchingMonster)
            {
                // #1271 S3a (D3): the watch is facing and posture only, and faces only while
                // WatchFaces. Combat and the brain run after it.
                if (WatchFaces) FaceCombatPoint(WatchPoint, 300f);
                return false;
            }
            else
            {
                // #988 F06: the member cannot hurt what it is running from, so it does not attack -
                // but its hands are its own and the weapon in them is not the stand-down mechanism.
                // This arm used to call TickServer(disabled: true), which is the hands-taken tick:
                // t=861.33 in the 2026-09-21 run logged Ready -> Disabled and forced a Ready baton
                // to Stowed, and t=861.54 - 0.21 s later, hands never touched - re-entered Combat
                // owing the whole 1.233 s draw again, with the #929 Equip cue replayed on every
                // peer for it. Capture, death, stun, cargo and the two-handed legs still reach the
                // disabled tick from their own branches, and HandsUnavailable still stows.
                _combatController?.HoldFireWithFreeHands();
                if (_evadeDue)
                {
                    _evadeDue = false;
                    // #1086: the Jester's escape is a WITHDRAWAL, not a seven-metre sidestep, so it
                    // answers before the generic retreat point does; TryEvadeJester refuses every
                    // contact that is not a POPPED Jester. A goal no different from the one just
                    // reached or refused is no new goal.
                    _hasEvadeGoal = (TryEvadeJester(_monsterReaction, out Vector3 goal)
                            || TryFindRetreatPoint(_monsterReaction.transform.position, out goal))
                        && (goal - _evadeGoal).sqrMagnitude > BlacksiteArrivalPolicy.MinDestinationChange * BlacksiteArrivalPolicy.MinDestinationChange;
                    if (_hasEvadeGoal) _evadeGoal = goal;
                    _evadeFrom = HorizontalDistance(transform.position, _monsterReaction.transform.position);
                }
                if (_hasEvadeGoal)
                {
                    BlacksiteMotionOutcome outcome = SetDestinationThrottled(_evadeGoal,
                        issuer: IsFleeingPoppedJester ? "order:JesterEscape" : "order:MonsterRetreat").Outcome;
                    if (outcome == BlacksiteMotionOutcome.Blocked) { _hasEvadeGoal = false; _evadeDue = true; }
                }
                // #1262 P4 (rule 4): a member backing off keeps its face on what it is backing off from.
                // The popped-Jester escape is a run for the door and keeps the agent's own heading.
                bool fleeing = IsFleeingPoppedJester;
                Navigator.SetAutomaticRotation(fleeing);
                if (!fleeing) FaceCombatPoint(_monsterReaction.transform.position + Vector3.up * 1.8f, 300f);
                SelectExclusiveBehaviour(BlacksiteBehaviour.Evade, EvasionReason(_monsterReaction));
            }
            return true;
        }
        // The one thing outside this file that reads the flight: a member running from a popped
        // Jester moves at the urgent role speed. #1305 H (C4): true exactly while that is this
        // member's stored evade indoors - a capture, a stun, a death or the contact being gone end it
        // with the reaction, with no window of its own.
        internal bool IsFleeingPoppedJester => !_watchingMonster && !isOutside && _monsterReaction is JesterAI
            && !_monsterReaction.isEnemyDead && BlacksiteMonsterCapabilities.JesterPopped(_monsterReaction);

        // #1086: what the Evade behaviour is NAMED, so a playtest log distinguishes what this arm
        // does. The generic string is unchanged for every other contact. #1096: the cranking string
        // is gone with the behaviour - SoldierReaction no longer answers a cranking Jester at all,
        // so the only Jester that reaches the Evade behaviour is one that has popped. #874 N8: the
        // Old Bird is named too, so its live acceptance ("Old Bird near raiders") can be read off
        // the [UAT][Behaviour] line rather than inferred from the retreat alone.
        private static string EvasionReason(EnemyAI contact)
            => contact is JesterAI ? "escaping-popped-jester"
                : contact is RadMechAI ? "evading-old-bird"
                : "evading-unstoppable-monster";

        /// <summary>
        /// #874 N8: this member's rifle is mid-burst at this contact. The one soldier fact the
        /// Old Bird warning budget reads beside its own memo
        /// (BlacksiteThreatAdapterRegistry.WarningFireOpen), so the burst that spends the budget
        /// finishes before the evade takes the member.
        /// </summary>
        internal bool IsFiringBurstAt(EnemyAI enemy)
            => _combatController != null && enemy != null
               && WeaponSnapshot.State == BlacksiteWeaponState.FiringBurst
               && ReferenceEquals(_combatController.CurrentEnemyTarget, enemy);

        /// <summary>
        /// #1086, amended by #1096 (WP5): the POPPED Jester arm of the evade, and it is now one
        /// behaviour over one contact. The member leaves through the nearest door at the urgent
        /// role speed.
        ///
        /// The cranking half - back off and hold at 26 m - is deleted. A cranking Jester cannot
        /// touch anybody (vanilla only kills a colliding player, and only from state 2), so the
        /// stand-off spent the whole of the one warning the squad gets standing still. The squad
        /// now keeps working and HURRIES instead; that is
        /// <see cref="BlacksiteVanillaMonsters.CrankingJesterKnown"/> feeding
        /// <c>GetRoleMoveSpeed</c>, not a reaction, so nothing here is spent on it.
        ///
        /// The escape is the EXISTING plumbing: <c>SetDestinationThrottled</c> for the walk and
        /// <c>TryUseBestEntranceTeleport</c> for the door, on the arm's re-target events (#1305 H,
        /// C4). Nothing here writes the agent, so the motion owner and the arrival contract are
        /// untouched. <paramref name="exit"/> is the door the walk is for.
        /// </summary>
        private bool TryEvadeJester(EnemyAI contact, out Vector3 exit)
        {
            exit = transform.position;
            // Outside, "the way out" is where the member already is: no entrance stands between it
            // and the sky, and the generic retreat point is then the better answer. A Jester that
            // has not popped is refused here as well as upstream, so this arm has one meaning even
            // if some later caller reaches it without SoldierReaction's gate.
            if (!(contact is JesterAI) || !BlacksiteMonsterCapabilities.JesterPopped(contact)
                || isOutside || !TryGetMission(out var mission)) return false;
            exit = mission.SquadDirector.InsideMainEntrance;
            int entranceId = 0;
            if (TryFindNearestExitDoor(contact, out var door)) { exit = GetEntrancePoint(door); entranceId = door.entranceId; }
            SetDestinationThrottled(exit, issuer: "order:JesterEscape");
            // The walk is the order above; the doorway is the entrance interaction's own step,
            // taken only once the member is actually at the threshold.
            if (BlacksiteCombatPolicy.CanBeginEntrance(
                    HorizontalDistance(transform.position, exit), transform.position.y - exit.y))
                TryUseBestEntranceTeleport(exit, isEntranceToBuilding: false, entranceId: entranceId, "popped jester escape");
            return true;
        }

        // #1086 (verifier finding 1): is this goal on the far side of the contact? The two facts
        // are the angle between "to the goal" and "to the contact" and which of the two is nearer;
        // BlacksiteCombatPolicy.RetreatGoalBlockedByContact owns the rule so it is provable without
        // a scene. A degenerate leg - the member standing on the goal or inside the contact - is
        // not a direction and is never called blocked.
        private bool GoalLiesBeyondContact(Vector3 goal, Vector3 contact)
        {
            Vector3 toContact = Vector3.ProjectOnPlane(contact - transform.position, Vector3.up);
            Vector3 toGoal = Vector3.ProjectOnPlane(goal - transform.position, Vector3.up);
            if (toContact.sqrMagnitude < .01f || toGoal.sqrMagnitude < .01f) return false;
            return BlacksiteCombatPolicy.RetreatGoalBlockedByContact(
                toGoal.magnitude, toContact.magnitude, Vector3.Angle(toGoal, toContact));
        }

        // The nearest way out of the facility, main entrance or fire exit, whichever this member is
        // actually closer to. BlacksiteEntranceInteraction.FindBestEntrance answers each half from
        // its own round cache: preferred id 0 is the main entrance, preferred id -1 is "any door
        // that is not the main entrance", and both are ordered by distance to the reference.
        //
        // #1086 (verifier finding 1): with two doors to choose from, a nearest door that lies past
        // the Jester loses to the other one - running out through the contact is not an escape. If
        // BOTH are past it, or there is only one, the nearest is kept: a door that exists beats a
        // corridor that does not.
        private bool TryFindNearestExitDoor(EnemyAI contact, out EntranceTeleport door)
        {
            EntranceTeleport main = BlacksiteEntranceInteraction.FindBestEntrance(
                transform.position, isEntranceToBuilding: false, preferredEntranceId: 0, allowAnyId: true);
            EntranceTeleport fire = BlacksiteEntranceInteraction.FindBestEntrance(
                transform.position, isEntranceToBuilding: false, preferredEntranceId: -1, allowAnyId: false);
            EntranceTeleport nearer = main == null ? fire
                : fire == null ? main
                : HorizontalDistance(transform.position, GetEntrancePoint(fire))
                  < HorizontalDistance(transform.position, GetEntrancePoint(main)) ? fire : main;
            EntranceTeleport other = ReferenceEquals(nearer, main) ? fire : main;
            door = nearer != null && other != null && contact != null
                   && GoalLiesBeyondContact(GetEntrancePoint(nearer), contact.transform.position)
                   && !GoalLiesBeyondContact(GetEntrancePoint(other), contact.transform.position)
                ? other : nearer;
            return door != null;
        }

        internal sealed partial class BlacksiteNavigator
        {
            private bool _captureAgentEnabled, _captureUpdatePosition;
            private Vector3 _captureFloor;
            internal void BeginMonsterCapture()
            {
                // #991 round 2 (blocking 3): before the snapshot, so _captureUpdatePosition records
                // ordinary navigation rather than the jump's detached agent. EndMonsterCapture
                // restores exactly what is read on the next line.
                ReleaseJump("captured");
                _captureFloor = _soldier.transform.position;
                _captureAgentEnabled = _agent != null && _agent.enabled;
                // #874 S4: a capture seats the body; nothing may take it from a monster that has it.
                BeginSeat("capture");
                if (_agent == null) return;
                _captureUpdatePosition = _agent.updatePosition;
                // The path wipe is deliberate - the agent is about to be disabled outright.
                if (_agent.enabled && _agent.isOnNavMesh) _motion.ClearPath();
                _agent.updatePosition = false;
                _agent.enabled = false;
            }
            internal void PoseMonsterCapture(Vector3 position, Quaternion rotation, bool alignHead)
            {
                _soldier.transform.rotation = rotation;
                if (alignHead) position -= _soldier.GetThreatLookTransform().position - _soldier.transform.position;
                _soldier.transform.position = position;
            }
            internal void EndMonsterCapture()
            {
                Vector3 current = _soldier.transform.position;
                // The native grab anchor can be several metres above the floor. Restore onto
                // a physical floor first; a nearby NavMesh alone cannot prove a safe landing.
                if (Physics.Raycast(current + Vector3.up * .1f, Vector3.down, out var floor, 12f,
                    BlacksiteVanillaMonsters.WorldMask, QueryTriggerInteraction.Ignore)) _soldier.transform.position = floor.point;
                else _soldier.transform.position = _captureFloor;
                if (_agent != null)
                {
                    _agent.updatePosition = _captureUpdatePosition;
                    if (_captureAgentEnabled && !_soldier.isEnemyDead)
                    {
                        _agent.enabled = true;
                        if (NavMesh.SamplePosition(_soldier.transform.position, out var nav, 1.5f, _agent.areaMask)
                            && Mathf.Abs(nav.position.y - _soldier.transform.position.y) < 1f)
                        {
                            _agent.Warp(nav.position);
                        }
                    }
                }
                EndSeat(_soldier.transform.position);
                if (IsServerAuthority()) _soldier.SyncPositionToClients();
            }
        }
    }
}
