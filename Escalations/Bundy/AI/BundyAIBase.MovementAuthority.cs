using System;
using System.Collections.Generic;
using Y4NGZCompany.Facility.Elevators;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.AI;

// Movement authority (direction plan, sequencing step 8).
//
// One resolver, one write: ResolvePursuitDestination owns the pursuit-layer priority chain and
// UpdateMovementDestination is its only caller. Every pursuit-layer destination write passes through
// RecordPursuitDestination, which emits the authoritative "[BundyMoveAuth]" line on change.
//
// Layers that deliberately keep their own writes (NOT pursuit; do not fold them in here):
//   - action approach / lunge drive (RootSync.cs DriveActionApproach) — action control overrides
//     navigation by design while an attack is running. Job B1: BeginTimedAction still records an
//     "action-override" marker so the hold is visible in the log rather than a silent gap; the writes
//     themselves stay in the action layer.
//   - battery-sabotage siege route (Actions.cs) and ship-siege prowl (Perception.cs) — siege layer.
// A future source (jump 5a/5b, door breach Job 6) must be added to KnownPursuitSources AND issue its
// destination with an explicit source string; an unregistered source logs an error, not a silent
// bypass.
public abstract partial class BundyAIBase
{
    private enum PursuitResolutionKind
    {
        // Caller issues the resolved destination.
        Destination,
        // Smart-entrance transition is holding at the threshold; caller holds the agent and idles.
        HoldThreshold,
        // Smart path task still computing; caller waits on the current path instead of falling
        // through to a worse source (the _lastSmartPathPending hold semantics — preserve the wait).
        HoldSmartPending,
        // Incremental fallback has no previous valid destination yet; keep the current agent path
        // alive without demoting locomotion while the next bounded slice runs.
        HoldFallbackPending,
        // Nothing reachable; caller runs the blocked-pursuit response chain.
        Blocked,
    }

    // Base source names (the part before any ':' detail suffix) the authority system knows about.
    private static readonly HashSet<string> KnownPursuitSources = new HashSet<string>(StringComparer.Ordinal)
    {
        "direct",
        "assigned-stranger-entrance",
        "smart-assigned",
        "stranger-snapshot",
        "smart",
        "smart-task",
        "target-approach",
        "desired-approach",
        "ai-node",
        "cached",
        "blocked",
        // #317: the guaranteed-entry tier the unrecoverable-Blocked handler engages when the primary
        // entrance router rejects every candidate.
        "blocked-entrance-fallback",
        "vanilla-target-chase",
        "pursuit-search",
        "blocked-hide",
        "recovery-ai-node",
        // #499: the movement watchdog's local sidestep (Navigation.cs TryMovementSidestep) — a real
        // pursuit-layer destination issued when the agent stalls in place, not a marker.
        "recovery-sidestep",
        "door-approach",
        // Not a pursuit destination: the marker an action leaves when it takes the agent for its
        // duration (Job B1). It makes the action hold visible in [BundyMoveAuth] instead of a silent
        // gap, and the next pursuit-layer line after ResumeMovementAfterAction is the release.
        "action-override",
        // Job 5a: drop-down follow. "jump-drop:begin" marks the airborne window (the arc owns movement
        // and the agent is parked, so there is no navigable destination to record); the bare "jump-drop"
        // is the real destination issued on landing once the NavMesh has been re-acquired.
        "jump-drop",
    };

    private static readonly HashSet<string> WarnedUnknownPursuitSources = new HashSet<string>(StringComparer.Ordinal);

    // One [BundyMoveAuth] line per destination CHANGE, not per tick: a change is a different base
    // source or a destination that moved beyond this tolerance since the last logged line.
    private const float MoveAuthorityDestinationChangeMeters = 2f;
    private const float LongRangeAsyncPathCheckMeters = 40f;

    // How long the pursuit layer will wait on an in-flight smart-path result before giving up on it
    // for this tick and using ordinary navigation instead.
    //
    // The wait exists so a tick that is about to receive a link route does not fall through to a
    // worse destination one tick early. It is NOT a licence to stand still: the task is restarted
    // every time its result comes back (including the "no link needed" answer, which is the normal
    // one when the target is on the same NavMesh island), so at long range there is almost always
    // a task in flight and an unbounded hold means almost every tick issues nothing at all.
    private const float SmartPathPendingHoldMaxSeconds = 0.75f;
    private float _smartPathHoldStartedAt = float.NegativeInfinity;

    // Bounded from the FIRST pending tick of a run of them, not from the last: consecutive holds
    // are the failure mode, so the clock has to survive them.
    private bool HasSmartPathHoldExpired()
    {
        if (float.IsNegativeInfinity(_smartPathHoldStartedAt))
        {
            _smartPathHoldStartedAt = Time.time;
            return false;
        }

        return Time.time - _smartPathHoldStartedAt > SmartPathPendingHoldMaxSeconds;
    }

