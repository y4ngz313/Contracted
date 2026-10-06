using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using Y4NGZCompany.Facility.Elevators;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.Bundy.AI;

public abstract partial class BundyAIBase
{
    // #495: a ring stage is 8 radii x 16 angles + the centre = 129 candidates. At 4 per AI interval
    // (0.2s) that is 6.5s of wall clock for ONE stage, against a ping interval that falls to 3.5s at
    // full escalation (EscalatedStrangerPingIntervalSeconds). 8 per interval scores 40/s, so a stage
    // finishes in ~3.2s and fits inside even the escalated ping. The re-anchor below already removes
    // the dependency on winning that race; this only makes the first answer arrive sooner. 8 samples
    // plus 8 CalculatePath calls still sit under the unchanged 2ms slice, which remains the real cap.
    private const int PursuitFallbackCandidatesPerInterval = 8;
    private const double PursuitFallbackMillisecondsPerInterval = 2d;

    // #495: how long a fallback search may stay pending before the continuation stops re-committing
    // the agent's standing destination, expressed in ping intervals so it tracks the escalation curve.
    // Two intervals is the smallest bound that still lets a full sweep land at the raised candidate
    // rate above; past it, re-issuing a destination that predates two estimate refreshes is what parks
    // him, so the caller falls through to walking at the current estimate instead.
    private const float PursuitFallbackContinuationPingIntervals = 2f;
    private static readonly float[] PursuitFallbackSearchRadii =
    {
        0f,
        0.75f,
        1.35f,
        2.1f,
        3.25f,
        4.75f,
        6.5f,
        8.5f,
        NavigationTargetApproachMaxRadius
    };

    private enum PursuitReachabilityStatus
    {
        Resolved,
        PendingWithContinuation,
        Pending,
        Blocked
    }

    private enum PursuitFallbackSearchStage
    {
        None,
        TargetApproach,
        DesiredApproach,
        AiNodes
    }

    private bool _pursuitFallbackSearchActive;
    private bool _pursuitFallbackSearchOutside;
    private PlayerControllerB _pursuitFallbackSearchTarget;
    private Vector3 _pursuitFallbackSearchTargetPosition;
    private Vector3 _pursuitFallbackSearchDesired;
    private PursuitFallbackSearchStage _pursuitFallbackSearchStage;
    private int _pursuitFallbackSearchRadiusIndex;
    private int _pursuitFallbackSearchAngleIndex;
    private int _pursuitFallbackSearchNodeIndex;
    private GameObject[] _pursuitFallbackSearchNodes;
    private float _pursuitFallbackSearchBestCost = float.PositiveInfinity;
    private Vector3 _pursuitFallbackSearchBestPoint;
    private string _pursuitFallbackSearchBestMode = "none";
    private bool _pursuitFallbackSearchFound;
    private float _pursuitFallbackSearchStartedAt = float.NegativeInfinity;
    private readonly NavMeshPath _pursuitFallbackSearchPath = new NavMeshPath();

    // #300 Tier 2: the reachable-point ring sweep is 9 radii x 16 angles + centre = 129 candidates,
    // each a NavMesh sample plus a synchronous CalculatePath, and it measured 74-95ms in one frame.
    // It now runs through the same shape as the #263 pursuit fallback -- bounded candidates per call
    // with a persisted cursor -- and only publishes a winner once every candidate has been scored, so
    // the ordering, the cost comparison and the final selection are unchanged.
    private const int ReachableNavPointCandidatesPerInterval = 4;
    private const double ReachableNavPointMillisecondsPerInterval = 2d;

    // #300 Tier 1: the stuck-recovery AI-node sweeps (route reroute, then the warp fallback) walked
    // every AI node in one frame and measured 235-276ms. Same treatment, same budget shape as #263.
    private const int StuckRecoveryCandidatesPerInterval = 4;
    private const double StuckRecoveryMillisecondsPerInterval = 2d;

    // #300 B2: MovementStuckMinProgress (0.18m) is a DETECTION threshold -- it is small enough that a
    // pinned agent with residual velocity crosses it repeatedly without going anywhere. Cancelling a
    // 1-4s sliced sweep on that would reset the cursor forever and the reroute would never land, so an
    // in-flight sweep only gives up on real escape: this much planar travel from where the sweep began.
    private const float StuckRecoveryCancelProgressMeters = 0.75f;

    // #300 B4: a request whose centre is further than the cache move tolerance from an in-flight
    // sweep's centre is a DIFFERENT question -- it may not read that sweep's answer, and it may not
    // restart its cursor either until the sweep has had this long to finish.
    private const float ReachableNavPointSearchStealSeconds = 3f;

    private enum ReachableNavPointSearchStatus
    {
        Resolved,
        Failed,
        Pending
    }

    private enum StuckRecoverySearchStage
    {
        None,
        RouteNodes,
        RecoveryNodes
    }

    private enum StuckRecoverySearchOutcome
    {
        Pending,
        Rerouted,
        Warped,
        Exhausted
    }

    private bool _reachableNavPointSearchActive;
    private bool _reachableNavPointSearchOutside;
    private Vector3 _reachableNavPointSearchCenter;
    private PlayerControllerB _reachableNavPointSearchTarget;
    private float _reachableNavPointSearchCenterDistance;
    private int _reachableNavPointSearchRadiusIndex;
    private int _reachableNavPointSearchAngleIndex;
    private float _reachableNavPointSearchBestCost = float.PositiveInfinity;
    private Vector3 _reachableNavPointSearchBestPoint;
    private string _reachableNavPointSearchBestMode = "none";
    private bool _reachableNavPointSearchFound;
    private readonly NavMeshPath _reachableNavPointSearchPath = new NavMeshPath();

    private float _reachableNavPointSearchStartedAt;

    // #300 B4: two slots so a near-target answer and a near-ship answer can coexist. One slot let a
    // near-target FAILURE suppress the near-ship probe whenever the player stood close to the pad.
    private struct ReachableNavPointCacheEntry
    {
        public bool Valid;
        public bool Succeeded;
        public Vector3 Center;
        public Vector3 Origin;
        public Vector3 Result;
        public string Mode;
        public float Until;
    }

    private ReachableNavPointCacheEntry _reachableNavPointCacheA;
    private ReachableNavPointCacheEntry _reachableNavPointCacheB;

    private bool _stuckRecoverySearchActive;
    private Vector3 _stuckRecoverySearchStartPosition;
    private float _stuckRecoverySearchStartStuckFor;
    private int _stuckRecoverySearchAdvancedFrame = -1;
    private StuckRecoverySearchStage _stuckRecoverySearchStage;
    private PlayerControllerB _stuckRecoverySearchTarget;
    private PlayerControllerB _stuckRecoverySearchHuntTarget;
    private Vector3 _stuckRecoverySearchDesired;
    private Vector3 _stuckRecoverySearchScoringTarget;
    private float _stuckRecoverySearchCurrentTargetDistance;
    private GameObject[] _stuckRecoverySearchNodes;
    private int _stuckRecoverySearchNodeIndex;
    private float _stuckRecoverySearchBestCost = float.PositiveInfinity;
    private Vector3 _stuckRecoverySearchBestPoint;
    private string _stuckRecoverySearchBestMode = "none";
    private bool _stuckRecoverySearchFound;
    private readonly NavMeshPath _stuckRecoverySearchPath = new NavMeshPath();

    private void UpdateTrackedTargetMotion()
    {
        PlayerControllerB target = GetCurrentHuntTarget();
        if (!IsReachableHuntTarget(target) || !CanUseLiveHuntTarget(target))
        {
            _hasTrackedTargetSample = false;
            _trackedTargetVelocity = Vector3.zero;
            return;
        }

        float now = Time.time;
        Vector3 position = target.transform.position;
        if (_hasTrackedTargetSample && now > _lastTrackedTargetSampleAt + 0.001f)
        {
            float dt = now - _lastTrackedTargetSampleAt;
            Vector3 sampleVelocity = (position - _lastTrackedTargetPosition) / dt;
            sampleVelocity.y = 0f;
            _trackedTargetVelocity = Vector3.Lerp(_trackedTargetVelocity, sampleVelocity, 0.35f);
        }

        _lastTrackedTargetPosition = position;
        _lastTrackedTargetSampleAt = now;
        _hasTrackedTargetSample = true;
    }

    private Vector3 PredictTargetPosition(PlayerControllerB target, float leadTime, float maxLeadDistance)
    {
        Vector3 predicted = target.transform.position;
        Vector3 planarVelocity = _trackedTargetVelocity;
        planarVelocity.y = 0f;
        if (planarVelocity.sqrMagnitude > 0.04f)
            predicted += Vector3.ClampMagnitude(planarVelocity * Mathf.Max(0f, leadTime), Mathf.Max(0.5f, maxLeadDistance));

        if (TryResolveNavigableDestination(predicted, out Vector3 navPredicted))
            return navPredicted;

        return target.transform.position;
    }

    private bool TryGetSmartDestination(Vector3 finalDestination, out Vector3 issuedDestination)
    {
        issuedDestination = Vector3.zero;
        _lastSmartPathPending = false;
        _lastSmartPathNoRoute = false;
        if ((!BundySmartPathingBridge.IsAvailable && !AnimatedElevatorRideCoordinator.HasAvailableElevator()) ||
            agent == null ||
            !agent.enabled ||
            !agent.isOnNavMesh)
        {
            _lastSmartPathNoRoute = true;
            return false;
        }

        if (_smartPathing == null)
            _smartPathing = new BundySmartPathingBridge();

        // The bridge issues link waypoints through IssueRawAgentDestination while resolving. Those
        // writes are part of THIS resolution — the caller records the outcome once. Without the
        // suppression, smart-task and the caller's source alternate every tick and defeat the
        // [BundyMoveAuth] change-dedupe (first playtest: 2 lines per tick while smart-routing).
        _moveAuthSuppressNestedRecords = true;
        try
        {
            return _smartPathing.TrySetDestination(
                this,
                agent,
                finalDestination,
                ShouldAllowBoundarySmartLinks(finalDestination),
                out issuedDestination,
                out _lastSmartPathPending,
                out _lastSmartPathNoRoute);
        }
        finally
        {
            _moveAuthSuppressNestedRecords = false;
        }
    }

