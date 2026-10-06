using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed class BlacksiteObjectiveInteraction
    {
        internal const float ShippingContainerOverrideSeconds = 10f;
        internal const float InternalCacheOverrideSeconds = 6f;
        private const float InterruptedProgressHoldSeconds = 3f;
        private const float InterruptedProgressDecayPerSecond = 0.5f;
        // #874 S6.2: the align gate and the duty order's reach contract are the same number, held in
        // one place (BlacksiteObjectiveManifestController's constants block, beside the pickup pair)
        // so a change to one cannot leave the other behind. The values are unchanged: 1.7 m and
        // 0.3 m are what this interaction has always used.
        internal const float OverrideUseRange = BlacksiteObjectiveManifestController.ContainerLockReachDistance;
        internal const float OverrideStoppingDistance = 0.3f;
        private const float AlignSeconds = 0.45f;
        private const float ConfirmSeconds = 2f;
        private const float ApproachRefreshSeconds = 0.22f;
        private const float GestureRefreshSeconds = 1.8f;

        private sealed class OverrideProgress
        {
            internal float ProgressSeconds;
            internal float LastUpdatedAt;
            internal float InterruptedAt;
            internal ulong ActiveOwner;
        }

        private sealed class ObjectiveContext
        {
            internal BlacksiteRetrievalMissionController Mission;
            internal BreachContainerController Container;
            internal ShadowRaidCargoCrate Cache;
            internal OverrideProgress Progress;
            internal float LastProgressTickAt;
            internal float NextGestureAt;
        }

        private static readonly Dictionary<int, OverrideProgress> ProgressByTarget =
            new Dictionary<int, OverrideProgress>();

        private readonly BlacksiteInteractionCoordinator _coordinator;
        private readonly BlacksiteSoldierAI _soldier;
        private readonly NavMeshAgent _agent;
        private float _nextApproachRefreshAt;

        internal BlacksiteObjectiveInteraction(
            BlacksiteInteractionCoordinator coordinator,
            BlacksiteSoldierAI soldier,
            NavMeshAgent agent)
        {
            _coordinator = coordinator;
            _soldier = soldier;
            _agent = agent;
        }

        internal static void ResetRoundProgress()
        {
            ProgressByTarget.Clear();
            // #950 S11H U2: member indices are reused by the next deployment, so the refusal gate
            // dies with the round exactly as the override progress does.
            ResetApproachRefusalGate();
        }

        internal bool TryResolvePendingObjective(
            BlacksiteRetrievalMissionController mission,
            out object target,
            out BlacksiteInteractionKind kind,
            out Vector3 destination,
            out Vector3 alignAnchor)
        {
            target = null;
            kind = BlacksiteInteractionKind.None;
            destination = Vector3.zero;
            alignAnchor = Vector3.zero;
            if (mission == null
                || mission.IsTerminal
                || mission.Contract != MoonContractType.ShadowRaid
                || mission.DriveState != BlacksiteHardDriveState.AtExpectedTarget)
            {
                return false;
            }

            BreachContainerController container =
                BreachContainerController.FindByNetworkId(mission.ExpectedTargetNetworkId);
            if (container != null && !container.IsOpen)
            {
                Vector3 anchor = container.LockAnchor != null
                    ? container.LockAnchor.position
                    : container.transform.position;
                // #950 S11H U2: a refusal is propagated, never substituted. Nothing is written to
                // target or kind until a destination exists, so a refused resolution cannot leave a
                // half-built objective behind for the coordinator to act on.
                if (!ResolveApproach(mission, anchor, BlacksiteObjectiveApproach.SolidBounds(container.transform),
                        out destination, out string containerRefusal))
                {
                    ReportApproachRefusal(BlacksiteInteractionKind.ContainerOverride,
                        container.gameObject.name, anchor, containerRefusal);
                    destination = Vector3.zero;
                    return false;
                }
                target = container;
                kind = BlacksiteInteractionKind.ContainerOverride;
                alignAnchor = anchor;
                return true;
            }

            ShadowRaidCargoCrate cache = ResolveCache(mission.SecondaryTargetNetworkId);
            if (cache != null && !cache.Opened)
            {
                Vector3 anchor = cache.InteractionPosition;
                if (!ResolveApproach(mission, anchor, BlacksiteObjectiveApproach.SolidBounds(cache.transform),
                        out destination, out string cacheRefusal))
                {
                    ReportApproachRefusal(BlacksiteInteractionKind.CacheOverride,
                        cache.gameObject.name, anchor, cacheRefusal);
                    destination = Vector3.zero;
                    return false;
                }
                target = cache;
                kind = BlacksiteInteractionKind.CacheOverride;
                alignAnchor = anchor;
                return true;
            }

            return false;
        }

        /// <summary>
        /// #874 S6.2: the interaction's own approach point is the SAME point the duty board is
        /// walking the operator to. It used to be an independent 4 m navmesh sample of the lock
        /// anchor, which is inside the container's own footprint, so the two owners disagreed about
        /// where the operator was supposed to stand.
        /// </summary>
        /// <remarks>
        /// #950 S11H U2: the fallback used to be "sample the navmesh within 4 m of the anchor, and
        /// if that finds nothing return the anchor itself". On 2026-09-10 the anchor was a lock
        /// 1.34 m up the door face of a container standing in rock 42 m outside the March dungeon:
        /// the sample found nothing, the raw anchor became the published destination, and the
        /// navigator rejected it 133 times with PathInvalid.
        ///
        /// #1106: that unit also wrote "no caller may keep that fallback", and this caller had
        /// kept it. A bare <c>NavMesh.SamplePosition</c> inside 4 m of the lock answers ONE of the
        /// resolver's three questions - is there mesh here - and neither of the other two: it does
        /// not test the closed barrier's expanded bounds and it does not test that a body fits.
        /// Around a container lock the nearest mesh within four metres is routinely the interior
        /// floor the doors are still shut on, so on every ring refusal the interaction published a
        /// standing point inside the sealed container and <c>ExecuteSquadOrderCore</c> walked the
        /// operator to it in preference to the board's own goal - the exact publication
        /// <see cref="BlacksiteObjectiveApproach"/> exists to make impossible, reintroduced one
        /// call below it. It also masked the board: a refusal that always substitutes a
        /// destination never reaches the objective's unreachable rule, so a revealed drive nobody
        /// can stand next to held the mission in Inspecting instead of escalating.
        ///
        /// A ring refusal is now the answer. The board's own refusal path does the same thing -
        /// <c>PickupApproach</c> holds the operator at his feet and writes NoReachableCandidate -
        /// so the two owners of this decision agree, and the probe budget can spend itself.
        /// </remarks>
        private bool ResolveApproach(
            BlacksiteRetrievalMissionController mission,
            Vector3 anchor,
            Bounds? bounds,
            out Vector3 destination,
            out string reason)
        {
            Vector3 origin = _soldier != null ? _soldier.transform.position : anchor;
            Transform body = _soldier != null ? _soldier.transform : null;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            // #1106 (2026-09-19 round 1 lines 5791-7182): the lock anchor (1.34 m up the container
            // door) was passed as both objectPosition and supportFloor, so GroundOf returned the
            // lock height and every ring candidate sat 0.8+ m above the navmesh. Sample the floor
            // under the anchor and pass that as the supporting floor so the ring centres on it.
            Vector3 supportFloor = anchor;
            if (NavMesh.SamplePosition(anchor, out NavMeshHit nav, AnchorSampleRadius, NavMesh.AllAreas))
                supportFloor = new Vector3(anchor.x, nav.position.y, anchor.z);
            // #874 S6.2: the same ring the duty board resolved, rotated by the same attempt count.
            // The heading order is `(i + attempt * Rings) * 30`, so a board that has already failed
            // an attempt starts its ring 90 degrees round while a hard-coded zero here starts it at
            // the door, and the two owners publish different points for the same lock on the same
            // tick - the operator then walks to one of them while the attempt's travel budget
            // measures the route to the other.
            int attempt = mission != null && mission.Objectives != null
                ? mission.Objectives.ObjectiveAttemptIndex(mission.DriveNetworkId)
                : 0;
            // #874 S2 (rule 4): the ring prefers points the navmesh reaches from the tile's entrance,
            // the board's own query. The operator is at the lock, so his world is the lock's.
            Vector3 routeOrigin = mission != null && mission.Objectives != null
                ? mission.Objectives.ReachabilityOrigin(supportFloor, _soldier != null && _soldier.isOutside)
                : origin;
            if (BlacksiteObjectiveApproach.TryResolve(supportFloor, supportFloor, true, anchor, bounds,
                    origin, body, routeOrigin, mask, attempt, out Vector3 point, out string refusal))
            {
                destination = point;
                reason = null;
                return true;
            }
            destination = Vector3.zero;
            reason = string.IsNullOrEmpty(refusal) ? "no standing point around the lock" : refusal;
            return false;
        }

        /// <summary>
        /// #950 S11H U2: the radius the FLOOR under the lock anchor is sampled at, named rather
        /// than inlined. The value is unchanged at the 4 m this call has always used; a prohibition
        /// of this unit is that it may not grow, because a wider sample hides an unreachable
        /// objective instead of naming it. #1106: it is now only ever used to find the storey the
        /// ring is centred on. It is never a destination, so nothing it returns skips the barrier
        /// and body-occupancy tests.
        /// </summary>
        private const float AnchorSampleRadius = 4f;

        /// <summary>
        /// #950 S11H U2: a refusal is an event, so it is reported on the interaction's own line with
        /// result=refused and the resolver's reason. Change-gated per member on the reason text, so
        /// a member standing in front of an unreachable lock prints once rather than every think;
        /// the count it folded is carried, so the rate is still recoverable from the log.
        /// </summary>
        private static readonly BlacksiteChangeGate ApproachRefusalGate = new BlacksiteChangeGate();

        private void ReportApproachRefusal(BlacksiteInteractionKind kind, string targetName, Vector3 anchor, string reason)
        {
            if (!BlacksiteDiagnostics.Enabled || _soldier == null) return;
            int member = _soldier.MemberIndex;
            if (!ApproachRefusalGate.ShouldReport(member, kind + "|" + targetName + "|" + reason, out int folded)) return;
            BlacksiteDiagnostics.Log(
                "Interaction",
                $"member={member}, kind={kind}, phase=none, target={targetName}, result=refused"
                + $", reason={reason}, anchor={BlacksiteDiagnosticFormat.V(anchor)}, folded={folded}.");
        }

        internal static void ResetApproachRefusalGate() => ApproachRefusalGate.Reset();

        /// <summary>
        /// #874 S6.2: opening the barrier is the first step of the drive job, so the task is created
        /// when the operator is inside the interaction's OWN use range of the lock, measured to the
        /// lock, not when he happens to be within 3.4 m of some substituted destination. Extracted so
        /// the fixture can ask the same question from a settled stop that the coordinator asks live.
        /// </summary>
        internal static bool CanBeginOverride(Vector3 operatorPosition, Vector3 alignAnchor)
            => HorizontalDistance(operatorPosition, alignAnchor) <= OverrideUseRange;

        internal bool TryCreateTask(
            BlacksiteRetrievalMissionController mission,
            object target,
            BlacksiteInteractionKind kind,
            Vector3 destination,
            out BlacksiteInteractionTask task)
        {
            task = null;
            if (mission == null
                || target == null
                || mission.Contract != MoonContractType.ShadowRaid
                || (kind != BlacksiteInteractionKind.ContainerOverride
                    && kind != BlacksiteInteractionKind.CacheOverride))
            {
                return false;
            }

            BreachContainerController container = target as BreachContainerController;
            ShadowRaidCargoCrate cache = target as ShadowRaidCargoCrate;
            if (container == null && cache == null)
                return false;

            float duration = kind == BlacksiteInteractionKind.ContainerOverride
                ? ShippingContainerOverrideSeconds
                : InternalCacheOverrideSeconds;
            string label = kind == BlacksiteInteractionKind.ContainerOverride
                ? $"shipping-container override '{container.gameObject.name}'"
                : $"internal-cache override '{cache.gameObject.name}'";
            OverrideProgress progress = GetProgress(target);
            ApplyHeldProgressDecay(progress);
            task = new BlacksiteInteractionTask(
                kind,
                target,
                destination,
                destination,
                label)
            {
                AlignPosition = kind == BlacksiteInteractionKind.ContainerOverride
                    ? container.LockAnchor.position
                    : cache.InteractionPosition,
                ConfirmPosition = destination,
                RequiredSeconds = duration,
                ProgressSeconds = Mathf.Clamp(progress.ProgressSeconds, 0f, duration),
                Deadline = Time.time + duration + 20f,
                Context = new ObjectiveContext
                {
                    Mission = mission,
                    Container = container,
                    Cache = cache,
                    Progress = progress,
                    LastProgressTickAt = Time.time,
                    NextGestureAt = 0f
                }
            };
            mission.MarkOverridingServer();
            return true;
        }

        internal BlacksiteInteractionTickResult Tick(
            BlacksiteInteractionTask task,
            out Vector3 issuedDestination)
        {
            issuedDestination = task != null ? task.ApproachPosition : _soldier.transform.position;
            // #874 S6.2: every exit through Failed names itself. The 2026-09-09 log printed
            // `[UAT][Interaction] ... result=Failed, reason=none` at L5292, L5774, L5781, L5936 and
            // L5951, which is a failure that cannot be told apart from any other failure.
            if (task == null || !(task.Context is ObjectiveContext context))
            {
                if (task != null) task.FailureReason = "objective override task carries no objective context";
                return BlacksiteInteractionTickResult.Failed;
            }
            if (_soldier == null
                || _soldier.IsWorldInteractionInterrupted
                || context.Mission == null
                || context.Mission.IsTerminal
                || context.Mission.Contract != MoonContractType.ShadowRaid)
            {
                Interrupt(task, "operator invalid, interrupted, or mission ended");
                return BlacksiteInteractionTickResult.Failed;
            }

            if (TargetAlreadyOpen(context))
                return CompleteOpenedBarrier(task, context);

            if (!_coordinator.TryAcquireReservation(task, out Vector3 queuePosition))
            {
                if (context.Progress.ActiveOwner == _coordinator.OwnerToken)
                    SaveInterruptedProgress(task, context);
                task.TransitionTo(BlacksiteInteractionPhase.Queued);
                issuedDestination = queuePosition;
                _soldier.IssueRawAgentDestination(queuePosition);
                return BlacksiteInteractionTickResult.Running;
            }

            _coordinator.RenewReservation(task);
            context.Progress.ActiveOwner = _coordinator.OwnerToken;
            if (task.Phase == BlacksiteInteractionPhase.Reserved
                || task.Phase == BlacksiteInteractionPhase.Queued)
            {
                ApplyHeldProgressDecay(context.Progress);
                task.ProgressSeconds = Mathf.Clamp(
                    context.Progress.ProgressSeconds,
                    0f,
                    task.RequiredSeconds);
                context.LastProgressTickAt = Time.time;
                task.TransitionTo(BlacksiteInteractionPhase.Approach);
            }

            switch (task.Phase)
            {
                case BlacksiteInteractionPhase.Approach:
                    _soldier.OverrideWorldInteractionStoppingDistance(OverrideStoppingDistance);
                    issuedDestination = task.ApproachPosition;
                    if (Time.time >= _nextApproachRefreshAt)
                    {
                        _nextApproachRefreshAt = Time.time + ApproachRefreshSeconds;
                        _soldier.IssueRawAgentDestination(task.ApproachPosition);
                    }

                    if (HorizontalDistance(_soldier.transform.position, task.AlignPosition) > OverrideUseRange)
                    {
                        if (Time.time >= task.Deadline)
                        {
                            SaveInterruptedProgress(task, context);
                            task.FailureReason =
                                $"approach deadline: still {HorizontalDistance(_soldier.transform.position, task.AlignPosition):0.00} m"
                                + $" from the lock, gate {OverrideUseRange:0.00} m, goal={task.ApproachPosition.ToString("0.00")}";
                            task.TransitionTo(BlacksiteInteractionPhase.Failed);
                            return BlacksiteInteractionTickResult.Failed;
                        }
                        return BlacksiteInteractionTickResult.Running;
                    }

                    _soldier.StopForWorldInteraction(AlignSeconds);
                    _soldier.FaceWorldInteraction(task.AlignPosition);
                    task.TransitionTo(BlacksiteInteractionPhase.Align);
                    return BlacksiteInteractionTickResult.Running;

                case BlacksiteInteractionPhase.Align:
                    _soldier.StopForWorldInteraction(0.15f);
                    _soldier.FaceWorldInteraction(task.AlignPosition);
                    if (!task.GesturePlayed)
                    {
                        task.GesturePlayed = true;
                        _soldier.PlayWorldInteractionGesture();
                    }
                    if (Time.time - task.PhaseStartedAt < AlignSeconds)
                        return BlacksiteInteractionTickResult.Running;
                    context.LastProgressTickAt = Time.time;
                    context.NextGestureAt = Time.time + GestureRefreshSeconds;
                    task.TransitionTo(BlacksiteInteractionPhase.Perform);
                    return BlacksiteInteractionTickResult.Running;

                case BlacksiteInteractionPhase.Perform:
                    _soldier.StopForWorldInteraction(0.2f);
                    _soldier.FaceWorldInteraction(task.AlignPosition);
                    float elapsed = Mathf.Clamp(
                        Time.time - context.LastProgressTickAt,
                        0f,
                        0.5f);
                    context.LastProgressTickAt = Time.time;
                    task.ProgressSeconds = Mathf.Min(
                        task.RequiredSeconds,
                        task.ProgressSeconds + elapsed);
                    context.Progress.ProgressSeconds = task.ProgressSeconds;
                    context.Progress.LastUpdatedAt = Time.time;
                    context.Progress.InterruptedAt = 0f;

                    if (Time.time >= context.NextGestureAt)
                    {
                        context.NextGestureAt = Time.time + GestureRefreshSeconds;
                        _soldier.PlayWorldInteractionGesture();
                    }

                    if (task.ProgressSeconds + 0.001f < task.RequiredSeconds)
                        return BlacksiteInteractionTickResult.Running;

                    if (!CompleteOverride(context))
                    {
                        SaveInterruptedProgress(task, context);
                        task.FailureReason =
                            $"override refused by the target after {task.ProgressSeconds:0.0}/{task.RequiredSeconds:0.0}s of work";
                        task.TransitionTo(BlacksiteInteractionPhase.Failed);
                        return BlacksiteInteractionTickResult.Failed;
                    }

                    task.OperationIssued = true;
                    task.Deadline = Time.time + ConfirmSeconds;
                    task.TransitionTo(BlacksiteInteractionPhase.Confirm);
                    return BlacksiteInteractionTickResult.Running;

                case BlacksiteInteractionPhase.Confirm:
                    _soldier.StopForWorldInteraction(0.1f);
                    if (TargetAlreadyOpen(context))
                        return CompleteOpenedBarrier(task, context);

                    if (Time.time < task.Deadline)
                        return BlacksiteInteractionTickResult.Running;
                    SaveInterruptedProgress(task, context);
                    task.FailureReason =
                        $"target did not report open within the {ConfirmSeconds:0.0}s confirmation window";
                    task.TransitionTo(BlacksiteInteractionPhase.Failed);
                    return BlacksiteInteractionTickResult.Failed;
            }

            task.FailureReason = "objective override reached phase " + task.Phase + ", which it cannot act on";
            return BlacksiteInteractionTickResult.Failed;
        }

        /// <summary>
        /// #874 F06: the barrier this member was working on is open.
        /// </summary>
        /// <remarks>
        /// The two Complete exits were byte-identical and are now one body, because the milestone
        /// below has to be reported from both: the confirmation window is the ordinary exit, and
        /// the top-of-tick test is the one taken when the target reports open a frame later or
        /// while a second operator is still holding a task on the same lock.
        ///
        /// Opening the container or the cache is the drive job's FIRST physical step, and as far as
        /// the job board is concerned it is a wait: <c>BlacksiteSoldierAI.IsWaitingForTraversal</c>
        /// is true for as long as the coordinator holds any task, so
        /// <c>BlacksiteObjectiveManifestController.ObserveJob</c> reads phase=Waiting from the
        /// moment the override is created. On 2026-09-21 the shipping-container override held that
        /// wait from t=1015.33 to t=1029.70 and the internal cache from t=1030.90 to t=1044.06, and
        /// neither renewed the wait clock: an override is not a crossing, so it emitted no milestone at
        /// all and the whole two-stage unlock had to fit inside one unrenewed 45 s cap along with
        /// both approaches and both confirmations.
        ///
        /// A barrier opening is nevertheless exactly what
        /// <see cref="BlacksiteSoldierAI.NoteTraversalMilestone"/> is defined as - a point this
        /// member reaches at most once, which a task-create/fail loop can never advance - so it is
        /// reported as one. The cap itself does not move; what changes is that a legitimate unlock
        /// renews it once per barrier actually opened, and that the live run can read the reason
        /// for the wait off the existing [UAT][Job] event=waitMilestone line instead of having to
        /// correlate stallOwner= on the motion channel. Nothing is replicated and the host remains
        /// the only writer: the milestone counter is host-side state the manifest already reads.
        /// </remarks>
        private BlacksiteInteractionTickResult CompleteOpenedBarrier(
            BlacksiteInteractionTask task,
            ObjectiveContext context)
        {
            context.Progress.ProgressSeconds = task.RequiredSeconds;
            context.Progress.ActiveOwner = 0UL;
            context.Mission.MarkInspectingServer();
            _soldier.NoteTraversalMilestone(context.Container != null ? "override:container" : "override:cache");
            task.TransitionTo(BlacksiteInteractionPhase.Complete);
            return BlacksiteInteractionTickResult.Complete;
        }

        internal void Interrupt(BlacksiteInteractionTask task, string reason)
        {
            if (task == null || !(task.Context is ObjectiveContext context))
                return;

            // #874 S6.2: the interruption reason is the task's failure reason, so the
            // [UAT][Interaction] line the coordinator prints can name it instead of "none".
            task.FailureReason = reason;
            SaveInterruptedProgress(task, context);
            context.Mission?.MarkApproachingExpectedDriveServer();
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Objective override interrupted: target='{task.Label}', " +
                $"progress={task.ProgressSeconds:0.0}/{task.RequiredSeconds:0.0}s, " +
                $"hold={InterruptedProgressHoldSeconds:0.0}s, reason={reason}.");
        }

        private bool CompleteOverride(ObjectiveContext context)
        {
            if (context.Container != null)
                return context.Container.TryOpenFromBlacksiteOverrideServer(context.Mission);
            if (context.Cache != null)
                return context.Cache.TryOpenFromBlacksiteOverrideServer(context.Mission);
            return false;
        }

        private static bool TargetAlreadyOpen(ObjectiveContext context)
        {
            if (context == null)
                return false;
            if (context.Container != null)
                return context.Container.IsOpen;
            return context.Cache != null && context.Cache.Opened;
        }

        private static OverrideProgress GetProgress(object target)
        {
            int key = RuntimeHelpers.GetHashCode(target);
            if (!ProgressByTarget.TryGetValue(key, out OverrideProgress progress))
            {
                progress = new OverrideProgress
                {
                    LastUpdatedAt = Time.time
                };
                ProgressByTarget[key] = progress;
            }
            return progress;
        }

        private static void ApplyHeldProgressDecay(OverrideProgress progress)
        {
            if (progress == null || progress.ActiveOwner != 0UL || progress.InterruptedAt <= 0f)
                return;

            float decayElapsed = Mathf.Max(
                0f,
                Time.time - progress.InterruptedAt - InterruptedProgressHoldSeconds);
            if (decayElapsed <= 0f)
                return;

            progress.ProgressSeconds = Mathf.Max(
                0f,
                progress.ProgressSeconds - decayElapsed * InterruptedProgressDecayPerSecond);
            progress.InterruptedAt = Time.time - InterruptedProgressHoldSeconds;
            progress.LastUpdatedAt = Time.time;
        }

        private static void SaveInterruptedProgress(
            BlacksiteInteractionTask task,
            ObjectiveContext context)
        {
            if (task == null || context?.Progress == null)
                return;
            context.Progress.ProgressSeconds = Mathf.Clamp(
                task.ProgressSeconds,
                0f,
                task.RequiredSeconds);
            context.Progress.LastUpdatedAt = Time.time;
            context.Progress.InterruptedAt = Time.time;
            context.Progress.ActiveOwner = 0UL;
        }

        private static ShadowRaidCargoCrate ResolveCache(ulong networkObjectId)
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (networkObjectId != 0UL
                && manager?.SpawnManager?.SpawnedObjects != null
                && manager.SpawnManager.SpawnedObjects.TryGetValue(
                    networkObjectId,
                    out NetworkObject networkObject)
                && networkObject != null)
            {
                return networkObject.GetComponent<ShadowRaidCargoCrate>();
            }
            return null;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
