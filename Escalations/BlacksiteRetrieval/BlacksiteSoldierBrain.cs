using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #1017 S3: the Unity-bound half of the soldier brain.
    //
    // Everything here is nested INSIDE BlacksiteSoldierAI for the same reason BlacksiteMotionOwner
    // is nested inside BlacksiteNavigator (BlacksiteMotionOwner.cs:59-73): C# accessibility flows
    // inward only, and Sense reads the soldier's own privates. A sibling type would need an
    // accessor per field.
    //
    // #1020 S6, #1293 R3: THE decision core (legacy citations are dated to main 74b3a7dd). A DECISION
    // (Tick) runs only when an event or the committed action's deadline marked the member dirty and
    // the mission's BlacksiteDecisionQueue admitted it; every other Frame runs the CONTINUATION.
    public sealed partial class BlacksiteSoldierAI
    {
        /// <summary>
        /// #1017 S3: the per-tick world view. Built once by <c>Sense()</c>, read by the availability
        /// mask, the decision rule and Plan, and never re-read from the world inside a tick.
        ///
        /// It carries what a phase actually reads and nothing else. The S2 blackboard facts - the
        /// committed route, the tile and explored counts, the squad storey goal and the broker
        /// wait - are deliberately ABSENT: the rule (#1305 H, C2) reads none of them.
        ///
        /// The constructor does every read itself rather than taking twenty arguments; its one
        /// write is the intent producer's own record (<c>LastThreatIntent</c>, the diagnostic's).
        ///
        /// The struct is not <c>readonly</c> for one reason: the duty board is asked LAZILY, only
        /// when <c>IdleWithGoal</c> wins a pass, exactly as the deleted legacy loop did
        /// (<c>Behaviour.cs:136-145</c> at 74b3a7dd), so the three order fields are filled
        /// mid-loop. Every other field is a readonly world fact.
        /// </summary>
        internal struct BlacksiteSense
        {
            internal readonly float Now;
            internal readonly int Member;
            internal readonly Vector3 Position;

            // ---- this member's own perception ----
            internal readonly BlacksiteThreatProximity Threat;
            /// <summary>
            /// #1011 A3, #1019 S5: the ONE intent producer's answer, sampled here once per decision.
            /// #1305 H (C2): sampled whatever the traversal state, because an attacking player or a
            /// rescue preempts an entrance use and the brain-level committed crossing.
            /// </summary>
            internal readonly BlacksiteThreatIntent Intent;

            // ---- traversal ownership: who may be offered a stop at all ----
            internal readonly bool CommittedTraversal, EntranceTraversalActive, Jumping;

            // ---- squad facts ----
            internal readonly bool ProtectedCarrier, Withdrawing;

            // ---- the duty board, filled lazily mid-loop
            //      (the deleted Behaviour.cs:136-145 at 74b3a7dd) ----
            internal bool HasOrder;
            internal BlacksiteSquadDirector Director;
            internal BlacksiteMemberOrder Order;

            // ---- #1018 S4 (design note B.4, G3): the assignment, filled by the SAME lazy resolve.
            internal bool HasAssignment;
            internal BlacksiteSquadAssignment Assignment;
            internal int AssignmentSequence;

            internal BlacksiteSense(BlacksiteSoldierAI soldier, float now)
            {
                Now = now;
                Member = soldier.MemberIndex;
                Position = soldier.transform.position;

                // #874 S7.2: the ONE perception input the rule receives, no vision, no side effects.
                Threat = soldier.ThreatProximity();
                Intent = soldier.ResolveThreatIntent();

                CommittedTraversal = soldier.HasCommittedJobTraversal;
                EntranceTraversalActive = soldier._interactionCoordinator != null
                    && soldier._interactionCoordinator.IsEntranceTraversalActive;
                Jumping = soldier._navigator != null && soldier._navigator.IsJumping;

                ProtectedCarrier = soldier.IsObjectiveCarrier;
                Withdrawing = soldier._transportLost || soldier.IsRaidWithdrawing
                    || now < soldier._raidRegroupUntil;

                HasOrder = false;
                Director = null;
                Order = default;
                HasAssignment = false;
                Assignment = default;
                AssignmentSequence = 0;
            }
        }

        /// <summary>
        /// #1017 S3: what a tick does with the body. S3 has two kinds; <c>Move</c> - the brain
        /// issuing a goal of its own through the navigator's request path - arrives with S4/S5,
        /// because every legacy decision that moves a member does it inside an executor and S3 owns
        /// WHICH action runs rather than how it moves.
        /// </summary>
        internal enum BlacksiteBrainActionKind
        {
            /// <summary>"Stand here, facing there, for this reason, until this time" - invariant 1.</summary>
            Stand,
            /// <summary>
            /// ONE existing executor owns the tick and makes its own single request:
            /// <c>ExecuteSquadOrder</c>, <c>TryExecuteSelectedCombat</c>,
            /// <c>TryExecuteRaidWithdrawal</c> or
            /// <c>BlacksiteInteractionCoordinator.TickActiveInteraction</c>.
            /// </summary>
            Delegate
        }

        /// <summary>
        /// #1017 S3: the one thing a tick decided. Never default, never invalid: <c>Plan</c> has a
        /// <c>default:</c> arm and it is a Stand, and <c>Tick</c> asserts <see cref="IsValid"/>
        /// before it executes.
        /// </summary>
        internal readonly struct BlacksiteBrainAction
        {
            internal readonly BlacksiteBrainActionKind Kind;
            /// <summary>What <c>CommitBehaviour</c> records. Never <see cref="BlacksiteBehaviour.None"/>.</summary>
            internal readonly BlacksiteBehaviour Behaviour;
            /// <summary>
            /// <c>CommitBehaviour</c>'s reason string, verbatim from the legacy vocabulary. An
            /// executor that reports its own reason (Combat, the entrance approach) replaces this
            /// one at execution time exactly as the legacy switch does.
            /// </summary>
            internal readonly string Reason;
            internal readonly Vector3 Position, Facing;
            internal readonly bool HasFacing;
            /// <summary>Absolute <c>Time.time</c> expiry. A Stand REQUIRES it.</summary>
            internal readonly float Until;
            /// <summary>Stand only: the hold reason the idle Action declares (<c>no-order-hold</c>).</summary>
            internal readonly string HoldReason;
            /// <summary>Stand only: the idle Action's cause. Null is the no-agent guard, which holds nothing.</summary>
            internal readonly string HoldCause;

            private BlacksiteBrainAction(
                BlacksiteBrainActionKind kind,
                BlacksiteBehaviour behaviour,
                string reason,
                Vector3 position,
                Vector3 facing,
                bool hasFacing,
                float until,
                string holdReason,
                string holdCause)
            {
                Kind = kind;
                Behaviour = behaviour;
                Reason = reason;
                Position = position;
                Facing = facing;
                HasFacing = hasFacing;
                Until = until;
                HoldReason = holdReason;
                HoldCause = holdCause;
            }

            /// <summary>Invariant 1, as a field the tick asserts before it executes.</summary>
            internal bool IsValid => Behaviour != BlacksiteBehaviour.None
                                    && !string.IsNullOrEmpty(Reason)
                                    && (Kind != BlacksiteBrainActionKind.Stand || Until > 0f);

            /// <summary>
            /// A stand. <paramref name="behaviour"/> is what is committed - the fallback stands as
            /// <c>IdleWithGoal</c>, exactly as the legacy terminators do, and the no-agent guard
            /// stands as <c>Hold</c>.
            /// </summary>
            internal static BlacksiteBrainAction Stand(
                BlacksiteBehaviour behaviour,
                Vector3 at,
                Vector3 facing,
                bool hasFacing,
                string reason,
                string holdReason,
                float until,
                string holdCause = null)
                => new BlacksiteBrainAction(BlacksiteBrainActionKind.Stand, behaviour, reason,
                    at, facing, hasFacing, until, holdReason, holdCause);

            internal static BlacksiteBrainAction Delegate(BlacksiteBehaviour behaviour, string reason)
                => new BlacksiteBrainAction(BlacksiteBrainActionKind.Delegate, behaviour, reason,
                    Vector3.zero, Vector3.zero, false, 0f, null, null);
        }

        /// <summary>
        /// #1017 S3, #1293 R3: the event-driven decision core. Nothing a client can see changes
        /// here: the brain writes no network state and no <c>NavMeshAgent</c> field and takes no
        /// lease; a Stand runs the idle Action and a Delegate hands the decision to one executor.
        /// </summary>
        internal sealed class BlacksiteSoldierBrain
        {
            private readonly BlacksiteSoldierAI _soldier;

            /// <summary>
            /// #1293 R2, R3: the declared dwell (and wake) of the brain's own Stands - the fallback
            /// hold, the candidates-exhausted terminator and the no-agent guard.
            /// </summary>
            private const float FallbackHoldSeconds = 15f;
            /// <summary>
            /// #1017 S3: the ONE hold reason the brain's fallback Stand declares. <c>HoldWithoutOrder</c>
            /// raises exactly this reason for itself when the member is already at its hold point.
            /// </summary>
            private const string FallbackHoldReason = "no-order-hold";

            /// <summary>
            /// The Delegate targets. <c>None</c> (the <c>CommittedTraversal</c> backstop and every
            /// exclusive commit) has no executor and no continuation; the three order behaviours
            /// share <see cref="SquadOrder"/>'s one executor.
            /// </summary>
            private enum DelegateTarget
            {
                None = 0,
                Interaction = 1,
                Withdrawal = 3,
                Combat = 4,
                SquadOrder = 5
            }

            private BlacksiteBehaviour _lastGoal;
            private BlacksiteBrainAction _lastAction;
            private DelegateTarget _lastTarget;
            private float _lastTickAt;
            // The assignment the committed Stand's continuation runs ExecuteIdleAction on.
            private BlacksiteSquadAssignment _standAssignment;
            // The committed action's own deadline (absolute Time.time), 0 when it has none.
            private float _wakeAt;
            // The committed continuation reported its own end and is waiting for the decision.
            private bool _continuationEnded;
            // A brain that has never decided owes its first decision (Spawned); see Reset.
            private bool _dirty = true;
            private BlacksiteDecisionCause _cause = BlacksiteDecisionCause.Spawned;
            // The threat band the last decision scored; Frame raises World when it moves.
            private BlacksiteThreatProximity _decidedThreat;
            // Whether the member had traversal work at the last decision - an interaction task
            // (HasActiveWorldInteraction); a change is Outcome.
            private bool _decidedTask;
            // The committed Combat's engagement has been held; its end is one Outcome.
            private bool _combatEngaged;
            // Inside this brain's own Tick: requests its own executor raises are dropped.
            private bool _inTick;
            private int _decisions;
            private int _invalidations;

            internal BlacksiteBehaviour LastGoal => _lastGoal;
            internal BlacksiteBrainAction LastAction => _lastAction;
            internal float LastTickAt => _lastTickAt;
            /// <summary>A decision is owed: an event marked this member since its last decision.</summary>
            internal bool Dirty => _dirty;
            /// <summary>The cause of the owed (or last) decision; <c>None</c> for a forced one.</summary>
            internal BlacksiteDecisionCause LastCause => _cause;
            /// <summary>#1293 R3: the decision sequence number, never reset ("this decision or the previous one").</summary>
            internal int Decisions => _decisions;

            internal BlacksiteSoldierBrain(BlacksiteSoldierAI soldier)
            {
                _soldier = soldier;
            }

            /// <summary>
            /// Death, despawn and redeploy: no committed action, wake or premise survives, and the
            /// member owes its first decision (Spawned), as a new brain does.
            /// </summary>
            internal void Reset()
            {
                _lastGoal = BlacksiteBehaviour.None;
                _lastAction = default;
                _lastTarget = DelegateTarget.None;
                _lastTickAt = 0f;
                _standAssignment = default;
                _wakeAt = 0f;
                _continuationEnded = false;
                _dirty = true;
                _cause = BlacksiteDecisionCause.Spawned;
                _decidedThreat = BlacksiteThreatProximity.None;
                _decidedTask = false;
                _combatEngaged = false;
                _reportedKnown = false;
            }

            /// <summary>
            /// #1293 R3: "decide now". Marks the member dirty (keeping the first cause) and queues it
            /// on its mission's <see cref="BlacksiteDecisionQueue"/>; ignored inside its own
            /// <see cref="Tick"/>. Unlike <see cref="Reset"/> it keeps the committed action.
            /// </summary>
            internal void Invalidate(BlacksiteDecisionCause cause)
            {
                if (_inTick) return;
                _invalidations++;
                if (!_dirty)
                {
                    _dirty = true;
                    _cause = cause;
                }
                if (TryGetQueue(out BlacksiteDecisionQueue queue)) queue.Mark(_soldier.NetworkObjectId);
            }

            /// <summary>
            /// #1020 S6: how many "decide now" requests this member accepted. Observation only, read
            /// as a delta by <c>AlertPropagationChecks</c>; a counter, not <see cref="Dirty"/>, because
            /// a member that never decided may already be dirty. <see cref="Reset"/> leaves it.
            /// </summary>
            internal int Invalidations => _invalidations;

            // =================================================================================
            // The frame
            // =================================================================================

            /// <summary>
            /// #1293 R3: the tail of <c>BlacksiteSoldierAI.Update</c>, every server frame. Raises an
            /// event when a premise of the last decision changed (agent lost or back: Outcome; threat
            /// band: World; traversal work begun or ended: Outcome) or its wake passed, then runs
            /// either a decision (dirty and admitted) or the committed action's continuation.
            /// </summary>
            internal void Frame()
            {
                BlacksiteSoldierAI soldier = _soldier;
                bool agentReady = soldier._agent != null && soldier._agent.enabled;
                // The guard is the Stand with no hold cause (Tick's first branch).
                bool guardCommitted = _lastAction.Kind == BlacksiteBrainActionKind.Stand
                    && _lastAction.Behaviour == BlacksiteBehaviour.Hold && _lastAction.HoldCause == null;
                if (!_dirty && agentReady == guardCommitted && (guardCommitted || _decisions > 0))
                    Raise(BlacksiteDecisionCause.Outcome);
                if (!_dirty && _decisions > 0 && soldier.ThreatProximity() != _decidedThreat)
                    Raise(BlacksiteDecisionCause.World);
                if (!_dirty && _decisions > 0 && soldier.HasActiveWorldInteraction != _decidedTask)
                    Raise(BlacksiteDecisionCause.Outcome);
                if (_wakeAt > 0f && Time.time >= _wakeAt)
                {
                    _wakeAt = 0f;
                    Raise(BlacksiteDecisionCause.Wake);
                }
                if (_dirty && Admitted())
                {
                    Tick();
                    return;
                }
                if (!agentReady) return;
                soldier.ApplyAgentMoveSpeed();
                Continue();
            }

            /// <summary>
            /// The committed action's per-frame half: no selection, scan or commit. An interaction
            /// ticks; a Stand runs the idle Action; a fight its baton
            /// swing, ending when the controller releases the engagement; an order its facing.
            /// </summary>
            private void Continue()
            {
                BlacksiteSoldierAI soldier = _soldier;
                // The coordinator advances a task at most once per frame.
                soldier._interactionCoordinator?.TickActiveInteraction();
                if (_continuationEnded) return;
                if (_lastAction.Kind == BlacksiteBrainActionKind.Stand)
                {
                    if (_lastAction.HoldCause != null)
                        soldier.ExecuteIdleAction(in _standAssignment, _lastAction.HoldCause,
                            out _, out _, out _, out _);
                    return;
                }
                switch (_lastTarget)
                {
                    case DelegateTarget.Combat:
                        if (soldier.UsesBaton) soldier.ContinueBatonCombat();
                        // The controller dropping the engagement it held ends the fight: one Outcome.
                        bool engaged = soldier._combatController != null && soldier._combatController.HasCombatIntent;
                        if (engaged) _combatEngaged = true;
                        else if (_combatEngaged)
                        {
                            _continuationEnded = true;
                            Raise(BlacksiteDecisionCause.Outcome);
                        }
                        break;
                    case DelegateTarget.SquadOrder:
                        soldier.ContinueOrderFacing();
                        break;
                }
            }

            /// <summary>Whether this dirty member decides now; a member with no mission is its own queue.</summary>
            private bool Admitted()
                => !TryGetQueue(out BlacksiteDecisionQueue queue)
                   || queue.Admit(_soldier.NetworkObjectId, Time.frameCount);

            // An event this brain observes itself (a premise, a wake, a continuation's end): the brain
            // and, for a planner cause, the planner's dirty set, as RequestDecision marks both. Never
            // raised inside this brain's Tick.
            private void Raise(BlacksiteDecisionCause cause)
            {
                Invalidate(cause);
                _soldier.MarkPlannerFor(cause);
            }

            private bool TryGetQueue(out BlacksiteDecisionQueue queue)
            {
                queue = _soldier.TryGetMission(out BlacksiteRetrievalMissionController mission)
                    ? mission.DecisionQueue
                    : null;
                return queue != null;
            }

            /// <summary>#1293 R3: the committed action's own next deadline (0: none): a Stand's
            /// <see cref="BlacksiteBrainAction.Until"/>, else the dwell end and its executor's deadlines.</summary>
            private float WakeFor(in BlacksiteBrainAction action, DelegateTarget target, float now)
            {
                if (action.Kind == BlacksiteBrainActionKind.Stand) return action.Until;
                if (target == DelegateTarget.None) return 0f;
                BlacksiteSoldierAI soldier = _soldier;
                float wake = Earliest(0f, soldier.Navigator.DwellUntil, now);
                switch (target)
                {
                    case DelegateTarget.Withdrawal:
                        wake = Earliest(wake, soldier._raidRegroupUntil, now);
                        wake = Earliest(wake, soldier._defendUntil, now);
                        if (soldier._transportLost) wake = Earliest(wake, _nextStrandedExitAt, now);
                        break;
                    case DelegateTarget.Combat:
                        if (soldier._combatController != null)
                            wake = Earliest(wake, soldier._combatController.NextDeadline(now), now);
                        break;
                }
                return wake;
            }

            // The earlier future deadline.
            private static float Earliest(float wake, float deadline, float now)
                => deadline > now && (wake <= 0f || deadline < wake) ? deadline : wake;

            // =================================================================================
            // The tick
            // =================================================================================

            /// <summary>
            /// ONE decision (forced by a direct call): the no-agent guard, Sense, Available, the fixed
            /// rule (<see cref="BlacksiteBehaviourSelector.Decide"/>), the lazy duty resolve, Plan, one
            /// Execute and one <c>CommitBehaviour</c>. A kind whose executor refuses is masked in
            /// <c>rejected</c> and the rule is asked again, at most
            /// <see cref="BlacksiteBehaviourSelector.CandidateCount"/> times.
            /// </summary>
            internal void Tick()
            {
                BlacksiteDecisionCause cause = _dirty ? _cause : BlacksiteDecisionCause.None;
                _dirty = false;
                _cause = cause;
                _decisions++;
                _wakeAt = 0f;
                _continuationEnded = false;
                if (TryGetQueue(out BlacksiteDecisionQueue queue)) queue.Remove(_soldier.NetworkObjectId);
                float now = Time.time;
                _inTick = true;
                try
                {
                    Decide(now, cause);
                }
                finally
                {
                    _inTick = false;
                }
                _wakeAt = WakeFor(in _lastAction, _lastTarget, now);
                _combatEngaged = _lastTarget == DelegateTarget.Combat && _soldier._combatController != null
                    && _soldier._combatController.HasCombatIntent;
            }

            private void Decide(float now, BlacksiteDecisionCause cause)
            {
                BlacksiteSoldierAI soldier = _soldier;
                _lastTickAt = now;

                // No agent: Hold, no lease; Frame raises the Outcome when the agent is back.
                if (soldier._agent == null || !soldier._agent.enabled)
                {
                    _decidedThreat = soldier.ThreatProximity();
                    _decidedTask = soldier.HasActiveWorldInteraction;
                    var guard = BlacksiteBrainAction.Stand(
                        BlacksiteBehaviour.Hold, soldier.transform.position, Vector3.zero, false,
                        "navigation-unavailable", null,
                        now + FallbackHoldSeconds);
                    Commit(BlacksiteBehaviour.Hold, in guard, DelegateTarget.None, default);
                    soldier.CommitBehaviour(BlacksiteBehaviour.Hold, "navigation-unavailable", soldier.transform.position);
                    Report(now, BlacksiteBehaviour.Hold, in guard, "navigation-unavailable", cause);
                    return;
                }
                soldier.ApplyAgentMoveSpeed();

                BlacksiteSense sense = Sense(now);
                _decidedThreat = sense.Threat;
                _decidedTask = soldier.HasActiveWorldInteraction;
                uint available = Available(in sense, out bool perception);
                // The deleted perception block (Behaviour.cs:77-87 at 74b3a7dd): these locals stay false
                // unless it ran.
                bool carrier = sense.ProtectedCarrier && perception;
                bool withdrawing = sense.Withdrawing && perception;
                // #1305 H (C2): the facts the rule reads. `attacker` is a rescue in band, or the player
                // the damage path stamped (CombatController.NotifyImmediateAttacker) in band himself.
                bool inBand = BlacksiteBehaviourSelector.InBand(sense.Intent, sense.Threat, carrier);
                bool attacker = BlacksiteBehaviourSelector.Attacker(inBand, sense.Intent,
                    soldier._combatController != null ? soldier._combatController.ImmediatePlayerAttackerProximity
                        : BlacksiteThreatProximity.None, carrier);
                bool fighting = BlacksiteBehaviourSelector.Fighting(sense.Intent);

                uint rejected = 0u;
                bool dutyResolved = false;
                for (int attempt = 0; attempt < BlacksiteBehaviourSelector.CandidateCount; attempt++)
                {
                    BlacksiteBehaviour goal = BlacksiteBehaviourSelector.Decide(
                        available & ~rejected, inBand, attacker, carrier, fighting);

                    // The deleted duty-resolve re-entry (Behaviour.cs:136-145 at 74b3a7dd), kept
                    // as a re-entry rather than hoisted into Sense: the board is asked ONLY when IdleWithGoal has won a
                    // pass, which is what stops a member with an actionable combat,
                    // withdrawal or traversal from poking the board at all. director.TryGetMemberOrder
                    // writes duty.Order, and that write rate is S4's to change, not S3's.
                    if (goal == BlacksiteBehaviour.IdleWithGoal && !dutyResolved)
                    {
                        dutyResolved = true;
                        ResolveDuty(ref sense);
                        if (sense.HasOrder)
                        {
                            available |= BlacksiteBehaviourSelector.Bit(ClassifyOrder(sense.Order, carrier));
                            continue;
                        }
                    }

                    // The deleted order arm (Behaviour.cs:213-217 at 74b3a7dd): an order arm can be reached
                    // the perception block offered ObjectiveJob for a protected carrier, so the
                    // board is resolved here too if it has not been already.
                    if (!dutyResolved && IsOrderBehaviour(goal))
                    {
                        dutyResolved = true;
                        ResolveDuty(ref sense);
                    }

                    BlacksiteBrainAction action = Plan(in sense, goal, carrier);
                    // Invariant 1: Plan's default arm is a Stand; an invalid action is substituted.
                    if (!action.IsValid) action = Fallback(in sense, "plan-invalid", "plan-invalid");

                    bool handled = Execute(in sense, in action, withdrawing,
                        out string reason, out Vector3? executed);
                    if (handled)
                    {
                        Commit(goal, in action, TargetFor(action), in sense.Assignment);
                        soldier.CommitBehaviour(goal, reason, executed);
                        Report(now, goal, in action, reason, cause);
                        return;
                    }

                    // #874 S4.0b: a candidate with no work is excluded for THIS decision only.
                    rejected |= BlacksiteBehaviourSelector.Bit(goal);
                }

                // The deleted candidate-exhausted terminator (Behaviour.cs:253-254): unreachable, kept.
                soldier.ExecuteIdleAction(in sense.Assignment, "behaviour candidates exhausted",
                    out _, out _, out Vector3 exhaustedGoal, out _);
                var exhausted = BlacksiteBrainAction.Stand(
                    BlacksiteBehaviour.IdleWithGoal, exhaustedGoal, Vector3.zero, false,
                    "fallback-hold", FallbackHoldReason,
                    now + FallbackHoldSeconds,
                    "behaviour candidates exhausted");
                Commit(BlacksiteBehaviour.IdleWithGoal, in exhausted, DelegateTarget.None, in sense.Assignment);
                soldier.CommitBehaviour(BlacksiteBehaviour.IdleWithGoal, "fallback-hold", exhaustedGoal);
                Report(now, BlacksiteBehaviour.IdleWithGoal, in exhausted, "fallback-hold", cause);
            }

            // The committed action: what the continuation runs and the wake is dated from.
            private void Commit(BlacksiteBehaviour goal, in BlacksiteBrainAction action, DelegateTarget target,
                in BlacksiteSquadAssignment assignment)
            {
                _lastGoal = goal;
                _lastAction = action;
                _lastTarget = target;
                _standAssignment = assignment;
            }

            /// <summary>
            /// #1017 S3, #1293 R3: the commit for the Update ladder's MANDATORY returns (motors,
            /// capture, stun, monster evade) that never reach <see cref="Frame"/>: records the caller's
            /// behaviour, replaces the committed action and marks the member dirty.
            /// </summary>
            internal void CommitExclusive(BlacksiteBehaviour behaviour, string reason)
            {
                float now = Time.time;
                _lastTickAt = now;
                var action = BlacksiteBrainAction.Delegate(behaviour, reason);
                Commit(behaviour, in action, DelegateTarget.None, default);
                _wakeAt = 0f;
                _continuationEnded = false;
                if (!_dirty) Invalidate(BlacksiteDecisionCause.Exclusive);
                _soldier.CommitBehaviour(behaviour, reason);
                Report(now, behaviour, in action, reason, BlacksiteDecisionCause.Exclusive);
            }

            private static bool IsOrderBehaviour(BlacksiteBehaviour behaviour)
                => behaviour == BlacksiteBehaviour.ObjectiveJob
                || behaviour == BlacksiteBehaviour.SquadOrder;

            // =================================================================================
            // Sense
            // =================================================================================

            private BlacksiteSense Sense(float now)
                => new BlacksiteSense(_soldier, now);

            /// <summary>The duty board query, once per decision at most; it writes nothing.</summary>
            private void ResolveDuty(ref BlacksiteSense sense)
            {
                bool hasOrder = _soldier.TryResolveBehaviourOrder(
                    out BlacksiteSquadDirector director, out BlacksiteMemberOrder order);
                // #1018 S4: the assignment owns the order when live; otherwise the legacy resolve answers.
                BlacksiteSquadAssignment assignment = default;
                bool hasAssignment = _soldier.TryGetMission(out var mission)
                    && mission.Objectives != null
                    && mission.Objectives.Planner.TryGetAssignment(
                        _soldier.NetworkObjectId, out assignment);
                // #1018 S4 (S12): HasOrder stays the legacy resolve's verdict; the assignment is extra.
                if (hasAssignment) order = assignment.ToMemberOrder();
                sense.HasOrder = hasOrder;
                sense.Director = director;
                sense.Order = order;
                sense.HasAssignment = hasAssignment;
                sense.Assignment = assignment;
                sense.AssignmentSequence = hasAssignment ? assignment.Sequence : 0;
            }

            // =================================================================================
            // Available - the port of the deleted availability block (Behaviour.cs:56-116 at 74b3a7dd)
            // =================================================================================

            /// <summary>
            /// The availability mask, over Sense facts, in the legacy order and with the legacy
            /// perception-block boundary.
            /// #1293 R1: the block's drive-perception scan is deleted; nothing read what it wrote.
            ///
            /// <paramref name="perception"/> reports whether that block ran, because the legacy
            /// <c>protectedCarrier</c> and <c>withdrawing</c> locals are both false whenever it did not.
            /// </summary>
            private uint Available(in BlacksiteSense sense, out bool perception)
            {
                BlacksiteSoldierAI soldier = _soldier;
                uint available = BlacksiteBehaviourSelector.Bit(BlacksiteBehaviour.IdleWithGoal);
                // #991: a member in the air joins the committed set.
                if (sense.CommittedTraversal || sense.Jumping)
                    available |= BlacksiteBehaviourSelector.Bit(BlacksiteBehaviour.CommittedTraversal);
                if (sense.EntranceTraversalActive)
                    available |= BlacksiteBehaviourSelector.Bit(BlacksiteBehaviour.EntranceTraversal);
                perception = (available & (BlacksiteBehaviourSelector.Bit(BlacksiteBehaviour.CommittedTraversal)
                    | BlacksiteBehaviourSelector.Bit(BlacksiteBehaviour.EntranceTraversal))) == 0;
                if (perception)
                {
                    if (sense.Withdrawing)
                        available |= BlacksiteBehaviourSelector.Bit(BlacksiteBehaviour.Withdrawal);
                    else if (soldier._combatController != null
                        && (soldier._combatController.DefendingDuringWithdrawal
                            || soldier._combatController.CombatSuspendedForThreat))
                    {
                        soldier._combatController.DefendingDuringWithdrawal = false;
                        soldier._combatController.CombatSuspendedForThreat = false;
                    }
                    available |= BlacksiteBehaviourSelector.Bit(BlacksiteBehaviour.Combat);
                    if (sense.ProtectedCarrier)
                        available |= BlacksiteBehaviourSelector.Bit(BlacksiteBehaviour.ObjectiveJob);
                }
                // #874 S7.2, R2 of the 2026-09-09 run: with a traversal bit set the Combat BIT alone
                // still joins on a live threat, so a member walking to an entrance is not blind to a
                // contact eight metres away. The rule decides the rest.
                else if (sense.Threat != BlacksiteThreatProximity.None)
                    available |= BlacksiteBehaviourSelector.Bit(BlacksiteBehaviour.Combat);
                return available;
            }

            // =================================================================================
            // Plan - the port of the deleted candidate switch (Behaviour.cs:170-241 at 74b3a7dd)
            // =================================================================================

            /// <summary>Exactly one action, never invalid; its side effects sit in <see cref="ExecuteDelegate"/>.</summary>
            private BlacksiteBrainAction Plan(in BlacksiteSense sense, BlacksiteBehaviour goal, bool carrier)
            {
                switch (goal)
                {
                    case BlacksiteBehaviour.CommittedTraversal:
                        // Defensive backstop if a traversal became committed during this decision.
                        // No executor: the motor already owns the body.
                        return BlacksiteBrainAction.Delegate(goal, "committed-traversal");
                    case BlacksiteBehaviour.EntranceTraversal:
                        return BlacksiteBrainAction.Delegate(goal, "entrance-traversal");
                    case BlacksiteBehaviour.Withdrawal:
                        return BlacksiteBrainAction.Delegate(goal, "withdrawal-or-survivor");
                    case BlacksiteBehaviour.Combat:
                        // The executor reports its own reason; this is the one it reports on the
                        // ordinary engagement path and it is replaced by whatever comes back.
                        return BlacksiteBrainAction.Delegate(goal, "combat-contact");
                    case BlacksiteBehaviour.ObjectiveJob:
                    case BlacksiteBehaviour.SquadOrder:
                        return BlacksiteBrainAction.Delegate(goal, carrier ? "carrier" : "squad-order");
                    default:
                        // Invariant 1's default arm, and it is a Stand (design note C.2).
                        return Fallback(in sense, "fallback-hold", "no actionable behaviour");
                }
            }

            /// <summary>
            /// The Stand every decision terminates on. It stands at the idle Action's own goal - the
            /// point that action owns and re-sites on its own phase clock - faces the order's
            /// preferred facing when there is an order, names <c>no-order-hold</c> as its lease
            /// reason (<c>OwnerFor</c> maps it to <c>Idle</c>, the lowest priority in the table,
            /// ceiling 15 s) and carries an expiry.
            /// </summary>
            private BlacksiteBrainAction Fallback(in BlacksiteSense sense, string reason, string cause)
                => BlacksiteBrainAction.Stand(
                    BlacksiteBehaviour.IdleWithGoal,
                    _soldier.HasIdleGoal ? _soldier.IdleGoal : sense.Position,
                    sense.Order.PreferredFacing,
                    sense.HasOrder,
                    reason,
                    FallbackHoldReason,
                    sense.Now + FallbackHoldSeconds,
                    cause);

            // =================================================================================
            // Execute
            // =================================================================================

            /// <summary>A Stand runs the idle Action, or a Delegate one executor; no agent write, no lease.</summary>
            private bool Execute(
                in BlacksiteSense sense,
                in BlacksiteBrainAction action,
                bool withdrawing,
                out string reason,
                out Vector3? goal)
            {
                if (action.Kind == BlacksiteBrainActionKind.Delegate)
                    return ExecuteDelegate(in sense, in action, withdrawing, out reason, out goal);

                reason = action.Reason;
                // #1018 S4 (G2): the decision enters ExecuteIdleAction once, the continuation every frame.
                string cause = action.HoldCause ?? "no actionable behaviour";
                _soldier.ExecuteIdleAction(in sense.Assignment, cause,
                    out _, out _, out Vector3 idleGoal, out _);
                goal = idleGoal;
                return true;
            }

            /// <summary>One executor per decision; a not-handled verdict re-selects.</summary>
            private bool ExecuteDelegate(
                in BlacksiteSense sense,
                in BlacksiteBrainAction action,
                bool withdrawing,
                out string reason,
                out Vector3? goal)
            {
                BlacksiteSoldierAI soldier = _soldier;
                reason = action.Reason;
                goal = null;

                DelegateTarget target = TargetFor(action.Behaviour);
                // Combat is a probe even when it has nothing to do. Only proven
                // ownership may release the scan; the executors do so at their accepted body actions.
                if (target == DelegateTarget.None || target == DelegateTarget.Interaction
                    || target == DelegateTarget.Withdrawal
                    || (target == DelegateTarget.SquadOrder && sense.HasOrder
                        && (!sense.Order.HasDutyKind || sense.Order.DutyKind != BlacksiteJobKind.Guard)))
                    soldier.Navigator.EndPatrolAlert();
                if (target == DelegateTarget.None) return true;   // the CommittedTraversal backstop

                bool handled;
                switch (target)
                {
                    case DelegateTarget.Interaction:
                        soldier._interactionCoordinator.TickActiveInteraction();
                        handled = true;
                        break;
                    case DelegateTarget.Withdrawal:
                        // #874 S7.2: Combat yielded, so the withdrawal takes its restraint back.
                        if (soldier._combatController != null) soldier._combatController.CombatSuspendedForThreat = false;
                        handled = soldier.TryExecuteRaidWithdrawal();
                        break;
                    case DelegateTarget.Combat:
                        // #874 S7.2: Combat can win over an AVAILABLE withdrawal, and a member that
                        // is fighting is not defending while travelling. The mask clears the flag
                        // only when the member is not withdrawing at all, so the lift is explicit
                        // and latched here and the Withdrawal arm restores it. `withdrawing` is the
                        // perception-scoped local, exactly as the deleted Combat arm read it (Behaviour.cs:204
                        // at 74b3a7dd): a member
                        // whose Combat bit joined through the S7.2 traversal exception never sees a
                        // true here, because the legacy local was never assigned on that path.
                        if (withdrawing && sense.Threat != BlacksiteThreatProximity.None
                            && soldier._combatController != null)
                        {
                            soldier._combatController.CombatSuspendedForThreat = true;
                        }
                        handled = soldier.TryExecuteSelectedCombat(out string combatReason);
                        reason = combatReason;
                        // #1233 S6 (rule 2): Combat's goal is the body it is fighting, so the
                        // committed goal - and the floor an elevator reads from it - is never the
                        // interrupted interaction's destination.
                        if (handled) goal = soldier.CombatGoal;
                        break;
                    default:
                        // #1015 S1: `handled` IS the order's motion verdict. The director is not
                        // null-checked, as the legacy arm did not (BlacksiteBehaviourChecks passes null).
                        if (!sense.HasOrder) return false;
                        handled = soldier.ExecuteSquadOrder(sense.Director, sense.Order);
                        if (handled) goal = sense.Order.Destination;
                        break;
                }
                if (handled && target != DelegateTarget.SquadOrder) soldier.Navigator.EndPatrolAlert();
                return handled;
            }

            private static DelegateTarget TargetFor(in BlacksiteBrainAction action)
                => action.Kind == BlacksiteBrainActionKind.Stand ? DelegateTarget.None : TargetFor(action.Behaviour);

            private static DelegateTarget TargetFor(BlacksiteBehaviour behaviour)
            {
                switch (behaviour)
                {
                    case BlacksiteBehaviour.EntranceTraversal: return DelegateTarget.Interaction;
                    case BlacksiteBehaviour.Withdrawal: return DelegateTarget.Withdrawal;
                    case BlacksiteBehaviour.Combat: return DelegateTarget.Combat;
                    case BlacksiteBehaviour.ObjectiveJob:
                    case BlacksiteBehaviour.SquadOrder: return DelegateTarget.SquadOrder;
                    default: return DelegateTarget.None;
                }
            }

            // =================================================================================
            // Diagnostics
            // =================================================================================

            /// <summary>
            /// #1017 S3 (E.1), #1293 R3: <c>[UAT][Brain]</c> keys <c>member, goal, action, reason, until,
            /// outcome, cause</c>, printed on a (goal, action) change or every 5 s, keyed per soldier.
            /// #1305 H (C2): the <c>utility=</c> and <c>runnerUp=</c> keys went with the table.
            /// </summary>
            private void Report(
                float now,
                BlacksiteBehaviour goal,
                in BlacksiteBrainAction action,
                string reason,
                BlacksiteDecisionCause cause)
            {
                if (!BlacksiteDiagnostics.Enabled) return;
                int key = BrainKey;
                // The window gate is evaluated on EVERY report, never short-circuited: it is what
                // keeps a standing decision visible at 0.2 Hz, and it counts its own folds.
                bool due = BrainWindow.ShouldReport(key, now, out _);
                // The change gate's key is a string, and building it is the only allocation on this
                // path - so the enums are compared first and the string is composed only when the
                // pair has actually moved. The mirror is per-brain and the gate is keyed per
                // soldier, so the two can never disagree about what was last reported.
                bool changed = false;
                if (!_reportedKnown || goal != _lastReportedGoal || action.Kind != _lastReportedKind)
                {
                    _reportedKnown = true;
                    _lastReportedGoal = goal;
                    _lastReportedKind = action.Kind;
                    changed = BrainGate.ShouldReport(key, "brain", goal + "|" + Token(action.Kind), out _);
                }
                if (!changed && !due) return;
                string actionToken = Token(action.Kind);
                BlacksiteDiagnostics.Log("Brain",
                    $"member={_soldier.MemberIndex}, goal={goal}, action={actionToken}, reason={reason}"
                    + $", until={action.Until:0.00}"
                    + $", outcome={BlacksiteMotionVerdict.Token(_soldier.Navigator.Motion.LastOutcome)}"
                    + $", cause={cause}");
            }

            private const float BrainRepeatSeconds = 5f;
            private static readonly BlacksiteChangeGate BrainGate = new BlacksiteChangeGate();
            private static readonly BlacksiteWindowGate BrainWindow = new BlacksiteWindowGate(BrainRepeatSeconds);
            // The last (goal, kind) pair this brain composed a gate key for. Mirrors the change
            // gate's own state so the key string is built only when the pair moves.
            private BlacksiteBehaviour _lastReportedGoal;
            private BlacksiteBrainActionKind _lastReportedKind;
            private bool _reportedKnown;

            /// <summary>
            /// Both gates are static and keyed by <c>NetworkObjectId</c>, which the next deployment
            /// reuses, so they are cleared with every other Blacksite diagnostic gate rather than
            /// carrying one mission's last decision into the next.
            /// </summary>
            internal static void ResetGates()
            {
                BrainGate.Reset();
                BrainWindow.Reset();
            }

            private int BrainKey
                => _soldier == null
                    ? 0
                    : _soldier.NetworkObject != null && _soldier.NetworkObject.IsSpawned
                        ? unchecked((int)_soldier.NetworkObject.NetworkObjectId)
                        : _soldier.GetInstanceID();

            private static string Token(BlacksiteBrainActionKind kind)
                => kind == BlacksiteBrainActionKind.Stand ? "stand" : "delegate";
        }
    }
}