    // Whether a smart route to `finalDestination` may cross the facility boundary (main entrance or a
    // fire exit).
    //
    // 2026-07-31 (#199): it may not when the destination is on the side Bundy is already on. A boundary
    // link puts him on the OTHER side, so it cannot be a step toward a same-side destination — but the
    // smart layer offered exactly that for a player camping outdoors, and taking it handed him to the
    // crossing route, which correctly walked him straight back out. Four minutes of that, gap never
    // closing (Rockwell, 23:12-23:16). Genuine crossings are unaffected: the destination is then on the
    // far side and this returns true.
    //
    // The test is on the DESTINATION, not on the hunted player's side flag, because the pursuit layer
    // also routes to ping/snapshot/siege points that no live player is standing on. Inconclusive
    // inference is permissive — the pre-fix behaviour — so an unfamiliar interior cannot lock him out
    // of entrances entirely.
    private bool ShouldAllowBoundarySmartLinks(Vector3 finalDestination)
    {
        if (!SmartBoundaryLinkGateEnabled)
            return true;

        if (_hasBoundarySmartLinkCache &&
            Time.time <= _boundarySmartLinkCacheUntil &&
            _boundarySmartLinkCacheOutside == isOutside &&
            Vector3.Distance(finalDestination, _boundarySmartLinkCacheDestination) <= BoundarySmartLinkCacheMoveTolerance)
        {
            return _boundarySmartLinkCacheAllowed;
        }

        bool allowed = true;
        if (TryInferOutsideStateFromAINodes(finalDestination, out bool destinationOutside, out _, out _, out _))
            allowed = destinationOutside != isOutside;

        _hasBoundarySmartLinkCache = true;
        _boundarySmartLinkCacheAllowed = allowed;
        _boundarySmartLinkCacheOutside = isOutside;
        _boundarySmartLinkCacheDestination = finalDestination;
        _boundarySmartLinkCacheUntil = Time.time + BoundarySmartLinkCacheSeconds;
        return allowed;
    }

    // Single-argument form is the smart-path bridge's Action<Vector3> callback; every other caller
    // names its source explicitly for the movement-authority line.
    internal void IssueRawAgentDestination(Vector3 resolved)
    {
        IssueRawAgentDestination(resolved, "smart-task", "smart path link routing");
    }

    internal void IssueRawAgentDestination(Vector3 resolved, string source, string reason)
    {
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return;

        agent.isStopped = false;
        movingTowardsTargetPlayer = false;
        moveTowardsDestination = true;
        RecordPursuitDestination(resolved, source, reason);
        SetDestinationToPosition(resolved);
        HoldLocomotionIntent(0.35f);
        SetLocalBehaviourState(BehaviourStateTracking);
    }

    internal void PauseForSmartInteraction(float seconds)
    {
        _smartInteractionHoldUntil = Mathf.Max(_smartInteractionHoldUntil, Time.time + Mathf.Max(0f, seconds));
        HoldAgentWithoutReset();
    }

    internal bool TryUseSmartEntranceTeleport(EntranceTeleport entrance, string reason)
    {
        if (entrance == null)
            return false;

        try
        {
            if (entrance.exitScript == null && !entrance.FindExitPoint())
                return false;

            EntranceTeleport exit = entrance.exitScript;
            if (exit == null)
                return false;

            Vector3 exitPosition = GetEntrancePoint(exit);
            Vector3 side = transform.right * 0.75f;
            Vector3 target = ResolveNavMeshPosition(exitPosition + side, 10f);
            bool outsideAfterUse = !entrance.isEntranceToBuilding;
            _doorOpener?.ClearPendingWorldInteractionForTeleport(reason);
            SetOutsideState(outsideAfterUse);
            WarpAgentTo(target);
            BeginSmartEntranceSettlePause(reason);
            LogInfo($"Used smart entrance {entrance.entranceId} for {reason}; outside={outsideAfterUse}, target={FormatVector(target)}.");
            return true;
        }
        catch (Exception e)
        {
            LogWarning($"Smart entrance use skipped for {reason}: {e.Message}");
            return false;
        }
    }

    internal bool TryUseSmartInternalTeleport(Transform target)
    {
        if (target == null)
            return false;

        Vector3 position = ResolveNavMeshPosition(target.position, 8f);
        _doorOpener?.ClearPendingWorldInteractionForTeleport("internal smart teleport");
        WarpAgentTo(position);
        PauseForSmartInteraction(0.25f);
        LogInfo($"Used smart internal teleport to {FormatVector(position)}.");
        return true;
    }

    private void BeginSmartEntranceSettlePause(string reason)
    {
        PauseForSmartInteraction(SmartEntrancePostTeleportSettleSeconds);
        _smartEntranceSettleUntil = Time.time + SmartEntrancePostTeleportSettleSeconds;
        _smartEntranceSettleLogPending = true;
        LogInfo($"Smart entrance settle start: reason={reason}, hold={SmartEntrancePostTeleportSettleSeconds:F2}s, position={FormatVector(transform.position)}.");
    }

    private void MaybeLogSmartEntranceSettleComplete()
    {
        if (!_smartEntranceSettleLogPending || Time.time < _smartEntranceSettleUntil)
            return;

        _smartEntranceSettleLogPending = false;
        LogInfo($"Smart entrance settle end: position={FormatVector(transform.position)}.");
    }

    private void WarpAgentTo(Vector3 target)
    {
        bool warped = false;
        if (agent != null && agent.enabled)
        {
            try
            {
                if (agent.isOnNavMesh)
                {
                    agent.ResetPath();
                    agent.isStopped = false;
                    warped = agent.Warp(target);
                }
            }
            catch (Exception e)
            {
                LogWarning($"Smart warp skipped: {e.Message}");
            }
        }

        transform.position = target;
        if (!warped)
            TryRecoverAgentOntoNavMesh();
    }

    private void SetOutsideState(bool outside)
    {
        try
        {
            SetEnemyOutside(outside);
        }
        catch
        {
            isOutside = outside;
        }

        isOutside = outside;
        if (RoundManager.Instance == null)
            return;

        GameObject[] nodes = FilterValidNavigationNodes(outside ? RoundManager.Instance.outsideAINodes : RoundManager.Instance.insideAINodes);
        if (nodes != null && nodes.Length > 0)
            allAINodes = nodes;
    }

    private void ReconcileOutsideStateWithPhysicalPosition()
    {
        if (!TryInferOutsideStateFromAINodes(
                transform.position,
                out bool physicalOutside,
                out float insideDistance,
                out float outsideDistance,
                out string source))
        {
            return;
        }

        if (physicalOutside == isOutside)
            return;

        bool previousOutside = isOutside;
        SetOutsideState(physicalOutside);
        StopPursuitSearchIfActive();
        _nextPursuitSearchAllowedAt = 0f;
        _hasCompletePathCache = false;
        _hasPursuitFallbackCache = false;

        LogInfo(
            $"Reconciled Bundy outside state from physical position: {previousOutside} -> {physicalOutside}; " +
            $"SetEnemyOutside({physicalOutside}); source={source}, insideNode={FormatMeters(insideDistance)}, " +
            $"outsideNode={FormatMeters(outsideDistance)}, position={FormatVector(transform.position)}.");
    }

    private bool TryInferOutsideStateFromAINodes(
        Vector3 position,
        out bool physicalOutside,
        out float insideDistance,
        out float outsideDistance,
        out string source)
    {
        physicalOutside = isOutside;
        source = "none";
        GameObject[] insideNodes = GetPhysicalStateAINodes(outside: false);
        GameObject[] outsideNodes = GetPhysicalStateAINodes(outside: true);
        bool hasInside = TryGetNearestAINodeDistance(insideNodes, position, out insideDistance);
        bool hasOutside = TryGetNearestAINodeDistance(outsideNodes, position, out outsideDistance);
        if (!hasInside && !hasOutside)
            return false;

        if (hasInside && hasOutside)
        {
            float nearestDistance = Mathf.Min(insideDistance, outsideDistance);
            if (nearestDistance > OutsideStateAINodeMaxDistance)
                return false;

            if (insideDistance + OutsideStateAINodeConfidenceMargin < outsideDistance)
            {
                physicalOutside = false;
                source = "nearest inside AINode";
                return true;
            }

            if (outsideDistance + OutsideStateAINodeConfidenceMargin < insideDistance)
            {
                physicalOutside = true;
                source = "nearest outside AINode";
                return true;
            }

            return false;
        }

        if (hasInside && insideDistance <= OutsideStateAINodeMaxDistance)
        {
            physicalOutside = false;
            source = "inside AINodes only";
            return true;
        }

        if (hasOutside && outsideDistance <= OutsideStateAINodeMaxDistance)
        {
            physicalOutside = true;
            source = "outside AINodes only";
            return true;
        }

        return false;
    }

    private static GameObject[] GetPhysicalStateAINodes(bool outside)
    {
        GameObject[] nodes = Array.Empty<GameObject>();
        if (RoundManager.Instance != null)
        {
            nodes = FilterValidNavigationNodes(outside
                ? RoundManager.Instance.outsideAINodes
                : RoundManager.Instance.insideAINodes);
        }

        if (nodes == null || nodes.Length == 0)
            nodes = FilterValidNavigationNodes(FindNavigationNodesByTag(outside));

        return nodes;
    }

    private static bool TryGetNearestAINodeDistance(GameObject[] nodes, Vector3 position, out float distance)
    {
        distance = float.PositiveInfinity;
        if (nodes == null || nodes.Length == 0)
            return false;

        for (int i = 0; i < nodes.Length; i++)
        {
            GameObject node = nodes[i];
            if (node == null)
                continue;

            float candidateDistance = Vector3.Distance(position, node.transform.position);
            if (candidateDistance < distance)
                distance = candidateDistance;
        }

        return !float.IsPositiveInfinity(distance);
    }

    private static string FormatMeters(float distance)
    {
        return float.IsPositiveInfinity(distance) ? "none" : $"{distance:F1}m";
    }

    private static Vector3 ResolveNavMeshPosition(Vector3 origin, float maxDistance)
    {
        if (NavMesh.SamplePosition(origin, out NavMeshHit hit, maxDistance, NavMesh.AllAreas))
            return hit.position;
        return origin;
    }

    private static Vector3 GetEntrancePoint(EntranceTeleport entrance)
    {
        if (entrance == null)
            return Vector3.zero;
        return entrance.entrancePoint != null ? entrance.entrancePoint.position : entrance.transform.position;
    }

    private bool IsPlayerBehindBundy(PlayerControllerB target)
    {
        if (!IsLivingPlayer(target))
            return false;

        Vector3 toTarget = target.transform.position - transform.position;
        toTarget.y = 0f;
        if (toTarget.sqrMagnitude < 0.001f)
            return false;

        return Vector3.Dot(transform.forward, toTarget.normalized) < -0.2f;
    }

    private bool TryResolveNavigableDestination(Vector3 desired, out Vector3 resolved)
    {
        resolved = desired;
        if (agent == null || !agent.enabled)
            return true;

        if (NavMesh.SamplePosition(desired, out NavMeshHit sample, NavigationDirectSampleDistance, NavMesh.AllAreas))
            resolved = sample.position;

        if (!TryCalculateCompletePath(resolved, out NavMeshPath path))
            return false;

        if (DebugLogs && path.corners != null && path.corners.Length > 1)
        {
            Vector3 nextCorner = path.corners[Mathf.Min(1, path.corners.Length - 1)];
            Debug.DrawLine(transform.position + Vector3.up * 0.1f, nextCorner + Vector3.up * 0.1f, Color.green, 0.3f, false);
        }

        return true;
    }