    private void ClearSmartPathHoldClock()
    {
        _smartPathHoldStartedAt = float.NegativeInfinity;
    }

    // Last resolution the navigation pass produced, with the timestamp it was produced at. Read by
    // the SEARCH roam watchdog: the two pending holds park the agent with no path on purpose while a
    // multi-tick incremental search runs, so they must not count as "unpathable". Stamped because a
    // tick that returns before navigation leaves the record standing.
    private PursuitResolutionKind _lastPursuitResolution = PursuitResolutionKind.Destination;
    private float _lastPursuitResolutionAt = float.NegativeInfinity;

    private bool _moveAuthHasRecord;
    private string _moveAuthLastSource;
    private Vector3 _moveAuthLastDestination;
    // True while a smart-path resolution is in flight; its nested raw writes are subsumed by the
    // caller's single record.
    private bool _moveAuthSuppressNestedRecords;

    // Crossing-route cache (CrossingRouteCacheSeconds): the assigned-entrance resolution is
    // deterministic in (entrance, his side, target side); the 2026-07-30 lag run re-resolved it 31
    // times in 45s producing a byte-identical answer each time.
    private bool _crossingRouteCacheValid;
    private bool _crossingRouteCacheSucceeded;
    private bool _crossingRouteCacheWantEnter;
    private float _crossingRouteCacheUntil;
    private EntranceTeleport _crossingRouteCacheEntrance;
    private Vector3 _crossingRouteCacheDestination;
    private Vector3 _crossingRouteCacheExit;
    private string _crossingRouteCacheApproachMode;
    private bool _lastCrossingRouteFromCache;

