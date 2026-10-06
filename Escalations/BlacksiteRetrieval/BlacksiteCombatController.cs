using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations._Shared;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed class BlacksiteCombatController
    {
        // #947 S12 U2: value unchanged; it lives in BlacksiteCombatPolicy now so the magazine rule
        // (BlacksiteRepositionReason.NoEffect) is derived from it in one Unity-free place.
        private const int MagazineCapacity = BlacksiteCombatPolicy.MagazineCapacity;
        // #1293 R1: also a ship guard's engage range, measured from himself (TryFindCombatTarget).
        internal const float MaximumShotRange = 16f;
        private const float AimSettleSeconds = 0.3f;
        private const float BurstCadenceSeconds = 0.12f;
        private const float SingleRecoverySeconds = 0.55f;
        private const float BurstRecoverySeconds = 0.8f;
        private const float ReloadSeconds = 1.78f;
        private const float MagazineReleaseMinimumSeconds = 0.12f;
        private const float TargetStickSeconds = 3f;
        private const float CoverSearchInterval = 1.45f;
        private const float RepositionCommitSeconds = 1.1f;
        private const float MinimumEngagementDistance = 4.5f;
        // #874 S7.2: the number itself moved to BlacksiteCombatPolicy so BlacksiteCombatPolicy.
        // ThreatRadius can be derived from it in one Unity-free place and the tier-one regression
        // can pin both. The value and every use of it here are unchanged.
        private const float MaximumEngagementDistance = BlacksiteCombatPolicy.MaximumEngagementDistance;
        private const float MinimumRepositionDistance = 1.5f;
        private const float FormationSlotLifetimeSeconds = 1.2f;
        private const float SlotSearchMaximumPathLength = 12f;
        private const float TacticalSearchMaximumPathLength = 8f;
        private const float PlayerKillConfirmSeconds = 2.5f;
        private const float SprintSpreadMultiplier = 2.5f;
        private const float SuppressionRetrySeconds = 0.25f;
        // #874 S10.3: a remembered point is a suppression target only while it still describes a
        // contact rather than the geometry behind one. Run 3 member 4 turned, aimed, played the fire
        // animation and aborted at the trigger three times in 2.5 s against one point,
        // (25.79, -217.21, 65.76) at t=1010.11 / 1011.66 / 1012.65, whose floor is y -219.59: the
        // point was the player's own camera 2.38 m up, left behind when he walked out of it.
        // One second is how long such a point stays defensible. A vanilla walk is 4.6 m/s, so a
        // contact unseen for one second is anywhere inside 4.6 m of the point - about one Vow
        // corridor width - and beyond that the round is aimed at whatever the corridor is made of.
        // How long the TARGET is retained is something else: the squad table's row
        // (BlacksiteWorldModel.MemorySeconds), not how long its last observed point may be fired at.
        private const float SuppressionStaleSeconds = 1f;
        // #874 S10.3: a shot the muzzle re-check cancelled for sight is itself evidence that the
        // point is already wrong, so the next attempt waits six SuppressionRetrySeconds intervals
        // (6 x 0.25 = 1.5 s) rather than one, so one cancelled shot costs one whole combat decision
        // instead of buying two more re-aims at the same wall inside 2.5 s.
        private const float SuppressionCancelDeferSeconds = 1.5f;

        private static readonly RaycastHitDistanceComparer HitComparer = new RaycastHitDistanceComparer();

        private readonly BlacksiteSoldierAI _soldier;
        private readonly RaycastHit[] _rayHits = new RaycastHit[32];
        private readonly RaycastHit[] _laneHits = new RaycastHit[24];
        private readonly List<BlacksiteSoldierAI> _squadScratch = new List<BlacksiteSoldierAI>();
        private readonly NavMeshPath _coverPath = new NavMeshPath();

        private PlayerControllerB _playerTarget;
        private EnemyAI _enemyTarget;
        private PlayerControllerB _immediatePlayerAttacker;
        private EnemyAI _immediateAttacker;
        // #1293 R1: WHICH squad-table row this member engages (the contact's NetworkObjectId),
        // written on a new selection and read only while _acquisition holds that identity.
        private ulong _targetKey;
        private float _targetStickyUntil;
        private float _aimSettleUntil;
        // #1293 R3: when the last acquisition ramp completes (one of the Combat wake's deadlines).
        private float _acquisitionCompletesAt;
        private float _nextShotAt;
        private float _stateEndsAt;
        private float _nextCoverSearchAt;
        private float _repositionCommittedUntil;
        private float _immediateAttackerUntil;
        private float _reloadStartedAt;
        private float _reloadInsertAt;
        private readonly BlacksiteCombatAcquisition _acquisition = new BlacksiteCombatAcquisition();
        private BlacksiteFireReadiness _lastFireReadiness;
        // #1019 S5: the eight-value collapse of the value beside it, stored where the value is
        // stored and for the same reason - one place holds what the last decision WAS.
        private BlacksiteEngagementOutcome _lastEngagementOutcome;
        internal BlacksiteEngagementOutcome LastEngagementOutcome => _lastEngagementOutcome;
        internal BlacksiteFireReadiness LastFireReadiness => _lastFireReadiness;
        private float _nextDecisionTraceAt;
        private float _decisionSince;
        private bool _hasDecisionTrace;
        private object _lastTraceContact;
        private Vector3 _formationSlot;
        private float _formationSlotSetAt = float.NegativeInfinity;
        private PlayerControllerB _pendingKillCheckPlayer;
        private float _pendingKillCheckUntil;
        private int _pendingKillCheckHealth;
        private float _nextSuppressionAttemptAt;
        private float _laneBlockedSince = float.NegativeInfinity;
        private Vector3 _repositionDestination;
        private PlayerControllerB _lastPlayerContact;
        private BlacksiteRepositionReason _lastMovementReason;
        // #1219 F2: when the next in-band lateral step is due (StrafeIntervalSeconds after the last
        // move ended, or after a new selection), and when [CombatMovement] may next repeat itself.
        private float _nextStrafeAt;
        private float _nextMovementTraceAt;
        private const float MovementTraceSeconds = 1f;
        private bool _lastMovementMoving;
        private int _shotsRemaining;
        private int _burstSize;
        private bool _magazineReleased;
        private bool _initialized;
        // #947 S12 U2: rounds this member has fired since one last reached a live target. Reset by
        // an Enemy or Player impact, by a target change, by ClearTarget and by the reposition the
        // magazine rule itself commits. Never a clock: a whole magazine is a count of rounds, and a
        // member that is reloading, held or out of contact is not firing any.
        private int _roundsWithoutEffect;
        // The last shot this member resolved, for impact= on [UAT][CombatDecision]. None until he
        // has fired at all, which is exactly what the log should say.
        private BlacksiteShotImpactKind _lastImpactKind = BlacksiteShotImpactKind.None;

        internal BlacksiteCombatController(BlacksiteSoldierAI soldier)
        {
            _soldier = soldier ?? throw new ArgumentNullException(nameof(soldier));
        }

        // #874 S2.4: a boarded, captured, dead or consumed actor has no combat intent whatever it
        // still remembers, so it can no longer trace a Ready weapon over a retained contact.
        internal bool HasCombatIntent => _soldier.AllowsCombatLifecycle
            && (_soldier.BatonCommitted || HasValidTarget()
            || TryGetTargetRow(out _)
            || CurrentState == BlacksiteWeaponState.FiringBurst
            || CurrentState == BlacksiteWeaponState.GrenadeCommitted
            || CurrentState == BlacksiteWeaponState.Reloading
            || CurrentState == BlacksiteWeaponState.Recovering);

        // #1293 R1: the engaged contact's row in the squad table is this member's whole memory of
        // it: present = remembered (the table's one decay, BlacksiteWorldModel.MemorySeconds),
        // Position = where it was last known, his sight bit = whether his own eyes have it.
        // Only a Player or Monster row is a combat contact: a contact the hazard pass re-classes
        // (a sapsucker gone cold) becomes a Live row that never decays and must not hold the
        // engagement for good. Server only: no table exists on a client.
        private bool TryGetTargetRow(out BlacksiteWorldRow row)
        {
            row = null;
            return _acquisition.Target != null
                && _soldier.TryGetWorld(out BlacksiteWorldModel world)
                && world.TryGet(_targetKey, out row)
                && (row.Class == BlacksiteWorldClass.Player || row.Class == BlacksiteWorldClass.Monster);
        }

        // The perception pass's own-sight bit for this member on that row, with the pass's
        // BlacksiteSightPolicy.VisualGraceSeconds hysteresis inside it.
        private bool InOwnSight => TryGetTargetRow(out BlacksiteWorldRow row) && row.SeenByMember(_soldier.PerceptionSlot);

        // #1293 R1: BlacksiteSoldierAI.IsCombatEngaged - the engaged row is still remembered, or
        // something hit this member inside the immediate-attacker window.
        internal bool HoldsTargetRow => TryGetTargetRow(out _) || IsImmediateAttackerContact();

        // #1293 R3: whether the table row keyed <paramref name="key"/> is the contact this member engages.
        internal bool IsEngagedRow(ulong key) => _acquisition.Target != null && _targetKey == key;

        // #988 B1: the live enemy this controller has selected, read-only. The baton sweep prefers
        // it when several candidates share one swept capsule, so a bystander cannot take a swing
        // that was aimed at the monster.
        internal EnemyAI CurrentEnemyTarget => _enemyTarget;

        // #1293 R3: the live contact this controller has selected, player or enemy, or null.
        internal Component CurrentContact => IsValidPlayer(_playerTarget) ? _playerTarget
            : IsValidEnemy(_enemyTarget) ? _enemyTarget : (Component)null;

        /// <summary>#1293 R3: the baton's per-frame half: the held target's engagement, no acquisition.</summary>
        internal void ContinueBatonEngagement()
        {
            if (!_soldier.UsesBaton || !HasValidTarget()) return;
            EngageSelectedTarget();
        }

        /// <summary>#1293 R3: the brain's Combat wake - the earliest combat deadline still ahead of
        /// <paramref name="now"/>, or 0 (weapon readiness is an Outcome from SetState instead).</summary>
        internal float NextDeadline(float now)
        {
            float next = 0f;
            next = EarliestAfter(next, _repositionCommittedUntil, now);
            next = EarliestAfter(next, _nextStrafeAt, now);
            next = EarliestAfter(next, _nextCoverSearchAt, now);
            next = EarliestAfter(next, _aimSettleUntil, now);
            next = EarliestAfter(next, _acquisitionCompletesAt, now);
            return next;
        }

        private static float EarliestAfter(float next, float deadline, float now)
            => deadline > now && (next <= 0f || deadline < next) ? deadline : next;

        // #874 G4: derived from inputs set by TryDefendDuringWithdrawal, the Brain's Combat arm
        // and the explicit clears — never assigned directly. One owner, zero per-frame writes.
        internal bool DefensiveOnly => DefendingDuringWithdrawal && !CombatSuspendedForThreat;
        /// <summary>Set by TryDefendDuringWithdrawal on target acquisition; cleared by withdrawal end,
        /// transport re-attach, passage end and HoldFireForTravel.</summary>
        internal bool DefendingDuringWithdrawal;
        /// <summary>Set by the Brain's Combat arm when it overrides a withdrawal defence under
        /// threat; cleared by the Withdrawal arm when Combat yields.</summary>
        internal bool CombatSuspendedForThreat;

        internal void HoldFireForTravel()
        {
            DefendingDuringWithdrawal = false; ClearTarget();
            EndReposition();
            if (_soldier.UsesBaton) _soldier.ReleaseBatonApproach();
            if (CurrentState == BlacksiteWeaponState.FiringBurst || CurrentState == BlacksiteWeaponState.GrenadeCommitted)
            { CancelCommittedAction(); SetState(BlacksiteWeaponState.Ready); }
        }

        /// <summary>
        /// #988 F06: "this member may not attack right now", and only that.
        ///
        /// The stand-down used to collapse three different facts into one boolean. They are
        /// distinct and each has its own owner:
        ///  1. hands or body physically taken - cargo, a two-handed leg, a capture, a stun,
        ///     death - is <see cref="TickServer"/>'s <c>disabled</c>, and it stows;
        ///  2. no worthwhile target is the ordinary tick's own business: no target, no aim
        ///     point, and the baton's four-second idle stow in <c>TickBatonServer</c>;
        ///  3. THIS one: the contact is right there and cannot be fought - an invulnerable
        ///     monster being evaded - while the member's hands are still its own.
        ///
        /// The 2026-09-21 run, round 3, member 0: the evade arm called
        /// <c>TickServer(disabled: true)</c>, so t=861.33 logged <c>Ready -&gt; Disabled</c> and
        /// forced the baton to <c>Stowed</c>; 0.21 s later t=861.54 re-entered Combat with
        /// <c>baton=Stowed, batonElapsed=0.21s</c> and owed the whole 1.233 s draw again. Every
        /// such switch also re-sequenced the replicated snapshot, so the #929 Equip cue played
        /// once per restart on every peer.
        ///
        /// What stops here is the ATTACK - the live target, the close-in, the combat facing and
        /// a committed burst or grenade - and it stops for THIS tick. Equipment work in flight is
        /// not an attack: a reload or a recovery keeps its own clock, because cancelling it is
        /// what re-presented a suppressed rifleman's magazine and restarted the reload he was
        /// owed. The ordinary tick then runs with <c>mayAttack: false</c>, so the weapon phases,
        /// those clocks, the replicated snapshot and the baton's alert draw keep the single owner
        /// they already have instead of being mirrored by a second stand-down state machine.
        ///
        /// What does NOT stop is what this member KNOWS. An earlier form of this method called
        /// <see cref="ClearTarget"/>, which drops the engaged row as well as the selection: on the
        /// same 0.21 s oscillation that cost the baton its draw, a rifleman then lost his memory
        /// of the contact on every hazard frame, and with it <see cref="HasCombatIntent"/> - which
        /// the baton branch below reads before it releases a close-in, and which the navigation
        /// recovery and the facing owner read too. The memory is the squad table's row (#1293 R1)
        /// and is dropped by the things that actually make it wrong, and by nothing else: the
        /// contact becoming ineligible and the row's own expiry - both of which run inside the
        /// tick below, on this tick as on any other - and a new selection.
        /// </summary>
        internal void HoldFireWithFreeHands()
        {
            ReleaseEngagement();
            EndReposition();
            if (CurrentState == BlacksiteWeaponState.FiringBurst || CurrentState == BlacksiteWeaponState.GrenadeCommitted)
            { CancelCommittedAction(); SetState(BlacksiteWeaponState.Ready); }
            TickWeaponServer(disabled: false, mayAttack: false);
        }
        // #1219 F2: a move keeps the combat facing while the contact is in sight - the member walks
        // his step looking at what he is shooting at, which is what lets the fire gate release on
        // the move. A blind move and the reload move still face their travel, as before.
        internal bool OwnsFacing => HasCombatIntent && (Time.time >= _repositionCommittedUntil || MoveFacesContact);
        private bool MoveFacesContact => CurrentState != BlacksiteWeaponState.Reloading && InOwnSight;
        // #1271 S3a: whether the combat tick turns the body onto its aim point (TickWeaponServer, while
        // the member may attack): a burst or a committed grenade, standing, or a move that keeps its
        // contact in sight. A blind move and the reload move leave the body to whoever else turns it.
        internal bool FacesAimPoint => FacesAimPointAt(GetCurrentAimPoint());
        private bool FacesAimPointAt(Vector3 aimPoint) => aimPoint != Vector3.zero
            && (CurrentState == BlacksiteWeaponState.FiringBurst
                || CurrentState == BlacksiteWeaponState.GrenadeCommitted
                || Time.time >= _repositionCommittedUntil || MoveFacesContact);

        // #1219 F2: the one moment the rifle slows the body - a burst actually firing. The motion
        // owner caps the agent at BlacksiteCombatPolicy.FiringWalkSpeed while this holds.
        internal bool SlowsForFiring => !_soldier.UsesBaton && CurrentState == BlacksiteWeaponState.FiringBurst;

        internal void InitializeServer()
        {
            if (_initialized)
                return;

            _initialized = true;
            BlacksiteWeaponSnapshot snapshot = _soldier.WeaponSnapshot;
            if (snapshot.Sequence == 0)
            {
                _soldier.SetWeaponSnapshotServer(BlacksiteWeaponSnapshot.CreateReady(MagazineCapacity, 0));
            }
        }

        internal void ConfigureServerLoadout()
        {
            _initialized = true;
            _soldier.SetWeaponSnapshotServer(BlacksiteWeaponSnapshot.CreateReady(MagazineCapacity, 0));
        }

        // #314: a soldier in contact still fights from his slot in the squad's formation. The
        // caller supplies the destination of his awareness-bound member order -- never the raw
        // squad order -- and it becomes the centre of the tactical position search.
        internal void SetFormationSlot(Vector3 destination)
        {
            _formationSlot = destination;
            _formationSlotSetAt = Time.time;
        }

        internal void ClearFormationSlot()
        {
            _formationSlotSetAt = float.NegativeInfinity;
        }

        private bool HasLiveFormationSlot =>
            Time.time - _formationSlotSetAt <= FormationSlotLifetimeSeconds;

        // Single source for where a round leaves the rifle. The suppression lane check and the
        // authoritative shot trace must start from the identical point or the friendly-lane veto
        // stops describing the shot it is vetoing.
        private Vector3 CombatShotOrigin => _soldier.CombatMuzzlePosition;

        /// <summary>
        /// #988 F06: <paramref name="disabled"/> is ONE fact - something other than this member's
        /// own steering owns its hands or its body. Every caller means exactly that:
        /// <c>BlacksiteSoldierAI.ResolveCombatDisabledServer</c> computes it from the hand owner
        /// and the lifecycle, and the direct <c>true</c> sites are death, a monster capture, a
        /// world ladder, a jump arc, a rappel, a stun and the despawn. A member that simply may
        /// not attack right now keeps its hands and goes through
        /// <see cref="HoldFireWithFreeHands"/> instead; a member with nothing worth shooting at
        /// falls out of the ordinary path below.
        /// </summary>
        internal void TickServer(bool disabled) => TickWeaponServer(disabled, mayAttack: true);

        // #988 F06 (B3): <paramref name="mayAttack"/> is false on exactly one path -
        // <see cref="HoldFireWithFreeHands"/> - and it suppresses the three things that ARE an
        // attack: the combat facing, the close-in and the suppression-fire arm at the end. It
        // stores nothing and starts no clock, so there is still one weapon state machine and one
        // set of readiness clocks; everything else on this tick - the weapon phases, the reload
        // and recovery deadlines, the replicated snapshot, the eligibility wipe and the ordinary
        // memory expiry - runs exactly as it does on any other tick.
        //
        // Deliberately not an OVERLOAD of TickServer: the melee and arbitration fixtures reach
        // this tick by name through AccessTools.Method(type, "TickServer"), and a second method
        // of that name makes that lookup ambiguous rather than merely different.
        private void TickWeaponServer(bool disabled, bool mayAttack)
        {
            InitializeServer();
            // #1271 S8: before the baton branch, which returns - a baton blow arms the same
            // player-kill confirm the rifle does (WatchPlayerKill).
            TickPendingKillReport();
            if (_soldier.UsesBaton)
            {
                // #874 S2.1: death, stun and hands taken by cargo end the close-in as well as the
                // swing; a disabled actor may not keep a melee stop distance on the agent.
                // #988 F06 (B3): so does a member that may not attack this tick, whatever it
                // still remembers - the memory is kept, the reach contract is not.
                if (disabled || !mayAttack || !HasCombatIntent) _soldier.ReleaseBatonApproach();
                else
                {
                    var aim = GetCurrentAimPoint();
                    if (aim != Vector3.zero) _soldier.FaceCombatPoint(aim, BlacksiteBatonPolicy.TurnDegreesPerSecond);
                }
                _soldier.TickBatonServer(handsUnavailable: disabled); SetState(disabled ? BlacksiteWeaponState.Disabled : BlacksiteWeaponState.Ready); return;
            }
            if (_lastPlayerContact != null && !IsValidPlayer(_lastPlayerContact))
            {
                // Eligibility changes invalidate the remembered location as well as the live target.
                ClearTarget();
                _immediatePlayerAttacker = null;
                if (CurrentState == BlacksiteWeaponState.FiringBurst
                    || CurrentState == BlacksiteWeaponState.GrenadeCommitted)
                {
                    CancelCommittedAction();
                    SetState(BlacksiteWeaponState.Ready);
                }
            }

            if (disabled)
            {
                EndReposition();
                CancelCommittedAction();
                SetState(BlacksiteWeaponState.Disabled);
                TraceDecision(BlacksiteFireReadiness.Disabled);
                return;
            }

            if (CurrentState == BlacksiteWeaponState.Disabled)
            {
                SetState(BlacksiteWeaponState.Ready);
                if (Magazine <= 0)
                    BeginReload();
            }

            if (!HasValidTarget() && !TryGetTargetRow(out _))
                ClearTarget();

            // #988 F06 (B3): turning the body onto the contact is part of the attack, and while
            // the hold lasts its caller owns the facing - the evade travels. The remembered point
            // survives; only this tick's use of it stops.
            if (mayAttack)
            {
                Vector3 aimPoint = GetCurrentAimPoint();
                if (FacesAimPointAt(aimPoint))
                {
                    _soldier.FaceCombatPoint(aimPoint, 240f);
                }
            }

            switch (CurrentState)
            {
                case BlacksiteWeaponState.FiringBurst:
                    // Give the first authored event a chance to release the shot. Later rounds
                    // follow the same host clock; the event guard prevents duplicate releases.
                    if (BlacksiteCombatPolicy.CanReleaseScheduledShot(Time.time, _nextShotAt, _shotsRemaining == _burstSize, false))
                        FireShotEvent();
                    break;
                case BlacksiteWeaponState.Recovering:
                    if (Time.time >= _stateEndsAt)
                        CompleteRecovery();
                    break;
                case BlacksiteWeaponState.Reloading:
                    if (Time.time >= _stateEndsAt)
                        CompleteReload();
                    break;

            }

            // #988 F06 (B3): the one arm that opens fire on a remembered point rather than on a
            // live target, and therefore the one the hold has to be visible to. Refused for this
            // tick only: the row and the retry throttle are both left where they are,
            // so the first ordinary tick after the hazard is an ordinary suppression decision on
            // the same contact rather than a re-acquisition from nothing.
            if (mayAttack
                && CurrentState == BlacksiteWeaponState.Ready
                && !HasValidTarget()
                && !IsRepositionActive
                && TryGetTargetRow(out _))
            {
                BeginSuppressionFire();
            }
        }

        internal void EngagePlayer(PlayerControllerB target)
        {
            if (!IsValidPlayer(target))
                return;

            SelectTarget(target, null);
            EngageSelectedTarget();
        }

        internal void EngageEnemy(EnemyAI target)
        {
            if (!IsValidEnemy(target))
                return;

            SelectTarget(null, target);
            EngageSelectedTarget();
        }

        internal void NotifyImmediateAttacker(EnemyAI attacker)
        {
            if (!IsValidEnemy(attacker))
                return;

            _immediatePlayerAttacker = null;
            _immediateAttacker = attacker;
            _immediateAttackerUntil = Time.time + 3f;
            SelectTarget(null, attacker, forcePriority: true);
        }

        internal void NotifyImmediateAttacker(PlayerControllerB attacker)
        {
            if (!IsValidPlayer(attacker))
                return;
            _immediateAttacker = null;
            _immediatePlayerAttacker = attacker;
            _immediateAttackerUntil = Time.time + 3f;
            SelectTarget(attacker, null, forcePriority: true);
        }

        internal bool ContinueWithoutContact()
        {
            if (_soldier.UsesBaton) { ClearTarget(); return _soldier.BatonCommitted; }
            // Keep only the row key while out of sight. The identity in _acquisition is retained
            // with it so a brief peek does not restart the ramp.
            _playerTarget = null;
            _enemyTarget = null;
            UpdateAcquisitionRamp(false);
            bool memory = TryGetTargetRow(out BlacksiteWorldRow row);
            // #874 S4.0b: acting on a retained point rather than a live contact is its own contact
            // source. Re-reported every think, so the registration collapses to one line per
            // remembered contact, not one per tick.
            if (memory && BlacksiteDiagnostics.Enabled)
                BlacksitePerceptionDiagnostics.NoteContact(
                    _soldier.MemberIndex, _soldier.NetworkObjectId,
                    _acquisition.Target is PlayerControllerB rememberedPlayer
                        ? BlacksiteDiagnosticFormat.Target(rememberedPlayer)
                        : BlacksiteDiagnosticFormat.Target(_acquisition.Target as EnemyAI),
                    BlacksitePerceptionDiagnostics.SourceMemory,
                    Vector3.Distance(_soldier.transform.position, row.Position), exact: false);
            bool repositioning = IsRepositionActive;
            var action = BlacksiteCombatPolicy.NoContactAction(memory,
                CurrentState == BlacksiteWeaponState.FiringBurst || CurrentState == BlacksiteWeaponState.GrenadeCommitted,
                repositioning, CurrentState == BlacksiteWeaponState.Reloading,
                CurrentState == BlacksiteWeaponState.Recovering, DefensiveOnly,
                memory && row.SeenByMember(_soldier.PerceptionSlot));
            if (!memory) ClearTarget();

            switch (action)
            {
                case BlacksiteNoContactAction.ResumeDuty:
                    EndReposition();
                    TraceDecision(BlacksiteFireReadiness.NoTarget);
                    return false;
                case BlacksiteNoContactAction.ContinueReposition:
                    TraceDecision(BlacksiteFireReadiness.Repositioning);
                    return true;
                case BlacksiteNoContactAction.Search:
                    Vector3 search = SearchPoint(row);
                    // #1293 R1: a ship guard does not walk off his post after a lost contact; he
                    // holds and watches it instead (OffGuardPost).
                    if (OffGuardPost(search, row.Outside)) break;
                    if (UpdateTacticalMovement(search,
                        Vector3.Distance(_soldier.transform.position, search), false, out bool searchBlocked))
                    {
                        TraceDecision(BlacksiteFireReadiness.Repositioning);
                        return true;
                    }
                    // #1271 S3a: nowhere to search from is no reason to stand; the duty takes the think.
                    if (searchBlocked)
                    {
                        TraceDecision(BlacksiteFireReadiness.ApproachBlocked);
                        return false;
                    }
                    break;
            }

            // Holds are explicit outcomes, never a blanket response to combat memory. In
            // particular, this cannot wipe the destination chosen by BeginReloadReposition.
            _soldier.HoldForCombatAction(.2f);
            TraceDecision(CurrentState == BlacksiteWeaponState.Reloading ? BlacksiteFireReadiness.Reloading
                : CurrentState == BlacksiteWeaponState.Recovering ? BlacksiteFireReadiness.Recovering
                : CurrentState == BlacksiteWeaponState.FiringBurst ? BlacksiteFireReadiness.Firing
                : CurrentState == BlacksiteWeaponState.GrenadeCommitted ? BlacksiteFireReadiness.GrenadeCommitted
                : BlacksiteFireReadiness.LostSight);
            return true;
        }

        private bool IsRepositionActive => Time.time < _repositionCommittedUntil
            && HorizontalDistance(_soldier.transform.position, _repositionDestination) > BlacksiteCombatPolicy.RepositionArrivalRadius;

        private void TraceDecision(BlacksiteFireReadiness reason)
        {
            object contact = _acquisition.Target;
            bool changed = !_hasDecisionTrace || reason != _lastFireReadiness || !ReferenceEquals(contact, _lastTraceContact);
            _lastTraceContact = contact;
            if (changed) _decisionSince = Time.time;
            _hasDecisionTrace = true;
            _lastFireReadiness = reason;
            _lastEngagementOutcome = BlacksiteCombatPolicy.Outcome(reason);
            if (!BlacksiteDiagnostics.Enabled || (!changed && Time.time < _nextDecisionTraceAt)) return;
            _nextDecisionTraceAt = Time.time + 2f;
            var contactPlayer = contact as PlayerControllerB;
            var contactEnemy = contact as EnemyAI;
            string target = contactPlayer != null ? $"player:{contactPlayer.actualClientId}"
                : contactEnemy != null ? $"enemy:{contactEnemy.NetworkObjectId}" : "none";
            // #874 S2.1: a melee actor's evidence is its own. Rifle unseen/magazine/weapon-state
            // fields describe nothing about a baton close-in and must not stand in for it, so the
            // [UAT][Melee] line below keeps carrying the baton evidence.
            // #874 S4.0b: but a baton member also emits [UAT][CombatDecision] at the same cadence,
            // with weapon=Baton|Rifle, so the two weapon classes are finally comparable. The rifle
            // line's old weapon= key held the WEAPON STATE and is now weaponState=.
            // #947 S12 U2: aimPoint=, sightPoint= and impact= appended at the END of the line, after
            // every key the score script already parses. aimPoint is where the round is going,
            // sightPoint the body sample that actually cleared, and impact the last shot this member
            // resolved. Run 2 needed a [UAT][WeaponState] cross-reference to see that the first two
            // were 4 m apart; now one line says it. Computed inside the interpolation, which the
            // change/window gate above has already decided to emit, so a think that is not logged
            // pays nothing.
            string points = $"aimPoint={BlacksiteDiagnosticFormat.V(GetCurrentAimPoint())}"
                + $", sightPoint={BlacksiteDiagnosticFormat.V(_soldier.LastCombatSightPoint)}"
                + $", impact={_lastImpactKind}";
            // #1011 A3: appended at the very end, after every key score.ps1 and the regressions
            // already parse, and only while the hold is actually operative. Nothing is renamed: a
            // rifleman holding on a captor still reports reason=FriendlyLane - the lane IS blocked
            // by a friendly, by the strictest reading there is - and this names which friendly and
            // why the lane cast could not see it.
            // #874 N7: operative means no line clears the held squadmate; a member shooting the
            // captor along one that does is not holding and says nothing here.
            if (IsAllyAttachedTarget() && !_hasCaptorShot) points += ", holdFire=ally-attached";
            // #1019 S5: three keys, APPENDED at the very end - after every key score.ps1 and the
            // regressions already parse, and after holdFire= when it is present, which is the same
            // convention #947 S12 U2 and #1011 A3 already added to this line twice. Nothing is
            // renamed, reordered or inserted mid-line, and no new area or event= value exists.
            //
            // outcome= is the eight-value collapse of reason=, which keeps its 28-value vocabulary;
            // profile= and intent= are what the threat table decided about this contact, or none
            // and Engage when there is no live contact to decide about. All three are enum
            // ToString()s computed INSIDE this interpolation, which the change/window gate above
            // has already decided to emit, so a think that is not logged pays nothing.
            // #1019 S5: the diagnostic REPORTS the decision; it does not take one of its own. An
            // earlier form described the contact here against a neutral squad context, which could
            // only ever answer Engage or Ignore - so intent=Rescue was unreachable and the scorer's
            // rescue pairing was dead. ResolveThreatIntent is the one producer, the one decision
            // core calls it, and it records what it decided and what it measured the contact as.
            string profile = _soldier.LastThreatProfile;
            var tracedIntent = _soldier.LastThreatIntent;
            // #1019 S5: the three keys are built once here and appended to BOTH the Melee line and
            // the CombatDecision line, so the two stay comparable. holdFire= is deliberately NOT on
            // the Melee line: it was never there, it is the rifle's refusal, and this stage renames
            // and reorders nothing.
            string tail = $"outcome={BlacksiteCombatPolicy.Outcome(reason)}, profile={profile}, intent={tracedIntent}";
            points += ", " + tail;
            // #1293 R1: seconds since THIS member's own eyes last had the engaged row; Infinity
            // when they never did or no row is held.
            float unseen = TryGetTargetRow(out BlacksiteWorldRow row)
                ? Time.time - row.MemberSawAt(_soldier.PerceptionSlot) : float.PositiveInfinity;
            if (_soldier.UsesBaton)
            {
                BlacksiteDiagnostics.Log("Melee",
                    $"member={_soldier.MemberIndex}, reason={reason}, heldFor={Time.time - _decisionSince:0.00}s, target={target}, liveContact={_playerTarget != null || _enemyTarget != null}, {_soldier.MeleeDecisionDetails}, {tail}");
                BlacksiteDiagnostics.Log("CombatDecision",
                    $"member={_soldier.MemberIndex}, weapon=Baton, reason={reason}, heldFor={Time.time - _decisionSince:0.00}s, target={target}, unseen={unseen:0.00}s, weaponState=none, magazine=none, liveContact={_playerTarget != null || _enemyTarget != null}, reposition={IsRepositionActive}, moveGoal={_repositionDestination}, {_soldier.MeleeDecisionDetails}, {points}");
                return;
            }
            BlacksiteDiagnostics.Log("CombatDecision",
                $"member={_soldier.MemberIndex}, weapon=Rifle, reason={reason}, heldFor={Time.time - _decisionSince:0.00}s, target={target}, unseen={unseen:0.00}s, weaponState={CurrentState}, magazine={Magazine}, liveContact={_playerTarget != null || _enemyTarget != null}, reposition={IsRepositionActive}, moveGoal={_repositionDestination}, {_soldier.CombatDecisionDetails}, {points}");
        }

        internal void NotifyShotAnimationEvent()
        {
            if (CurrentState == BlacksiteWeaponState.FiringBurst
                && BlacksiteCombatPolicy.CanReleaseScheduledShot(Time.time, _nextShotAt, _shotsRemaining == _burstSize, true))
            {
                FireShotEvent();
            }
        }



        internal void NotifyMagazineReleaseAnimationEvent()
        {
            if (CurrentState != BlacksiteWeaponState.Reloading
                || Time.time < _reloadStartedAt + MagazineReleaseMinimumSeconds)
            {
                return;
            }
            _magazineReleased = true;
        }

        internal void NotifyMagazineInsertAnimationEvent()
        {
            if (CurrentState != BlacksiteWeaponState.Reloading
                || Time.time < _reloadInsertAt - .025f)
            {
                return;
            }
            if (!_magazineReleased && Time.time < _reloadStartedAt + ReloadSeconds * 0.7f)
                return;
            CompleteReload();
        }

        internal void NotifyActionCompleteAnimationEvent()
        {
            if (CurrentState == BlacksiteWeaponState.Reloading
                && Time.time >= _reloadInsertAt)
            {
                CompleteReload();
                return;
            }
            if (CurrentState == BlacksiteWeaponState.Recovering
                && Time.time >= _stateEndsAt - 0.25f)
            {
                CompleteRecovery();
            }
        }

        internal void Reset()
        {
            ClearTarget();
            EndReposition();
            ClearFormationSlot();
            DefendingDuringWithdrawal = false;
            CombatSuspendedForThreat = false;
            _shotsRemaining = 0;
            _burstSize = 0;
            _nextStrafeAt = 0f;
            _pendingKillCheckPlayer = null;
            _pendingKillCheckUntil = 0f;
            _reloadStartedAt = 0f;
            _magazineReleased = false;
            _initialized = false;
        }

        // The authoritative damage call lands on the victim's owning client, so a kill is
        // confirmed a few frames later off the replicated death flag rather than in-line.
        private void TickPendingKillReport()
        {
            if (_pendingKillCheckPlayer == null)
                return;
            if (_pendingKillCheckPlayer.isPlayerDead)
            {
                _pendingKillCheckPlayer = null;
                _soldier.ReportKillClearToSquad();
                return;
            }
            // Healed back above where he was when the round landed: the hit no longer explains a
            // later death inside the window, so drop the claim rather than bark someone else's kill.
            if (_pendingKillCheckPlayer.health > _pendingKillCheckHealth
                || Time.time > _pendingKillCheckUntil)
            {
                _pendingKillCheckPlayer = null;
            }
        }

        // #1271 S8: the baton's player hit. Its damage also lands by ClientRpc on the victim's
        // owner, so it arms the rifle's own confirm window rather than a second one.
        internal void WatchPlayerKill(PlayerControllerB player, int healthBefore)
        {
            if (player == null)
                return;
            _pendingKillCheckPlayer = player;
            _pendingKillCheckHealth = healthBefore;
            _pendingKillCheckUntil = Time.time + PlayerKillConfirmSeconds;
        }

        private void EngageSelectedTarget()
        {
            if (_soldier.UsesBaton)
            {
                _soldier.LastBatonReadiness = _soldier.EngageBaton(_playerTarget, _enemyTarget);
                TraceDecision(_soldier.LastBatonReadiness); return;
            }
            if (CurrentState == BlacksiteWeaponState.Disabled
                || CurrentState == BlacksiteWeaponState.Reloading
                || CurrentState == BlacksiteWeaponState.GrenadeCommitted)
            {
                TraceDecision(CurrentState == BlacksiteWeaponState.Disabled ? BlacksiteFireReadiness.Disabled
                    : CurrentState == BlacksiteWeaponState.Reloading ? BlacksiteFireReadiness.Reloading
                    : BlacksiteFireReadiness.GrenadeCommitted);
                return;
            }

            // #874 N7: a captor holding a squadmate is aimed at the part of it this member's round can
            // reach without crossing the squadmate, so the choice is made before the aim point is read.
            ResolveCaptorShot();
            Vector3 aimPoint = GetCurrentAimPoint();
            if (aimPoint == Vector3.zero)
            {
                TraceDecision(BlacksiteFireReadiness.NoTarget);
                return;
            }

            // #947 S12 U2: root to root, matching awareness. See EngagementPoint.
            float distance = Vector3.Distance(_soldier.transform.position, EngagementPoint(aimPoint));
            // #1293 R1: sight is the perception pass's bit for this member on the engaged row. The
            // firing lane is the weapon's own test of where a round goes and gates only the shot.
            bool visible = InOwnSight;
            bool firingLane = distance <= MaximumShotRange + 6f && _soldier.HasCombatLineOfSight(aimPoint, _playerTarget, _enemyTarget);

            UpdateAcquisitionRamp(visible);
            // #1219 F2: movement is decided on every think by one policy and never returns ahead of
            // the fire decision. It used to: a committed move returned Repositioning before any shot,
            // and every readiness step below raised its own HoldForCombatAction, so a rifleman either
            // walked or shot and never both. Now UpdateTacticalMovement alone decides whether this
            // member moves or holds his spot this think, and the steps below decide only whether a
            // round may go. A burst still slows a committed step to FiringWalkSpeed (the motion
            // owner reads CurrentState); nothing below stops it.
            bool moving = UpdateTacticalMovement(aimPoint, distance, visible, out bool approachBlocked);
            if (CurrentState != BlacksiteWeaponState.Ready)
            {
                TraceDecision(CurrentState == BlacksiteWeaponState.FiringBurst ? BlacksiteFireReadiness.Firing
                    : moving ? BlacksiteFireReadiness.Repositioning : BlacksiteFireReadiness.Recovering);
                return;
            }

            if (Magazine <= 0)
            {
                BeginReload();
                // #1271 S3a: every return traces this think, so Combat never yields on a stale value.
                TraceDecision(BlacksiteFireReadiness.Reloading);
                return;
            }

            if (distance > MaximumShotRange)
            {
                // #1271 S3a: with no cover and no approach either, the geometry is wrong this think
                // (ApproachBlocked, OutOfBand) and Combat yields it to the duty.
                TraceDecision(moving ? BlacksiteFireReadiness.Repositioning
                    : approachBlocked ? BlacksiteFireReadiness.ApproachBlocked : BlacksiteFireReadiness.OutOfRange);
                return;
            }

            // #874 N7: out of sight is LostSight, not Acquiring. The ramp below can never complete on
            // a blind think (Observe(false) stamps _lostSightAt), so round 1 of the 2026-09-23 run
            // printed reason=Acquiring with unseen=2.72 s and 2.89 s (log 4903, 4936) for members who
            // could not see the dog at all.
            // #1293 R1: and so is a seen contact the rifle has no lane to this think.
            if (!visible || !firingLane)
            {
                TraceDecision(moving ? BlacksiteFireReadiness.Repositioning
                    : approachBlocked ? BlacksiteFireReadiness.ApproachBlocked : BlacksiteFireReadiness.LostSight);
                return;
            }

            if (!IsAcquisitionRampComplete())
            {
                TraceDecision(BlacksiteFireReadiness.Acquiring);
                return;
            }

            if (_aimSettleUntil <= 0f)
            {
                _aimSettleUntil = Time.time + (IsImmediateAttackerContact() ? .25f : AimSettleSeconds);
                TraceDecision(BlacksiteFireReadiness.Settling);
                return;
            }

            if (Time.time < _aimSettleUntil)
            {
                TraceDecision(BlacksiteFireReadiness.Settling);
                return;
            }

            var readiness = _soldier.GetCombatAimReadiness(aimPoint, out _, out _);
            // #1019 S5: Ready is the only value that maps to Firing from GetCombatAimReadiness,
            // so this is the same test read as the decision it is - "is this member acting".
            if (BlacksiteCombatPolicy.Outcome(readiness) != BlacksiteEngagementOutcome.Firing)
            {
                TraceDecision(readiness);
                return;
            }
            // #874 N7 (decision 2): a rifleman shoots the captor of a grabbed squadmate when he has a
            // line to it that clears the squadmate - ResolveCaptorShot found one and the aim point
            // above is already on it. #1011 A3's hold stands only when no such line exists: the flea
            // riding a head is never shot (its one sample is the victim's own head), and neither is a
            // captor whose every body sample lies behind or beside the victim. UpdateTacticalMovement
            // has already read that as a blocked lane, so the member moves for a line, not stands.
            if (IsAllyAttachedTarget() && !_hasCaptorShot)
            {
                RequestRescueVoice();
                TraceDecision(BlacksiteFireReadiness.FriendlyLane);
                return;
            }
            Vector3 lane = aimPoint - CombatShotOrigin;
            if (lane.sqrMagnitude > .04f && IsFriendlyLaneBlocked(CombatShotOrigin, lane.normalized, Mathf.Min(MaximumShotRange, lane.magnitude)))
            {
                // Do not start a fire/recovery cycle for a shot already known to be unsafe.
                // UpdateTacticalMovement owns the short blockage grace and subsequent sidestep.
                TraceDecision(BlacksiteFireReadiness.FriendlyLane);
                return;
            }
            TraceDecision(BlacksiteFireReadiness.Ready);
            BeginFiring(distance);
        }

        // #1011 A3: whether the rifleman's selected enemy is a captor holding a squadmate. Not
        // distance-gated: it is about the round this member is about to release. #874 N7: it names
        // the captor; whether a round may go is ResolveCaptorShot's answer, and the #1011 A3 hold
        // applies only when that answer is "no line clears him".
        private bool IsAllyAttachedTarget()
            => !_soldier.UsesBaton && _enemyTarget != null && _soldier.IsAllyAttachedContact(_enemyTarget);

        // #874 N7 (decision 2): where on a captor holding a squadmate this member's round may go, as
        // an offset from the captor's root so the aim follows the captor between thinks. Written on
        // every rifle think against a captor, cleared with the engagement and on a new selection;
        // _hasCaptorShot false is the #1011 A3 hold.
        private Vector3 _captorShotOffset;
        private bool _hasCaptorShot;

        private void ResolveCaptorShot()
        {
            _hasCaptorShot = false;
            if (!IsAllyAttachedTarget() || !TryFindCaptorShot(CombatShotOrigin, out Vector3 point)) return;
            _captorShotOffset = point - _enemyTarget.transform.position;
            _hasCaptorShot = true;
        }

        // #874 N7: the first captor sample a round from `origin` reaches without passing the held
        // squadmate. The samples are the combat sight test's own, in its order - the aim point, then
        // the torso and shin heights (BlacksiteSoldierAI.HasEnemyLineOfSight) - so no new point on a
        // body is invented. A captor RIDING its victim has exactly one sample, the capture anchor on
        // the victim's own head, and that one never clears him: a flea on a squadmate is still
        // never shot, which is the #1011 A3 observation (24 rounds into a flea riding member 2).
        private bool TryFindCaptorShot(Vector3 origin, out Vector3 point)
        {
            point = Vector3.zero;
            EnemyAI captor = _enemyTarget;
            BlacksiteSoldierAI victim = BlacksiteVanillaMonsters.CaptureVictim(captor);
            if (victim == null) return false;
            VictimBody(victim, out Vector3 bottom, out Vector3 top, out float radius);
            if (BlacksiteVanillaMonsters.TryGetCaptureAnchor(captor, out Vector3 anchor))
                return CaptorSampleClears(origin, anchor, bottom, top, radius, ref point);
            Vector3 root = captor.transform.position;
            return CaptorSampleClears(origin, ResolveEnemyAimPoint(captor), bottom, top, radius, ref point)
                || CaptorSampleClears(origin, root + Vector3.up * BlacksiteSoldierAI.EnemyTorsoSampleHeight, bottom, top, radius, ref point)
                || CaptorSampleClears(origin, root + Vector3.up * BlacksiteSightPolicy.ShinSampleHeight, bottom, top, radius, ref point);
        }

        // #874 N7: one sample is shootable when the world lets a line reach it (the same occlusion
        // test the reposition search scores cover with) and a round fired at it passes the squadmate
        // by the lane's own radius plus the cone the FIRST round's spread opens by the time it gets
        // to him. Measured along the whole MaximumShotRange flight rather than to the sample: a round
        // that misses the captor keeps flying, and a squadmate behind the captor is as much in the
        // way as one in front. Later rounds of a burst spread wider; FireShotEvent resolves each of
        // them before it is spent and withholds any that would stop on a squadmate.
        // The spread term is deliberately RoundSpread(0, range): it is the ANGLE (radians, the
        // recoil offset FireShotEvent adds to a unit direction), and FireShotEvent picks that angle
        // from the distance to the point aimed at - this sample - not from where the round is. The
        // cone's radius at the closest approach is that angle times the distance flown to it,
        // along * MaximumShotRange.
        private bool CaptorSampleClears(Vector3 origin, Vector3 sample, Vector3 bottom, Vector3 top, float radius,
            ref Vector3 point)
        {
            Vector3 lane = sample - origin;
            float range = lane.magnitude;
            if (range < .2f || range > MaximumShotRange) return false;
            Vector3 end = origin + lane * (MaximumShotRange / range);
            float gap = SegmentGap(origin, end, bottom, top, out float along);
            if (gap < radius + FriendlyLaneRadius + along * MaximumShotRange * RoundSpread(0, range)
                || IsWorldOccluded(origin, sample))
                return false;
            point = sample;
            return true;
        }

        // #874 N7: the held squadmate's solid torso capsule (BlacksiteBodyCollision) in world space,
        // so a body posed on a captor's socket is measured where it is and at the angle it is held.
        // A member without the capsule is measured as the capsule Ensure would have built.
        private static void VictimBody(BlacksiteSoldierAI victim, out Vector3 bottom, out Vector3 top, out float radius)
        {
            CapsuleCollider capsule = BlacksiteBodyCollision.Find(victim.gameObject);
            if (capsule == null)
            {
                radius = BlacksiteBodyCollision.Radius;
                float half = BlacksiteBodyCollision.Height * .5f - radius;
                Vector3 centre = victim.transform.position + Vector3.up * BlacksiteBodyCollision.CenterY;
                bottom = centre - Vector3.up * half;
                top = centre + Vector3.up * half;
                return;
            }
            Transform frame = capsule.transform;
            Vector3 scale = frame.lossyScale;
            radius = capsule.radius * Mathf.Max(Mathf.Abs(scale.x), Mathf.Abs(scale.z));
            Vector3 axis = frame.up * Mathf.Max(0f, capsule.height * .5f * Mathf.Abs(scale.y) - radius);
            Vector3 middle = frame.TransformPoint(capsule.center);
            bottom = middle - axis;
            top = middle + axis;
        }

        // The shortest distance between segments [p1,q1] and [p2,q2], and where along the first it
        // falls (0..1). Ericson, Real-Time Collision Detection 5.1.9; the first segment is a round's
        // flight and never degenerate, the second a capsule axis and may be.
        private static float SegmentGap(Vector3 p1, Vector3 q1, Vector3 p2, Vector3 q2, out float along)
        {
            Vector3 d1 = q1 - p1, d2 = q2 - p2, r = p1 - p2;
            float a = Vector3.Dot(d1, d1), e = Vector3.Dot(d2, d2), c = Vector3.Dot(d1, r);
            float t;
            if (e <= 1e-6f)
            {
                along = Mathf.Clamp01(-c / a);
                t = 0f;
            }
            else
            {
                float b = Vector3.Dot(d1, d2), f = Vector3.Dot(d2, r), denominator = a * e - b * b;
                along = denominator > 1e-6f ? Mathf.Clamp01((b * f - c * e) / denominator) : 0f;
                t = (b * along + f) / e;
                if (t < 0f) { t = 0f; along = Mathf.Clamp01(-c / a); }
                else if (t > 1f) { t = 1f; along = Mathf.Clamp01((b - c) / a); }
            }
            return Vector3.Distance(p1 + d1 * along, p2 + d2 * t);
        }

        // #1011 A3: the captor this member has already called out. A reference latch rather than a
        // clock, so the line is spoken once per capture and again for a different captor; the voice
        // director's own transition key (the captor's network id) is the second guard, which is
        // what keeps four riflemen holding on one flea from producing four barks.
        private EnemyAI _rescueVoiceCaptor;

        private void RequestRescueVoice()
        {
            if (_enemyTarget == null || ReferenceEquals(_enemyTarget, _rescueVoiceCaptor)) return;
            _rescueVoiceCaptor = _enemyTarget;
            _soldier.ReportAllyAttachedToSquad(_enemyTarget);
        }

        // #314 acquisition ramp: a target has to be held in sight for the soldier's own beat before
        // the first shot, so contact reads as spot-call-fire instead of an instant volley. A soldier
        // whose squad table already holds a threat (IsAware) was expecting it and ramps faster.
        private void UpdateAcquisitionRamp(bool visible)
        {
            float multiplier = _soldier.IsAware ? BlacksiteRetrievalTuning.AlertedAcquisitionRampMultiplier : 1f;
            float rampSeconds = BlacksiteRetrievalTuning.GetAcquisitionRampSeconds(_soldier.AnimationSeed) * multiplier;
            if (!_acquisition.Observe(Time.time, visible, rampSeconds,
                BlacksiteRetrievalTuning.AcquisitionRampLineOfSightGraceSeconds))
                return;
            _acquisitionCompletesAt = Time.time + rampSeconds;
            if (_playerTarget == null || !TryGetTargetRow(out BlacksiteWorldRow row) || row.Announce(_soldier.PerceptionSlot))
                _soldier.RequestAcquisitionVoice();
        }

        private bool IsAcquisitionRampComplete() => _acquisition.IsComplete(Time.time);

        private void ResetAcquisitionRamp()
        {
            _acquisition.Clear();
            _acquisitionCompletesAt = 0f;
        }

        // #874 S10.1: a swing already in flight is a commitment. Run 3 t=961.05 member 3 began a
        // swing on enemy:412 at 1.83 m; t=962.13 the same member reported Firing on player:0 at
        // 12.63 m and 129.7 degrees off the nose, because SelectTarget swapped the contact mid-arc
        // and the swing played out into empty air. Only something that has just hit this member -
        // the immediate-attacker score below - may take the decision back before the recovery frame,
        // which is at most BlacksiteBatonPolicy.ReadyAt(swing) = 1.55 to 1.95 s away. Anything else
        // is remembered and applied on the first selection after the swing ends.
        private const int ImmediateAttackerPriority = 420;
        private PlayerControllerB _swingDeferredPlayer;
        private EnemyAI _swingDeferredEnemy;
        private uint _swingLockReportedFor = uint.MaxValue;

        private void SelectTarget(
            PlayerControllerB player,
            EnemyAI enemy,
            bool forcePriority = false)
        {
            int incomingPriority = GetTargetPriority(player, enemy);
            int currentPriority = GetTargetPriority(_playerTarget, _enemyTarget);
            if (_soldier.UsesBaton && _soldier.BatonSwingInFlight)
            {
                if (!forcePriority && incomingPriority < ImmediateAttackerPriority
                    && !ReferenceEquals((object)player ?? enemy, _acquisition.Target)
                    && HasValidTarget())
                {
                    _swingDeferredPlayer = player;
                    _swingDeferredEnemy = enemy;
                    uint sequence = _soldier.BatonSwingSequence;
                    if (BlacksiteDiagnostics.Enabled && _swingLockReportedFor != sequence)
                    {
                        _swingLockReportedFor = sequence;
                        BlacksiteDiagnostics.Log("Baton",
                            $"member={_soldier.MemberIndex}, target locked through swing, incoming="
                            + (player != null ? BlacksiteDiagnosticFormat.Target(player)
                                : enemy != null ? BlacksiteDiagnosticFormat.Target(enemy) : "none")
                            + $", incomingPriority={incomingPriority}");
                    }
                    return;
                }
            }
            else if (_swingDeferredPlayer != null || _swingDeferredEnemy != null)
            {
                // The swing has ended. The contact the lock refused is taken now unless this call
                // already offers one at least as good, so a swap is deferred rather than dropped.
                int deferred = GetTargetPriority(_swingDeferredPlayer, _swingDeferredEnemy);
                if (deferred > incomingPriority
                    && (IsValidPlayer(_swingDeferredPlayer) || IsValidEnemy(_swingDeferredEnemy)))
                {
                    player = _swingDeferredPlayer;
                    enemy = _swingDeferredEnemy;
                    incomingPriority = deferred;
                }
                _swingDeferredPlayer = null;
                _swingDeferredEnemy = null;
            }
            object incoming = (object)player ?? enemy;
            bool same = ReferenceEquals(incoming, _acquisition.Target);

            if (BlacksiteCombatPolicy.RefusesTargetSwap(
                    same, HasValidTarget(), forcePriority, Time.time,
                    _targetStickyUntil, incomingPriority, currentPriority))
            {
                return;
            }

            _playerTarget = player;
            _lastPlayerContact = player;
            _enemyTarget = enemy;
            // #874 S11 U2 (W1): a commitment is to a CHOICE. This write used to happen on every
            // selection, including the same-target re-selection that runs on every 0.14 s think, so
            // the 3 s commitment was re-armed for as long as a contact was held and the +80 swap
            // margin never lapsed - the S10.1 arbitration and the S7.2 score table were both built
            // on a clock that could not expire. The rule now lives in BlacksiteCombatPolicy so
            // BlacksiteReactiveRegression pins it without Unity.
            _targetStickyUntil = BlacksiteCombatPolicy.NextTargetStickyDeadline(
                same, Time.time, _targetStickyUntil, TargetStickSeconds);
            _acquisition.Select(incoming);
            if (same) return;

            // #1293 R1: a new selection engages a different row of the squad table. Whether this
            // member's own eyes have it - which the suppression gate reads - is that row's, so
            // nothing about the dropped contact carries over to the new one.
            _targetKey = player != null ? player.NetworkObjectId : enemy.NetworkObjectId;
            _aimSettleUntil = 0f;
            // #1219 F2: a new contact is spotted and shot at from where he stands before the first
            // lateral step is due.
            _nextStrafeAt = Time.time + BlacksiteCombatPolicy.StrafeIntervalSeconds;
            // #874 N7: a captor line belongs to the captor it was found on.
            _hasCaptorShot = false;
            // #947 S12 U2: the magazine rule is a verdict about ONE contact and one firing position.
            // A new selection owes its own magazine before it can produce one.
            _roundsWithoutEffect = 0;
        }

        // #1011 A3 (observed failure O3): a captor holding a squadmate is the preferred target for
        // the baton member committed to rescuing it (#874 Plan F: exactly one per capture), above
        // the confirmed drive carrier's 500 and above the 420 something
        // that has just hit this member gets. That ordering is the decision itself: a squadmate
        // losing 10 HP every two seconds to a thing one connected swing removes outranks the loot
        // and outranks this member's own bruise, and 520 is the next 100-grid step above the
        // highest priority that existed. It deliberately clears ImmediateAttackerPriority too, so a
        // swing already in flight may be taken back for a rescue - the only contact that may.
        private const int RescuePriority = 520;
        // #874 Plan F: the same captor for every OTHER baton member while it has a committed
        // rescuer. It is the rescuer's, so it is the lowest-priority contact there is and any
        // other live contact takes the selection. It is asked after IsRescueTarget, which has
        // just run the election.
        private const int OtherRescuersCaptorPriority = 0;
        // #874 N8 (decision 7): an Old Bird is held only for a rifle's warning fire, which its
        // rounds cannot turn into a kill. Lowest there is, so any live contact that appears while
        // it is held clears the swap margin and takes the rifle off it inside the sticky window.
        private const int WarningFirePriority = 0;

        private int GetTargetPriority(PlayerControllerB player, EnemyAI enemy)
        {
            if (enemy != null && _soldier.IsRescueTarget(enemy))
                return RescuePriority;
            if (enemy != null && _soldier.UsesBaton && BlacksiteVanillaMonsters.RescuedByAnother(enemy, _soldier))
                return OtherRescuersCaptorPriority;
            if (enemy != null && BlacksiteThreatAdapterRegistry.EngagementRefusedFor(enemy))
                return WarningFirePriority;
            if (player != null)
            {
                if (_soldier.IsConfirmedDriveCarrier(player))
                    return 500;
                return player == _immediatePlayerAttacker && Time.time <= _immediateAttackerUntil
                    ? 420
                    : 220;
            }
            if (enemy == null)
                return 0;
            if (enemy == _immediateAttacker && Time.time <= _immediateAttackerUntil)
                return 420;
            float distance = Vector3.Distance(_soldier.transform.position, enemy.transform.position);
            return distance <= BlacksiteCombatPolicy.MonsterPriorityRange ? 340 : 190;
        }

        private void BeginFiring(float distance)
        {
            // #874 N8 (decision 7): a burst committed at an Old Bird is one of the rifle's two
            // warning bursts. Counted here, where every aimed burst or single is committed, and a
            // no-op for any other target.
            BlacksiteThreatAdapterRegistry.NoteWarningBurst(_enemyTarget, _soldier);
            bool burst = Magazine >= 3 && _soldier.ShouldFireBurst(distance);
            _burstSize = burst ? 3 : 1;
            _shotsRemaining = _burstSize;
            _nextShotAt = Time.time + _soldier.GetFireEventDelay();
            // Keep settled aim on this contact through recoil. Target changes clear it; every round
            // still verifies the actual body and muzzle alignment.
            SetState(BlacksiteWeaponState.FiringBurst);
            // #1219 F2: no hold here. Whether the body stops is UpdateTacticalMovement's decision,
            // already taken this think; a committed step walks on through the burst at
            // FiringWalkSpeed. The authored StandingFire / CrouchFire states (clips StandingFire and
            // CrouchRapidFire) are single full-body clips that plant the legs, and the bundle's
            // WalkingFire / RunningFire are single forward clips that cannot follow a lateral step,
            // so a member firing on the move stays in his directional aimed locomotion
            // (StandingAimed, or the aimed CrouchedLocomotion tree - MoveX/MoveZ blend trees, both
            // accepted fire poses) and the stride keeps playing; the round releases on the
            // scheduled clock (CanReleaseScheduledShot without the event), exactly as it does
            // whenever the clip's event does not arrive. A standing shot keeps the authored clip and
            // its recoil; if a step starts before that clip ends, the presentation hands the base
            // layer back to the aimed locomotion (BlacksiteSoldierAI.ReleasePlantedFireClip).
            if (!IsRepositionActive) _soldier.TriggerCombatFireAnimation();
        }

        // #314 D9: suppression is the Support gunner's job, not a squad-wide reflex, and it is
        // aimed at a point he can actually see. The Leader picks it up only once Support is down.
        private void BeginSuppressionFire()
        {
            if (Magazine <= 0)
            {
                BeginReload();
                return;
            }
            if (!_soldier.IsSuppressionFireRole || !TryGetTargetRow(out BlacksiteWorldRow row))
                return;
            // #874 S10.3: a point this member's own eyes have not had for SuppressionStaleSeconds is
            // not a suppression target. Refused ahead of the retry throttle so the reason is on the
            // trace for every tick it is the operative refusal, and so a stale point never consumes
            // a retry slot that a re-sighted point would want. TickServer only reaches here while
            // the row is held, so this names the band from 1 s to the row's expiry.
            if (Time.time - row.MemberSawAt(_soldier.PerceptionSlot) > SuppressionStaleSeconds)
            {
                TraceDecision(BlacksiteFireReadiness.SuppressionStale);
                return;
            }
            // Ready-with-no-target re-enters here every frame; the gate is raycast-backed, so it
            // gets its own retry throttle rather than one occlusion trace per frame.
            if (Time.time < _nextSuppressionAttemptAt)
                return;
            _nextSuppressionAttemptAt = Time.time + SuppressionRetrySeconds;
            Vector3 point = RememberedAimPoint(row);
            if (!_soldier.HasCombatAimAlignment(point)
                || !_soldier.HasCombatLineOfSight(point, null, null))
                return;

            Vector3 laneOrigin = CombatShotOrigin;
            Vector3 laneDelta = point - laneOrigin;
            float laneDistance = Mathf.Min(MaximumShotRange, laneDelta.magnitude);
            if (laneDistance < 0.2f
                || IsFriendlyLaneBlocked(laneOrigin, laneDelta.normalized, laneDistance))
            {
                return;
            }

            _burstSize = 1;
            _shotsRemaining = 1;
            // Suppression rounds count against the same reposition budget as aimed fire, otherwise a
            // Support gunner could hold one spot indefinitely by suppressing instead of engaging.
            _nextShotAt = Time.time + _soldier.GetFireEventDelay();
            SetState(BlacksiteWeaponState.FiringBurst);
            _soldier.HoldForCombatAction(SingleRecoverySeconds + 0.2f);
            _soldier.TriggerCombatFireAnimation();
        }

        private void FireShotEvent()
        {
            // #874 S13: a round is refused by the HANDS, not by objective custody. A witness
            // escort walks a living man out and keeps its rifle; only cargo actually occupying
            // the hands stops the trigger.
            if (_soldier.UsesBaton || _soldier.HasOccupiedCargoHands || CurrentState != BlacksiteWeaponState.FiringBurst || _shotsRemaining <= 0)
                return;

            Vector3 origin = CombatShotOrigin;
            Vector3 targetPoint = GetCurrentAimPoint();
            Vector3 toTarget = targetPoint - origin;
            float targetDistance = Mathf.Min(MaximumShotRange, toTarget.magnitude);
            if (targetDistance < 0.2f)
            {
                FinishFiring();
                return;
            }

            Vector3 baseDirection = toTarget.normalized;
            bool aligned = _soldier.HasCombatAimAlignment(targetPoint);
            bool visible = _soldier.HasCombatLineOfSight(targetPoint, _playerTarget, _enemyTarget);
            if (!aligned
                || !visible
                || (_lastPlayerContact != null && !IsValidPlayer(_lastPlayerContact)))
            {
                TraceDecision(_lastPlayerContact != null && !IsValidPlayer(_lastPlayerContact)
                    ? BlacksiteFireReadiness.Ineligible
                    : !visible ? BlacksiteFireReadiness.LostSight
                    : _soldier.GetCombatAimReadiness(targetPoint, out _, out _));
                // #874 S10.3: the muzzle re-check is the last word on whether this point can be
                // shot at, and a cancel for sight says the point is wrong, not that the trigger was
                // early. Defer the next attempt so the member cannot re-aim at the same wall three
                // times in 2.5 s. deferred= is appended at the end of the existing line and reads
                // 0.0 on a cancel the sight test did not cause.
                float deferred = 0f;
                if (!visible)
                {
                    deferred = SuppressionCancelDeferSeconds;
                    _nextSuppressionAttemptAt = Time.time + SuppressionCancelDeferSeconds;
                }
                // The line's old trailing full stop moves off the end with the appended key: the
                // score reader strips a trailing "." only from a value that does not end in a
                // digit, so "deferred=1.5." would parse as the string "1.5.".
                BlacksiteDiagnostics.Log("Shot", $"cancelled member={_soldier.MemberIndex}, aligned={aligned}, visible={visible}, target={targetPoint}, deferred={deferred:0.0}");
                FinishFiring();
                return;
            }
            if (IsFriendlyLaneBlocked(origin, baseDirection, targetDistance))
            {
                TraceDecision(BlacksiteFireReadiness.FriendlyLane);
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Soldier {_soldier.MemberIndex} suppressed a shot because a squadmate occupied the muzzle lane.");
                _shotsRemaining = 0;
                FinishFiring();
                return;
            }

            int shotIndex = Mathf.Max(0, _burstSize - _shotsRemaining);
            float spread = RoundSpread(shotIndex, targetDistance);
            Vector2 recoil = UnityEngine.Random.insideUnitCircle * spread;
            Vector3 right = Vector3.Cross(Vector3.up, baseDirection).normalized;
            Vector3 up = Vector3.Cross(baseDirection, right).normalized;
            Vector3 direction = (baseDirection + right * recoil.x + up * recoil.y).normalized;

            ShotResolution resolution = ResolveAuthoritativeShot(origin, direction);
            // #874 N7: a round at a captor holding a squadmate is resolved before it is spent. The
            // decision cleared the squadmate for the first round's cone; a later round of the burst,
            // or a captor that turned its catch into the line since, can still put him first on the
            // round's path, and such a round is not fired at all - no magazine, no tracer, no noise.
            if (resolution.ImpactKind == BlacksiteShotImpactKind.FriendlySuppressed && IsAllyAttachedTarget())
            {
                // The round had no effect on the contact, exactly as a spent FriendlySuppressed round
                // has none: impact= says so and it counts toward NoEffect, so a member whose rounds
                // keep being withheld is moved to a different vantage by the magazine rule.
                _lastImpactKind = BlacksiteShotImpactKind.FriendlySuppressed;
                if (_roundsWithoutEffect < int.MaxValue) _roundsWithoutEffect++;
                TraceDecision(BlacksiteFireReadiness.FriendlyLane);
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Soldier {_soldier.MemberIndex} withheld a round at a captor because it would have stopped on a squadmate.");
                _shotsRemaining = 0;
                FinishFiring();
                return;
            }
            SetMagazine(Magazine - 1);
            // #947 S12 U2: the magazine-without-effect count, and the impact= key on the decision
            // trace. A round that reached a live target - an enemy, or the player this member is
            // engaging - clears it; World, None and FriendlySuppressed all count against it.
            // Player is treated as an effect as well as Enemy, deliberately: the rule is "nothing
            // this member fires is reaching anything", and scoring a landed player hit as a miss
            // would walk every rifleman in a player fight out of position once per magazine.
            _lastImpactKind = resolution.ImpactKind;
            if (resolution.ImpactKind == BlacksiteShotImpactKind.Enemy
                || resolution.ImpactKind == BlacksiteShotImpactKind.Player)
            {
                _roundsWithoutEffect = 0;
            }
            else if (_roundsWithoutEffect < int.MaxValue)
            {
                _roundsWithoutEffect++;
            }
            // #1219 F2: every round spent says how fast its shooter was moving when it left, so a
            // log can count shots fired on the move. speed= is the agent's planar velocity, moving=
            // the same IsMovingShot test the alignment band was read with. Its own "fired" prefix,
            // beside the existing "cancelled" one; the enemy-damage [Shot] line is unchanged.
            if (BlacksiteDiagnostics.Enabled)
            {
                float shooterSpeed = _soldier.CombatPlanarSpeed;
                BlacksiteDiagnostics.Log("Shot",
                    $"fired member={_soldier.MemberIndex}, speed={shooterSpeed:0.00}, moving={BlacksiteCombatPolicy.IsMovingShot(shooterSpeed)}"
                    + $", round={shotIndex + 1}/{_burstSize}, distance={targetDistance:0.0}, impact={resolution.ImpactKind}"
                    + $", reposition={IsRepositionActive}, moveReason={_lastMovementReason}");
            }
            bool burst = _burstSize > 1;
            ApplyShotDamage(resolution, burst);
            _soldier.PresentAuthoritativeShot(
                origin,
                resolution.EndPoint,
                burst,
                shotIndex == 0,
                resolution.ImpactKind);
            if (!burst || shotIndex == 0)
            {
                // #1233 S8: the gunshot is unchanged - same 24/32 m, same forced acquires, same
                // audible noise every vanilla listener hears. It is named as this soldier's for
                // the length of the dispatch so a squadmate's DetectNoise drops its own squad's
                // rifle instead of reacting to it as a player; the squad already holds the target's
                // row (#1293 R1), so the shot itself writes nothing. This is the only call site of
                // SignalSoldierThreat, so the marking is complete.
                using (BlacksiteSoldierAI.AnnounceSquadNoise(_soldier))
                    BlacksiteMonsterTargeting.SignalSoldierThreat(_soldier, burst ? 32f : 24f, burst ? 8f : 5.5f);
            }

            _shotsRemaining--;
            if (_shotsRemaining > 0 && Magazine > 0)
            {
                _nextShotAt = Time.time + BurstCadenceSeconds;
                return;
            }

            FinishFiring();
        }

        // The cone one round leaves the muzzle in: wider down a burst and with range, and opened
        // further by a moving target. #874 N7: one formula for the round FireShotEvent releases and
        // for the captor clearance that has to allow for it.
        private float RoundSpread(int shotIndex, float targetDistance)
            => (0.008f + shotIndex * 0.006f + Mathf.InverseLerp(4f, MaximumShotRange, targetDistance) * 0.012f)
                * GetTargetMotionSpreadMultiplier();

        // A sprinting target is genuinely hard to hit: the cone opens up to 2.5x, so running is
        // real counterplay against fire that would otherwise be near-hitscan.
        private float GetTargetMotionSpreadMultiplier()
        {
            return Mathf.Lerp(
                1f,
                SprintSpreadMultiplier,
                Mathf.InverseLerp(1.5f, 7f, GetTargetPlanarSpeed()));
        }

        private float GetTargetPlanarSpeed()
        {
            if (IsValidPlayer(_playerTarget))
                return BlacksiteSoldierAI.GetPlanarSpeed(_playerTarget);
            if (!IsValidEnemy(_enemyTarget)
                || _enemyTarget.agent == null
                || !_enemyTarget.agent.enabled
                || !_enemyTarget.agent.isOnNavMesh)
            {
                return 0f;
            }
            Vector3 velocity = _enemyTarget.agent.velocity;
            velocity.y = 0f;
            return velocity.magnitude;
        }

        private ShotResolution ResolveAuthoritativeShot(Vector3 origin, Vector3 direction)
        {
            BlacksiteHitchTracer.CountPhysicsQuery();
            int count = Physics.RaycastNonAlloc(
                origin,
                direction,
                _rayHits,
                MaximumShotRange,
                ~0,
                QueryTriggerInteraction.Collide);
            Array.Sort(_rayHits, 0, count, HitComparer);

            for (int i = 0; i < count; i++)
            {
                RaycastHit hit = _rayHits[i];
                Collider collider = hit.collider;
                if (collider == null || collider.transform.IsChildOf(_soldier.transform))
                    continue;

                BlacksiteSoldierAI teammate = collider.GetComponentInParent<BlacksiteSoldierAI>();
                if (teammate != null)
                    return new ShotResolution(hit.point, BlacksiteShotImpactKind.FriendlySuppressed, null, null);

                PlayerControllerB player = collider.GetComponentInParent<PlayerControllerB>();
                if (player != null)
                    return new ShotResolution(hit.point, BlacksiteShotImpactKind.Player, player, null);

                EnemyAI enemy = ResolveEnemy(collider);
                if (enemy != null)
                    return new ShotResolution(hit.point, BlacksiteShotImpactKind.Enemy, null, enemy);

                if (collider.isTrigger)
                    continue;

                return new ShotResolution(hit.point, BlacksiteShotImpactKind.World, null, null);
            }

            return new ShotResolution(origin + direction * MaximumShotRange, BlacksiteShotImpactKind.None, null, null);
        }

        private void ApplyShotDamage(ShotResolution resolution, bool burst)
        {
            if (resolution.ImpactKind == BlacksiteShotImpactKind.Player
                && IsValidPlayer(resolution.Player))
            {
                // A burst out-damages a single aimed shot: three rounds landing is meant to be the
                // punishing outcome, not the cheaper one.
                // Snapshot the pre-hit health so the confirm pass can tell a death that followed
                // this round from one that followed a full heal in between.
                int healthBeforeHit = resolution.Player.health;
                _soldier.ApplyAuthoritativePlayerDamage(
                    resolution.Player,
                    burst ? 6 : 4,
                    CauseOfDeath.Gunshots);
                _pendingKillCheckPlayer = resolution.Player;
                _pendingKillCheckHealth = healthBeforeHit;
                _pendingKillCheckUntil = Time.time + PlayerKillConfirmSeconds;
                return;
            }

            if (resolution.ImpactKind != BlacksiteShotImpactKind.Enemy
                || !IsValidEnemy(resolution.Enemy))
            {
                return;
            }

            try
            {
                int before = resolution.Enemy.enemyHP;
                // #1065 WP4: the damage is a named policy fact rather than the literal 1 that took
                // an eyeless dog from 12 to 7 over a whole engagement. Nothing else about this call
                // moves: it is still the vanilla entry point, still made under TickServer's host
                // authority, and still wrapped so a modded enemy that throws cannot stop the tick.
                //
                resolution.Enemy.HitEnemyOnLocalClient(BlacksiteCombatPolicy.RifleDamagePerRound, Vector3.zero, null, playHitSFX: true);
                // adapter= is APPENDED to the existing keys and is the reason this line exists in
                // WP4: BlacksiteMonsterCombatAdapterRegistry has no MOTOR adapter for eight of the
                // classes seen in D2/D3, and that has never gated the rifle - IsValidEnemy above
                // asks about weapon exclusion and liveness only. The key makes that provable from a
                // log instead of from this comment, by naming the support level on the round that
                // landed.
                //
                // Read AFTER the hit, deliberately: both calls are pure today, but the damage
                // application is the only thing in this try block that has to happen, and nothing
                // added for a log line is allowed to sit in front of it. Reading the level after
                // the round lands is also the more honest answer - a sapsucker the hit just turned
                // hostile reports the level it has now rather than the one it had a frame ago.
                var support = BlacksiteMonsterCapabilities.GetSupportLevel(resolution.Enemy);
                string adapter = BlacksiteMonsterCapabilities.IsSupported(resolution.Enemy)
                    ? support.ToString()
                    : "unsupported";
                BlacksiteDiagnostics.Log("Shot", $"member={_soldier.MemberIndex}, enemy={resolution.Enemy.enemyType?.enemyName}, hp={before}->{resolution.Enemy.enemyHP}, damage={BlacksiteCombatPolicy.RifleDamagePerRound}, adapter={adapter}, state={resolution.Enemy.currentBehaviourStateIndex}, impact={resolution.EndPoint}.");
                // #1083 (WP I): the give-up clock READS the two numbers this line has been printing
                // since WP4 - hp={before}->{after} - instead of leaving them for a human. The round
                // is confirmed by construction: IsValidEnemy above already refused a dead or
                // weapons-excluded contact, so the contact was alive before the call and the hit was
                // applied, not missed. Host-only: this whole path is TickServer's.
                BlacksiteThreatAdapterRegistry.ObserveDamageAttempt(resolution.Enemy, before, resolution.Enemy.enemyHP, "rifle");
                // #947 S12 U2: a miss does not renew the creature's interest in this member. The
                // guard is restated here rather than left to the early return above so the contract
                // is local and provable: soldier FIRE forces aggro only for a round this trace
                // resolved as ImpactKind.Enemy. The other two ForceAggro paths are unchanged and
                // stay unchanged - a baton hit that connects (BlacksiteSoldierAI.Equipment.cs
                // TickBatonSweep) and the gunfire NOISE every listener inside 24/32 m hears
                // (BlacksiteMonsterTargeting.SignalSoldierThreat, which is the run's other source of
                // repeated `forced acquire` lines and is out of this unit's scope).
                if (resolution.ImpactKind == BlacksiteShotImpactKind.Enemy)
                    BlacksiteMonsterTargeting.ForceAggro(resolution.Enemy, _soldier, 7.5f);
                if (resolution.Enemy.isEnemyDead)
                    _soldier.ReportKillClearToSquad();
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Authoritative enemy shot skipped: {e.Message}");
            }
        }

        // The radius the friendly-lane cast sweeps down a round's path. #874 N7: named because the
        // captor clearance keeps the same distance from a held squadmate that this cast keeps from
        // any squadmate in the lane.
        private const float FriendlyLaneRadius = 0.14f;

        // #1305 H (C3, coordinator Q3): a lane is blocked by a squadmate in it, or by a live hazard
        // disc it crosses that a squadmate stands in - one rule for every fire site. A shot across
        // an empty disc is allowed.
        private bool IsFriendlyLaneBlocked(Vector3 origin, Vector3 direction, float distance)
            => SquadmateInLane(origin, direction, distance) != null
               || (_soldier.TryGetSquadDirector(out BlacksiteSquadDirector director)
                   && BlacksiteHazardAwareness.LaneCrossesOccupiedDisc(director.World, director.LivingMembers, _soldier,
                       origin, origin + direction * distance));

        // #1293 R3: the squadmate the friendly-lane cast meets first, or null for a clear lane.
        private BlacksiteSoldierAI SquadmateInLane(Vector3 origin, Vector3 direction, float distance)
        {
            BlacksiteHitchTracer.CountPhysicsQuery();
            int count = Physics.SphereCastNonAlloc(
                origin,
                FriendlyLaneRadius,
                direction,
                _laneHits,
                Mathf.Max(0.1f, distance),
                ~0,
                QueryTriggerInteraction.Collide);
            Array.Sort(_laneHits, 0, count, HitComparer);

            for (int i = 0; i < count; i++)
            {
                Collider collider = _laneHits[i].collider;
                if (collider == null || collider.transform.IsChildOf(_soldier.transform))
                    continue;
                var squadmate = collider.GetComponentInParent<BlacksiteSoldierAI>();
                if (squadmate != null)
                    return squadmate;
                if (collider.GetComponentInParent<PlayerControllerB>() != null
                    || ResolveEnemy(collider) != null)
                {
                    return null;
                }
                if (!collider.isTrigger)
                    return null;
            }

            return null;
        }

        private void FinishFiring()
        {
            _shotsRemaining = 0;
            _stateEndsAt = Time.time + (_burstSize > 1 ? BurstRecoverySeconds : SingleRecoverySeconds);
            SetState(BlacksiteWeaponState.Recovering);
        }

        private void CompleteRecovery()
        {
            if (CurrentState != BlacksiteWeaponState.Recovering)
                return;
            if (Magazine <= 0)
                BeginReload();
            else
                SetState(BlacksiteWeaponState.Ready);
        }

        private int _reserveMagazines = 4;
        internal bool AmmunitionExhausted => Magazine == 0 && _reserveMagazines == 0;
        private void BeginReload()
        {
            if (_reserveMagazines <= 0 || CurrentState == BlacksiteWeaponState.Disabled
                || CurrentState == BlacksiteWeaponState.GrenadeCommitted)
            {
                return;
            }

            _shotsRemaining = 0;
            _reloadStartedAt = Time.time;
            _magazineReleased = false;
            _reloadInsertAt = Time.time + _soldier.GetActionMarkerDelay("WalkingReload", "CrouchReload", "OnBlacksiteMagazineInsert", ReloadSeconds);
            _stateEndsAt = _reloadInsertAt + .15f;
            SetState(BlacksiteWeaponState.Reloading);
            BeginReloadReposition();
            _soldier.TriggerCombatReloadAnimation();
        }

        // #314: the reload is the cheapest window to break the spot he just fired from. The
        // reload animation plays on the upper body regardless, so nothing here may hold position:
        // an existing HoldForCombatAction would leave the agent stopped for the whole 1.78s.
        private void BeginReloadReposition()
        {
            Vector3 aimPoint = GetCurrentAimPoint();
            if (aimPoint != Vector3.zero
                && TryFindCombatPosition(
                    aimPoint,
                    Vector3.Distance(_soldier.transform.position, aimPoint),
                    requireLine: false,
                    out Vector3 destination))
            {
                CommitReposition(destination, Mathf.Min(ReloadSeconds, RepositionCommitSeconds));
                return;
            }

            _soldier.HoldForCombatAction(Mathf.Max(.2f, _stateEndsAt - Time.time));
        }

        private void CommitReposition(Vector3 destination, float commitSeconds)
        {
            _repositionDestination = destination;
            _repositionCommittedUntil = Time.time + Mathf.Clamp(
                Vector3.Distance(_soldier.transform.position, destination) / 2f + commitSeconds, 1.1f, 5f);
            _nextCoverSearchAt = _repositionCommittedUntil + CoverSearchInterval;
            // The agent must be able to reach the controller's arrival gate. The default 1.1 m
            // stop parked a short strafe outside the 0.8 m gate, stationary through whole bursts
            // while IsRepositionActive remained true until its deadline (#1219 F2 native 30 FPS).
            _soldier.Navigator.ApplyCombatApproachProfile(new BlacksiteApproachProfile(
                BlacksiteCombatPolicy.RepositionStopDistance, BlacksiteCombatPolicy.RepositionArrivalRadius,
                Vector3.zero, requiresFacing: false, operatorLane: false));
            _soldier.SetCombatDestination(destination);
        }

        // #1305 H (C1, VerifyLive5): the committed move asked for again once Combat has interrupted
        // a job interaction, which had answered the first ask with the task's own step; false: none.
        internal bool ReissueCommittedMove()
        {
            if (!IsRepositionActive) return false;
            _soldier.SetCombatDestination(_repositionDestination);
            return true;
        }

        private void EndReposition()
        {
            // Despawn disposes the navigator before resetting combat. Release only an existing
            // navigator, never acquire a replacement through its lazy property during teardown.
            _soldier.ReleaseCombatApproach();
            _repositionCommittedUntil = 0f;
        }

        private void CompleteReload()
        {
            if (CurrentState != BlacksiteWeaponState.Reloading)
                return;
            if (_reserveMagazines <= 0) { SetState(BlacksiteWeaponState.Ready); return; }
            _reserveMagazines--;
            SetMagazine(MagazineCapacity);
            SetState(BlacksiteWeaponState.Ready);
            _aimSettleUntil = 0f;
            _reloadStartedAt = 0f;
            _magazineReleased = false;
            _soldier.PresentMagazineStateNetworked(visible: true);
        }



















        // #874 S10.2: the engaged row carried forward at its measured velocity
        // (BlacksiteWorldRow.PredictedPosition, clamped by BlacksiteSightPolicy) and put on the floor
        // with the S7.6 3 m NavMesh sample, for MOVEMENT only. #1293 R1: monsters are predicted from
        // their row as players are; aim and suppression read the unpredicted Position.
        private static Vector3 SearchPoint(BlacksiteWorldRow row)
        {
            Vector3 predicted = row.PredictedPosition(Time.time);
            return NavMesh.SamplePosition(predicted, out NavMeshHit hit, 3f, NavMesh.AllAreas) ? hit.position : predicted;
        }

        // #874 S13: where a member that found no firing position may WALK. Run 1 t=169.62 members
        // 4 and 5 held reason=OutOfRange, liveContact=True, target=enemy:248, unseen=Infinity,
        // movedMetresLast5s=0.00 against a Forest Keeper 35 m away: the reposition search had
        // nothing to aim at, so TryFindApproachPoint answered false for the rest of the fight.
        //
        // He can see it: EngagementPoint rather than the aim point, because an approach closes the
        // ROOT-to-root band the reposition reason was derived from and an aim point sits up to
        // EnemyAimCeiling 2 m up a body, which is not a place to walk to. Otherwise (#1293 R1) the
        // engaged row's search point, a RECORDED position the squad table holds - never the
        // contact's live transform, which would be a wallhack the #314 locality model does not
        // grant. The row's own expiry is what stops a lost contact from buying a permanent chase,
        // and nothing about FIRING moves with this: a member may walk on a row and still not shoot
        // on one.
        private Vector3 ApproachAnchor(Vector3 targetPoint, bool visible)
        {
            if (visible) return EngagementPoint(targetPoint);
            return TryGetTargetRow(out BlacksiteWorldRow row) ? SearchPoint(row) : Vector3.zero;
        }

        // #1293 R1: the 12 m leash is an outside ship guard's POST and nothing else. It is never a
        // perception or target gate - he sees and shoots past it - but no combat search,
        // reposition, strafe, approach or reload step takes him off it: a point he may walk to is
        // one BlacksiteDeploymentPolicy.CanGuardPursue accepts from the ship rendezvous.
        private bool OffGuardPost(Vector3 point, bool pointOutside)
            => _soldier.isOutside && _soldier.IsShipGuard
                && _soldier.TryGetMission(out var mission) && mission.Objectives.Ship != null
                && !BlacksiteDeploymentPolicy.CanGuardPursue(pointOutside,
                    Vector3.Distance(point, mission.Objectives.Ship.RendezvousPosition));

        // #1219 F2: the one combat movement policy. It runs on every rifle think, before and
        // independently of the fire decision, and its answer is the whole of what the body does:
        // true while a committed step is walking, false when it holds the member where he stands
        // (it raises that hold itself on every false path, so no readiness step needs its own).
        // #1271 S3a: the one false path without a hold is approachBlocked - see its end.
        private bool UpdateTacticalMovement(Vector3 targetPoint, float distance, bool visible, out bool approachBlocked)
        {
            approachBlocked = false;
            if (CurrentState == BlacksiteWeaponState.GrenadeCommitted)
            {
                _soldier.HoldForCombatAction(0.2f);
                TraceMovement(_lastMovementReason, visible, distance, false);
                return false;
            }
            // A visible close attacker can interrupt preferred-position movement. It still needs
            // acquisition, aim alignment and a clear friendly lane before any shot is released.
            if (DefensiveOnly || (visible && distance <= 6f && IsImmediateAttackerContact()))
            {
                EndReposition(); _soldier.HoldForCombatAction(.18f);
                TraceMovement(_lastMovementReason, visible, distance, false);
                return false;
            }

            // A committed move ends on arrival or its bounded deadline, never on a search tick.
            if (IsRepositionActive)
            {
                TraceMovement(_lastMovementReason, visible, distance, true);
                return true;
            }
            if (_repositionCommittedUntil > 0f)
            {
                // #874 N7: a move that ARRIVED before its deadline makes the next search due now. The
                // deadline is a bound on the walk, and _nextCoverSearchAt was dated from it, so an
                // early arrival used to stand where it arrived until deadline + CoverSearchInterval.
                // Round 1 of the 2026-09-23 run, member 4: committed at t=138.08 (log 4767), arrived
                // blind by 139.43 (4933, 4937 remaining=0.44) and searched again only at 142.75
                // (5191) while a squadmate was grabbed at 138.62 and killed at 139.28.
                if (_repositionCommittedUntil > Time.time) _nextCoverSearchAt = Time.time;
                // #1219 F2: the in-band cadence counts from the end of the last step.
                _nextStrafeAt = Time.time + BlacksiteCombatPolicy.StrafeIntervalSeconds;
            }
            EndReposition();

            Vector3 lane = targetPoint - CombatShotOrigin;
            BlacksiteSoldierAI blocker = visible && lane.sqrMagnitude > 0.04f
                ? SquadmateInLane(CombatShotOrigin, lane.normalized, Mathf.Min(MaximumShotRange, lane.magnitude))
                : null;
            bool blocked = blocker != null;
            // #874 N7: a captor holding a squadmate with no line that clears him (ResolveCaptorShot)
            // blocks this lane as surely as a squadmate standing in it, and does not clear on its own:
            // the capture holds the body there. It is a blocked lane from its first think, so the
            // member moves for a line inside the capture rather than after BlockedLaneGraceSeconds.
            bool captorBlocked = visible && IsAllyAttachedTarget() && !_hasCaptorShot;
            if (!blocked) _laneBlockedSince = float.NegativeInfinity;
            else if (float.IsNegativeInfinity(_laneBlockedSince)) _laneBlockedSince = Time.time;
            // The cadence and arrival clock keep running during a burst. Deferring arrival until
            // recovery couples the next step to the fire cycle and repeatedly leaves shots planted.
            // Presentation releases a standing fire clip into aimed locomotion once the body moves.
            bool strafeDue = Time.time >= _nextStrafeAt;
            var reason = BlacksiteCombatPolicy.RepositionReason(distance, visible,
                captorBlocked ? BlacksiteCombatPolicy.BlockedLaneGraceSeconds
                    : blocked ? Time.time - _laneBlockedSince : 0f,
                _roundsWithoutEffect, strafeDue);
            // #874 N7: a contact this member can see down a clear lane and reach with a round is
            // shot from where he stands; the 14 m band ceiling is where he prefers to fight, not a
            // reason to walk away from a shot. Round 1 of the 2026-09-23 run, member 5: Settling on
            // the dog at t=136.07 (log 4539), then OutOfRange at 15.9 m (4576) committed a walk,
            // and the dog was out of his sight when he arrived (5045 LostSight, unseen=4.20).
            if (reason == BlacksiteRepositionReason.OutOfRange && visible && !blocked && !captorBlocked
                && distance <= MaximumShotRange)
                reason = BlacksiteRepositionReason.None;
            // A valid position remains valid when the periodic cover-search clock expires.
            if (reason == BlacksiteRepositionReason.None)
            {
                _soldier.HoldForCombatAction(0.18f);
                TraceMovement(reason, visible, distance, false);
                return false;
            }

            // #1219 F2: the stand-up fight's step. The existing position search, not a new one:
            // centred on the target-relative in-band shuffle (never the formation slot, which is a
            // place to go rather than a step), scored for cover and the 8 m preference inside the
            // unchanged band, with a line from the candidate's own muzzle to the contact so the
            // member can keep shooting from where he walks to. It never closes on the contact and
            // never takes the approach fallback. Nowhere to step is not a stance: he holds, stays
            // in the fight, and tries again one cadence later.
            if (reason == BlacksiteRepositionReason.Strafe)
            {
                _nextStrafeAt = Time.time + BlacksiteCombatPolicy.StrafeIntervalSeconds;
                if (TryFindCombatPosition(targetPoint, distance, requireLine: true, out Vector3 step, useFormationSlot: false))
                {
                    CommitReposition(step, RepositionCommitSeconds);
                    TraceMovement(reason, visible, distance, true);
                    return true;
                }
                _soldier.HoldForCombatAction(0.18f);
                TraceMovement(reason, visible, distance, false);
                return false;
            }

            if (Time.time < _nextCoverSearchAt)
            {
                _soldier.HoldForCombatAction(0.18f);
                TraceMovement(reason, visible, distance, false);
                return false;
            }

            // #947 S12 U2: NoEffect takes the lateral candidate search FIRST, like every other
            // reason - the answer to "this line does not reach the body" is a different vantage at
            // the same range, not a charge into a Giant Sapsucker's reach. It joins OutOfRange and
            // LostSight on the approach fallback only for the case that produced the stall: the
            // lateral search found nowhere to go and the member stood still for the rest of the fight.
            // Closing one 4 m path step is then the only way left to change the geometry, and it
            // stays inside the unchanged 3.5-14 m band.
            // #874 N7: a captor with no clear line joins them. When no lateral spot has a line that
            // clears the held squadmate, one path step toward the captor changes the angle around
            // him, and it is taken only if the step's own muzzle has such a line (below); otherwise
            // he holds and the next due search tries again.
            bool closeOnFailedSearch = reason == BlacksiteRepositionReason.OutOfRange
                || reason == BlacksiteRepositionReason.LostSight
                || reason == BlacksiteRepositionReason.NoEffect
                || captorBlocked;
            Vector3 approach = ApproachAnchor(targetPoint, visible);
            // #874 N7: a member moving because he cannot see the contact, or cannot shoot it past a
            // squadmate, moves only to a place with a line to it. The cover score alone prefers a
            // spot whose body is occluded and whose PEEK clears, which is a spot the member himself
            // cannot see from: round 1, member 4 moved to (-5.06, -100.63) (log 5191) and was still
            // LostSight there 8.8 m from the dog with unseen=9.81 s (5449).
            bool requireLine = reason == BlacksiteRepositionReason.LostSight
                || reason == BlacksiteRepositionReason.BlockedLane;
            bool cover = TryFindCombatPosition(targetPoint, distance, requireLine, out Vector3 destination);
            // #1293 R3: a squadmate standing still in the lane blocks it from every step beside this
            // one, and it does not clear on its own, so the search goes on round the contact's flanks.
            // A captor holding a squadmate is answered by its own captor line (closeOnFailedSearch).
            bool standingBlocker = reason == BlacksiteRepositionReason.BlockedLane && blocker != null
                && blocker.IsSettledForInteraction && !IsAllyAttachedTarget();
            bool flank = !cover && standingBlocker && TryFindFlankPosition(targetPoint, distance, out destination);
            if (cover
                || flank
                || (closeOnFailedSearch
                    && TryFindApproachPoint(approach, out destination)
                    && (!captorBlocked
                        || TryFindCaptorShot(destination + (CombatShotOrigin - _soldier.transform.position), out _))))
            {
                // A member that is moving has answered the magazine; he owes another one before he
                // may claim the same verdict again.
                if (reason == BlacksiteRepositionReason.NoEffect) _roundsWithoutEffect = 0;
                // #874 S13: the approach fallback is the one movement decision a stalled member
                // never produced, so it says so once per committed step rather than only through
                // the reason= line that precedes the search. #1293 R3: so does a flank step, whose
                // anchor is the contact it walks round.
                if (!cover && BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("CombatMovement",
                        $"member={_soldier.MemberIndex}, event={(flank ? "flank" : "approach")}, reason={reason}, visible={visible}, distance={distance:0.0}"
                        + $", anchor={BlacksiteDiagnosticFormat.V(flank ? targetPoint : approach)}, destination={BlacksiteDiagnosticFormat.V(destination)}.");
                CommitReposition(destination, RepositionCommitSeconds);
                TraceMovement(reason, visible, distance, true);
                return true;
            }
            // #1271 S3a: no cover and no approach. A member with a shot left (seen, in range) holds
            // and searches again one interval later, as before. Without one no dwell is raised and
            // the search clock is not advanced: EngageSelectedTarget reports ApproachBlocked, Combat
            // yields the think to the duty, and the next Combat think asks the geometry again.
            // #1293 R3: nowhere to stand clears a standing squadmate, so another contact this member
            // sees down a clear lane is engaged instead; the target commitment still bounds the swap.
            if (standingBlocker) TrySwitchToClearLane();
            if (visible && distance <= MaximumShotRange)
            {
                _nextCoverSearchAt = Time.time + CoverSearchInterval;
                _soldier.HoldForCombatAction(0.18f);
            }
            else approachBlocked = true;
            TraceMovement(reason, visible, distance, false);
            return false;
        }

        // #1219 F2: [CombatMovement] reports every movement decision, rate-limited per member
        // (MovementTraceSeconds) and immediately on a new reason or a newly committed step, so a
        // log can show how much of a fight a member spent moving. The first five keys are the ones
        // the line always carried, in their order; moving=, speed= and moveGoal= are appended.
        private void TraceMovement(BlacksiteRepositionReason reason, bool visible, float distance, bool moving)
        {
            bool changed = reason != _lastMovementReason || moving != _lastMovementMoving;
            _lastMovementReason = reason;
            _lastMovementMoving = moving;
            if (!BlacksiteDiagnostics.Enabled || (!changed && Time.time < _nextMovementTraceAt)) return;
            _nextMovementTraceAt = Time.time + MovementTraceSeconds;
            BlacksiteDiagnostics.Log("CombatMovement",
                $"member={_soldier.MemberIndex}, reason={reason}, visible={visible}, distance={distance:0.0}, state={CurrentState}"
                + $", moving={moving}, speed={_soldier.CombatPlanarSpeed:0.00}, moveGoal={(moving ? BlacksiteDiagnosticFormat.V(_repositionDestination) : "none")}.");
        }

        // Formation first, weapon band second: the awareness-bound slot decides where he ought to
        // be standing, and the band scoring decides whether anything around it is somewhere he can
        // actually shoot from. A slot that satisfies neither falls back to a target-relative shuffle.
        // #874 N7: requireLine keeps only candidates with a line to the contact (see UpdateTacticalMovement).
        // #1219 F2: useFormationSlot false is the in-band step, which is a shuffle around where he
        // stands and never a walk to the slot.
        private bool TryFindCombatPosition(Vector3 targetPoint, float distance, bool requireLine, out Vector3 best,
            bool useFormationSlot = true)
        {
            if (useFormationSlot && HasLiveFormationSlot
                && TryFindBoundedCombatPosition(
                    targetPoint,
                    _formationSlot,
                    SlotSearchMaximumPathLength,
                    requireLine,
                    out best))
            {
                return true;
            }

            return TryFindBoundedCombatPosition(
                targetPoint,
                ResolveTargetRelativeCenter(targetPoint, distance),
                TacticalSearchMaximumPathLength,
                requireLine,
                out best);
        }

        // A distant contact may have no firing position within a single four-metre shuffle.
        // Advance along its observed route, then reassess; never invent a hidden target update.
        private bool TryFindApproachPoint(Vector3 observed, out Vector3 destination)
        {
            destination = default;
            Vector3 origin = _soldier.transform.position;
            // #874 S13: the 3 m projection SearchPoint uses for this exact operation - putting a
            // contact point on the floor before walking to it. The old 1.5 m sample could not reach
            // the ground under an anchor that sits on a body, so the fallback answered false for
            // contacts that were perfectly walkable.
            if (observed == Vector3.zero || !NavMesh.SamplePosition(observed, out var target, 3f, NavMesh.AllAreas)) return false;
            var route = BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(_soldier), origin, target.position, _coverPath);
            if (!route.Planned) return false;
            int cornerCount = ReadCoverCorners();
            if (cornerCount < 2) return false;
            Vector3[] corners = _coverCorners;
            // #874 S13: a partial path is the route AROUND the blocker as far as the mesh reaches,
            // and this method only ever takes one bounded step along it before the next search
            // reassesses. It is accepted only when that reach is progress - the far end has to be
            // at least one reposition step nearer the contact than the member is now - so a
            // partial route that leads away into a dead end is refused exactly as before. The
            // destination itself is still audited physically by WalkRoute below, and the step is
            // handed to the existing navigator through CommitReposition; nothing here steers.
            if (!route.Reachable
                && Vector3.Distance(corners[cornerCount - 1], target.position)
                    > Vector3.Distance(origin, target.position) - MinimumRepositionDistance) return false;
            float remaining = 4f;
            for (int i = 1; i < cornerCount; i++)
            {
                float length = Vector3.Distance(corners[i - 1], corners[i]);
                destination = Vector3.MoveTowards(corners[i - 1], corners[i], remaining);
                if (length >= remaining) break;
                remaining -= length;
            }
            if (HorizontalDistance(origin, destination) < MinimumRepositionDistance
                || OffGuardPost(destination, _soldier.isOutside)) return false;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            return BlacksiteLandingGeometry.WalkRoute(_soldier.transform, origin, destination, mask, out _, out _);
        }

        private Vector3 ResolveTargetRelativeCenter(Vector3 targetPoint, float distance)
        {
            Vector3 origin = _soldier.transform.position;
            Vector3 away = origin - targetPoint;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
                away = -_soldier.transform.forward;
            away.Normalize();
            // Inside the band he only steps sideways; a backwards shift here would walk the whole
            // squad out of the fight one burst at a time.
            float shift = distance < MinimumEngagementDistance
                ? 4.5f
                : distance > MaximumEngagementDistance ? -3.5f : 0.8f;
            return origin + away * shift;
        }

        private bool TryFindBoundedCombatPosition(
            Vector3 targetPoint,
            Vector3 center,
            float maximumPathLength,
            bool requireLine,
            out Vector3 best)
        {
            best = Vector3.zero;
            Vector3 origin = _soldier.transform.position;
            Vector3 away = center - targetPoint;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
            {
                away = origin - targetPoint;
                away.y = 0f;
            }
            if (away.sqrMagnitude < 0.01f)
                away = -_soldier.transform.forward;
            away.Normalize();
            Vector3 lateral = Vector3.Cross(Vector3.up, away).normalized;
            float bestScore = float.NegativeInfinity;

            for (int i = 0; i < 7; i++)
            {
                Vector3 raw;
                if (i == 0)
                {
                    raw = center;
                }
                else
                {
                    float side = (i - 1) % 2 == 0 ? 1f : -1f;
                    float ring = 2.4f + ((i - 1) / 2) * 0.9f;
                    raw = center + lateral * side * ring;
                }
                ConsiderCombatPosition(raw, targetPoint,
                    lateral * (i == 0 || (i - 1) % 2 == 0 ? BlacksiteCombatPolicy.CoverPeekOffset : -BlacksiteCombatPolicy.CoverPeekOffset),
                    maximumPathLength, requireLine, ref best, ref bestScore);
            }

            return bestScore > -1f;
        }

        // #1293 R3: the bearings round the contact, either side of the member's own, a flank search
        // stands at. A squadmate standing at the contact blocks the lanes that come in near his own
        // bearing from it; these bring the lane in from the side.
        private static readonly float[] FlankBearings = { 45f, 70f, 95f };

        // #1293 R3: the blocked-lane search for a squadmate standing in the lane: spots round the
        // contact at the member's own range (inside the band), scored like any position, reached
        // within the slot search's path length and holding a clear lane and a line to the contact. A
        // spot nearer another contact is left out: the nearest-contact choice (TryFindCombatTarget)
        // would hand him that one on his next decision, and the walk would not answer this lane.
        private bool TryFindFlankPosition(Vector3 targetPoint, float distance, out Vector3 best)
        {
            best = Vector3.zero;
            Vector3 away = _soldier.transform.position - targetPoint;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
                away = -_soldier.transform.forward;
            away.Normalize();
            float radius = Mathf.Clamp(distance, MinimumEngagementDistance, MaximumEngagementDistance);
            float bestScore = float.NegativeInfinity;
            for (int i = 0; i < FlankBearings.Length * 2; i++)
            {
                float side = i % 2 == 0 ? 1f : -1f;
                Vector3 bearing = Quaternion.AngleAxis(side * FlankBearings[i / 2], Vector3.up) * away;
                Vector3 raw = targetPoint + bearing * radius;
                if (NearerOtherContact(raw, radius)) continue;
                ConsiderCombatPosition(raw, targetPoint,
                    Vector3.Cross(Vector3.up, bearing) * (side * BlacksiteCombatPolicy.CoverPeekOffset),
                    SlotSearchMaximumPathLength, requireLine: true, ref best, ref bestScore);
            }
            return bestScore > -1f;
        }

        // One candidate of a position search: reachable, a real move, on post, with a clear lane
        // (and, when required, a line) from its own muzzle; kept in best when it outscores it.
        private void ConsiderCombatPosition(Vector3 raw, Vector3 targetPoint, Vector3 peekOffset,
            float maximumPathLength, bool requireLine, ref Vector3 best, ref float bestScore)
        {
            Vector3 origin = _soldier.transform.position;
            if (!NavMesh.SamplePosition(raw, out NavMeshHit navHit, 2.2f, NavMesh.AllAreas))
                return;
            // Repositioning has to actually move him; otherwise the search keeps re-picking the
            // spot he is already standing on and he reads as rooted.
            if (HorizontalDistance(navHit.position, origin) < MinimumRepositionDistance)
                return;
            if (OffGuardPost(navHit.position, _soldier.isOutside))
                return;
            if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(_soldier), origin, navHit.position, _coverPath).Reachable)
                return;

            float pathLength = CoverPathLength();
            if (pathLength > maximumPathLength)
                return;

            // #874 S11 U2 (I6): the scorer below is unchanged; it simply lives in one place now,
            // shared with the mission cover ring, which was scoring a slot on one sightline
            // linecast and preferring the open ground.
            Vector3 bodyPoint = navHit.position + Vector3.up * BlacksiteCombatPolicy.CoverBodySampleHeight;
            float cover = PeekCoverScore(bodyPoint, targetPoint, peekOffset);
            float resultingDistance = Vector3.Distance(navHit.position, targetPoint);
            float bandPenalty = resultingDistance < MinimumEngagementDistance
                ? (MinimumEngagementDistance - resultingDistance) * 3.5f
                : resultingDistance > MaximumEngagementDistance
                    ? (resultingDistance - MaximumEngagementDistance) * 3.5f
                    : 0f;
            Vector3 candidateMuzzle = navHit.position + (CombatShotOrigin - origin);
            Vector3 candidateLane = targetPoint - candidateMuzzle;
            if (candidateLane.sqrMagnitude > .04f
                && IsFriendlyLaneBlocked(candidateMuzzle, candidateLane.normalized, Mathf.Min(MaximumShotRange, candidateLane.magnitude)))
                return;
            // #874 N7: the candidate's own muzzle has to reach the contact - for a captor holding
            // a squadmate, a captor sample that clears him - or it is no answer to lost sight or a
            // blocked lane. The peek above is a shoulder's step to the side at body height, and a
            // spot whose body is covered from the contact is, from where he stands, usually blind.
            if (requireLine && (IsAllyAttachedTarget()
                    ? !TryFindCaptorShot(candidateMuzzle, out _)
                    : IsWorldOccluded(candidateMuzzle, targetPoint)))
                return;
            float score = cover
                - Mathf.Abs(resultingDistance - 8f) * 0.22f
                - bandPenalty
                - pathLength * 0.08f;
            if (score <= bestScore)
                return;
            bestScore = score;
            best = navHit.position;
        }

        // #1293 R3: the blocked lane's tie-break. The nearest other monster this member sees himself
        // that he may engage, in shot range, with a clear lane and a line from his muzzle, is selected
        // through SelectTarget, so the target commitment still decides whether the swap is taken.
        private void TrySwitchToClearLane()
        {
            if (!_soldier.TryGetWorld(out BlacksiteWorldModel world)) return;
            Vector3 muzzle = CombatShotOrigin;
            int slot = _soldier.PerceptionSlot;
            EnemyAI best = null;
            float bestDistance = MaximumShotRange;
            IReadOnlyList<BlacksiteWorldRow> rows = world.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                BlacksiteWorldRow row = rows[i];
                if (!IsOtherCombatContact(row, out EnemyAI enemy) || !row.SeenByMember(slot) || _soldier.IsAllyAttachedContact(enemy))
                    continue;
                Vector3 aim = ResolveEnemyAimPoint(enemy);
                Vector3 lane = aim - muzzle;
                float laneLength = lane.magnitude;
                if (laneLength >= bestDistance || laneLength < .2f
                    || IsFriendlyLaneBlocked(muzzle, lane / laneLength, laneLength)
                    || IsWorldOccluded(muzzle, aim))
                    continue;
                best = enemy;
                bestDistance = laneLength;
            }
            if (best == null) return;
            SelectTarget(null, best);
            if (BlacksiteDiagnostics.Enabled && ReferenceEquals(_enemyTarget, best))
                BlacksiteDiagnostics.Log("CombatMovement",
                    $"member={_soldier.MemberIndex}, event=switch, reason=BlockedLane, target={BlacksiteDiagnosticFormat.Target(best)}, distance={bestDistance:0.0}.");
        }

        // #1293 R3: whether a monster other than this member's target that his target choice could
        // take stands nearer <paramref name="at"/> than <paramref name="range"/>.
        private bool NearerOtherContact(Vector3 at, float range)
        {
            if (!_soldier.TryGetWorld(out BlacksiteWorldModel world)) return false;
            IReadOnlyList<BlacksiteWorldRow> rows = world.Rows;
            for (int i = 0; i < rows.Count; i++)
                if (IsOtherCombatContact(rows[i], out _) && HorizontalDistance(rows[i].Position, at) < range)
                    return true;
            return false;
        }

        // #1293 R3: a squad-table row for a monster other than this member's target that his combat
        // target choice may take: seen by the squad, attackable, engageable and not warning fire only.
        private bool IsOtherCombatContact(BlacksiteWorldRow row, out EnemyAI enemy)
        {
            enemy = row.Subject as EnemyAI;
            return row.Source == BlacksiteWorldSource.Saw && row.Class == BlacksiteWorldClass.Monster
                && enemy != null && enemy != _enemyTarget && IsValidEnemy(enemy) && _soldier.ContactEngageable(enemy)
                && !BlacksiteThreatAdapterRegistry.EngagementRefusedFor(enemy);
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            a.y = 0f;
            b.y = 0f;
            return Vector3.Distance(a, b);
        }

        // #1219 F2: the reposition search reads _coverPath's corners into one reused buffer; the
        // in-band strafe runs this search every StrafeIntervalSeconds, and NavMeshPath.corners
        // allocates a fresh array on every read. Grows once, like the planner's patrol buffer.
        private Vector3[] _coverCorners = new Vector3[16];

        private int ReadCoverCorners()
        {
            int count = _coverPath.GetCornersNonAlloc(_coverCorners);
            while (count >= _coverCorners.Length)
            {
                _coverCorners = new Vector3[_coverCorners.Length * 2];
                count = _coverPath.GetCornersNonAlloc(_coverCorners);
            }
            return count;
        }

        private float CoverPathLength()
        {
            int count = ReadCoverCorners();
            float length = 0f;
            for (int i = 1; i < count; i++)
                length += Vector3.Distance(_coverCorners[i - 1], _coverCorners[i]);
            return length;
        }

        // #874 S11 U2 (I6): the peek-cover half of TryFindBoundedCombatPosition's candidate score,
        // extracted so the mission cover ring in BlacksiteObjectiveManifestController.Players.cs
        // uses this implementation rather than its own. Two rays: does the candidate's own body sit
        // behind something, and can it still see the contact from one shoulder's step to the side.
        // Static and world-only, so a fixture can score a piece of geometry without a member.
        internal static float PeekCoverScore(Vector3 bodyPoint, Vector3 targetPoint, Vector3 peekOffset)
            => BlacksiteCombatPolicy.PeekCoverScore(
                IsWorldOccluded(bodyPoint, targetPoint),
                !IsWorldOccluded(bodyPoint + peekOffset, targetPoint));

        private static bool IsWorldOccluded(Vector3 origin, Vector3 target)
        {
            Vector3 delta = target - origin;
            if (delta.sqrMagnitude < 0.01f)
                return false;
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : ~0;
            BlacksiteHitchTracer.CountPhysicsQuery();
            return Physics.Raycast(origin, delta.normalized, delta.magnitude, mask, QueryTriggerInteraction.Ignore);
        }

        private void CancelCommittedAction()
        {
            _shotsRemaining = 0;
            _burstSize = 0;
            _aimSettleUntil = 0f;
            _reloadStartedAt = 0f;
            _magazineReleased = false;
            _soldier.PresentMagazineStateNetworked(visible: true);
        }

        internal bool HasValidTarget()
        {
            return IsValidPlayer(_playerTarget) || IsValidEnemy(_enemyTarget);
        }

        /// <summary>
        /// #874 G4, #1293 R3: during an open defensive window, re-checks range and LOS (once per
        /// withdrawal decision) so a ship-passage walker stops defending what it cannot see or reach.
        /// </summary>
        internal bool RevalidateDefensiveWindow(Vector3 soldierPosition, float maxRange)
        {
            if (!HasValidTarget()) return false;
            Vector3 point = GetCurrentAimPoint();
            if (point == Vector3.zero) return false;
            if (Vector3.Distance(soldierPosition, point) > maxRange) return false;
            return _soldier.HasCombatLineOfSight(point, _playerTarget, _enemyTarget);
        }

        // #1233 S6: widened to internal for BlacksiteSoldierAI.CombatGoal, unchanged otherwise.
        internal Vector3 GetCurrentAimPoint()
        {
            if (IsValidPlayer(_playerTarget))
                return _soldier.GetCombatAimPoint(_playerTarget);
            if (IsValidEnemy(_enemyTarget))
            {
                // #874 N7: a rifleman with a line to a captor that clears the squadmate it holds aims
                // down that line, so the facing, the sight test and the round all use the sample
                // ResolveCaptorShot chose rather than one that crosses the squadmate.
                if (_hasCaptorShot && IsAllyAttachedTarget())
                    return _enemyTarget.transform.position + _captorShotOffset;
                // #1011 A3 (O3): a captor that is RIDING somebody is not where its transform root
                // says a body of its size would be. BlacksiteVanillaMonsters.PoseCapture parks a
                // clung flea on the victim's own head socket every frame, so the anchor the capture
                // record names is the point the swing has to arrive at and the point the sight test
                // has to clear. Derived from the capture, not from the monster type: a captor that
                // holds its victim instead of riding it answers false here and keeps the unchanged
                // body-sampled aim point below.
                return BlacksiteVanillaMonsters.TryGetCaptureAnchor(_enemyTarget, out Vector3 anchor)
                    ? anchor
                    : ResolveEnemyAimPoint(_enemyTarget);
            }
            return TryGetTargetRow(out BlacksiteWorldRow row) ? RememberedAimPoint(row) : Vector3.zero;
        }

        // #1293 R1: where a remembered contact is aimed at - the row's last-known ROOT lifted to the
        // chest sample the sight test probes, never the predicted point (prediction is for
        // pursuit, not for shooting).
        private static Vector3 RememberedAimPoint(BlacksiteWorldRow row)
            => row.Position + Vector3.up * BlacksiteSightPolicy.ChestSampleHeight;

        // #947 S12 U2: the tallest point on an enemy a levelled rifle inside the band can reach.
        //
        // Derivation. Three measured things decide it, none of them new:
        //  - the muzzle line. CombatMuzzlePosition is a rig anchor with no compile-time value, but
        //    it is bounded: its own documented fallback is GetEyePosition(), 1.75 m
        //    (BlacksiteSightPolicy.EyeHeight of BlacksiteBodyCollision.CenterY 1.0 and Height 1.8),
        //    and a shouldered rifle cannot hang below the body capsule's centre, 1.0 m. 1.5 m is
        //    the shoulder line, the midpoint of that interval.
        //  - the closest square-on engagement the band allows. RepositionReason sends a member
        //    below 3.5 m away, so 3.5 m is the shortest lane a held shot is ever fired down.
        //  - RifleAimToleranceDegrees 10, the per-round alignment gate CanReleaseShot applies.
        // 1.5 + 3.5 * tan(10 degrees) = 1.5 + 0.617 = 2.117 m. 2.0 m is the nearest tenth of a metre
        // below that, so the clamped point still releases a round at the band floor with 0.1 m in
        // hand; the interval's own ends give 1.62 m and 2.37 m, so 2.0 sits inside the band the real
        // rig anchor produces. Above it the shot starts failing HasCombatAimAlignment at 3.5 m and
        // the member holds instead of firing, which is the stall this unit removes.
        //
        // No range constant grows: this only moves a point DOWN a tall body, and a human-sized enemy
        // is unaffected because its eye is within one soldier body height of the torso point.
        private const float EnemyAimCeiling = 2f;

        private static Vector3 ResolveEnemyAimPoint(EnemyAI enemy)
        {
            Vector3 root = enemy.transform.position;
            Vector3 torso = root + Vector3.up * BlacksiteSoldierAI.EnemyTorsoSampleHeight;
            if (enemy.eye == null)
                return torso;
            Vector3 eye = enemy.eye.position;
            // "Within a body height" of the torso point, measured with the soldier's OWN body
            // (BlacksiteBodyCollision.Height 1.8 m) because that is the geometry the rifle, the aim
            // pose and the sight origin are all built from. Every human-sized vanilla enemy keeps
            // its authored eye; a Giant Sapsucker, whose eye sits about 4.9 m up, does not.
            if (Vector3.Distance(eye, torso) <= BlacksiteBodyCollision.Height)
                return eye;
            float eyeHeight = Mathf.Max(0f, eye.y - root.y);
            return root + Vector3.up * Mathf.Min(eyeHeight * .5f, EnemyAimCeiling);
        }

        // #947 S12 U2: where the engagement distance is measured FROM on the contact. The band, the
        // burst choice and the range gate used to measure to the aim point, which for an enemy was
        // its eye: a bird 9.30 m away read as 9.97 m to RepositionReason, MonsterPriorityRange and
        // ThreatRadius while [UAT][Sighting], the exact sighting report and
        // ThreatProximity all said 9.30. Root to root, so every band in the system speaks about one
        // number. A remembered point has no root and is its own answer.
        private Vector3 EngagementPoint(Vector3 aimPoint)
        {
            if (IsValidPlayer(_playerTarget))
                return _playerTarget.transform.position;
            if (IsValidEnemy(_enemyTarget))
                return _enemyTarget.transform.position;
            return aimPoint;
        }

        // #315: the point the aim pose should track. Same source the rifle uses, so the torso,
        // head and muzzle agree; false while there is nothing to aim at.
        internal bool TryGetAimPoint(out Vector3 point)
        {
            point = Vector3.zero;
            if (!HasCombatIntent)
                return false;
            point = GetCurrentAimPoint();
            return point != Vector3.zero;
        }

        // #988 F06 (B3): the LIVE engagement, and only that - the selected contact, the close-in
        // committed to it, a swap the swing lock deferred, the target
        // commitment and the settled aim. Which row this member engages - the key, which player it
        // was, and the contact identity the acquisition ramp is keyed on - is not part of the
        // engagement and is not touched here. That split is what lets a member stop attacking for
        // one hazard frame without unlearning where the contact was; ContinueWithoutContact
        // already draws the same line for a brief occlusion.
        private void ReleaseEngagement()
        {
            // #874 S2.1: no target, no close-in. The melee reach contract cannot outlive the
            // contact it was measured against.
            if (_soldier.UsesBaton) _soldier.ReleaseBatonApproach();
            // #874 S10.1: nor can a swap the swing lock deferred outlive the selection it was
            // deferred from.
            _swingDeferredPlayer = null;
            _swingDeferredEnemy = null;
            _playerTarget = null;
            _enemyTarget = null;
            _targetStickyUntil = 0f;
            _aimSettleUntil = 0f;
            // #874 N7: nor can a captor line outlive the engagement it was found for.
            _hasCaptorShot = false;
        }

        // The engagement AND which row it engaged: the contact is gone, ineligible, expired or
        // replaced. The row itself is the squad table's; the key is read through the acquisition
        // identity ResetAcquisitionRamp drops, so this member's hold on it ends here.
        private void ClearTarget()
        {
            ReleaseEngagement();
            _lastPlayerContact = null;
            // #947 S12 U2: no contact, no magazine verdict.
            _roundsWithoutEffect = 0;
            ResetAcquisitionRamp();
        }

        // #1233 S6: unchanged from S3, deliberately. The rifle's TARGET is gated at selection
        // (BlacksiteSoldierAI.ContactEngageable), so it never aims at a creature without a body;
        // the round itself still lands on any collider of the enemy it was aimed at, because a
        // vanilla enemy's solid physics colliders are not all the EnemyAICollisionDetect trigger,
        // and a single sorted ray that refused them would stop as a World hit on the target's own
        // flank (the #874 N7 CAPTOR_CLEAR row).
        private static EnemyAI ResolveEnemy(Collider collider)
        {
            if (collider == null)
                return null;
            EnemyAICollisionDetect detect = collider.GetComponent<EnemyAICollisionDetect>();
            if (detect != null && detect.mainScript != null)
                return detect.mainScript;
            return collider.GetComponentInParent<EnemyAI>();
        }

        private bool IsValidPlayer(PlayerControllerB player)
        {
            return _soldier.IsPlayerCombatEligible(player);
        }

        private bool IsImmediateAttackerContact() => Time.time <= _immediateAttackerUntil && (_immediateAttacker != null || _immediatePlayerAttacker != null);

        // #1233 S6 (rule 5): the "it hurt or touched us" half of the engagement gate - THIS enemy is
        // the attacker NotifyAttackedByEnemy stamped inside its 3 s window. Read-only over host state.
        internal bool IsImmediateAttacker(EnemyAI enemy)
            => enemy != null && ReferenceEquals(enemy, _immediateAttacker) && Time.time <= _immediateAttackerUntil;

        // #1305 H (C2 attacker): how close the engageable player who hit this member inside the same
        // 3 s window (NotifyImmediateAttacker) stands, banded as ThreatProximity bands; None with no
        // such player. Only THAT contact in band is the attacker exception - another contact in band
        // while the attacker is out of it is not. Read-only over host state.
        internal BlacksiteThreatProximity ImmediatePlayerAttackerProximity
        {
            get
            {
                if (Time.time > _immediateAttackerUntil || !IsValidPlayer(_immediatePlayerAttacker))
                    return BlacksiteThreatProximity.None;
                float distance = Vector3.Distance(_soldier.transform.position, _immediatePlayerAttacker.transform.position);
                return distance <= BlacksiteCombatPolicy.BatonAttackRange ? BlacksiteThreatProximity.MeleeReach
                    : distance <= BlacksiteCombatPolicy.ThreatRadius ? BlacksiteThreatProximity.ThreatRadius
                    : BlacksiteThreatProximity.None;
            }
        }

        // #874 WP F: the weapon's own target gate asks the attackable-contact definition, so the
        // rifle never holds, aims at or fires at an Old Bird. It is not the sight gate - the member
        // still sees and reports it (BlacksiteSoldierAI.CanSightEnemy). #874 N8 (decision 7): asked
        // for THIS member, so its warning bursts at an Old Bird pass until they are spent or the
        // squad is attacked, and a baton member is still refused.
        private bool IsValidEnemy(EnemyAI enemy)
        {
            return BlacksiteThreatAdapterRegistry.IsAttackableBy(enemy, _soldier)
                && enemy != _soldier
                && !(enemy is BlacksiteSoldierAI)
                && enemy.gameObject != null
                && enemy.gameObject.activeInHierarchy;
        }

        private BlacksiteWeaponState CurrentState => _soldier.WeaponSnapshot.State;
        private int Magazine => _soldier.WeaponSnapshot.Magazine;

        private void SetState(BlacksiteWeaponState state)
        {
            BlacksiteWeaponSnapshot snapshot = _soldier.WeaponSnapshot;
            if (snapshot.State == state)
                return;
            BlacksiteDiagnostics.Log("WeaponState", $"member={_soldier.MemberIndex}, from={snapshot.State}, to={state}, magazine={snapshot.Magazine}, target={_playerTarget?.actualClientId.ToString() ?? (_enemyTarget != null ? _enemyTarget.NetworkObjectId.ToString() : "memory")}, point={GetCurrentAimPoint()}.");
            BlacksiteWeaponState previous = snapshot.State;
            bool firingChanged = (previous == BlacksiteWeaponState.FiringBurst) != (state == BlacksiteWeaponState.FiringBurst);
            snapshot.State = state;
            snapshot.Sequence++;
            _soldier.SetWeaponSnapshotServer(snapshot);
            // #1219 F2: the burst's walk cap starts and ends with the burst, not on the next think.
            if (firingChanged) _soldier.RefreshAgentMoveSpeed();
            // #1293 R3: the weapon ready again is an Outcome for the brain.
            if (state == BlacksiteWeaponState.Ready && previous != BlacksiteWeaponState.Disabled)
                _soldier.RequestDecision(BlacksiteDecisionCause.Outcome);
        }

        private void SetMagazine(int magazine)
        {
            BlacksiteWeaponSnapshot snapshot = _soldier.WeaponSnapshot;
            snapshot.Magazine = (byte)Mathf.Clamp(magazine, 0, MagazineCapacity);
            snapshot.Sequence++;
            _soldier.SetWeaponSnapshotServer(snapshot);
        }



        private readonly struct ShotResolution
        {
            internal ShotResolution(
                Vector3 endPoint,
                BlacksiteShotImpactKind impactKind,
                PlayerControllerB player,
                EnemyAI enemy)
            {
                EndPoint = endPoint;
                ImpactKind = impactKind;
                Player = player;
                Enemy = enemy;
            }

            internal Vector3 EndPoint { get; }
            internal BlacksiteShotImpactKind ImpactKind { get; }
            internal PlayerControllerB Player { get; }
            internal EnemyAI Enemy { get; }
        }

        private sealed class RaycastHitDistanceComparer : IComparer<RaycastHit>
        {
            public int Compare(RaycastHit x, RaycastHit y)
            {
                return x.distance.CompareTo(y.distance);
            }
        }
    }

    internal enum BlacksiteShotImpactKind : byte
    {
        None = 0,
        World = 1,
        Player = 2,
        Enemy = 3,
        FriendlySuppressed = 4
    }
}