    private PursuitReachabilityStatus TryResolveReachablePursuitDestination(
        PlayerControllerB target,
        Vector3 desired,
        out Vector3 resolved,
        out string navigationMode)
    {
        navigationMode = "direct";
        if (TryResolveNavigableDestination(desired, out resolved))
        {
            CancelPursuitFallbackSearch();
            return PursuitReachabilityStatus.Resolved;
        }

        if (TryUseCachedReachablePursuitDestination(target, desired, out bool cachedSuccess, out resolved, out navigationMode))
        {
            CancelPursuitFallbackSearch();
            return cachedSuccess
                ? PursuitReachabilityStatus.Resolved
                : PursuitReachabilityStatus.Blocked;
        }

        EnsurePursuitFallbackSearch(target, desired);
        PursuitReachabilityStatus searchStatus = AdvancePursuitFallbackSearch(out resolved, out navigationMode);
        if (searchStatus == PursuitReachabilityStatus.Resolved ||
            searchStatus == PursuitReachabilityStatus.Blocked)
        {
            return searchStatus;
        }

        if (TryGetPursuitFallbackContinuation(out resolved, out navigationMode))
            return PursuitReachabilityStatus.PendingWithContinuation;

        resolved = desired;
        navigationMode = "direct:search-pending";
        return PursuitReachabilityStatus.Pending;
    }

    private void EnsurePursuitFallbackSearch(PlayerControllerB target, Vector3 desired)
    {
        Vector3 targetPosition = GetPursuitCacheTargetPosition(target, desired);

        // #495: the anchors used to be part of the restart test, against NavigationSearchRestartDistance
        // (6m). A stranger ping displaces the estimate by up to PingPositionNoiseMeters (12m) every ping
        // interval, so that test was satisfied on every single ping and the cursor was thrown away at
        // roughly twice the rate a sweep could finish -- the search never completed and the continuation
        // below re-issued a stale destination for the whole pursuit.
        //
        // What actually invalidates a sweep in flight is a change of QUESTION: a different target, or a
        // boundary side from which none of the scored candidates are walkable. Anchor drift is not that;
        // it only reshapes the scoring of the candidates still ahead of the cursor. So the anchors are
        // re-anchored in place and the cursor, the stage and the best-so-far are left alone -- the same
        // trade RefreshStuckRecoveryScoringAnchors already makes for the stuck-recovery sweep, and for
        // the same reason. A ring stage's centre moving mid-sweep costs some sample coverage around the
        // old centre; never finishing costs the entire behaviour.
        if (_pursuitFallbackSearchActive &&
            _pursuitFallbackSearchOutside == isOutside &&
            _pursuitFallbackSearchTarget == target)
        {
            _pursuitFallbackSearchTargetPosition = targetPosition;
            _pursuitFallbackSearchDesired = desired;
            return;
        }

        CancelPursuitFallbackSearch();
        _pursuitFallbackSearchStartedAt = Time.time;
        _pursuitFallbackSearchActive = true;
        _pursuitFallbackSearchOutside = isOutside;
        _pursuitFallbackSearchTarget = target;
        _pursuitFallbackSearchTargetPosition = targetPosition;
        _pursuitFallbackSearchDesired = desired;
        BeginPursuitFallbackSearchStage(
            target != null && CanUseLiveHuntTarget(target)
                ? PursuitFallbackSearchStage.TargetApproach
                : PursuitFallbackSearchStage.DesiredApproach);
    }

    private void BeginPursuitFallbackSearchStage(PursuitFallbackSearchStage stage)
    {
        _pursuitFallbackSearchStage = stage;
        _pursuitFallbackSearchRadiusIndex = 0;
        _pursuitFallbackSearchAngleIndex = 0;
        _pursuitFallbackSearchNodeIndex = 0;
        _pursuitFallbackSearchNodes = stage == PursuitFallbackSearchStage.AiNodes
            ? GetNavigationNodesForTarget(_pursuitFallbackSearchTarget)
            : null;
        _pursuitFallbackSearchBestCost = float.PositiveInfinity;
        _pursuitFallbackSearchBestPoint = _pursuitFallbackSearchDesired;
        _pursuitFallbackSearchBestMode = "none";
        _pursuitFallbackSearchFound = false;
    }

    private PursuitReachabilityStatus AdvancePursuitFallbackSearch(
        out Vector3 resolved,
        out string navigationMode)
    {
        resolved = _pursuitFallbackSearchDesired;
        navigationMode = "direct:search-pending";
        if (!_pursuitFallbackSearchActive)
            return PursuitReachabilityStatus.Pending;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int candidatesExamined = 0;
        while (candidatesExamined < PursuitFallbackCandidatesPerInterval)
        {
            switch (_pursuitFallbackSearchStage)
            {
                case PursuitFallbackSearchStage.TargetApproach:
                case PursuitFallbackSearchStage.DesiredApproach:
                    if (_pursuitFallbackSearchRadiusIndex >= PursuitFallbackSearchRadii.Length)
                    {
                        if (_pursuitFallbackSearchFound)
                        {
                            string prefix = _pursuitFallbackSearchStage == PursuitFallbackSearchStage.TargetApproach
                                ? "target-approach:"
                                : "desired-approach:";
                            return CompletePursuitFallbackSearch(
                                true,
                                _pursuitFallbackSearchBestPoint,
                                prefix + _pursuitFallbackSearchBestMode,
                                out resolved,
                                out navigationMode);
                        }

                        BeginPursuitFallbackSearchStage(
                            _pursuitFallbackSearchStage == PursuitFallbackSearchStage.TargetApproach
                                ? PursuitFallbackSearchStage.DesiredApproach
                                : PursuitFallbackSearchStage.AiNodes);
                        continue;
                    }

                    EvaluateNextPursuitFallbackRingCandidate();
                    candidatesExamined++;
                    break;

                case PursuitFallbackSearchStage.AiNodes:
                    if (_pursuitFallbackSearchNodes == null ||
                        _pursuitFallbackSearchNodeIndex >= _pursuitFallbackSearchNodes.Length)
                    {
                        if (_pursuitFallbackSearchFound)
                        {
                            return CompletePursuitFallbackSearch(
                                true,
                                _pursuitFallbackSearchBestPoint,
                                "ai-node:" + _pursuitFallbackSearchBestMode,
                                out resolved,
                                out navigationMode);
                        }

                        return CompletePursuitFallbackSearch(
                            false,
                            _pursuitFallbackSearchDesired,
                            "blocked",
                            out resolved,
                            out navigationMode);
                    }

                    EvaluateNextPursuitFallbackNodeCandidate();
                    candidatesExamined++;
                    break;

                default:
                    return PursuitReachabilityStatus.Pending;
            }

            if (PursuitFallbackBudgetExpired(started))
                break;
        }

        return PursuitReachabilityStatus.Pending;
    }

    private void EvaluateNextPursuitFallbackRingCandidate()
    {
        float radius = PursuitFallbackSearchRadii[_pursuitFallbackSearchRadiusIndex];
        int angleSteps = radius <= 0.01f ? 1 : 16;
        int angleIndex = _pursuitFallbackSearchAngleIndex;
        AdvancePursuitFallbackRingCursor(angleSteps);

        float angle = angleSteps == 1 ? 0f : (360f / angleSteps) * angleIndex;
        Vector3 offset = angleSteps == 1
            ? Vector3.zero
            : Quaternion.Euler(0f, angle, 0f) * Vector3.forward * radius;
        Vector3 center = _pursuitFallbackSearchStage == PursuitFallbackSearchStage.TargetApproach
            ? _pursuitFallbackSearchTargetPosition
            : _pursuitFallbackSearchDesired;
        Vector3 candidate = center + offset;
        float sampleDistance = radius <= 0.01f
            ? NavigationDirectSampleDistance
            : NavigationTargetApproachSampleDistance;

        if (!NavMesh.SamplePosition(candidate, out NavMeshHit sample, sampleDistance, NavMesh.AllAreas))
            return;

        float centerDistanceFromBundy = Vector3.Distance(transform.position, center);
        if (centerDistanceFromBundy > StopDistance + NavigationTargetApproachMinProgress &&
            Vector3.Distance(transform.position, sample.position) < NavigationTargetApproachMinProgress)
        {
            return;
        }

        if (!TryCalculatePursuitFallbackPath(sample.position))
            return;

        float centerDistance = Vector3.Distance(sample.position, center);
        float pathLength = EstimatePathLength(_pursuitFallbackSearchPath);
        float targetYCost = _pursuitFallbackSearchTarget != null
            ? Mathf.Abs(sample.position.y - _pursuitFallbackSearchTarget.transform.position.y) * 0.4f
            : 0f;
        float cost = centerDistance * 1.6f + pathLength * 0.25f + targetYCost;
        if (cost >= _pursuitFallbackSearchBestCost)
            return;

        _pursuitFallbackSearchBestCost = cost;
        _pursuitFallbackSearchBestPoint = sample.position;
        _pursuitFallbackSearchBestMode = $"r{radius:F1}/path{pathLength:F1}";
        _pursuitFallbackSearchFound = true;
    }

    private void AdvancePursuitFallbackRingCursor(int angleSteps)
    {
        _pursuitFallbackSearchAngleIndex++;
        if (_pursuitFallbackSearchAngleIndex < angleSteps)
            return;

        _pursuitFallbackSearchAngleIndex = 0;
        _pursuitFallbackSearchRadiusIndex++;
    }

    private void EvaluateNextPursuitFallbackNodeCandidate()
    {
        int nodeIndex = _pursuitFallbackSearchNodeIndex++;
        GameObject node = _pursuitFallbackSearchNodes[nodeIndex];
        if (node == null ||
            !NavMesh.SamplePosition(node.transform.position, out NavMeshHit sample, NavigationNodeRouteSampleDistance, NavMesh.AllAreas) ||
            !TryCalculatePursuitFallbackPath(sample.position))
        {
            return;
        }

        bool useLiveTarget = _pursuitFallbackSearchTarget != null &&
            CanUseLiveHuntTarget(_pursuitFallbackSearchTarget);
        Vector3 scoringTarget = useLiveTarget
            ? _pursuitFallbackSearchTargetPosition
            : _pursuitFallbackSearchDesired;
        float currentTargetDistance = PlanarDistance(transform.position, scoringTarget);
        float selfDistance = PlanarDistance(transform.position, sample.position);
        if (selfDistance <= StopDistance + 0.35f && currentTargetDistance > StopDistance + 1.5f)
            return;

        float targetDistance = PlanarDistance(sample.position, scoringTarget);
        if (targetDistance > currentTargetDistance + NavigationNodeRouteMaxTargetRegression)
            return;

        float desiredDistance = PlanarDistance(sample.position, _pursuitFallbackSearchDesired);
        float pathLength = EstimatePathLength(_pursuitFallbackSearchPath);
        float heightCost = Mathf.Abs(sample.position.y - scoringTarget.y) * 1.2f;
        float progressReward = Mathf.Max(0f, currentTargetDistance - targetDistance) * 0.75f;
        float cost = targetDistance * 1.65f
            + desiredDistance * 0.45f
            + pathLength * 0.35f
            + selfDistance * 0.08f
            + heightCost
            - progressReward;
        if (cost >= _pursuitFallbackSearchBestCost)
            return;

        _pursuitFallbackSearchBestCost = cost;
        _pursuitFallbackSearchBestPoint = sample.position;
        _pursuitFallbackSearchBestMode = $"node#{nodeIndex}/target{targetDistance:F1}/path{pathLength:F1}";
        _pursuitFallbackSearchFound = true;
    }