    // The pursuit-layer priority chain, verbatim from the pre-refactor UpdateMovementDestination.
    // Returns what the caller should do; only the caller writes the agent. `pursuitDestination` is
    // ref because the stranger-snapshot fallback replaces it, and the caller's arrival/stop
    // distances must see the replacement.
    private PursuitResolutionKind ResolvePursuitDestination(
        PlayerControllerB target,
        PlayerControllerB navigationTarget,
        ref Vector3 pursuitDestination,
        Vector3 navigationTargetPosition,
        bool pursuingAssignedStrangerTarget,
        bool hasCompleteTargetPath,
        bool deferredToAsyncPath,
        Vector3 initialDestination,
        out Vector3 destination,
        out string source,
        out string reason)
    {
        destination = initialDestination;
        source = "direct";
        reason = "pursuit destination walkable";
        bool destinationIssuedByAssignedBoundaryRoute = false;
        // Cross-boundary routing must work for snapshot/ping destinations too — a TRACK/SEARCH/PING
        // destination on the interior NavMesh is unreachable from outside without entrance routing,
        // and gating this on live HUNT left Bundy loitering outdoors whenever the target went inside.
        //
        // 2026-07-31: `!deferredToAsyncPath` is load-bearing. `hasCompleteTargetPath` is false for
        // TWO different reasons — "the check ran and there is no route" and "the long-range perf
        // gate skipped the check" — and folding the second into a flag named cross-boundary made
        // every same-side target past 40m look like it was on the far side of the facility. That is
        // the camp-at-the-ship case exactly, and it is why the `deferredToAsyncPath` branch further
        // down was unreachable for the assigned target: this flag always claimed the tick first.
        // The genuine crossing case is unaffected — ShouldSuppressCrossBoundaryTargetPathCheck
        // returns before the long-range gate, so it never sets deferredToAsyncPath.
        //
        // 2026-07-31 (#199), the other half of the same lesson: the whole assigned-boundary block below
        // was entered on `!hasCompleteTargetPath` alone, so at >40m it also claimed same-side ticks and
        // labelled them "cross-boundary". There is no boundary to cross when he and his target are on
        // the same side, and the branch that fired there (`smart-assigned`) is what walked him into the
        // facility after an outdoor player. Require an actual side difference, the same test the
        // crossing router already applies internally.
        bool targetAcrossBoundary = IsLivingPlayer(target) && isOutside == target.isInsideFactory;
        bool assignedBoundaryPursuit = pursuingAssignedStrangerTarget &&
            !hasCompleteTargetPath &&
            targetAcrossBoundary;
        bool assignedCrossBoundaryRoute = assignedBoundaryPursuit && !deferredToAsyncPath;
        // True on any tick where the assigned-target smart attempt below actually ran, so the
        // general smart attempt further down does not resolve the same task a second time in one
        // tick (a second call with a >4m-different argument tears the in-flight task down and
        // restarts it, which is the one thing a pending hold must not do). It must track the branch
        // exactly: claiming an attempt that did not happen leaves the general branch gated off AND
        // the _lastSmartPath* flags it feeds stale from a previous tick.
        bool assignedSmartAttempted = assignedBoundaryPursuit;
        // #317: `!_blockedEntranceFallbackActive` is load-bearing. The fallback drives the SAME pending
        // threshold state, and it engages on ticks where this flag is false (a non-assigned target, or
        // an async-deferred one) -- so without the guard this line would wipe the threshold pause every
        // tick and the crossing could never accumulate enough hold to fire. Disengaging the fallback
        // hands the clear back on the next tick.
        if (!assignedCrossBoundaryRoute && !_blockedEntranceFallbackActive)
            ClearPendingSmartEntranceThreshold();
        // #317: the fallback tier is subordinate by construction, but its own state must not outlive the
        // condition that justified it -- yield it the moment there is no boundary to cross.
        if (!targetAcrossBoundary)
            ClearBlockedEntranceFallback();
        if (assignedBoundaryPursuit &&
            TryRouteAssignedStrangerBoundaryEntrance(target, out Vector3 assignedEntranceDestination))
        {
            destination = assignedEntranceDestination;
            source = "assigned-stranger-entrance";
            reason = "target cross-boundary";
            destinationIssuedByAssignedBoundaryRoute = true;
            // #317: the primary routed again; drop the fallback so the two never issue against each other.
            ClearBlockedEntranceFallback();
            ClearBlockedPursuitTarget(target);
            LogStrangerBoundaryRoute(target, source, destination);
            if (IsSmartEntranceTransitionHolding())
                return PursuitResolutionKind.HoldThreshold;
        }
        else if (assignedBoundaryPursuit &&
            TryGetSmartDestination(navigationTargetPosition, out Vector3 assignedSmartDestination))
        {
            destination = assignedSmartDestination;
            source = "smart-assigned";
            reason = "cross-boundary via smart path";
            destinationIssuedByAssignedBoundaryRoute = true;
            ClearBlockedPursuitTarget(target);
            LogStrangerBoundaryRoute(target, source, destination);
        }
        else if (assignedCrossBoundaryRoute)
        {
            if (_lastSmartPathPending && !HasSmartPathHoldExpired())
            {
                return PursuitResolutionKind.HoldSmartPending;
            }
            else if (_lastSmartPathNoRoute && HasStrangerSnapshotDestination(target))
            {
                pursuitDestination = GetStrangerSnapshotDestination();
                destination = pursuitDestination;
                source = "stranger-snapshot";
                reason = "smart path found no route";
                LogStrangerBoundaryRoute(target, source, destination);
            }
            else
            {
                LogStrangerBoundaryRoute(target, "smart-unavailable", target.transform.position);
            }
        }

        if (!destinationIssuedByAssignedBoundaryRoute &&
            !assignedCrossBoundaryRoute &&
            !assignedSmartAttempted &&
            TryGetSmartDestination(destination, out Vector3 smartDestination))
        {
            destination = smartDestination;
            source = "smart";
            reason = "smart path rewrote destination";
            ClearBlockedPursuitTarget(target);
        }
        else if (!destinationIssuedByAssignedBoundaryRoute &&
            !assignedCrossBoundaryRoute &&
            deferredToAsyncPath &&
            _lastSmartPathPending &&
            !HasSmartPathHoldExpired())
        {
            // Long-range async gate: the synchronous target path check was skipped this interval and
            // the smart task is still computing. Wait on it rather than falling through to the
            // synchronous reachability scans the gate exists to avoid — but only for as long as the
            // wait is plausibly about to end (see HasSmartPathHoldExpired). Past that, walking at a
            // merely-approximate destination beats standing still holding for a result.
            return PursuitResolutionKind.HoldSmartPending;
        }
        else if (!destinationIssuedByAssignedBoundaryRoute)
        {
            PursuitReachabilityStatus reachability = TryResolveReachablePursuitDestination(
                navigationTarget,
                destination,
                out Vector3 reachableDestination,
                out source);
            if (reachability == PursuitReachabilityStatus.Pending)
            {
                reason = "incremental reachability search pending";
                return PursuitResolutionKind.HoldFallbackPending;
            }

            if (reachability == PursuitReachabilityStatus.Blocked)
            {
                source = "blocked";
                reason = assignedCrossBoundaryRoute
                    ? "blocked assigned Stranger boundary pursuit"
                    : "blocked direct pursuit";
                return PursuitResolutionKind.Blocked;
            }

            bool continuingPreviousDestination =
                reachability == PursuitReachabilityStatus.PendingWithContinuation;

            destination = continuingPreviousDestination
                ? reachableDestination
                : ResolveObstacleAwareDestination(
                    reachableDestination,
                    target,
                    allowDetour: true,
                    allowReachabilityFallback: false);
            // C1.4: the obstacle-detour rewrite used to be invisible — a substituted detour still
            // logged as its pre-rewrite source ("source=direct" pointing somewhere the reachability
            // layer never chose), which is what kept the two-floor oscillation undiagnosable at
            // default verbosity. Annotate the suffix (base source is unchanged, so the MoveAuth
            // dedupe and KnownPursuitSources are unaffected).
            if (!continuingPreviousDestination && (destination - reachableDestination).sqrMagnitude > 0.01f)
                source += ":detour";
            ClearBlockedPursuitTarget(target);
            reason = continuingPreviousDestination
                ? "incremental reachability search pending; continuing last valid destination"
                : GetPursuitSourceBase(source) == "direct"
                    ? "pursuit destination walkable"
                    : "reachability fallback";
        }

        return PursuitResolutionKind.Destination;
    }

