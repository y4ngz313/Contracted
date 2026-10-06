using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed class BlacksiteEntranceInteraction
    {
        private const float UseRange = BlacksiteEntrancePolicy.UseRange;
        private const float StoppingDistance = 0.12f;
        private const float ApproachRefreshSeconds = 0.2f;
        // #874 S5.4: both values are unchanged; they live in the Unity-free
        // BlacksiteEntranceColumn so the service model the regression pins is the one the runtime
        // actually spends at the threshold.
        private const float AlignSeconds = BlacksiteEntranceColumn.AlignSeconds;
        private const float TransferSettleSeconds = BlacksiteEntranceColumn.TransferSettleSeconds;

        private static readonly List<EntranceTeleport> CachedEntrances =
            new List<EntranceTeleport>();
        private static RoundManager _cachedRound;
        private static int _cachedRoundKey = int.MinValue;
        private static bool _cacheBuilt;

        private readonly BlacksiteInteractionCoordinator _coordinator;
        private readonly BlacksiteSoldierAI _soldier;
        private readonly NavMeshAgent _agent;
        private float _nextApproachRefreshAt;

        private sealed class EntranceContext
        {
            internal EntranceTeleport Entrance;
            internal string Reason;
        }

        internal BlacksiteEntranceInteraction(
            BlacksiteInteractionCoordinator coordinator,
            BlacksiteSoldierAI soldier,
            NavMeshAgent agent)
        {
            _coordinator = coordinator;
            _soldier = soldier;
            _agent = agent;
            EnsureRoundCache();
        }

        internal static void ResetRoundCache()
        {
            BlacksiteSoldierAI.ResetStrandedExitSpacing();
            CachedEntrances.Clear();
            _cachedRound = null;
            _cachedRoundKey = int.MinValue;
            _cacheBuilt = false;
        }

        // #874 S8.2: excludeEntranceId is additive and defaults to "exclude nothing" (authored
        // entrance ids are never negative). It names the door that just refused a member three
        // times, so the search returns the next-nearest one instead of the same failure. The
        // meaning of preferredEntranceId is unchanged: -1 still means "any door that is not the
        // main one", which is why excluding the current door is a separate term and not a reuse
        // of it - a member re-targeting AWAY from a fire exit has to be able to pick the main door
        // back up.
        internal static EntranceTeleport FindBestEntrance(
            Vector3 reference,
            bool isEntranceToBuilding,
            int preferredEntranceId,
            bool allowAnyId,
            int excludeEntranceId = int.MinValue)
        {
            EnsureRoundCache();
            EntranceTeleport bestPreferred = CachedEntrances
                .Where(entrance => entrance != null
                    && entrance.isActiveAndEnabled
                    && entrance.isEntranceToBuilding == isEntranceToBuilding
                    && entrance.entranceId != excludeEntranceId
                    && (preferredEntranceId == -1
                        ? entrance.entranceId != 0
                        : preferredEntranceId < 0 || entrance.entranceId == preferredEntranceId))
                .OrderBy(entrance => HorizontalDistance(GetEntrancePoint(entrance), reference))
                .FirstOrDefault();
            if (bestPreferred != null || !allowAnyId)
                return bestPreferred;

            return CachedEntrances
                .Where(entrance => entrance != null
                    && entrance.isActiveAndEnabled
                    && entrance.isEntranceToBuilding == isEntranceToBuilding
                    && entrance.entranceId != excludeEntranceId)
                .OrderBy(entrance => HorizontalDistance(GetEntrancePoint(entrance), reference))
                .FirstOrDefault();
        }

        /// <summary>
        /// #874 N6: every usable door of one side except <paramref name="excludeEntranceId"/>,
        /// nearest to <paramref name="reference"/> first. This is <see cref="FindBestEntrance"/>'s
        /// filter and order without its single pick. The up-front door choice walks the list
        /// because the nearest alternate is not always the one that reaches the objective. It
        /// allocates, and is called once per mission.
        /// </summary>
        internal static List<EntranceTeleport> FindEntrancesNearest(
            Vector3 reference,
            bool isEntranceToBuilding,
            int excludeEntranceId)
        {
            EnsureRoundCache();
            return CachedEntrances
                .Where(entrance => entrance != null
                    && entrance.isActiveAndEnabled
                    && entrance.isEntranceToBuilding == isEntranceToBuilding
                    && entrance.entranceId != excludeEntranceId)
                .OrderBy(entrance => HorizontalDistance(GetEntrancePoint(entrance), reference))
                .ToList();
        }

        /// <summary>#1305 G1 (R15): the round cache's size, for the route service's pairs (<see cref="TryGetRoutePair"/>).</summary>
        internal static int RouteEntranceCount
        {
            get
            {
                EnsureRoundCache();
                return CachedEntrances.Count;
            }
        }

        /// <summary>
        /// #1305 G1 (R15): the route service's entrance leg, read only and without allocating. The
        /// <paramref name="index"/>-th cached doorway when it is usable from <paramref name="outside"/> and is (or, with
        /// <paramref name="main"/> false, is not) the main entrance: where a body coming from <paramref name="from"/>
        /// walks to (the task's own threshold approach) and the standable far endpoint the transfer would land it on
        /// (<see cref="TryFindExit"/>; no exitScript is written - pairing is the entrance task's).
        /// </summary>
        internal static bool TryGetRoutePair(int index, bool outside, bool main, Vector3 from,
            out EntranceTeleport entrance, out Vector3 approach, out Vector3 exitEndpoint)
        {
            entrance = CachedEntrances[index];
            approach = exitEndpoint = Vector3.zero;
            if (entrance == null || !entrance.isActiveAndEnabled || entrance.isEntranceToBuilding != outside
                || (entrance.entranceId == 0) != main
                || !TryFindExit(entrance, out _, out exitEndpoint, out _))
                return false;
            approach = ResolveThresholdApproach(from, GetEntrancePoint(entrance), entrance.transform);
            return true;
        }

        internal bool TryCreateEntranceTask(
            EntranceTeleport entrance,
            Vector3 finalDestination,
            string reason,
            out BlacksiteInteractionTask task)
        {
            task = null;
            if (entrance == null || !TryResolvePair(entrance, out EntranceTeleport exit, out _, out _))
                return false;
            // #1305 G2 (R16): the world gate is the route's input (BlacksiteInteractionCoordinator
            // .TryHandleDestination asks AdmitsWorld before handing a cross-world transition); Tick asks again until the
            // transfer, so a task whose doorway stops leading to this member's world fails there.
            RegisterEntrance(entrance);
            RegisterEntrance(exit);
            EntranceTeleport canonical = entrance.GetInstanceID() <= exit.GetInstanceID()
                ? entrance
                : exit;
            Vector3 threshold = GetEntrancePoint(entrance);
            Vector3 approach = ResolveThresholdApproach(_soldier.transform.position, threshold, entrance.transform);
            string taskReason = string.IsNullOrWhiteSpace(reason)
                ? "entrance traversal"
                : reason;
            task = new BlacksiteInteractionTask(
                BlacksiteInteractionKind.Entrance,
                canonical,
                approach,
                finalDestination,
                $"entrance {entrance.entranceId} ({taskReason})")
            {
                AlignPosition = threshold,
                ConfirmPosition = GetEntrancePoint(exit),
                Context = new EntranceContext
                {
                    Entrance = entrance,
                    Reason = taskReason
                }
            };
            return true;
        }

        // #1233 S2: the gate's one predicate, read live: a doorway into the toOutside world is
        // admitted iff that is the member's own world (the mission world, which is the objective's, or
        // its duty world), or the passage keeps its own semantics (extraction, egress, a popped-Jester
        // flight, a player response). The objective's world can change while a task waits, walks or
        // aligns, and the planner does not re-plan a member mid-crossing, so an admitted task is not a licence.
        internal bool AdmitsWorld(bool toOutside)
            => toOutside == _soldier.isOutside || !_soldier.IsServerAuthorityForSquad
                || !_soldier.TryGetMission(out BlacksiteRetrievalMissionController mission) || mission.Objectives == null
                || mission.Objectives.Planner.AdmitsWorldCrossing(_soldier, toOutside);

        /// <summary>
        /// #1134: does a task in this phase still need the doorway?
        ///
        /// True while the member is still walking to the threshold, standing on it or being
        /// transferred through it - the two phases <see cref="BlacksiteEntranceColumn.ThresholdServiceSeconds"/>
        /// measures, plus the approach and the wait in front of them. False from Confirm on,
        /// because Perform's transfer puts the member on the FAR side and
        /// <c>BlacksiteSquadDirector.NoteEntranceCleared</c> hands the lane straight to the next
        /// member in that same call (<c>column.NoteCleared</c> then <c>column.Leave</c>, which is
        /// <c>BlacksiteResourceLane.Release</c>): a Confirm that asked again could only be refused,
        /// and the refusal dropped the member that had just crossed back to Queued and failed it on
        /// the queued bound of the day, inside the facility it had just entered (2026-09-18 run 3:
        /// 16 of 37 crossings failed that way, and each one then walked back out to the stale outside
        /// goal the smart path restarted toward). The 2026-09-21 round 3 log shows the same
        /// re-acquire at t=903.56 - granted only because that column had nobody behind it - and the
        /// lane stayed held for a further 0.63 s.
        ///
        /// PR #1138 put this rule on <c>BlacksiteEntranceColumn</c> so the Unity-free
        /// <c>BlacksiteTierTwoThreeRegression</c> could name it. It lives here instead, on the one
        /// caller of the entrance reservation, because that file is outside this change's
        /// boundary; the five true phases, the behaviour and the call site are PR #1138's exactly.
        /// </summary>
        internal static bool RequiresDoorway(BlacksiteInteractionPhase phase)
            => phase == BlacksiteInteractionPhase.Reserved
                || phase == BlacksiteInteractionPhase.Queued
                || phase == BlacksiteInteractionPhase.Approach
                || phase == BlacksiteInteractionPhase.Align
                || phase == BlacksiteInteractionPhase.Perform;

        internal BlacksiteInteractionTickResult Tick(
            BlacksiteInteractionTask task,
            out Vector3 issuedDestination)
        {
            issuedDestination = task != null ? task.ApproachPosition : _soldier.transform.position;
            if (task == null || !(task.Context is EntranceContext context))
                return BlacksiteInteractionTickResult.Failed;
            if (_soldier == null || _soldier.IsWorldInteractionInterrupted)
                return BlacksiteInteractionTickResult.Failed;

            // #1233 S2: until the transfer, the doorway must still lead to this member's world. A
            // refusal ends the task through the ordinary Failed path (the coordinator releases the
            // column reservation), and the planner re-plans on its next pass.
            if (context.Entrance != null && RequiresDoorway(task.Phase) && !AdmitsWorld(BlacksiteWorld.AfterUsing(context.Entrance)))
            {
                task.FailureReason = "notMissionWorld";
                task.TransitionTo(BlacksiteInteractionPhase.Failed);
                return BlacksiteInteractionTickResult.Failed;
            }

            // #1134: only ask for the doorway while this member still has to cross it. See
            // RequiresDoorway: Perform's transfer already handed the lane to the member behind, so
            // a Confirm that asked again was refused by the successor it had just admitted.
            if (RequiresDoorway(task.Phase))
            {
                if (!_coordinator.TryAcquireReservation(task, out Vector3 queuePosition))
                {
                    // #874 S5.4 (#873): the waiting place is a real destination like any other. It is
                    // issued through the navigator with the interaction's own stop distance (S4.2: no
                    // new stoppingDistance writer, and never the movement default) and published as a
                    // crowd claim (S4.3) so two members refused in the same frame separate instead of
                    // standing in each other.
                    task.TransitionTo(BlacksiteInteractionPhase.Queued);
                    // #1293 R2: a queue is declared progress - never judged and never timed out. The
                    // ledger judges the holder ahead; a miss there withdraws its task through the
                    // planner's stall answer (BlacksiteSoldierAI.WithdrawEntranceCrossing), which
                    // releases the doorway to this member.
                    _soldier.OverrideWorldInteractionStoppingDistance(BlacksiteEntranceColumn.QueueStopMetres);
                    issuedDestination = _coordinator.ClaimDestination(queuePosition);
                    _soldier.IssueRawAgentDestination(issuedDestination);
                    _soldier.FaceWorldInteraction(task.AlignPosition);
                    return BlacksiteInteractionTickResult.Running;
                }

                _coordinator.RenewReservation(task);
                if (task.Phase == BlacksiteInteractionPhase.Reserved
                    || task.Phase == BlacksiteInteractionPhase.Queued)
                    task.TransitionTo(BlacksiteInteractionPhase.Approach);
            }

            switch (task.Phase)
            {
                case BlacksiteInteractionPhase.Approach:
                    _soldier.OverrideWorldInteractionStoppingDistance(StoppingDistance);
                    issuedDestination = task.ApproachPosition;
                    if (Time.time >= _nextApproachRefreshAt)
                    {
                        _nextApproachRefreshAt = Time.time + ApproachRefreshSeconds;
                        _soldier.IssueRawAgentDestination(task.ApproachPosition);
                    }

                    // #1293 R2: the walk to the threshold is a travel commitment the ledger judges; a
                    // miss withdraws this task through the planner's stall answer. No deadline here.
                    if (HorizontalDistance(_soldier.transform.position, task.AlignPosition) > UseRange
                        || Mathf.Abs(_soldier.transform.position.y - task.AlignPosition.y) > BlacksiteEntrancePolicy.HeightTolerance)
                        return BlacksiteInteractionTickResult.Running;

                    _soldier.StopForWorldInteraction(AlignSeconds);
                    _soldier.FaceWorldInteraction(task.AlignPosition);
                    task.TransitionTo(BlacksiteInteractionPhase.Align);
                    return BlacksiteInteractionTickResult.Running;

                case BlacksiteInteractionPhase.Align:
                    _soldier.StopForWorldInteraction(0.12f);
                    _soldier.FaceWorldInteraction(task.AlignPosition);
                    if (!task.GesturePlayed)
                    {
                        task.GesturePlayed = true;
                        _soldier.PlayWorldInteractionGesture();
                    }
                    if (Time.time - task.PhaseStartedAt < AlignSeconds)
                        return BlacksiteInteractionTickResult.Running;
                    task.TransitionTo(BlacksiteInteractionPhase.Perform);
                    return BlacksiteInteractionTickResult.Running;

                case BlacksiteInteractionPhase.Perform:
                    _soldier.StopForWorldInteraction(TransferSettleSeconds);
                    bool transferred = TransferEntrance(context, out string refusal);
                    if (!transferred)
                    {
                        // #1150: the refusal reaches the [UAT][Interaction] reason= line.
                        task.FailureReason = refusal;
                        task.TransitionTo(BlacksiteInteractionPhase.Failed);
                        return BlacksiteInteractionTickResult.Failed;
                    }

                    // #874 S5.4 (#873): the soldier is on the far side from here; the threshold
                    // itself is physically free. Record the clear so the next log answers "how far
                    // apart did this column arrive" in one grep.
                    _coordinator.NoteEntranceCleared(task);
                    task.OperationIssued = true;
                    task.TransitionTo(BlacksiteInteractionPhase.Confirm);
                    issuedDestination = task.ConfirmPosition;
                    return BlacksiteInteractionTickResult.Running;

                case BlacksiteInteractionPhase.Confirm:
                    // #1219 B1: Confirm is reached only through Perform's successful transfer, which
                    // is the whole of "the member is in the world the doorway leads to". #1293 R2: it
                    // completes once the settle has run and the agent is bound, or once the navigator
                    // has declared the body stranded there - that fact owns the member from here. No
                    // deadline.
                    _soldier.StopForWorldInteraction(0.1f);
                    bool agentReady = _agent != null && _agent.enabled && _agent.isOnNavMesh;
                    if (Time.time - task.PhaseStartedAt < TransferSettleSeconds
                        || (!agentReady && !_soldier.Navigator.IsStranded))
                        return BlacksiteInteractionTickResult.Running;
                    task.TransitionTo(BlacksiteInteractionPhase.Complete);
                    return BlacksiteInteractionTickResult.Complete;
            }

            return BlacksiteInteractionTickResult.Failed;
        }

        private bool TransferEntrance(EntranceContext context, out string refusal)
        {
            EntranceTeleport entrance = context.Entrance;
            if (entrance == null)
            {
                refusal = "entrance task carries no entrance";
                return false;
            }

            try
            {
                if (entrance.entranceId == 0)
                    entrance.StartOpeningEntrance();
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.Blacksite] Entrance visual-open adapter skipped: {e.Message}");
            }

            return _soldier.TransferThroughEntranceServer(entrance, context.Reason, out refusal);
        }

        private static readonly float[] ApproachOffsets = { 0.35f, 0.5f, 0.65f };

        // The standable point just before a threshold on the side a body comes from.
        private static Vector3 ResolveThresholdApproach(
            Vector3 body,
            Vector3 threshold,
            Transform orientation)
        {
            Vector3 away = body - threshold;
            away.y = 0f;
            if (away.sqrMagnitude < 0.01f)
                away = orientation != null ? -orientation.forward : Vector3.forward;
            away.Normalize();

            for (int i = 0; i < ApproachOffsets.Length; i++)
            {
                Vector3 candidate = threshold + away * ApproachOffsets[i];
                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, .3f, NavMesh.AllAreas)
                    && HorizontalDistance(hit.position, threshold) <= BlacksiteEntrancePolicy.RouteArrivalRange)
                    return hit.position;
            }
            // #1305 G2 (R16): an anchor off the navmesh (Vertigo's door recess, navigation 1.57 m out) is
            // approached from the nearest navigation still inside Align's reach: settled within the task's
            // StoppingDistance of it, the body keeps RouteArrivalRange's margin under UseRange.
            if (NavMesh.SamplePosition(threshold, out NavMeshHit near, BlacksiteEntrancePolicy.RouteArrivalRange, NavMesh.AllAreas)
                && HorizontalDistance(near.position, threshold) <= BlacksiteEntrancePolicy.RouteArrivalRange - StoppingDistance
                && Mathf.Abs(near.position.y - threshold.y) <= BlacksiteEntrancePolicy.HeightTolerance)
                return near.position;
            return threshold;
        }

        private static void EnsureRoundCache()
        {
            RoundManager round = RoundManager.Instance;
            int roundKey = GetRoundKey();
            if (!ReferenceEquals(round, _cachedRound)
                || roundKey != _cachedRoundKey)
            {
                BlacksiteSoldierAI.ResetStrandedExitSpacing();
                CachedEntrances.Clear();
                _cachedRound = round;
                _cachedRoundKey = roundKey;
                _cacheBuilt = false;
            }
            if (_cacheBuilt)
                return;

            _cacheBuilt = true;
            EntranceTeleport[] entrances = UnityEngine.Object.FindObjectsOfType<EntranceTeleport>();
            if (entrances == null)
                return;
            for (int i = 0; i < entrances.Length; i++)
                RegisterEntrance(entrances[i]);
        }

        // The round cache's key; the door hand's round registry reads the same one (#1305 G1, R14).
        internal static int GetRoundKey()
        {
            unchecked
            {
                int seed = StartOfRound.Instance != null
                    ? StartOfRound.Instance.randomMapSeed
                    : 0;
                int scene = UnityEngine.SceneManagement.SceneManager
                    .GetActiveScene()
                    .handle;
                return seed * 397 ^ scene;
            }
        }

        private static void RegisterEntrance(EntranceTeleport entrance)
        {
            if (entrance != null && !CachedEntrances.Contains(entrance))
                CachedEntrances.Add(entrance);
        }

        // #1150: an interior may spawn more than one counterpart with the same id. The Museum
        // interior's Entry tile spawns two inside id-0 teleports, one at floor height and a leftover
        // one 2.0 m up inside the north wall; vanilla's lazy exitScript and this cache's first match
        // are both arbitrary between them, and the wrong one refuses every transfer. The pairing
        // therefore prefers the counterpart whose endpoint the transfer would accept, and only a
        // pairing with no standable counterpart keeps the first one so the refusal can name it.
        private static bool TryResolvePair(
            EntranceTeleport entrance,
            out EntranceTeleport exit,
            out Vector3 endpoint,
            out string endpointRefusal)
        {
            exit = null;
            endpoint = Vector3.zero;
            endpointRefusal = null;
            if (entrance == null)
                return false;
            EnsureRoundCache();

            if (TryFindExit(entrance, out exit, out endpoint, out EntranceTeleport first))
            {
                if (exit != entrance.exitScript)
                    Pair(entrance, exit);
                return true;
            }
            if (first == null)
                return false;

            exit = first;
            TrySampleExitEndpoint(first, out _, out endpointRefusal);
            Pair(entrance, exit);
            return true;
        }

        // The counterpart the pairing prefers - exitScript, then the cached teleports with the same id on
        // the other side - whose far endpoint the transfer would accept, and the first one looked at.
        // Read only: nothing is paired and no text is made.
        private static bool TryFindExit(EntranceTeleport entrance, out EntranceTeleport exit, out Vector3 endpoint,
            out EntranceTeleport first)
        {
            exit = first = entrance.exitScript;
            if (exit != null && SampleExitEndpoint(exit, out endpoint, out _, out _))
                return true;
            for (int i = 0; i < CachedEntrances.Count; i++)
            {
                exit = CachedEntrances[i];
                if (exit == null
                    || exit == entrance
                    || exit == entrance.exitScript
                    || exit.entranceId != entrance.entranceId
                    || exit.isEntranceToBuilding == entrance.isEntranceToBuilding)
                    continue;
                if (first == null)
                    first = exit;
                if (SampleExitEndpoint(exit, out endpoint, out _, out _))
                    return true;
            }
            exit = null;
            endpoint = Vector3.zero;
            return false;
        }

        private static void Pair(EntranceTeleport entrance, EntranceTeleport exit)
        {
            entrance.exitScript = exit;
            if (exit.exitScript == null)
                exit.exitScript = entrance;
        }

        // #1150: the far endpoint of a paired doorway, resolved one way for the transfer itself and
        // for the up-front door choice at mission start, so the two agree about what refuses. Only
        // the paired doorway changes world spaces; spacing happens by walking away after arrival,
        // never by scattering actors with a wide endpoint sample, which is why the sample is 0.35 m
        // ahead of the doorway within 0.65 m and within 1.25 m of the doorway's height. Every
        // refusal names itself, with the state of the doorway it looked at: the 2026-09-19 Museum
        // run failed 37 transfers with reason=none and the log could not say whether the pairing
        // or the endpoint was gone.
        internal const float ExitEndpointMetres = .35f;
        internal const float ExitEndpointSampleRadius = .65f;

        internal static Vector3 ExitForward(EntranceTeleport exit)
            => exit.entrancePoint != null ? exit.entrancePoint.forward : exit.transform.forward;

        internal static bool TryResolveExitEndpoint(
            EntranceTeleport entrance,
            out EntranceTeleport exit,
            out Vector3 endpoint,
            out string refusal)
        {
            if (entrance == null)
            {
                exit = null;
                endpoint = Vector3.zero;
                refusal = "entrance is null";
                return false;
            }
            // Vanilla sets exitScript lazily; the round cache pairs an unset one by id, as the task
            // creation does. A pairing that is still missing after that is a missing counterpart.
            if (!TryResolvePair(entrance, out exit, out endpoint, out refusal))
            {
                refusal = $"entrance {entrance.entranceId} has no exitScript and no cached counterpart; entrance={DescribeState(entrance)}";
                return false;
            }
            return refusal == null;
        }

        // The far endpoint the transfer lands on, without text: 0.35 m ahead of the doorway, on the
        // navmesh within 0.65 m of that and within the doorway's height tolerance.
        private static bool SampleExitEndpoint(EntranceTeleport exit, out Vector3 endpoint, out NavMeshHit hit, out Vector3 desired)
        {
            Vector3 exitPosition = GetEntrancePoint(exit);
            desired = exitPosition + ExitForward(exit) * ExitEndpointMetres;
            bool standable = NavMesh.SamplePosition(desired, out hit, ExitEndpointSampleRadius, NavMesh.AllAreas)
                && Mathf.Abs(hit.position.y - exitPosition.y) <= BlacksiteEntrancePolicy.HeightTolerance;
            endpoint = standable ? hit.position : Vector3.zero;
            return standable;
        }

        private static bool TrySampleExitEndpoint(EntranceTeleport exit, out Vector3 endpoint, out string refusal)
        {
            refusal = null;
            if (SampleExitEndpoint(exit, out endpoint, out NavMeshHit hit, out Vector3 desired))
                return true;
            Vector3 exitPosition = GetEntrancePoint(exit);
            float dy = Mathf.Abs(hit.position.y - exitPosition.y);
            refusal = !hit.hit
                ? $"exit endpoint {desired:F2} has no NavMesh within {ExitEndpointSampleRadius:0.00} m; exit={DescribeState(exit)}"
                : $"exit endpoint sample {hit.position:F2} is {dy:0.00} m off the doorway height at {exitPosition:F2}, limit {BlacksiteEntrancePolicy.HeightTolerance:0.00} m; exit={DescribeState(exit)}";
            return false;
        }

        // What a refused doorway looked like at the moment it refused: whether the script and its
        // objects were active, where its point was and what it was paired with. An interior that
        // moves or disables its entrance at distance shows up here as the field that changed.
        internal static string DescribeState(EntranceTeleport teleport)
        {
            if (teleport == null) return "null";
            string point = teleport.entrancePoint != null
                ? $"{teleport.entrancePoint.position:F2} active={teleport.entrancePoint.gameObject.activeInHierarchy}"
                : "null";
            return $"[id={teleport.entranceId}, toBuilding={teleport.isEntranceToBuilding}, enabled={teleport.isActiveAndEnabled}, "
                + $"objectActive={teleport.gameObject.activeInHierarchy}, transform={teleport.transform.position:F2}, "
                + $"entrancePoint={point}, pairedWith={(teleport.exitScript != null ? teleport.exitScript.entranceId.ToString() : "none")}]";
        }

        private static Vector3 GetEntrancePoint(EntranceTeleport entrance)
        {
            if (entrance == null)
                return Vector3.zero;
            return entrance.entrancePoint != null
                ? entrance.entrancePoint.position
                : entrance.transform.position;
        }

        private static float HorizontalDistance(Vector3 a, Vector3 b)
        {
            float dx = a.x - b.x;
            float dz = a.z - b.z;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