    private bool TryCalculatePursuitFallbackPath(Vector3 destination)
    {
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return false;

        try
        {
            return agent.CalculatePath(destination, _pursuitFallbackSearchPath) &&
                _pursuitFallbackSearchPath.status == NavMeshPathStatus.PathComplete;
        }
        catch
        {
            return false;
        }
    }

    private static bool PursuitFallbackBudgetExpired(long started)
    {
        double elapsedMilliseconds =
            (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d /
            System.Diagnostics.Stopwatch.Frequency;
        return elapsedMilliseconds >= PursuitFallbackMillisecondsPerInterval;
    }

    private PursuitReachabilityStatus CompletePursuitFallbackSearch(
        bool succeeded,
        Vector3 result,
        string mode,
        out Vector3 resolved,
        out string navigationMode)
    {
        PlayerControllerB target = _pursuitFallbackSearchTarget;
        Vector3 desired = _pursuitFallbackSearchDesired;
        resolved = result;
        navigationMode = mode;
        RememberReachablePursuitDestination(target, desired, result, mode, succeeded);
        CancelPursuitFallbackSearch();
        return succeeded
            ? PursuitReachabilityStatus.Resolved
            : PursuitReachabilityStatus.Blocked;
    }

    private bool TryGetPursuitFallbackContinuation(out Vector3 destination, out string mode)
    {
        if (_hasPursuitFallbackCache && _cachedPursuitFallbackSucceeded)
        {
            destination = _cachedPursuitFallbackResolved;
            mode = (string.IsNullOrEmpty(_cachedPursuitFallbackMode)
                ? "cached"
                : _cachedPursuitFallbackMode) + ":search-pending";
            return true;
        }

        // #495: the standing destination is only a legitimate continuation while the sweep that will
        // replace it is plausibly still on its way. Past the bound it is a destination computed from an
        // estimate two or more refreshes old, and re-committing it every interval is precisely what
        // parked him 38-68m out: the caller falls through to Pending and walks at the current estimate
        // instead, which is the same thing an untouched, never-searched pursuit does.
        if (agent != null && agent.enabled && agent.isOnNavMesh && !PursuitFallbackContinuationExpired())
        {
            try
            {
                Vector3 currentDestination = agent.destination;
                if (agent.hasPath &&
                    !float.IsNaN(currentDestination.x) &&
                    !float.IsInfinity(currentDestination.x) &&
                    !float.IsNaN(currentDestination.y) &&
                    !float.IsInfinity(currentDestination.y) &&
                    !float.IsNaN(currentDestination.z) &&
                    !float.IsInfinity(currentDestination.z))
                {
                    destination = currentDestination;
                    mode = "direct:search-pending";
                    return true;
                }
            }
            catch
            {
            }
        }

        destination = _pursuitFallbackSearchDesired;
        mode = "direct:search-pending";
        return false;
    }

    private bool PursuitFallbackContinuationExpired()
    {
        if (float.IsNegativeInfinity(_pursuitFallbackSearchStartedAt))
            return false;

        float bound = Mathf.Max(1f, GetEffectiveStrangerPingInterval()) *
            PursuitFallbackContinuationPingIntervals;
        return Time.time - _pursuitFallbackSearchStartedAt > bound;
    }

    private void CancelPursuitFallbackSearch()
    {
        _pursuitFallbackSearchActive = false;
        _pursuitFallbackSearchStartedAt = float.NegativeInfinity;
        _pursuitFallbackSearchTarget = null;
        _pursuitFallbackSearchStage = PursuitFallbackSearchStage.None;
        _pursuitFallbackSearchNodes = null;
        _pursuitFallbackSearchRadiusIndex = 0;
        _pursuitFallbackSearchAngleIndex = 0;
        _pursuitFallbackSearchNodeIndex = 0;
        _pursuitFallbackSearchBestCost = float.PositiveInfinity;
        _pursuitFallbackSearchFound = false;
    }


    private bool TryUseCachedReachablePursuitDestination(
        PlayerControllerB target,
        Vector3 desired,
        out bool succeeded,
        out Vector3 resolved,
        out string navigationMode)
    {
        succeeded = false;
        resolved = desired;
        navigationMode = "cached:none";
        if (!_hasPursuitFallbackCache || Time.time > _pursuitFallbackCacheUntil)
            return false;

        Vector3 targetPosition = GetPursuitCacheTargetPosition(target, desired);
        if (Vector3.Distance(targetPosition, _cachedPursuitFallbackTargetPosition) > NavigationPathCacheTargetMoveTolerance)
            return false;
        if (Vector3.Distance(desired, _cachedPursuitFallbackDesired) > NavigationPathCacheTargetMoveTolerance)
            return false;
        if (Vector3.Distance(transform.position, _cachedPursuitFallbackOrigin) > NavigationPathCacheSelfMoveTolerance)
            return false;

        succeeded = _cachedPursuitFallbackSucceeded;
        resolved = succeeded ? _cachedPursuitFallbackResolved : desired;
        navigationMode = string.IsNullOrEmpty(_cachedPursuitFallbackMode)
            ? "cached"
            : _cachedPursuitFallbackMode + ":cached";
        return true;
    }

    private void RememberReachablePursuitDestination(
        PlayerControllerB target,
        Vector3 desired,
        Vector3 resolved,
        string navigationMode,
        bool succeeded)
    {
        _hasPursuitFallbackCache = true;
        _cachedPursuitFallbackSucceeded = succeeded;
        _cachedPursuitFallbackOrigin = transform.position;
        _cachedPursuitFallbackTargetPosition = GetPursuitCacheTargetPosition(target, desired);
        _cachedPursuitFallbackDesired = desired;
        _cachedPursuitFallbackResolved = resolved;
        _cachedPursuitFallbackMode = navigationMode;
        _pursuitFallbackCacheUntil = Time.time + (succeeded
            ? NavigationFallbackSuccessCacheSeconds
            : NavigationFallbackFailureCacheSeconds);
    }

    private Vector3 GetPursuitCacheTargetPosition(PlayerControllerB target, Vector3 desired)
    {
        return target != null && CanUseLiveHuntTarget(target)
            ? target.transform.position
            : desired;
    }

    // #300: the bool face kept for callers that only want "give me a point or don't". A sweep still in
    // flight reads as "not found", exactly as an exhausted sweep did before, so no caller had to change
    // its failure handling; TryResolveShipSiegeAnchor is the one caller that distinguishes Pending.
    private bool TryFindReachableNavPointAround(
        Vector3 center,
        PlayerControllerB target,
        out Vector3 bestPoint,
        out string mode)
    {
        return AdvanceReachableNavPointSearch(center, target, out bestPoint, out mode) ==
            ReachableNavPointSearchStatus.Resolved;
    }

    private ReachableNavPointSearchStatus AdvanceReachableNavPointSearch(
        Vector3 center,
        PlayerControllerB target,
        out Vector3 bestPoint,
        out string mode)
    {
        bestPoint = center;
        mode = "none";
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return ReachableNavPointSearchStatus.Failed;

        if (TryUseCachedReachableNavPoint(center, out bool cachedSucceeded, out bestPoint, out mode))
        {
            return cachedSucceeded
                ? ReachableNavPointSearchStatus.Resolved
                : ReachableNavPointSearchStatus.Failed;
        }

        // #300 B4: an in-flight sweep answers exactly one question -- the one it was centred on. A
        // request further than the cache move tolerance from that centre is a different question, so it
        // must not read this sweep's best point (measured around someone else's centre, up to the ring
        // radius away) and must not restart the cursor either, or two alternating callers starve each
        // other into permanent Pending. It waits out the steal bound instead, then takes the cursor.
        if (!EnsureReachableNavPointSearch(center, target))
        {
            bestPoint = center;
            mode = "none";
            return ReachableNavPointSearchStatus.Failed;
        }

        // #300 minor 2: re-read his own position once per advance (not per candidate, not once per
        // sweep) so the min-progress reject guard tracks him as he drifts, matching the live read the
        // one-frame sweep did.
        _reachableNavPointSearchCenterDistance =
            Vector3.Distance(transform.position, _reachableNavPointSearchCenter);
        AdvanceReachableNavPointCursor();

        if (_reachableNavPointSearchRadiusIndex < PursuitFallbackSearchRadii.Length)
        {
            bestPoint = center;
            mode = "search-pending";
            return ReachableNavPointSearchStatus.Pending;
        }

        return CompleteReachableNavPointSearch(out bestPoint, out mode);
    }

    private void AdvanceReachableNavPointCursor()
    {
        double budget = Math.Min(
            ReachableNavPointMillisecondsPerInterval,
            NavSearchBudgetRemainingMilliseconds());
        if (budget <= 0d)
            return;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        int candidatesExamined = 0;
        while (candidatesExamined < ReachableNavPointCandidatesPerInterval &&
            _reachableNavPointSearchRadiusIndex < PursuitFallbackSearchRadii.Length)
        {
            EvaluateNextReachableNavPointCandidate();
            candidatesExamined++;
            if (NavSearchBudgetExpired(started, budget))
                break;
        }

        RecordNavSearchBudgetSpent(started);
    }

    // #300: a completed sweep is remembered on the same clocks the pursuit fallback cache uses, so the
    // callers that re-ask every tick (ResolveObstacleAwareDestination in particular) do not restart a
    // full 129-candidate sweep the frame after one finished.
    private bool TryUseCachedReachableNavPoint(
        Vector3 center,
        out bool succeeded,
        out Vector3 result,
        out string mode)
    {
        return TryReadReachableNavPointCacheEntry(_reachableNavPointCacheA, center, out succeeded, out result, out mode)
            || TryReadReachableNavPointCacheEntry(_reachableNavPointCacheB, center, out succeeded, out result, out mode);
    }

    private bool TryReadReachableNavPointCacheEntry(
        ReachableNavPointCacheEntry entry,
        Vector3 center,
        out bool succeeded,
        out Vector3 result,
        out string mode)
    {
        succeeded = false;
        result = center;
        mode = "none";
        if (!entry.Valid || Time.time > entry.Until)
            return false;
        if (Vector3.Distance(center, entry.Center) > NavigationPathCacheTargetMoveTolerance)
            return false;
        if (Vector3.Distance(transform.position, entry.Origin) > NavigationPathCacheSelfMoveTolerance)
            return false;

        succeeded = entry.Succeeded;
        result = succeeded ? entry.Result : center;
        mode = succeeded ? entry.Mode : "none";
        return true;
    }

    private void StoreReachableNavPointCacheEntry(ReachableNavPointCacheEntry entry)
    {
        // Slot the answer over whichever slot already covers this centre; failing that, over the slot
        // that expires first. Two slots is exactly what the ship-siege caller needs (near-target and
        // near-ship) and one more than any other caller uses.
        bool matchesA = _reachableNavPointCacheA.Valid &&
            Vector3.Distance(entry.Center, _reachableNavPointCacheA.Center) <= NavigationPathCacheTargetMoveTolerance;
        bool matchesB = _reachableNavPointCacheB.Valid &&
            Vector3.Distance(entry.Center, _reachableNavPointCacheB.Center) <= NavigationPathCacheTargetMoveTolerance;

        if (matchesA)
            _reachableNavPointCacheA = entry;
        else if (matchesB)
            _reachableNavPointCacheB = entry;
        else if (!_reachableNavPointCacheA.Valid || _reachableNavPointCacheA.Until <= _reachableNavPointCacheB.Until)
            _reachableNavPointCacheA = entry;
        else
            _reachableNavPointCacheB = entry;
    }

    // Returns false when an in-flight sweep owns the cursor and this request is not the question it is
    // answering; the caller must then decline without disturbing it (#300 B4).
    private bool EnsureReachableNavPointSearch(Vector3 center, PlayerControllerB target)
    {
        if (_reachableNavPointSearchActive)
        {
            bool sameQuestion = _reachableNavPointSearchOutside == isOutside &&
                _reachableNavPointSearchTarget == target &&
                Vector3.Distance(center, _reachableNavPointSearchCenter) <= NavigationPathCacheTargetMoveTolerance;
            if (sameQuestion)
                return true;

            if (Time.time - _reachableNavPointSearchStartedAt < ReachableNavPointSearchStealSeconds)
                return false;
        }

        _reachableNavPointSearchActive = true;
        _reachableNavPointSearchStartedAt = Time.time;
        _reachableNavPointSearchOutside = isOutside;
        _reachableNavPointSearchTarget = target;
        _reachableNavPointSearchCenter = center;
        _reachableNavPointSearchCenterDistance = Vector3.Distance(transform.position, center);
        _reachableNavPointSearchRadiusIndex = 0;
        _reachableNavPointSearchAngleIndex = 0;
        _reachableNavPointSearchBestCost = float.PositiveInfinity;
        _reachableNavPointSearchBestPoint = center;
        _reachableNavPointSearchBestMode = "none";
        _reachableNavPointSearchFound = false;
        return true;
    }

    private void CancelReachableNavPointSearch()
    {
        _reachableNavPointSearchActive = false;
        _reachableNavPointSearchTarget = null;
        _reachableNavPointSearchRadiusIndex = 0;
        _reachableNavPointSearchAngleIndex = 0;
        _reachableNavPointSearchBestCost = float.PositiveInfinity;
        _reachableNavPointSearchFound = false;
    }

    private ReachableNavPointSearchStatus CompleteReachableNavPointSearch(out Vector3 bestPoint, out string mode)
    {
        bool found = _reachableNavPointSearchFound;
        Vector3 center = _reachableNavPointSearchCenter;
        bestPoint = found ? _reachableNavPointSearchBestPoint : center;
        mode = found ? _reachableNavPointSearchBestMode : "none";

        StoreReachableNavPointCacheEntry(new ReachableNavPointCacheEntry
        {
            Valid = true,
            Succeeded = found,
            Center = center,
            Origin = transform.position,
            Result = bestPoint,
            Mode = mode,
            Until = Time.time + (found
                ? NavigationFallbackSuccessCacheSeconds
                : NavigationFallbackFailureCacheSeconds),
        });

        CancelReachableNavPointSearch();
        return found
            ? ReachableNavPointSearchStatus.Resolved
            : ReachableNavPointSearchStatus.Failed;
    }

    // #300: a pending sweep must not depend on its caller asking again -- TryResolveShipSiegeAnchor
    // backs off for seconds at a time. The owner Update pumps it so a sweep finishes at frame rate.
    private void AdvancePendingReachableNavPointSearch()
    {
        if (!_reachableNavPointSearchActive)
            return;
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return;

        _reachableNavPointSearchCenterDistance =
            Vector3.Distance(transform.position, _reachableNavPointSearchCenter);
        AdvanceReachableNavPointCursor();

        if (_reachableNavPointSearchRadiusIndex >= PursuitFallbackSearchRadii.Length)
            CompleteReachableNavPointSearch(out _, out _);
    }

    private void EvaluateNextReachableNavPointCandidate()
    {
        float radius = PursuitFallbackSearchRadii[_reachableNavPointSearchRadiusIndex];
        int angleSteps = radius <= 0.01f ? 1 : 16;
        int angleIndex = _reachableNavPointSearchAngleIndex;
        _reachableNavPointSearchAngleIndex++;
        if (_reachableNavPointSearchAngleIndex >= angleSteps)
        {
            _reachableNavPointSearchAngleIndex = 0;
            _reachableNavPointSearchRadiusIndex++;
        }

        float angle = angleSteps == 1 ? 0f : (360f / angleSteps) * angleIndex;
        Vector3 offset = angleSteps == 1
            ? Vector3.zero
            : Quaternion.Euler(0f, angle, 0f) * Vector3.forward * radius;
        Vector3 candidate = _reachableNavPointSearchCenter + offset;
        float sampleDistance = radius <= 0.01f
            ? NavigationDirectSampleDistance
            : NavigationTargetApproachSampleDistance;

        if (!NavMesh.SamplePosition(candidate, out NavMeshHit sample, sampleDistance, NavMesh.AllAreas))
            return;

        if (_reachableNavPointSearchCenterDistance > StopDistance + NavigationTargetApproachMinProgress &&
            Vector3.Distance(transform.position, sample.position) < NavigationTargetApproachMinProgress)
        {
            return;
        }

        if (!TryCalculateReachableNavPointPath(sample.position))
            return;

        float centerDistance = Vector3.Distance(sample.position, _reachableNavPointSearchCenter);
        float pathLength = EstimatePathLength(_reachableNavPointSearchPath);
        float targetYCost = _reachableNavPointSearchTarget != null
            ? Mathf.Abs(sample.position.y - _reachableNavPointSearchTarget.transform.position.y) * 0.4f
            : 0f;
        float cost = centerDistance * 1.6f + pathLength * 0.25f + targetYCost;
        if (cost >= _reachableNavPointSearchBestCost)
            return;

        _reachableNavPointSearchBestCost = cost;
        _reachableNavPointSearchBestPoint = sample.position;
        _reachableNavPointSearchBestMode = $"r{radius:F1}/path{pathLength:F1}";
        _reachableNavPointSearchFound = true;
    }

    private bool TryCalculateReachableNavPointPath(Vector3 destination)
    {
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return false;

        try
        {
            return agent.CalculatePath(destination, _reachableNavPointSearchPath) &&
                _reachableNavPointSearchPath.status == NavMeshPathStatus.PathComplete;
        }
        catch
        {
            return false;
        }
    }

    // #300: the two sliced machines can land on the same frame -- the ring sweep advances from
    // DoAIInterval, which runs inside base.Update(), before RecoverMovementIfNeeded advances the stuck
    // sweep later in the same Update. They therefore share ONE per-frame allowance so the worst case is
    // the allowance plus a single over-running candidate, not the sum of two independent budgets.
    private const double NavSearchMillisecondsPerFrame = 2.5d;
    private int _navSearchBudgetFrame = -1;
    private double _navSearchBudgetSpentMilliseconds;

    private double NavSearchBudgetRemainingMilliseconds()
    {
        if (_navSearchBudgetFrame != Time.frameCount)
        {
            _navSearchBudgetFrame = Time.frameCount;
            _navSearchBudgetSpentMilliseconds = 0d;
        }

        return NavSearchMillisecondsPerFrame - _navSearchBudgetSpentMilliseconds;
    }

    private void RecordNavSearchBudgetSpent(long started)
    {
        if (_navSearchBudgetFrame != Time.frameCount)
        {
            _navSearchBudgetFrame = Time.frameCount;
            _navSearchBudgetSpentMilliseconds = 0d;
        }

        _navSearchBudgetSpentMilliseconds += ElapsedMillisecondsSince(started);
    }

    private static double ElapsedMillisecondsSince(long started)
    {
        return (System.Diagnostics.Stopwatch.GetTimestamp() - started) * 1000d /
            System.Diagnostics.Stopwatch.Frequency;
    }

    private static bool NavSearchBudgetExpired(long started, double budgetMilliseconds)
    {
        return ElapsedMillisecondsSince(started) >= budgetMilliseconds;
    }

    // #300: the stuck-recovery AI-node machine. Stage RouteNodes reproduces the old
    // TryResolveAINodeRouteDestination scoring candidate-for-candidate; stage RecoveryNodes reproduces
    // TryFindAINodeRecoveryPosition. Node order, the reject guards and the cost comparison are
    // unchanged, so the winner of a completed sweep is the same node the one-frame sweep picked.
    private void BeginStuckRecoverySearch(
        PlayerControllerB huntTarget,
        PlayerControllerB target,
        Vector3 desired,
        float stuckFor)
    {
        _stuckRecoverySearchActive = true;
        // The hunt target is kept alongside the navigation target (which is null whenever the live hunt
        // target cannot be used) purely so the destination can be re-anchored per advance.
        _stuckRecoverySearchHuntTarget = huntTarget;
        _stuckRecoverySearchTarget = target;
        _stuckRecoverySearchDesired = desired;
        _stuckRecoverySearchStartPosition = transform.position;
        // #300 B3: the warp stage is gated on how long he had ALREADY been stuck when the sweep opened,
        // never on time the sweep itself spent. The one-frame version could barely ever warp -- every
        // non-warp terminal reset the progress tracker, so stuckFor was pinned near
        // MovementStuckCheckSeconds -- and accruing the sweep's own duration would have made the warp
        // the default outcome. Only a sweep that BEGAN past MovementStuckWarpAfterSeconds (recovery was
        // skipped for a long time, exactly the old escalation) may enter it.
        _stuckRecoverySearchStartStuckFor = stuckFor;
        BeginStuckRecoverySearchStage(StuckRecoverySearchStage.RouteNodes);
    }

    // #300 B1: a resumed sweep must still be solving the problem it was opened for -- and the thing that
    // defines "the problem" is WHO he is trying to reach, not where that player happened to stand when
    // the sweep opened. Target identity plus the eight cancel sites deliver the actual guarantee (never
    // publish a reroute scored toward someone he stopped hunting).
    //
    // A destination-distance term here does NOT: in HUNT, GetPursuitDestination returns the live player
    // position, so any fixed tolerance is crossed by ordinary walking in a fraction of a second -- 1m at
    // 4-8 m/s is 0.13-0.26s, against a RouteNodes stage that needs ~1.7s of uninterrupted matching at
    // ~2 candidates/frame. The cursor would top out around node 16 of 200 and reset forever, and because
    // Pending returns early and the cooldown is bypassed while a sweep is active, a Bundy pinned while
    // his target kites would get no reroute, no warp and no generic fallback at all. The one-frame
    // version fit in a single frame and never had that hole. Instead the destination is re-anchored live
    // once per advance (see RefreshStuckRecoveryScoringAnchors) and the cursor is left alone.
    private bool StuckRecoverySearchMatches(PlayerControllerB target)
    {
        return _stuckRecoverySearchActive && _stuckRecoverySearchTarget == target;
    }

    // True when the stuck machine was ATTEMPTED this frame (the stamp is set before its budget check),
    // which is what the reachable-point pump wants: stand down either way.
    private bool StuckRecoveryAttemptedThisFrame =>
        _stuckRecoverySearchAdvancedFrame == Time.frameCount;

    private bool StuckRecoverySearchEscaped()
    {
        return _stuckRecoverySearchActive &&
            PlanarDistance(transform.position, _stuckRecoverySearchStartPosition) >= StuckRecoveryCancelProgressMeters;
    }

    private void BeginStuckRecoverySearchStage(StuckRecoverySearchStage stage)
    {
        _stuckRecoverySearchStage = stage;
        _stuckRecoverySearchNodes = GetNavigationNodesForTarget(_stuckRecoverySearchTarget);
        _stuckRecoverySearchNodeIndex = 0;
        _stuckRecoverySearchBestCost = float.PositiveInfinity;
        _stuckRecoverySearchBestPoint = stage == StuckRecoverySearchStage.RouteNodes
            ? _stuckRecoverySearchDesired
            : transform.position;
        _stuckRecoverySearchBestMode = "none";
        _stuckRecoverySearchFound = false;
        RefreshStuckRecoveryScoringAnchors();
    }

    // #300 minor 2: the scoring anchors are re-read once per advance -- not per candidate (which would
    // shift the comparison between two candidates of the same sweep) and not once per sweep (which
    // would let a target who walked away invalidate the regression guard for seconds). This is the
    // closest resumable analogue of the one-frame sweep's single live read.
    //
    // The pursuit destination is re-anchored on the same cadence, for the same reason: it is what
    // replaces the (removed) destination-distance restart test, so a kiting target reshapes the scoring
    // of the candidates still ahead of the cursor instead of throwing the cursor away. Its only use is
    // the desiredDistance term at weight 0.45 -- a strictly smaller inconsistency across a sweep than
    // the scoring-target refresh already accepted above, and the reject guard is live-anchored anyway.
    // The node index is deliberately untouched.
    private void RefreshStuckRecoveryScoringAnchors()
    {
        if (IsLivingPlayer(_stuckRecoverySearchHuntTarget))
            _stuckRecoverySearchDesired = GetPursuitDestination(_stuckRecoverySearchHuntTarget);

        bool useLiveTarget = _stuckRecoverySearchTarget != null &&
            CanUseLiveHuntTarget(_stuckRecoverySearchTarget);
        _stuckRecoverySearchScoringTarget = useLiveTarget
            ? _stuckRecoverySearchTarget.transform.position
            : _stuckRecoverySearchDesired;
        _stuckRecoverySearchCurrentTargetDistance =
            PlanarDistance(transform.position, _stuckRecoverySearchScoringTarget);
    }

    private void CancelStuckRecoverySearch()
    {
        _stuckRecoverySearchActive = false;
        _stuckRecoverySearchStage = StuckRecoverySearchStage.None;
        _stuckRecoverySearchTarget = null;
        _stuckRecoverySearchHuntTarget = null;
        _stuckRecoverySearchNodes = null;
        _stuckRecoverySearchNodeIndex = 0;
        _stuckRecoverySearchBestCost = float.PositiveInfinity;
        _stuckRecoverySearchFound = false;
    }

    private StuckRecoverySearchOutcome AdvanceStuckRecoverySearch(out Vector3 result, out string mode)
    {
        result = _stuckRecoverySearchDesired;
        mode = "none";
        if (!_stuckRecoverySearchActive)
            return StuckRecoverySearchOutcome.Exhausted;

        bool allowWarpStage = _stuckRecoverySearchStartStuckFor >= MovementStuckWarpAfterSeconds;
        // #300 B1c: the reachable-point pump reads this, not the raw active flag -- a sweep that was
        // suspended (action lock, smart hold) leaves the flag up while never reaching this line, and
        // gating the pump on the flag alone silently killed it for the rest of the round. Note the
        // stamp lands BEFORE the budget check below, so it means "the stuck machine was ATTEMPTED this
        // frame", not "it spent budget": on a frame where the shared pot is already empty both machines
        // stand down. That is the conservative direction and is intentional.
        _stuckRecoverySearchAdvancedFrame = Time.frameCount;
        RefreshStuckRecoveryScoringAnchors();

        double budget = Math.Min(
            StuckRecoveryMillisecondsPerInterval,
            NavSearchBudgetRemainingMilliseconds());
        if (budget <= 0d)
            return StuckRecoverySearchOutcome.Pending;

        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            return AdvanceStuckRecoveryCursor(allowWarpStage, started, budget, out result, out mode);
        }
        finally
        {
            RecordNavSearchBudgetSpent(started);
        }
    }