    // The single "why this destination" line (owed since Job 1; LogMovement category, verbosity 0).
    // Call at every pursuit-layer destination write. Unknown sources are a loud error, not a silent
    // bypass — jump (5a/5b) and door breach (Job 6) must register in KnownPursuitSources.
    private void RecordPursuitDestination(Vector3 destination, string source, string reason)
    {
        if (_moveAuthSuppressNestedRecords)
            return;

        string baseSource = GetPursuitSourceBase(source);
        if (!KnownPursuitSources.Contains(baseSource) && WarnedUnknownPursuitSources.Add(baseSource))
        {
            LogError(
                $"[BundyMoveAuth] unknown destination source '{source}' — every pursuit-layer source " +
                "must be registered in KnownPursuitSources (movement authority, plan step 8).");
        }

        bool changed = !_moveAuthHasRecord ||
            !string.Equals(baseSource, GetPursuitSourceBase(_moveAuthLastSource), StringComparison.Ordinal) ||
            Vector3.Distance(destination, _moveAuthLastDestination) > MoveAuthorityDestinationChangeMeters;
        if (!changed)
            return;

        string prev = _moveAuthHasRecord ? _moveAuthLastSource : "none";
        LogInfo(
            BundyLogCategory.Movement,
            0,
            $"[BundyMoveAuth] dest={FormatVector(destination)} source={source} reason={reason} prev={prev}");
        _moveAuthHasRecord = true;
        _moveAuthLastSource = source;
        _moveAuthLastDestination = destination;
    }

    private static string GetPursuitSourceBase(string source)
    {
        if (string.IsNullOrEmpty(source))
            return "none";

        int separator = source.IndexOf(':');
        return separator > 0 ? source.Substring(0, separator) : source;
    }

    // Cross-island suppression (CrossBoundaryPathSuppressionEnabled): when the target is known to be
    // on the other side of the boundary (same flag test as 3c's same-side gate), a reachability
    // CalculatePath at the target exhausts the whole disconnected graph before returning "incomplete"
    // — worst-case cost for an answer that is already known. The boundary route owns that situation.
    private bool ShouldSuppressCrossBoundaryTargetPathCheck(PlayerControllerB target)
    {
        if (!CrossBoundaryPathSuppressionEnabled || !IsLivingPlayer(target))
            return false;

        return isOutside == target.isInsideFactory;
    }

    // Long-range async preference: beyond ~40m straight-line, reachability goes through the
    // smart-path task (spread work) instead of a synchronous CalculatePath per AI interval. Only
    // engages when the smart layer can actually take the request.
    private bool ShouldDeferLongRangePathCheckToAsync(float straightLineDistance)
    {
        return LongRangeAsyncReachabilityEnabled &&
            straightLineDistance > LongRangeAsyncPathCheckMeters &&
            (BundySmartPathingBridge.IsAvailable || AnimatedElevatorRideCoordinator.HasAvailableElevator());
    }

    // Gate for the synchronous complete-path check at the pursuit target. Returns false without
    // pathing when a perf gate applies; `pathDistance` then carries the straight-line distance, the
    // same value a failed synchronous check would have produced.
    private bool TryGetPursuitPathDistance(
        PlayerControllerB target,
        Vector3 targetPosition,
        out float pathDistance,
        out bool deferredToAsync)
    {
        pathDistance = Vector3.Distance(transform.position, targetPosition);
        deferredToAsync = false;
        if (ShouldSuppressCrossBoundaryTargetPathCheck(target))
            return false;

        if (ShouldDeferLongRangePathCheckToAsync(pathDistance))
        {
            deferredToAsync = true;
            return false;
        }

        return TryGetCompleteNavigationPathDistanceCached(targetPosition, out pathDistance);
    }

    private bool TryGetPursuitPathDistance(PlayerControllerB target, Vector3 targetPosition, out float pathDistance)
    {
        return TryGetPursuitPathDistance(target, targetPosition, out pathDistance, out _);
    }
}