    private StuckRecoverySearchOutcome AdvanceStuckRecoveryCursor(
        bool allowWarpStage,
        long started,
        double budget,
        out Vector3 result,
        out string mode)
    {
        result = _stuckRecoverySearchDesired;
        mode = "none";
        int candidatesExamined = 0;
        while (candidatesExamined < StuckRecoveryCandidatesPerInterval)
        {
            GameObject[] nodes = _stuckRecoverySearchNodes;
            if (nodes == null || _stuckRecoverySearchNodeIndex >= nodes.Length)
            {
                if (_stuckRecoverySearchStage == StuckRecoverySearchStage.RouteNodes)
                {
                    if (_stuckRecoverySearchFound)
                    {
                        result = _stuckRecoverySearchBestPoint;
                        mode = _stuckRecoverySearchBestMode;
                        return StuckRecoverySearchOutcome.Rerouted;
                    }

                    if (!allowWarpStage)
                        return StuckRecoverySearchOutcome.Exhausted;

                    BeginStuckRecoverySearchStage(StuckRecoverySearchStage.RecoveryNodes);
                    continue;
                }

                if (_stuckRecoverySearchFound)
                {
                    result = _stuckRecoverySearchBestPoint;
                    mode = _stuckRecoverySearchBestMode;
                    return StuckRecoverySearchOutcome.Warped;
                }

                return StuckRecoverySearchOutcome.Exhausted;
            }

            if (_stuckRecoverySearchStage == StuckRecoverySearchStage.RouteNodes)
                EvaluateNextStuckRecoveryRouteCandidate();
            else
                EvaluateNextStuckRecoveryWarpCandidate();

            candidatesExamined++;
            if (NavSearchBudgetExpired(started, budget))
                break;
        }

        return StuckRecoverySearchOutcome.Pending;
    }

    private void EvaluateNextStuckRecoveryRouteCandidate()
    {
        int nodeIndex = _stuckRecoverySearchNodeIndex++;
        GameObject node = _stuckRecoverySearchNodes[nodeIndex];
        if (node == null)
            return;

        if (!NavMesh.SamplePosition(node.transform.position, out NavMeshHit sample, NavigationNodeRouteSampleDistance, NavMesh.AllAreas))
            return;

        if (!TryCalculateStuckRecoveryPath(sample.position))
            return;

        float selfDistance = PlanarDistance(transform.position, sample.position);
        if (selfDistance <= StopDistance + 0.35f &&
            _stuckRecoverySearchCurrentTargetDistance > StopDistance + 1.5f)
        {
            return;
        }

        float targetDistance = PlanarDistance(sample.position, _stuckRecoverySearchScoringTarget);
        if (targetDistance > _stuckRecoverySearchCurrentTargetDistance + NavigationNodeRouteMaxTargetRegression)
            return;

        float desiredDistance = PlanarDistance(sample.position, _stuckRecoverySearchDesired);
        float pathLength = EstimatePathLength(_stuckRecoverySearchPath);
        float heightCost = Mathf.Abs(sample.position.y - _stuckRecoverySearchScoringTarget.y) * 1.2f;
        float progressReward = Mathf.Max(0f, _stuckRecoverySearchCurrentTargetDistance - targetDistance) * 0.75f;
        float cost = targetDistance * 1.65f
            + desiredDistance * 0.45f
            + pathLength * 0.35f
            + selfDistance * 0.08f
            + heightCost
            - progressReward;

        if (cost >= _stuckRecoverySearchBestCost)
            return;

        _stuckRecoverySearchBestCost = cost;
        _stuckRecoverySearchBestPoint = sample.position;
        _stuckRecoverySearchBestMode = $"node#{nodeIndex}/target{targetDistance:F1}/path{pathLength:F1}";
        _stuckRecoverySearchFound = true;
    }

    private void EvaluateNextStuckRecoveryWarpCandidate()
    {
        int nodeIndex = _stuckRecoverySearchNodeIndex++;
        GameObject node = _stuckRecoverySearchNodes[nodeIndex];
        if (node == null)
            return;

        if (!NavMesh.SamplePosition(node.transform.position, out NavMeshHit sample, NavigationNodeRouteSampleDistance, NavMesh.AllAreas))
            return;

        float selfDistance = PlanarDistance(transform.position, sample.position);
        float targetDistance = _stuckRecoverySearchTarget != null
            ? PlanarDistance(sample.position, _stuckRecoverySearchTarget.transform.position)
            : 0f;
        float heightCost = Mathf.Abs(sample.position.y - transform.position.y) * 0.45f;
        float pathCost = 0f;
        bool reachableByCurrentPath = TryCalculateStuckRecoveryPath(sample.position);
        if (reachableByCurrentPath)
            pathCost = EstimatePathLength(_stuckRecoverySearchPath) * 0.22f;

        float cost = selfDistance
            + targetDistance * 0.35f
            + heightCost
            + pathCost
            + (reachableByCurrentPath ? -8f : 18f);

        if (cost >= _stuckRecoverySearchBestCost)
            return;

        _stuckRecoverySearchBestCost = cost;
        _stuckRecoverySearchBestPoint = sample.position;
        _stuckRecoverySearchBestMode = $"node#{nodeIndex}/self{selfDistance:F1}/target{targetDistance:F1}/reachable={reachableByCurrentPath}";
        _stuckRecoverySearchFound = true;
    }

    private bool TryCalculateStuckRecoveryPath(Vector3 destination)
    {
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return false;

        try
        {
            return agent.CalculatePath(destination, _stuckRecoverySearchPath) &&
                _stuckRecoverySearchPath.status == NavMeshPathStatus.PathComplete;
        }
        catch
        {
            return false;
        }
    }


    // #300 minor 1: the only surviving caller is the off-navmesh emergency in TryRecoverAgentOntoNavMesh,
    // which hangs off AgentReadyForMovement -- and that has 28 call sites, so a single frame could run
    // this whole node loop many times over. One frame, one sweep: the answer cannot change within a
    // frame anyway (same transform, same node set, same agent state).
    private int _aiNodeRecoveryLatchFrame = -1;
    private bool _aiNodeRecoveryLatchResult;
    private Vector3 _aiNodeRecoveryLatchPosition;
    private string _aiNodeRecoveryLatchMode = "none";

    private bool TryFindAINodeRecoveryPosition(PlayerControllerB target, out Vector3 position, out string mode)
    {
        if (_aiNodeRecoveryLatchFrame == Time.frameCount)
        {
            position = _aiNodeRecoveryLatchResult ? _aiNodeRecoveryLatchPosition : transform.position;
            mode = _aiNodeRecoveryLatchMode;
            return _aiNodeRecoveryLatchResult;
        }

        bool found = FindAINodeRecoveryPosition(target, out position, out mode);
        _aiNodeRecoveryLatchFrame = Time.frameCount;
        _aiNodeRecoveryLatchResult = found;
        _aiNodeRecoveryLatchPosition = position;
        _aiNodeRecoveryLatchMode = mode;
        return found;
    }

    private bool FindAINodeRecoveryPosition(PlayerControllerB target, out Vector3 position, out string mode)
    {
        position = transform.position;
        mode = "none";

        GameObject[] nodes = GetNavigationNodesForTarget(target);
        if (nodes == null || nodes.Length == 0)
            return false;

        bool canCheckPath = agent != null && agent.enabled && agent.isOnNavMesh;
        float bestCost = float.PositiveInfinity;
        bool found = false;

        for (int i = 0; i < nodes.Length; i++)
        {
            GameObject node = nodes[i];
            if (node == null)
                continue;

            if (!NavMesh.SamplePosition(node.transform.position, out NavMeshHit sample, NavigationNodeRouteSampleDistance, NavMesh.AllAreas))
                continue;

            float selfDistance = PlanarDistance(transform.position, sample.position);
            float targetDistance = target != null
                ? PlanarDistance(sample.position, target.transform.position)
                : 0f;
            float heightCost = Mathf.Abs(sample.position.y - transform.position.y) * 0.45f;
            float pathCost = 0f;
            bool reachableByCurrentPath = false;
            if (canCheckPath && TryCalculateCompletePath(sample.position, out NavMeshPath path))
            {
                reachableByCurrentPath = true;
                pathCost = EstimatePathLength(path) * 0.22f;
            }

            float cost = selfDistance
                + targetDistance * 0.35f
                + heightCost
                + pathCost
                + (reachableByCurrentPath ? -8f : 18f);

            if (cost >= bestCost)
                continue;

            bestCost = cost;
            position = sample.position;
            mode = $"node#{i}/self{selfDistance:F1}/target{targetDistance:F1}/reachable={reachableByCurrentPath}";
            found = true;
        }

        return found;
    }

    private GameObject[] GetNavigationNodesForTarget(PlayerControllerB target)
    {
        bool outside = target != null && CanUseLiveHuntTarget(target) ? !target.isInsideFactory : isOutside;
        GameObject[] nodes = null;
        RoundManager round = RoundManager.Instance;
        if (round != null)
            nodes = FilterValidNavigationNodes(outside ? round.outsideAINodes : round.insideAINodes);

        if (nodes == null || nodes.Length == 0)
            nodes = FilterValidNavigationNodes(FindNavigationNodesByTag(outside));

        if (nodes != null && nodes.Length > 0)
            allAINodes = nodes;

        return nodes;
    }

    private static GameObject[] FilterValidNavigationNodes(GameObject[] nodes)
    {
        if (nodes == null || nodes.Length == 0)
            return Array.Empty<GameObject>();

        int validCount = 0;
        for (int i = 0; i < nodes.Length; i++)
        {
            if (nodes[i] != null)
                validCount++;
        }

        if (validCount == nodes.Length)
            return nodes;
        if (validCount == 0)
            return Array.Empty<GameObject>();

        GameObject[] filtered = new GameObject[validCount];
        int index = 0;
        for (int i = 0; i < nodes.Length; i++)
        {
            GameObject node = nodes[i];
            if (node != null)
                filtered[index++] = node;
        }

        return filtered;
    }

    private static GameObject[] FindNavigationNodesByTag(bool outside)
    {
        try
        {
            return GameObject.FindGameObjectsWithTag(outside ? "OutsideAINode" : "AINode");
        }
        catch
        {
            return Array.Empty<GameObject>();
        }
    }

    private bool TryCalculateCompletePath(Vector3 destination, out NavMeshPath path)
    {
        path = new NavMeshPath();
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return false;

        try
        {
            return agent.CalculatePath(destination, path) && path.status == NavMeshPathStatus.PathComplete;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetCompleteNavigationPath(
        Vector3 targetPosition,
        out Vector3 resolvedTarget,
        out NavMeshPath path,
        out float pathLength)
    {
        resolvedTarget = ResolveNavigationPosition(targetPosition, 3f);
        path = new NavMeshPath();
        pathLength = 0f;
        if (agent == null || !agent.enabled || !agent.isOnNavMesh)
            return false;

        try
        {
            if (!agent.CalculatePath(resolvedTarget, path) || path.status != NavMeshPathStatus.PathComplete)
                return false;

            int corners = path.GetCornersNonAlloc(RoundManager.Instance.storedPathCorners);
            if (corners <= 0)
                return false;

            Vector3 finalCorner = RoundManager.Instance.storedPathCorners[corners - 1];
            if (Vector3.Distance(finalCorner, resolvedTarget) > 1.75f)
                return false;

            pathLength = EstimatePathLength(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool TryGetCompleteNavigationPathDistanceCached(Vector3 targetPosition, out float pathLength)
    {
        Vector3 origin = transform.position;
        pathLength = Vector3.Distance(origin, targetPosition);

        if (_hasCompletePathCache &&
            Time.time <= _completePathCacheUntil &&
            Vector3.Distance(origin, _cachedCompletePathOrigin) <= NavigationPathCacheSelfMoveTolerance &&
            Vector3.Distance(targetPosition, _cachedCompletePathTargetPosition) <= NavigationPathCacheTargetMoveTolerance)
        {
            pathLength = _cachedCompletePathLength;
            return _cachedCompletePathSucceeded;
        }

        bool succeeded = TryGetCompleteNavigationPath(targetPosition, out _, out _, out float freshLength);
        if (succeeded)
            pathLength = freshLength;

        _hasCompletePathCache = true;
        _cachedCompletePathSucceeded = succeeded;
        _cachedCompletePathOrigin = origin;
        _cachedCompletePathTargetPosition = targetPosition;
        _cachedCompletePathLength = pathLength;

        float distance = Vector3.Distance(origin, targetPosition);
        float cacheSeconds = distance > MovementRecoveryActivePlayerRange
            ? NavigationCompletePathCacheFarSeconds
            : NavigationCompletePathCacheNearSeconds;
        if (!succeeded)
            cacheSeconds = Mathf.Max(cacheSeconds, NavigationFallbackFailureCacheSeconds);
        _completePathCacheUntil = Time.time + cacheSeconds;

        return succeeded;
    }

    private Vector3 ResolveNavigationPosition(Vector3 origin, float maxDistance)
    {
        if (RoundManager.Instance != null)
        {
            Vector3 resolved = RoundManager.Instance.GetNavMeshPosition(origin, RoundManager.Instance.navHit, maxDistance, agentMask);
            if (RoundManager.Instance.GotNavMeshPositionResult)
                return resolved;

            resolved = RoundManager.Instance.GetNavMeshPosition(origin, RoundManager.Instance.navHit, Mathf.Max(maxDistance * 1.75f, 6f), agentMask);
            if (RoundManager.Instance.GotNavMeshPositionResult)
                return resolved;
        }

        int mask = agent != null ? agent.areaMask : NavMesh.AllAreas;
        if (NavMesh.SamplePosition(origin, out NavMeshHit hit, maxDistance, mask))
            return hit.position;

        return origin;
    }

    private Vector3 ResolveObstacleAwareDestination(
        Vector3 desired,
        PlayerControllerB target,
        bool allowDetour,
        bool allowReachabilityFallback = true)
    {
        if (!allowDetour || !AgentReadyForMovement())
            return desired;

        Vector3 resolved = desired;
        if (!TryResolveNavigableDestination(desired, out resolved) &&
            (!allowReachabilityFallback ||
             !TryFindReachableNavPointAround(desired, target, out resolved, out _)))
        {
            return desired;
        }

        if (!TryFindNavigationObstacle(resolved, target, out RaycastHit physicsHit, out Vector3 obstaclePoint))
            return resolved;

        if (TryFindObstacleDetour(resolved, target, physicsHit, obstaclePoint, out Vector3 detour))
        {
            if (ShouldLogObstacleDetourDiagnostic())
            {
                Debug.DrawLine(transform.position + Vector3.up * 0.2f, detour + Vector3.up * 0.2f, Color.yellow, 0.45f, false);
                LogDiagnostic(
                    $"BundyObstacleDetour target={PlayerLabel(target)}, desired={FormatVector(desired)}, " +
                    $"resolved={FormatVector(resolved)}, detour={FormatVector(detour)}, blocker={ColliderLabel(physicsHit.collider)}.");
            }
            return detour;
        }

        if (ShouldLogObstacleDetourDiagnostic())
        {
            Debug.DrawLine(transform.position + Vector3.up * 0.2f, obstaclePoint + Vector3.up * 0.2f, Color.red, 0.35f, false);
            LogDiagnostic(
                $"BundyObstacleDetour failed; desired={FormatVector(desired)}, blocker={ColliderLabel(physicsHit.collider)}, " +
                $"obstaclePoint={FormatVector(obstaclePoint)}.");
        }

        return resolved;
    }

    private bool ShouldLogObstacleDetourDiagnostic()
    {
        if (!ShouldLog(BundyLogCategory.Movement, 2) || Time.time < _nextObstacleDetourLogAt)
            return false;

        _nextObstacleDetourLogAt = Time.time + ObstacleDetourDiagnosticIntervalSeconds;
        return true;
    }

    // C1.4: beyond this navmesh-to-navmesh height difference the destination is on another floor and
    // the local obstacle probe must stand down (see the gate below). 3m clears ramps, stair landings
    // and door thresholds while excluding real storey gaps (the observed failure was a 21m gap).
    // Config-backed since wave 2 (AI/NavigationObstacleMaxVerticalGap, default preserved at 3.0).
    private static float NavigationObstacleMaxVerticalGap =>
        BundyPlugin.ModConfig?.NavigationObstacleMaxVerticalGap?.Value ?? 3f;

    private bool TryFindNavigationObstacle(Vector3 destination, PlayerControllerB target, out RaycastHit physicsHit, out Vector3 obstaclePoint)
    {
        physicsHit = default;
        obstaclePoint = Vector3.zero;

        if (!AgentReadyForMovement())
            return false;

        Vector3 origin = transform.position;
        Vector3 navOrigin = origin;
        Vector3 navDestination = destination;
        if (NavMesh.SamplePosition(origin, out NavMeshHit originSample, NavigationDirectSampleDistance, NavMesh.AllAreas))
            navOrigin = originSample.position;
        if (NavMesh.SamplePosition(destination, out NavMeshHit destinationSample, NavigationDirectSampleDistance, NavMesh.AllAreas))
            navDestination = destinationSample.position;

        Vector3 planarDelta = navDestination - navOrigin;
        planarDelta.y = 0f;
        Vector3 delta = navDestination - navOrigin;
        float distance = delta.magnitude;
        if (planarDelta.magnitude <= 0.35f || distance <= 0.35f)
            return false;

        // C1.4 (2026-07-30 two-floor oscillation): a straight probe toward a destination on ANOTHER
        // FLOOR is meaningless — the walkable route is inherently non-straight, and both probes below
        // misread the situation. NavMesh.Raycast trips on the edge of Bundy's own floor polys; the
        // SphereCast hits the other floor's slab or furniture between the floors. Either way the
        // "obstacle" manufactured a detour point on Bundy's OWN floor, and because the probe outcome
        // flickered tick to tick, the issued destination alternated ~2Hz between the real upper-floor
        // snapshot and a near-Bundy detour 21m below it (19:27:36-40 in the 2026-07-30 run), starving
        // path progress and feeding the recovery-ai-node reroute spam. Local obstacle avoidance is a
        // same-level tool: past this vertical gap, trust NavMesh pathfinding to route around geometry.
        if (Mathf.Abs(delta.y) > NavigationObstacleMaxVerticalGap)
            return false;

        // NavMesh.Raycast tests whether the STRAIGHT LINE is walkable -- not whether something is in
        // the way. Over long distances any route that bends trips it, so treating a hit as an
        // obstacle made the detour system fire on nearly every cross-map destination (run 3: all 173
        // detour decisions came from this branch, every one with a null blocker). Trust it only for
        // local avoidance; past that, let NavMesh pathfinding route around the geometry itself.
        if (distance <= NavigationObstacleLocalDetourRange &&
            NavMesh.Raycast(navOrigin, navDestination, out NavMeshHit navHit, NavMesh.AllAreas))
        {
            obstaclePoint = navHit.position;
            return true;
        }

        int mask = StartOfRound.Instance != null
            ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
            : Physics.DefaultRaycastLayers;

        float radius = agent != null ? Mathf.Max(0.3f, agent.radius * 1.1f) : 0.45f;
        Vector3 direction = delta / distance;
        Vector3 probeOrigin = origin + Vector3.up * NavigationObstacleProbeHeight;
        Vector3 probeDestination = destination + Vector3.up * NavigationObstacleProbeHeight;
        Vector3 probeDelta = probeDestination - probeOrigin;
        float probeDistance = probeDelta.magnitude;
        if (probeDistance <= 0.35f)
            return false;

        direction = probeDelta / probeDistance;
        if (Physics.SphereCast(
                probeOrigin,
                radius,
                direction,
                out physicsHit,
                Mathf.Max(0f, probeDistance - Mathf.Max(0.2f, StopDistance * 0.35f)),
                mask,
                QueryTriggerInteraction.Ignore) &&
            IsNavigationBlockingCollider(physicsHit.collider, target))
        {
            obstaclePoint = physicsHit.point;
            return true;
        }

        return false;
    }

    private bool TryFindObstacleDetour(
        Vector3 desired,
        PlayerControllerB target,
        RaycastHit blockerHit,
        Vector3 obstaclePoint,
        out Vector3 detour)
    {
        detour = desired;
        if (!AgentReadyForMovement())
            return false;

        Vector3 origin = transform.position;
        Vector3 toDesired = desired - origin;
        toDesired.y = 0f;
        if (toDesired.sqrMagnitude < 0.001f)
            return false;

        float originToDesired = toDesired.magnitude;
        Vector3 forward = toDesired.normalized;
        Vector3 right = Vector3.Cross(Vector3.up, forward).normalized;
        if (right.sqrMagnitude < 0.001f)
            right = transform.right;

        float clearance = agent != null ? Mathf.Max(1.25f, agent.radius + 1.2f) : 1.75f;
        if (blockerHit.collider != null)
        {
            Bounds bounds = blockerHit.collider.bounds;
            float horizontalExtent = Mathf.Max(bounds.extents.x, bounds.extents.z);
            clearance = Mathf.Clamp(clearance + horizontalExtent * 0.35f, 1.75f, 7f);
        }

        float bestCost = float.PositiveInfinity;
        Vector3 best = desired;
        bool found = false;
        float[] sideMultipliers = { 1f, -1f, 1.55f, -1.55f, 2.1f, -2.1f };
        float[] forwardSteps = { NavigationObstacleDetourForwardStep, 3f, 0f };

        for (int i = 0; i < sideMultipliers.Length; i++)
        {
            for (int j = 0; j < forwardSteps.Length; j++)
            {
                Vector3 candidate = obstaclePoint +
                    right * (clearance * sideMultipliers[i]) +
                    forward * forwardSteps[j];

                if (!NavMesh.SamplePosition(candidate, out NavMeshHit sample, NavigationObstacleMaxSampleDistance, NavMesh.AllAreas))
                    continue;

                if (TryFindNavigationObstacle(sample.position, target, out _, out _))
                    continue;

                if (!TryCalculateCompletePath(sample.position, out NavMeshPath path))
                    continue;

                // A detour has to actually make progress toward the goal. The old cost weighted
                // path-length-from-Bundy at 1.0 but distance-to-goal at only 0.35, so at long range
                // the cheapest candidate was always the one nearest Bundy -- it rewarded standing
                // still (run 3 chose points 116m and 148m off-goal).
                // Scale the requirement so close-range sidesteps around a pillar or doorway still
                // qualify, while long-range candidates must earn real ground.
                float requiredProgress = Mathf.Min(NavigationObstacleDetourMinProgress, originToDesired * 0.2f);
                float candidateToDesired = PlanarDistance(sample.position, desired);
                if (candidateToDesired > originToDesired - requiredProgress)
                    continue;

                // f = g + h: real travel cost to the candidate plus the straight-line remainder.
                // Scale-correct at any range, and needs no hand-tuned weight.
                float cost = EstimatePathLength(path) + candidateToDesired;
                if (cost >= bestCost)
                    continue;

                bestCost = cost;
                best = sample.position;
                found = true;
            }
        }

        detour = best;
        return found;
    }

    private bool IsNavigationBlockingCollider(Collider collider, PlayerControllerB target)
    {
        if (collider == null)
            return false;
        if (collider.isTrigger || IsNonBlockingUtilityCollider(collider))
            return false;

        Transform hitTransform = collider.transform;
        if (hitTransform == null)
            return false;

        if (hitTransform.IsChildOf(transform))
            return false;

        if (target != null && hitTransform.IsChildOf(target.transform))
            return false;

        if (collider.GetComponentInParent<PlayerControllerB>() != null ||
            collider.GetComponentInParent<EnemyAI>() != null ||
            collider.GetComponentInParent<GrabbableObject>() != null)
        {
            return false;
        }

        return true;
    }

    private static float EstimatePathLength(NavMeshPath path)
    {
        if (path == null || path.corners == null || path.corners.Length < 2)
            return 0f;

        float length = 0f;
        for (int i = 1; i < path.corners.Length; i++)
            length += Vector3.Distance(path.corners[i - 1], path.corners[i]);
        return length;
    }

    private static float PlanarDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private bool TryWarpAgentToNavPosition(Vector3 navPosition, string reason)
    {
        if (agent == null || !agent.enabled)
            return false;

        bool warped = false;
        try
        {
            if (agent.isOnNavMesh)
                agent.ResetPath();
            warped = agent.Warp(navPosition);
        }
        catch (Exception e)
        {
            LogWarning($"NavMesh recovery warp failed for {reason}: {e.Message}");
            return false;
        }

        if (!warped)
            return false;

        transform.position = navPosition;
        ResetMovementProgressTracker();
        _warnedAgentNotOnNavMesh = false;
        LogWarning($"Recovered NavMeshAgent via {reason} at {FormatVector(navPosition)}.");
        return true;
    }

    // ignorePlayer: the player this lunge is aimed at. Their own body must never count as an obstruction
    // on the way to them. Previously only the current `targetPlayer` was excluded, so an attack whose
    // victim differed from targetPlayer could have its own victim read as a wall and suppress the lunge.
    private bool HasClearLungePathTo(Vector3 targetPosition, PlayerControllerB ignorePlayer = null)
    {
        Vector3 origin = transform.position;
        Vector3 destination = targetPosition;
        origin.y = transform.position.y;
        destination.y = transform.position.y;
        Vector3 delta = destination - origin;
        float distance = delta.magnitude;
        if (distance <= 0.25f)
            return true;

        if (NavMesh.Raycast(origin, destination, out NavMeshHit navHit, NavMesh.AllAreas))
        {
            if (DebugLogs)
                Debug.DrawLine(origin + Vector3.up * 0.2f, navHit.position + Vector3.up * 0.2f, Color.red, 0.35f, false);
            return false;
        }

        int mask = StartOfRound.Instance != null
            ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
            : Physics.DefaultRaycastLayers;
        float radius = agent != null ? Mathf.Max(0.25f, agent.radius * 0.9f) : 0.35f;
        Vector3 direction = delta / distance;
        if (Physics.SphereCast(origin + Vector3.up * 0.6f, radius, direction, out RaycastHit hit, Mathf.Max(0f, distance - AttackLungeStopDistance), mask, QueryTriggerInteraction.Ignore) &&
            hit.collider != null &&
            !hit.collider.transform.IsChildOf(transform) &&
            !(targetPlayer != null && hit.collider.transform.IsChildOf(targetPlayer.transform)) &&
            !(ignorePlayer != null && hit.collider.transform.IsChildOf(ignorePlayer.transform)))
        {
            if (DebugLogs)
                Debug.DrawLine(origin + Vector3.up * 0.2f, hit.point, Color.red, 0.35f, false);
            return false;
        }

        if (DebugLogs)
            Debug.DrawLine(origin + Vector3.up * 0.2f, destination + Vector3.up * 0.2f, Color.magenta, 0.25f, false);

        return true;
    }
}
