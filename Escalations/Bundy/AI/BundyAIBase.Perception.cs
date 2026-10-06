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
    internal void ActivateStrangerTargeting(PlayerControllerB target, Vector3 snapshotPosition, float snapshotTime)
    {
        _strangerTargetingActive = true;
        _pursuitMode = PursuitMode.PrimaryStalk;
        _pursuitZeroTarget = null;
        _pursuitZeroAttackAttentionUntil = float.NegativeInfinity;
        UpdateStrangerTargetSnapshot(target, snapshotPosition, snapshotTime);

        _smartInteractionHoldUntil = 0f;
        _movementSidestepHoldUntil = 0f;
        ClearPendingSmartEntranceThreshold();
        LogInfo($"Stranger target assigned; routing starts immediately without pull-start action lock: target={PlayerLabel(target)}.");

        ResolveRuntimeReferences();
        if (RoundReady() &&
            IsLivingPlayer(target) &&
            agent != null &&
            agent.enabled &&
            agent.isOnNavMesh &&
            !IsActionLocked())
        {
            ReconcileOutsideStateWithPhysicalPosition();
            UpdateMovementDestination(target);
            UpdateDirectionalLocomotion();
        }
    }

    internal void UpdateStrangerTargetSnapshot(PlayerControllerB target, Vector3 snapshotPosition, float snapshotTime)
    {
        if (!IsLivingPlayer(target))
            return;
        if (_strangerTargetingActive &&
            IsLivingPlayer(_strangerTarget) &&
            target != _strangerTarget)
        {
            LogInfo(
                $"Ignored Stranger retarget from living primary={PlayerLabel(_strangerTarget)} " +
                $"to candidate={PlayerLabel(target)}.");
            return;
        }

        bool targetChanged = target != _strangerTarget;
        bool firstAssignment = !_strangerPerceptionInitialized;
        _strangerTargetingActive = true;
        _strangerTarget = target;
        _primaryTarget = target;
        targetPlayer = target;

        if (targetChanged || firstAssignment)
        {
            _strangerLiveTargetUntil = float.NegativeInfinity;
            _strangerHuntHoldUntil = float.NegativeInfinity;
            // #307: a new target means the stored estimate and heading describe someone else.
            _strangerRecentMemoryUntil = float.NegativeInfinity;
            _strangerLastKnownVelocity = Vector3.zero;
            BeginStrangerSearch(transform.position, targetChanged ? "primary target assigned" : "telegraphed arrival");
            QueueVocal(BundyAudioCue.Search, 0.2f, "Stranger arrival");
            if (IsServer)
                TryStartMovementLoopAudioWindow("Stranger arrival idle");
        }

        LogInfo(
            $"Stranger primary target assigned: target={PlayerLabel(target)}, " +
            $"searchCenter={FormatVector(_strangerSearchCenter)}, suppliedSnapshotIgnored={FormatVector(snapshotPosition)}, " +
            $"snapshotAge={Mathf.Max(0f, Time.time - snapshotTime):F1}s.");
    }

    /// <summary>
    /// Feeds the client-side escalation music cue (#166). Runs on every client from
    /// Update, because the player being hunted is very often not the owner of this
    /// AI and the audio layer is deliberately not networked.
    ///
    /// _strangerPerceptionState is owner-only, so a remote client would always read
    /// Search and never stop the cue. movingTowardsTargetPlayer is the client-visible
    /// half of the same fact, so the stop condition ORs the two: whichever a given
    /// client can actually see is the one that ends the cue.
    /// </summary>
    private void ReportEscalationAudioSignal()
    {
        if (isEnemyDead)
            return;

        PlayerControllerB target = _strangerTarget ?? _primaryTarget ?? targetPlayer;
        if (target == null)
            return;

        bool hunting = _strangerPerceptionState == StrangerPerceptionState.Hunt ||
            movingTowardsTargetPlayer;
        float distance = Vector3.Distance(transform.position, target.transform.position);

        // Through the bridge since #393: the audio layer is its own plugin now and
        // is only a soft dependency, so the call must not bind the type here.
        BundyAtmosphereBridge.ReportStalker(target, distance, hunting);
    }

    internal void UpdateStrangerPerceptionEscalation(float elapsedSeconds)
    {
        if (!IsOwner || !_strangerTargetingActive)
            return;

        _strangerEscalationElapsedSeconds = Mathf.Max(0f, elapsedSeconds);
    }

    private void UpdateStrangerPerception()
    {
        // C1.1: before the state-machine guards — the saw-stall watchdog must run every interval no
        // matter what perception is doing, because SEARCH/TRACK is exactly where the stall used to
        // survive (the hunt loop was the only path to BeginPullStart).
        MaybeRecoverStalledSaw();

        if (!_strangerTargetingActive ||
            _actionKind == ActionKind.Stagger ||
            !IsLivingPlayer(_strangerTarget))
        {
            return;
        }

        // #289 V4. The interrupt used to return here with the rest of perception, which meant any
        // teammate who stayed in LOS within NonTargetInterruptRange froze Bundy's knowledge of the
        // player he is actually hunting for as long as he cared to stand there -- an unintended and
        // unbounded hard counter. The interrupt owns MOVEMENT and ACTIONS, and it keeps them: what
        // resumes below is only the ping SCHEDULER, which updates his position estimate and its
        // clock and issues no destination and no state transition. Knowledge is not a movement
        // decision, so it does not need to wait for the interrupt to clear.
        if (_pursuitMode == PursuitMode.NonTargetInterrupt)
        {
            UpdateStrangerPingSchedulerDuringInterrupt();
            return;
        }

        AccumulateElevatorRideSuspension();

        if (!_strangerPerceptionInitialized)
        {
            BeginStrangerSearch(transform.position, "perception initialized");
            return;
        }

        if (CanLocallyAcquireStrangerTarget(_strangerTarget))
        {
            AcquireStrangerContact(_strangerTarget.transform.position, "direct LOS", routineSightRefresh: true);
            return;
        }

        if (_strangerPerceptionState == StrangerPerceptionState.Hunt)
        {
            bool committedToTarget =
                _actionTarget == _strangerTarget ||
                _activeBasicVictim == _strangerTarget ||
                _grabbedTarget == _strangerTarget;
            if (committedToTarget)
                return;

            // Two clocks, and the split is the whole of D5. _strangerLiveTargetUntil is what he KNOWS;
            // it still expires on the old schedule and CanUseLiveHuntTarget still reads it alone, so an
            // inferred hold drops him onto the last-known snapshot instead of live-tracking a player he
            // only heard. _strangerHuntHoldUntil is what he is DOING -- gait, the running saw loop,
            // search-chatter suppression and the siege gate all key off the HUNT state itself, and
            // dropping all four every 2.5s is the flap. Exiting on the knowledge clock made a noise
            // contact structurally incapable of becoming a pursuit.
            string earlyRelease = null;
            bool dwellExpired = Time.time > _strangerHuntHoldUntil;
            if (!dwellExpired && !TryReleaseStrangerHuntHold(out earlyRelease))
                return;

            float heldFor = Mathf.Max(0f, Time.time - _strangerHuntStartedAt);
            string exitReason = dwellExpired ? "sight memory expired" : earlyRelease;
            TransitionStrangerPerception(
                StrangerPerceptionState.Track,
                $"{exitReason} (contact={(_strangerHuntContactQuality == StrangerContactQuality.Inferred ? "inferred" : "live")} " +
                $"heldFor={heldFor:F1}s)");
            return;
        }

        if (_strangerPerceptionState == StrangerPerceptionState.Track)
        {
            if (!_hasStrangerLastKnownPosition)
            {
                BeginStrangerSearch(transform.position, "TRACK had no last-known position");
                return;
            }

            // #307: TRACK aims at where the estimate says he has GOT to, not where it was pinned. The
            // arrival test, the progress metric and the destination all read the same point, or the
            // watchdog would be measuring progress toward somewhere he is not walking. Falls back to the
            // raw last-known the moment recent memory lapses, which is the pre-#307 behaviour exactly.
            Vector3 trackAim = TryGetPredictedPursuitPosition(out Vector3 predictedTrackAim)
                ? predictedTrackAim
                : _strangerLastKnownPosition;

            float distance = Vector3.Distance(transform.position, trackAim);
            if (distance <= StopDistance + StrangerDestinationReachedPadding)
            {
                BeginStrangerSearch(trackAim, "last-known position reached");
                return;
            }

            // Watchdog. Arrival used to be the ONLY exit, so an unreachable last-known -- or a
            // destination hijacked further down the movement stack -- pinned him in TRACK forever,
            // and because pings only run in the PING state he stopped receiving any new information
            // at all (run 3 lost 4m34s of an 8m round to exactly this). Dropping to SEARCH at his
            // own position re-enters PING one search window later.
            //
            // #289 V1: progress is measured along the ROUTE, not as the crow flies. TRACK is the only
            // state whose destination can elect an elevator ride, and every legitimate vertical route
            // starts by walking AWAY from the target (to the lobby, the stairwell, the shaft). Under a
            // straight-line metric that opening leg is indistinguishable from standing still, so the
            // one state that can descend aborted itself before it ever boarded. Remaining path length
            // shrinks the moment he starts walking the detour, so a detour now counts as the progress
            // it is.
            //
            // An elevator ride owns the agent (KeepActorCarried resets the path every tick) and the
            // ride IS the progress, so a ride short-circuits the whole measurement -- checked before
            // the path call, because during a ride the answer would be both meaningless and the most
            // expensive kind of query to ask.
            bool ridingElevator = _smartPathing != null && _smartPathing.ElevatorRideInProgress;
            if (ridingElevator)
            {
                _strangerTrackBestDistanceAt = Time.time;
            }
            else
            {
                // Perf (#263 class): a synchronous CalculatePath is only affordable under the same two
                // gates the pursuit reachability check already uses. Bundy outside with an interior
                // last-known is the pathological case -- the query exhausts a disconnected graph before
                // answering "incomplete" -- and TRACK would have asked it every AI interval for 45s.
                // When either gate applies the straight line is used instead; it is a worse progress
                // signal but it is the one the old code used, so this is never a regression.
                float trackStraightLine = distance;
                bool pathCheckAffordable =
                    !ShouldSuppressCrossBoundaryTargetPathCheck(_strangerTarget) &&
                    !ShouldDeferLongRangePathCheckToAsync(trackStraightLine);

                float trackProgressDistance = trackStraightLine;
                bool fromPath = pathCheckAffordable &&
                    TryGetCompleteNavigationPathDistanceCached(
                        trackAim,
                        out trackProgressDistance);
                if (!fromPath)
                    trackProgressDistance = trackStraightLine;

                // Remaining-path and straight-line distances are not comparable, so switching between
                // them has to re-baseline -- but a re-baseline also resets the 12s clock, and a route
                // that flaps complete/incomplete (a door cycling, the 1.75m final-corner rejection)
                // would reset it forever and leave the hard cap as the only exit. So a switch has to
                // PERSIST for StrangerTrackMetricSwitchHoldSeconds before it is honoured; until then
                // the old metric's baseline and its clock both stand and the watchdog keeps counting.
                // Held on the wall clock rather than a tick count on purpose -- see the constant.
                if (float.IsPositiveInfinity(_strangerTrackBestDistance))
                {
                    _strangerTrackBestFromPath = fromPath;
                    _strangerTrackBestDistance = trackProgressDistance;
                    _strangerTrackBestDistanceAt = Time.time;
                    _strangerTrackMetricSwitchSince = float.NegativeInfinity;
                }
                else if (fromPath != _strangerTrackBestFromPath)
                {
                    if (float.IsNegativeInfinity(_strangerTrackMetricSwitchSince))
                        _strangerTrackMetricSwitchSince = Time.time;

                    if (Time.time - _strangerTrackMetricSwitchSince >= StrangerTrackMetricSwitchHoldSeconds)
                    {
                        _strangerTrackBestFromPath = fromPath;
                        _strangerTrackBestDistance = trackProgressDistance;
                        _strangerTrackBestDistanceAt = Time.time;
                        _strangerTrackMetricSwitchSince = float.NegativeInfinity;
                    }
                }
                else
                {
                    _strangerTrackMetricSwitchSince = float.NegativeInfinity;
                    if (trackProgressDistance < _strangerTrackBestDistance - StrangerTrackProgressEpsilon)
                    {
                        _strangerTrackBestDistance = trackProgressDistance;
                        _strangerTrackBestDistanceAt = Time.time;
                    }
                }

                if (Time.time - _strangerTrackBestDistanceAt >= StrangerTrackNoProgressSeconds)
                {
                    // #307: seeded on the knowledge, not on his own feet. A TRACK that gave up 30m short
                    // of the last-known used to open its search annulus around wherever it happened to
                    // stall, which is the one place in the level he already knows the player is not.
                    BeginStrangerSearch(
                        GetStrangerSearchSeedPosition(),
                        $"TRACK made no progress for {StrangerTrackNoProgressSeconds:F0}s " +
                        $"({(_strangerTrackBestFromPath ? "path" : "direct")} {trackProgressDistance:F1}m " +
                        $"to last-known, {distance:F1}m straight-line)");
                    return;
                }
            }

            // The hard cap. Suspended for the duration of an elevator ride (a legitimate call-wait plus
            // shaft traversal can now run to 14+60s, which would otherwise expire the cap mid-shaft and
            // snap a search centre onto a moving car) but armed at all other times, including a ride
            // that never resolves -- the coordinator's own budgets end that, and the held seconds stop
            // accruing the moment it does.
            if (Time.time - _strangerTrackStartedAt - _strangerTrackElevatorHeldSeconds >= StrangerTrackMaxSeconds)
            {
                BeginStrangerSearch(
                    GetStrangerSearchSeedPosition(),
                    $"TRACK exceeded {StrangerTrackMaxSeconds:F0}s (last-known {distance:F1}m away, " +
                    $"{_strangerTrackElevatorHeldSeconds:F0}s of elevator ride excluded)");
            }
            return;
        }

        if (_strangerPerceptionState == StrangerPerceptionState.Search)
        {
            // Elevator-suspended like TRACK's cap: a seeded cross-floor search can spend more than the
            // whole 25s window inside a single legitimate ride, and expiring mid-shaft would hand
            // BeginStrangerPings a position update while he is standing in a moving car.
            if (Time.time - _strangerSearchStartedAt - _strangerSearchElevatorHeldSeconds >= SearchWindowSeconds)
            {
                BeginStrangerPings("search window expired");
                return;
            }

            if (MaybeSkipUnpathableSearchRoamPoint())
                return;

            // Arrival in SEARCH is arrival at the roam point, which is a destination and not knowledge.
            // Before the split this read _strangerLastKnownPosition and happened to work only because
            // the roam point had been written into it.
            if ((_hasStrangerDestinationOverride || _hasStrangerLastKnownPosition) &&
                Vector3.Distance(transform.position, GetStrangerSnapshotDestination()) <=
                    StopDistance + StrangerDestinationReachedPadding)
            {
                TrySetNextStrangerSearchRoamPoint();
            }
            return;
        }

        if (_strangerPerceptionState == StrangerPerceptionState.Ping &&
            Time.time >= _nextStrangerPingAt)
        {
            if (IsStrangerTargetShelteredInClosedShip())
            {
                _nextStrangerPingAt = Time.time + GetEffectiveStrangerPingInterval();
                return;
            }

            IssueStrangerPing();
        }
    }

    // ---- Elevator ride clock suspension (#289) --------------------------------------------------
    // Splitting the elevator budgets (14s call-wait + 60s ride) made a legitimate ride longer than
    // both of the perception clocks that were supposed to outlive it: SearchWindowSeconds (25s) and
    // StrangerTrackMaxSeconds (45s). Either one expiring mid-shaft ends the state from under the
    // coordinator and snaps a search centre or a ping onto a moving car.
    //
    // Measured as OBSERVED time, for the same reason MaybeSkipUnpathableSearchRoamPoint is: this runs
    // from the perception tick, which several early returns above it can skip for seconds at a time,
    // so a wall-clock subtraction would credit him for suspension he never observed. Per-tick deltas
    // are capped so one long gap cannot bank a large exclusion.
    // Total credit is also capped. The coordinator bounds every stage it owns (1.35s approach-stall
    // detection, 14s call wait, 60s ride), but "suspend a clock while a subsystem says it is busy" is
    // exactly the shape that turns one unbounded subsystem state into an unbounded perception state,
    // and the perception caps exist because that has happened before. 90s covers the worst legitimate
    // sequence with slack; past it the caps re-arm and TRACK/SEARCH end normally even mid-ride.
    private const float ElevatorSuspensionMaxTickSeconds = 1f;
    private const float ElevatorSuspensionMaxHeldSeconds = 90f;

    private void AccumulateElevatorRideSuspension()
    {
        float now = Time.time;
        float observed = float.IsNegativeInfinity(_elevatorSuspensionTickAt)
            ? 0f
            : Mathf.Clamp(now - _elevatorSuspensionTickAt, 0f, ElevatorSuspensionMaxTickSeconds);
        _elevatorSuspensionTickAt = now;

        if (_smartPathing == null || !_smartPathing.ElevatorRideInProgress)
            return;

        _strangerTrackElevatorHeldSeconds = Mathf.Min(
            _strangerTrackElevatorHeldSeconds + observed,
            ElevatorSuspensionMaxHeldSeconds);
        _strangerSearchElevatorHeldSeconds = Mathf.Min(
            _strangerSearchElevatorHeldSeconds + observed,
            ElevatorSuspensionMaxHeldSeconds);
    }

    // ---- Pursuit stall watchdog (2026-07-31) ---------------------------------------------------
    // "He wanders around his spawn and never comes to the ship" was reported after four sessions of
    // work whose diagnostics all sit behind DebugLogs AND a BepInEx disk log level that excludes
    // Info — so the round that produced the report contains not one line about what he was doing.
    // Every other Bundy diagnostic is opt-in on purpose (they are per-tick and they flood). This one
    // is not: it fires only when he has demonstrably failed to close on the player he is assigned to
    // hunt, and it fires at WARNING, so the failure puts its own evidence in the log the user
    // already has. It reports state, not a guess — whatever is actually holding him is named here.
    private const float PursuitStallGraceSeconds = 45f;      // clears a full SEARCH window + a ping cycle
    private const float PursuitStallReportIntervalSeconds = 15f;
    private const float PursuitStallProgressEpsilon = 3f;    // metres of closing that count as progress
    private const float PursuitStallMinDistance = 8f;        // already on top of them = not a stall

    // #496: the baseline used to be a best-EVER distance that was only cleared when targeting stopped.
    // One close-quarters engagement wrote 2.1m into it and every later pursuit in the same assignment
    // was measured against that, so a fresh 68m re-acquisition read as "no progress since the moment he
    // was standing on them" and warned immediately while he was actively closing to 12.9m.
    //
    // The baseline is now a TRAILING-window best, kept in two buckets: the best of the bucket being
    // filled and the best of the one before it. The effective baseline is the smaller of the two, so a
    // reading survives between one and two window lengths and then expires. A reading that expires makes
    // the baseline rise, and a rising baseline is exactly the re-arm case -- the old, better distance
    // stopped being evidence about the present -- so the clock is restarted there. A genuine stall never
    // trips that: a Bundy pinned at 50m fills every bucket with 50m and the baseline never moves.
    private const float PursuitStallBaselineWindowSeconds = 30f;
    private const float PursuitStallBaselineRearmEpsilon = 1f;
    // Slow but real closing (a long corridor route, a lift, a detour) can advance less than the 3m
    // epsilon between samples for a long time. The trend anchor catches it: any net closing over its
    // window counts as progress, which is ~0.1m/s -- far under a walk, far over positional jitter.
    private const float PursuitStallTrendWindowSeconds = 10f;
    private const float PursuitStallTrendProgressMeters = 1f;

    private void UpdateStrangerPursuitStallWatchdog()
    {
        if (!IsOwner || isEnemyDead || !_strangerTargetingActive || !IsLivingPlayer(_strangerTarget))
        {
            ResetStrangerPursuitStallWatchdog();
            return;
        }

        float now = Time.time;
        float distance = Vector3.Distance(transform.position, _strangerTarget.transform.position);

        if (float.IsNegativeInfinity(_pursuitStallBestAt))
        {
            _pursuitStallBestDistance = distance;
            _pursuitStallPreviousBestDistance = distance;
            _pursuitStallBucketStartedAt = now;
            _pursuitStallTrendDistance = distance;
            _pursuitStallTrendAt = now;
            _pursuitStallBestAt = now;
            return;
        }

        // Read the baseline BEFORE folding this sample in -- folding first would make the baseline the
        // minimum including the current distance, and "closer than the best" could never be true.
        float baseline = Mathf.Min(_pursuitStallBestDistance, _pursuitStallPreviousBestDistance);
        bool closedOnBaseline = distance < baseline - PursuitStallProgressEpsilon;

        // Trailing-window roll.
        bool rearmed = false;
        if (now - _pursuitStallBucketStartedAt >= PursuitStallBaselineWindowSeconds)
        {
            _pursuitStallPreviousBestDistance = _pursuitStallBestDistance;
            _pursuitStallBestDistance = distance;
            _pursuitStallBucketStartedAt = now;
            float rolledBaseline = Mathf.Min(_pursuitStallBestDistance, _pursuitStallPreviousBestDistance);
            rearmed = rolledBaseline > baseline + PursuitStallBaselineRearmEpsilon;
        }
        else
        {
            _pursuitStallBestDistance = Mathf.Min(_pursuitStallBestDistance, distance);
        }

        // Slow-closing trend, evaluated on its own window.
        bool closingTrend = false;
        if (now - _pursuitStallTrendAt >= PursuitStallTrendWindowSeconds)
        {
            closingTrend = distance <= _pursuitStallTrendDistance - PursuitStallTrendProgressMeters;
            _pursuitStallTrendDistance = distance;
            _pursuitStallTrendAt = now;
        }
        // The anchor is deliberately NOT tracked between rolls: it is the distance at the start of the
        // window, and the comparison is a net-displacement test over that window. Tracking it downward
        // would turn it into a second best-so-far and reintroduce the ratchet this issue is about.

        if (distance <= PursuitStallMinDistance || closedOnBaseline || closingTrend || rearmed)
        {
            _pursuitStallBestAt = now;
            if (rearmed)
            {
                _pursuitStallTrendDistance = distance;
                _pursuitStallTrendAt = now;
            }

            return;
        }

        float stalledFor = now - _pursuitStallBestAt;
        if (stalledFor < PursuitStallGraceSeconds || Time.time < _nextPursuitStallReportAt)
            return;

        _nextPursuitStallReportAt = now + PursuitStallReportIntervalSeconds;
        string destinationOwner = _hasStrangerDestinationOverride
            ? _strangerDestinationOverrideKind
            : "last-known";
        float destinationDistance = HasStrangerSnapshotDestination(_strangerTarget)
            ? Vector3.Distance(transform.position, GetStrangerSnapshotDestination())
            : -1f;

        LogWarning(
            "[BundyPursuitStall] no progress toward the hunted player for " +
            $"{stalledFor:F0}s: distance={distance:F1}m " +
            $"(window best {Mathf.Min(_pursuitStallBestDistance, _pursuitStallPreviousBestDistance):F1}m " +
            $"over {PursuitStallBaselineWindowSeconds:F0}s, trend anchor {_pursuitStallTrendDistance:F1}m), " +
            $"state={_strangerPerceptionState.ToString().ToUpperInvariant()}, " +
            $"destOwner={destinationOwner}, destDistance={destinationDistance:F1}m, " +
            $"lastSource={(_moveAuthHasRecord ? _moveAuthLastSource : "<none>")}, " +
            $"action={_actionKind}, lurk={(Time.time < _strangerExitLurkCommittedUntil ? "held" : "no")}, " +
            $"siegeFallback={_shipSiegeFallbackActive}, sawStalled={_sawStalled}, " +
            $"bundyOutside={isOutside}, targetInside={_strangerTarget.isInsideFactory}, " +
            $"boundaryLinks={(ShouldAllowBoundarySmartLinks(_strangerTarget.transform.position) ? "allowed" : "withheld")}, " +
            $"agent={DescribeAgentDriveState()}, " +
            $"failsafe=(best={_boundaryEntryBestEntranceDistance:F1}m, " +
            $"heldFor={(float.IsNegativeInfinity(_boundaryEntryLastProgressAt) ? 0f : Time.time - _boundaryEntryLastProgressAt):F0}s), " +
            $"nextPing={Mathf.Max(0f, _nextStrangerPingAt - Time.time):F1}s.");
    }

    private void ResetStrangerPursuitStallWatchdog()
    {
        _pursuitStallBestDistance = float.PositiveInfinity;
        _pursuitStallPreviousBestDistance = float.PositiveInfinity;
        _pursuitStallBucketStartedAt = float.NegativeInfinity;
        _pursuitStallTrendDistance = float.PositiveInfinity;
        _pursuitStallTrendAt = float.NegativeInfinity;
        _pursuitStallBestAt = float.NegativeInfinity;
    }

    // The ship teleporter is the loudest, least ambiguous event on the map -- the beam is audible and
    // every player knows it. Design says noise feeds him, but the 14m hearing radius meant a cross-map
    // yank left no trail at all, making the teleporter a silent hard counter. Revealing the ship here
    // closes that and funnels him into the battery siege. Only fires when the HUNTED player is the one
    // teleported: pinning last-known to the ship for someone else's teleport would push him away from
    // his target and turn the teleporter into a free lure. D4 kept its own config gate and its own
    // "always trusted, always map-wide" terms; the bus below reproduces both through the catalog.
    internal void OnShipTeleporterActivated(PlayerControllerB teleportedPlayer, Vector3 teleporterPosition)
    {
        if (!TeleporterRevealEnabled)
            return;

        OnWorldNoiseEvent(teleporterPosition, BundyWorldNoise.ShipTeleporter, teleportedPlayer);
    }

    // Job 3b -- the world-noise bus. Every hook in BundyWorldNoisePatches funnels here, and D4 above
    // is now just the first caller rather than a special case.
    //
    // Five gates, in the order that throws work away soonest. Each exists because of a specific way
    // this feature could make him worse rather than better:
    //
    //   1. ATTRIBUTION. Reacting to noise from a player he is not hunting turns every hook into a free
    //      lure and quietly ends the single-target stalk the whole design rests on. This is D4's rule,
    //      generalised: when the causer is not supplied, resolve it by proximity using the same helper
    //      and radius DetectNoise already trusts.
    //   2. RANGE. A breaker lever is not audible from the other end of the map. Only the three events
    //      the catalog marks map-wide ignore this.
    //   3. RATE. Several of these vanilla events repeat for as long as the player keeps using the
    //      thing, so each meaning gets its own cooldown.
    //   4. PRIORITY. A jittered inferred position is strictly worse than live tracking he already has,
    //      so a weak contact must never overwrite a strong one. Trusted events are exempt -- a teleport
    //      is exactly the case where his current knowledge just became wrong.
    //   5. DOUBT. Below the trust threshold the position is scattered in proportion to the doubt, so a
    //      walkie transmission gives him a neighbourhood and a terminal gives him a room. Without this
    //      every hook would be a pinpoint and the bus would read as wallhacking.
    internal void OnWorldNoiseEvent(
        Vector3 position,
        BundyWorldNoise meaning,
        PlayerControllerB causedBy = null,
        float confidenceOverride = -1f)
    {
        if (!IsOwner ||
            !WorldNoiseBusEnabled ||
            !_strangerTargetingActive ||
            _pursuitMode == PursuitMode.NonTargetInterrupt ||
            _actionKind == ActionKind.Stagger ||
            !IsLivingPlayer(_strangerTarget))
        {
            return;
        }

        string label = BundyWorldNoiseCatalog.GetLabel(meaning);

        // 1. Attribution.
        PlayerControllerB causer = IsLivingPlayer(causedBy)
            ? causedBy
            : FindLivingPlayerNearPosition(position, WorldNoiseAttributionRadiusMeters);
        if (causer != _strangerTarget)
        {
            if (DebugLogs)
            {
                LogInfo(BundyLogCategory.Perception, 1,
                    $"[BundyWorldNoise] {label} ignored; causer={PlayerLabel(causer)} is not the hunted player.");
            }
            return;
        }

        // 2. Range.
        float radius = BundyWorldNoiseCatalog.GetAudibleRadius(meaning);
        int slot = (int)meaning;
        if (!float.IsPositiveInfinity(radius))
        {
            radius = Mathf.Max(0f, radius * WorldNoiseRadiusScale);
            float distance = Vector3.Distance(transform.position, position);
            if (distance > radius)
            {
                if (DebugLogs)
                {
                    LogInfo(BundyLogCategory.Perception, 1,
                        $"[BundyWorldNoise] {label} out of range distance={distance:F1}m radius={radius:F1}m.");
                }
                return;
            }
        }

        // 3. Rate. Terminal audio and walkie transmissions are not single events -- vanilla fires them
        // repeatedly while the player keeps using the thing. Unthrottled, a player typing at the
        // terminal would re-pin his last-known every few frames and print a perception line each time,
        // which is precisely the raw-Debug.Log flooding Job 1 exists to prevent. Throttled after
        // attribution and range so that only events he would actually have reacted to consume a slot.
        if (slot >= 0 && slot < _worldNoiseNextAcceptAt.Length)
        {
            if (Time.time < _worldNoiseNextAcceptAt[slot])
                return;

            _worldNoiseNextAcceptAt[slot] = Time.time + Mathf.Max(0f, WorldNoiseEventCooldownSeconds);
        }

        float confidence = confidenceOverride >= 0f
            ? Mathf.Clamp01(confidenceOverride)
            : Mathf.Clamp01(BundyWorldNoiseCatalog.GetConfidence(meaning));
        bool trusted = confidence >= Mathf.Clamp01(WorldNoiseLiveConfidenceThreshold);

        // 4. Priority.
        if (!trusted && Time.time <= _strangerLiveTargetUntil)
        {
            if (DebugLogs)
            {
                LogInfo(BundyLogCategory.Perception, 1,
                    $"[BundyWorldNoise] {label} skipped; live tracking is better than confidence={confidence:F2}.");
            }
            return;
        }

        // 5. Doubt.
        float jitter = Mathf.Max(0f, WorldNoisePositionNoiseMeters) * (1f - confidence);
        Vector3 reported = ApplyInferredPositionJitter(position, jitter);

        StrangerContactQuality quality = trusted
            ? StrangerContactQuality.Live
            : StrangerContactQuality.Inferred;

        AcquireStrangerContact(
            reported,
            $"world noise {label} confidence={confidence:F2}",
            quality);

        if (DebugLogs)
        {
            LogInfo(BundyLogCategory.Perception, 0,
                $"[BundyWorldNoise] {label} accepted confidence={confidence:F2} quality={(trusted ? "live" : "inferred")} " +
                $"jitter={jitter:F1}m source={FormatVector(position)} pinned={FormatVector(_strangerLastKnownPosition)}");
        }
    }

    // Scatter an INFERRED position in the horizontal plane. Every path that gives Bundy a position he
    // did not see has to go through something like this or it reads as a wallhack; this is the shared
    // implementation the world-noise bus and DetectNoise (#289 V-jitter) both use. Height is left
    // alone on purpose -- displacing y would drop the point off its floor and ResolveSnapshotToNavMesh
    // would then snap it somewhere arbitrary.
    private static Vector3 ApplyInferredPositionJitter(Vector3 position, float jitterMeters)
    {
        if (jitterMeters <= 0.01f)
            return position;

        Vector2 offset = UnityEngine.Random.insideUnitCircle * jitterMeters;
        return new Vector3(position.x + offset.x, position.y, position.z + offset.y);
    }

    // routineSightRefresh marks the per-AI-interval LOS re-acquire, which is the only caller that
    // repeats for as long as the condition holds. It is excluded from the mid-hunt line below; every
    // other caller is a discrete event and is worth one line each.
    private void AcquireStrangerContact(
        Vector3 contactPosition,
        string reason,
        StrangerContactQuality quality = StrangerContactQuality.Live,
        bool routineSightRefresh = false)
    {
        if (!_strangerTargetingActive || !IsLivingPlayer(_strangerTarget))
            return;

        Vector3 resolvedContact = ResolveSnapshotToNavMesh(contactPosition);
        // #307: measured against the PREVIOUS last-known, so it has to happen before the overwrite.
        UpdateStrangerLastKnownVelocity(resolvedContact, quality);
        _strangerLastKnownPosition = resolvedContact;
        _strangerLastKnownAt = Time.time;
        _hasStrangerLastKnownPosition = true;
        _strangerPredictedPursuitAt = float.NegativeInfinity;
        _lastKnownPursuitPosition = _strangerLastKnownPosition;
        _hasLastKnownPursuitPosition = true;

        // Real knowledge outranks any roam or lurk destination that was standing in for it.
        ClearStrangerDestinationOverride();
        EndStrangerExitLurk();

        // Deliberately NOT contact-typed. Every consumer of live tracking -- the live-vs-snapshot
        // destination, target.isInsideFactory in GetNavigationNodesForTarget, attack eligibility,
        // manual facing -- reads _strangerLiveTargetUntil through CanUseLiveHuntTarget. Granting an
        // inferred contact extra frames here would turn one footstep at HearingRadiusMeters into a
        // wallhack. The dwell below buys commitment, never knowledge.
        _strangerLiveTargetUntil = Time.time + Mathf.Max(0f, SightMemoryGraceSeconds);

        // #307. The third tier. Contact-agnostic on purpose -- an inferred contact is a worse ESTIMATE
        // (it is jittered, and it carries no velocity), not a shorter-lived one, and the whole failure
        // this fixes is him discarding a perfectly good estimate 2.5s after the last confirmation.
        // Nothing that can hurt the player reads this clock.
        _strangerRecentMemoryUntil = Time.time + Mathf.Max(0f, PursuitMemorySeconds);

        bool inferred = quality == StrangerContactQuality.Inferred && HuntDwellEnabled;
        float holdUntil = Time.time + Mathf.Max(
            0f,
            inferred ? NoiseContactHuntDwellSeconds : SightMemoryGraceSeconds);
        bool enteringHunt = _strangerPerceptionState != StrangerPerceptionState.Hunt ||
            !_strangerPerceptionInitialized;

        if (enteringHunt)
        {
            _strangerHuntHoldUntil = holdUntil;
            _strangerHuntStartedAt = Time.time;
            _strangerHuntContactQuality = quality;
        }
        else
        {
            // A weak contact landing mid-hold must not cut a stronger one short, and the per-tick LOS
            // re-acquire must still push the hold forward exactly as it pushes the live grace forward.
            // The label follows whichever contact owns the current hold end, so the exit line answers
            // the question the log is actually asked: what kept him committed this long?
            float previousHoldUntil = _strangerHuntHoldUntil;
            if (holdUntil >= _strangerHuntHoldUntil)
                _strangerHuntContactQuality = quality;
            _strangerHuntHoldUntil = Mathf.Max(_strangerHuntHoldUntil, holdUntil);

            // TransitionStrangerPerception drops the reason whenever the state does not change, so a
            // contact arriving DURING a hunt used to leave no trace at all: run 8 exited with
            // contact=inferred while the log held not one line saying where that contact came from, and
            // the only way to know it happened was that the value is unreachable without a DetectNoise
            // call. Mid-hunt contacts decide how long he commits, and since the world-noise bus they
            // arrive from many more places, so each one gets a line naming itself and what it bought.
            if (!routineSightRefresh && DebugLogs)
            {
                LogInfo(BundyLogCategory.Perception, 0,
                    $"[BundyPerception] state=HUNT reason={reason} (mid-hunt " +
                    $"contact={(quality == StrangerContactQuality.Inferred ? "inferred" : "live")} " +
                    $"heldFor={Mathf.Max(0f, Time.time - _strangerHuntStartedAt):F1}s " +
                    $"extendedBy={Mathf.Max(0f, _strangerHuntHoldUntil - previousHoldUntil):F1}s " +
                    $"holdRemaining={Mathf.Max(0f, _strangerHuntHoldUntil - Time.time):F1}s)");
            }
        }

        TransitionStrangerPerception(StrangerPerceptionState.Hunt, reason);
    }

    // ---- #307 recent-memory tier ----------------------------------------------------------------
    // Sampling bounds. Below the minimum the divisor blows a centimetre of navmesh snapping up into a
    // metres-per-second heading; above the maximum the two samples are not the same motion any more
    // (the player has turned a corner, or ridden an elevator, in between). The speed bound sits a hair
    // above a sprinting player, and going over it REJECTS the sample rather than clamping it: anything
    // faster than a sprint is a discontinuity, not motion -- a ship teleport (which the world-noise bus
    // reports as a trusted Live contact), an elevator arrival, a navmesh snap across a floor -- and
    // clamping would keep the one part of it that is certainly wrong, the direction.
    private const float StrangerVelocityMinSampleSeconds = 0.05f;
    private const float StrangerVelocityMaxSampleSeconds = 2f;
    private const float StrangerVelocityMaxSpeed = 10f;
    // The extrapolation cap IS the counterplay. Past ~1.5s a heading says nothing about where someone
    // who knows they are being chased actually went, and extending it is how a prediction tier turns
    // into a tracking beam. Deliberately not configurable.
    private const float PursuitPredictionMaxExtrapolationSeconds = 1.5f;
    private const float PursuitPredictionCacheSeconds = 0.1f;

    private void UpdateStrangerLastKnownVelocity(Vector3 newPosition, StrangerContactQuality quality)
    {
        // Inferred contacts are scattered by ApplyInferredPositionJitter, so the difference between two
        // of them is jitter divided by dt -- a fast heading pointing nowhere. They still move the
        // estimate; they simply refuse to claim they saw which way he went.
        if (quality != StrangerContactQuality.Live || !_hasStrangerLastKnownPosition)
        {
            _strangerLastKnownVelocity = Vector3.zero;
            return;
        }

        float dt = Time.time - _strangerLastKnownAt;
        if (!(dt > StrangerVelocityMinSampleSeconds) || dt > StrangerVelocityMaxSampleSeconds)
        {
            _strangerLastKnownVelocity = Vector3.zero;
            return;
        }

        Vector3 velocity = (newPosition - _strangerLastKnownPosition) / dt;
        float maxSpeedSquared = StrangerVelocityMaxSpeed * StrangerVelocityMaxSpeed;
        _strangerLastKnownVelocity = velocity.sqrMagnitude > maxSpeedSquared
            ? Vector3.zero
            : velocity;
    }

    // Where he believes the target has got to. Valid for as long as the recent-memory clock holds, which
    // is strictly longer than live tracking and strictly shorter than forever. Returns the raw estimate
    // when there is no heading to extrapolate, so the caller never has to distinguish the two cases.
    private bool TryGetPredictedPursuitPosition(out Vector3 predicted)
    {
        predicted = _strangerLastKnownPosition;
        if (!_hasStrangerLastKnownPosition || Time.time > _strangerRecentMemoryUntil)
            return false;

        if (_strangerLastKnownVelocity.sqrMagnitude <= 0.01f)
            return true;

        if (Time.time - _strangerPredictedPursuitAt <= PursuitPredictionCacheSeconds)
        {
            predicted = _strangerPredictedPursuitPosition;
            return true;
        }

        float age = Mathf.Clamp(
            Time.time - _strangerLastKnownAt,
            0f,
            PursuitPredictionMaxExtrapolationSeconds);
        predicted = ResolveSnapshotToNavMesh(_strangerLastKnownPosition + _strangerLastKnownVelocity * age);
        _strangerPredictedPursuitPosition = predicted;
        _strangerPredictedPursuitAt = Time.time;
        return true;
    }

    // #307. Runs from Update rather than the AI interval, so a corner-peek shorter than one interval is
    // no longer invisible: at AIIntervalTime he could look straight at a player for 0.19s and record
    // nothing. Owner-only (Update's owner gate is above the call site) and HUNT/TRACK only -- SEARCH's
    // deafness and PING's noise are the hiding design and must not be given a free per-frame eye.
    // Costs at most the three linecasts in HasLineOfSightToPlayer, and allocates nothing.
    private const float StrangerSightRefreshIntervalSeconds = 0.1f;

    private void UpdateStrangerSightRefresh()
    {
        if (isEnemyDead ||
            !_strangerTargetingActive ||
            !_strangerPerceptionInitialized ||
            _actionKind == ActionKind.Stagger ||
            (_strangerPerceptionState != StrangerPerceptionState.Hunt &&
             _strangerPerceptionState != StrangerPerceptionState.Track) ||
            !IsLivingPlayer(_strangerTarget))
        {
            return;
        }

        if (Time.time < _nextStrangerSightRefreshAt)
            return;

        _nextStrangerSightRefreshAt = Time.time + StrangerSightRefreshIntervalSeconds;
        TryRefreshStrangerLiveTargetKnowledge(_strangerTarget);
    }

    // The dwell is a floor on commitment, not a cage. Job 3a's lesson was that any hold able to outlive
    // its own purpose eventually parks him somewhere doing nothing, so the extension past the live grace
    // stays negotiable. Returns false while the ordinary LOS coyote window is still running -- D5 was not
    // asked to change sighted behaviour, and releasing in there would shorten it.
    private bool TryReleaseStrangerHuntHold(out string reason)
    {
        reason = null;

        if (Time.time <= _strangerLiveTargetUntil)
            return false;

        // Standing on the last-known with nobody in sight. TRACK's own arrival test would drop him to
        // SEARCH a tick after handover anyway; holding HUNT here would just be the freeze wearing a
        // different state name.
        if (_hasStrangerLastKnownPosition &&
            Vector3.Distance(transform.position, _strangerLastKnownPosition) <=
                StopDistance + StrangerDestinationReachedPadding)
        {
            reason = "hunt dwell reached last-known position";
            return true;
        }

        // TryHandleStrangerShipSiege() is gated on NOT being in HUNT. #127 only just started firing end
        // to end; an inferred dwell must not be able to delay the battery route by its own length.
        if (IsStrangerTargetShelteredInClosedShip())
        {
            reason = "hunt dwell yielded to ship siege";
            return true;
        }

        return false;
    }

    private void TransitionStrangerPerception(StrangerPerceptionState state, string reason)
    {
        bool changed = !_strangerPerceptionInitialized || _strangerPerceptionState != state;
        StrangerPerceptionState previous = _strangerPerceptionState;
        _strangerPerceptionState = state;
        _strangerPerceptionInitialized = true;
        if (!changed)
            return;

        // #307. HUNT->TRACK is the one transition that does not change what he is looking for: the sweep
        // is anchored on _lastKnownPursuitPosition and TRACK walks at that same knowledge, so tearing it
        // down here threw away a partially completed node search and made him restart the recovery he
        // was already halfway through -- every 2.5s, in the exact state that needs it most. Every other
        // transition (into SEARCH, into PING, back into HUNT) does change the question, so it still ends
        // the sweep.
        bool sweepSurvives = previous == StrangerPerceptionState.Hunt &&
            state == StrangerPerceptionState.Track;
        if (!sweepSurvives)
            StopPursuitSearchIfActive();
        if (DebugLogs)
        {
            LogInfo(BundyLogCategory.Perception, 0,
                $"[BundyPerception] state={state.ToString().ToUpperInvariant()} " +
                $"reason={reason} previous={previous.ToString().ToUpperInvariant()}");
        }

        // C1.1: a perception state change must never carry a saw stall silently — the 105s hole
        // began exactly here (stall held across HUNT->SEARCH with nothing in the log saying so).
        // The stall-recovery watchdog is what clears it; this line is the audit trail.
        if (_sawStalled)
        {
            LogInfo(
                $"Saw stall held across perception change " +
                $"{previous.ToString().ToUpperInvariant()}->{state.ToString().ToUpperInvariant()}; " +
                $"stalledFor={Time.time - _sawStalledAt:F1}s (pull-start watchdog armed).");
        }

        if (state == StrangerPerceptionState.Track)
        {
            _strangerTrackStartedAt = Time.time;
            // #289 V1: left as +Infinity on purpose when there is no last-known -- the TRACK tick
            // re-baselines on the first metric it actually measures (path or straight line).
            _strangerTrackBestDistance = float.PositiveInfinity;
            _strangerTrackBestFromPath = false;
            _strangerTrackMetricSwitchSince = float.NegativeInfinity;
            _strangerTrackElevatorHeldSeconds = 0f;
            _strangerTrackBestDistanceAt = Time.time;
        }

        if (state == StrangerPerceptionState.Hunt)
        {
            QueueVocal(BundyAudioCue.Spot, DialogueAfterEventDelaySeconds, $"perception acquire: {reason}");
            if (IsServer)
            {
                TryStartMovementLoopAudioWindow("Stranger HUNT acquire");
                if (UsesPerceptionAcquireRev && PullStartAudioCue.HasValue)
                {
                    PlayRandomBundyAudio(
                        PullStartAudioCue.Value,
                        ChainsawAudioVolume,
                        0.98f,
                        1.02f);
                }
            }
            return;
        }

        if (previous == StrangerPerceptionState.Hunt &&
            IsServer &&
            _movementLoopAudioRequested)
        {
            SetMovementLoopAudioRequested(true, forceUpdate: true);
        }
    }

    // #307. Where a SEARCH that ends a failed TRACK should open. His knowledge, when he has any -- his
    // own feet are the one position in the level he has already proved the player is not standing on.
    // The no-knowledge callers (perception init, TRACK with no last-known, the knockdown wipe) still
    // pass transform.position explicitly, because for them it is the only honest answer.
    private Vector3 GetStrangerSearchSeedPosition()
    {
        return _hasStrangerLastKnownPosition ? _strangerLastKnownPosition : transform.position;
    }

    private void BeginStrangerSearch(Vector3 center, string reason)
    {
        _strangerSearchCenter = ResolveSnapshotToNavMesh(center);
        _strangerSearchStartedAt = Time.time;
        _strangerSearchElevatorHeldSeconds = 0f;
        _strangerSearchRoamPointsRemaining = Mathf.Clamp(SearchRoamPointCount, 2, 4);
        _strangerSearchRoamBiasPending = true;
        _searchRoamPathlessSeconds = 0f;
        _nextStrangerPingAt = float.PositiveInfinity;
        _strangerLiveTargetUntil = float.NegativeInfinity;
        _strangerHuntHoldUntil = float.NegativeInfinity;
        // #307: _strangerRecentMemoryUntil is deliberately NOT zeroed here. Opening a search is him
        // admitting he cannot see the player, not him forgetting which way they went -- that clock and
        // the stored heading are exactly what the first roam point is seeded from below.

        // Was: write the search centre into _strangerLastKnownPosition. Most callers pass
        // transform.position, so that asserted "I think the target is exactly where I am standing"
        // every time a search opened -- the conflation at its most obviously wrong. The centre is a
        // destination, so it goes in the override and leaves his actual knowledge untouched.
        SetStrangerDestinationOverride(_strangerSearchCenter, "search-center");
        EndStrangerExitLurk();
        TransitionStrangerPerception(StrangerPerceptionState.Search, reason);
        TrySetNextStrangerSearchRoamPoint();
    }

    private bool TrySetNextStrangerSearchRoamPoint()
    {
        if (_strangerSearchRoamPointsRemaining <= 0 || !AgentReadyForMovement())
            return false;

        float baseRadius = Mathf.Max(2f, SearchRoamRadiusMeters);

        // #307. The annulus was a fixed 10m no matter how old the knowledge was, so a search opened on a
        // 40s-old estimate swept the same tiny circle as one opened a second after losing sight -- a
        // guaranteed miss dressed up as a search. Widen with the age of what he is searching FROM,
        // floored at the configured radius so this can only ever add coverage. Capped at 2.5x: past
        // that the annulus is thin enough that roaming it is worse than pinging.
        float knowledgeAge = Mathf.Max(0f, Time.time - _strangerLastKnownAt);
        float ageFraction = Mathf.Clamp01(knowledgeAge / Mathf.Max(1f, SearchWindowSeconds));
        float radius = baseRadius * Mathf.Lerp(1f, 2.5f, ageFraction);

        // The first point of a window is the only one worth aiming: it is sampled while the heading is
        // still fresh, and it is the one he actually walks to before the estimate has decayed. Later
        // points stay uniformly random, because by then the heading is older than the extrapolation cap
        // and a biased sweep would just be three guesses at the same wrong corridor.
        bool biasToHeading = _strangerSearchRoamBiasPending &&
            Time.time <= _strangerRecentMemoryUntil &&
            _strangerLastKnownVelocity.sqrMagnitude > 0.01f;
        float biasYaw = 0f;
        if (biasToHeading)
        {
            Vector3 heading = _strangerLastKnownVelocity;
            heading.y = 0f;
            if (heading.sqrMagnitude > 0.0001f)
                biasYaw = Quaternion.LookRotation(heading).eulerAngles.y;
            else
                biasToHeading = false;
        }

        // Two sampling passes, and the second is the point of this change. Pass 0 is the original: a
        // random point in the 0.35r-r annulus around the search centre. In tight interior geometry that
        // annulus can lie almost entirely inside walls, and when all twelve attempts missed, the roam
        // counter was zeroed -- ending the roam permanently, mid-window, leaving him standing still for
        // the remainder of SearchWindowSeconds. That is the "hollow SEARCH" symptom, and it was never
        // roam-point exhaustion: it was a sampling failure being mistaken for one.
        //
        // Pass 1 re-centres on his own position, which is on the navmesh by construction, drops the
        // inner bound so short hops qualify, and widens the sample radius. A short hop to somewhere
        // reachable is a strictly better search than standing still.
        for (int pass = 0; pass < 2; pass++)
        {
            Vector3 center = pass == 0 ? _strangerSearchCenter : transform.position;
            float minFraction = pass == 0 ? 0.35f : 0.15f;
            float sampleDistance = pass == 0 ? 4f : Mathf.Max(6f, radius * 0.75f);

            for (int attempt = 0; attempt < 12; attempt++)
            {
                // Biased attempts get the first half of pass 0 only. If a 90-degree wedge along the
                // heading is all walls, the remaining attempts fall back to the uniform annulus rather
                // than spending the whole budget on a direction the geometry has already refused.
                bool useBias = biasToHeading && pass == 0 && attempt < 6;
                float angle = useBias
                    ? biasYaw + UnityEngine.Random.Range(-45f, 45f)
                    : UnityEngine.Random.Range(0f, 360f);
                float distance = UnityEngine.Random.Range(radius * (useBias ? 0.5f : minFraction), radius);
                Vector3 offset = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * distance;
                Vector3 candidate = center + offset;
                if (!NavMesh.SamplePosition(candidate, out NavMeshHit hit, sampleDistance, NavMesh.AllAreas) ||
                    !TryCalculateCompletePath(hit.position, out _))
                {
                    continue;
                }

                _strangerSearchRoamPointsRemaining--;
                _strangerSearchRoamBiasPending = false;
                SetStrangerDestinationOverride(
                    hit.position,
                    pass == 0 ? "search-roam" : "search-roam-recentered");

                // Pass 0 succeeding is the ordinary case and stays silent -- it happens up to three
                // times per search window and says nothing. Pass 1 succeeding means pass 0 exhausted
                // all twelve attempts, which is the condition that used to end the roam outright, so
                // it is worth a line: it is the only positive evidence this fallback ever runs.
                if (pass == 1 && DebugLogs)
                {
                    LogInfo(BundyLogCategory.Perception, 0,
                        $"[BundyPerception] state=SEARCH reason=roam re-centered after failed annulus " +
                        $"(center={FormatVector(_strangerSearchCenter)} recenteredOn={FormatVector(transform.position)} " +
                        $"point={FormatVector(hit.position)} radius={radius:F1}m remaining={_strangerSearchRoamPointsRemaining})");
                }

                return true;
            }
        }

        // Both passes failed. Geometry that hostile is worth a line: it is the one remaining way SEARCH
        // can genuinely go hollow, and it now says so rather than looking like ordinary exhaustion.
        _strangerSearchRoamPointsRemaining = 0;
        _strangerSearchRoamBiasPending = false;
        if (DebugLogs)
        {
            LogInfo(BundyLogCategory.Perception, 0,
                $"[BundyPerception] state=SEARCH reason=roam sampling failed both passes " +
                $"(center={FormatVector(_strangerSearchCenter)} radius={radius:F1}m roam ended early)");
        }
        return false;
    }

    // SEARCH roam watchdog (#286). Suspending pings for the whole search window is deliberate --
    // BeginStrangerSearch parks _nextStrangerPingAt at +Infinity so roaming is deaf and hiding
    // actually works -- which means a roam point he can never reach costs him the entire window:
    // arrival never fires, the roam never advances or exhausts, and nothing restores the ping
    // clock. Field evidence: state=SEARCH destOwner=search-roam destDistance=6.1m with
    // hasPath=False pending=False, held past the 45s stall grace. TryCalculateCompletePath
    // validates the point when it is chosen, so what this catches is the route dying afterwards
    // (door state, an off-mesh link, a destination hijacked further down the movement stack).
    // It measures OBSERVED pathless time -- time accumulated on ticks where it actually ran and
    // actually saw no path -- not wall clock. UpdateStrangerPerception can be skipped for seconds at
    // a time by the early returns above it (agent not ready, smart-interaction hold, pending taunt,
    // no snapshot target), and several of those reset the path themselves, so a wall clock would
    // convert a suppressed stretch into an instant skip.
    private const float SearchRoamUnpathableSeconds = 2f;
    private const float SearchRoamWatchdogMaxTickSeconds = 0.5f;

    private bool MaybeSkipUnpathableSearchRoamPoint()
    {
        float now = Time.time;
        float observed = float.IsNegativeInfinity(_searchRoamWatchdogTickAt)
            ? 0f
            : Mathf.Clamp(now - _searchRoamWatchdogTickAt, 0f, SearchRoamWatchdogMaxTickSeconds);
        _searchRoamWatchdogTickAt = now;

        bool roamingToOverride =
            _hasStrangerDestinationOverride &&
            (_strangerDestinationOverrideKind == "search-roam" ||
             _strangerDestinationOverrideKind == "search-roam-recentered");

        // The parked tail of the window is NOT a stall. Once the roam points are spent, arrival at
        // the final one deliberately leaves the override standing and StopMovement holds him there
        // (no path, no pending request) until the window expires -- that park IS the deaf tail the
        // hiding design depends on. There is also nothing left to skip to, so the only thing the
        // watchdog could do here is end every search ~10s early. Both the counter and the arrival
        // range are checked, because the counter alone would still let the final approach fire.
        bool roamPointsSpent = _strangerSearchRoamPointsRemaining <= 0;
        bool parkedOnRoamPoint = roamingToOverride &&
            Vector3.Distance(transform.position, GetStrangerSnapshotDestination()) <=
                StopDistance + StrangerDestinationReachedPadding;

        // Legitimate no-path states that must not accumulate: an action owns the agent; the smart
        // path or the incremental fallback is mid-search and holding on purpose (both park the agent
        // with hasPath=false across several ticks); an elevator ride owns the agent and resets the
        // path every tick.
        bool resolutionFresh = now - _lastPursuitResolutionAt <= Mathf.Max(1f, AIIntervalTime * 3f);
        bool holdingForPendingPath = resolutionFresh &&
            (_lastPursuitResolution == PursuitResolutionKind.HoldSmartPending ||
             _lastPursuitResolution == PursuitResolutionKind.HoldFallbackPending);

        if (!roamingToOverride ||
            roamPointsSpent ||
            parkedOnRoamPoint ||
            holdingForPendingPath ||
            (_smartPathing != null && _smartPathing.ElevatorRideInProgress) ||
            IsActionLocked() ||
            !AgentReadyForMovement() ||
            agent.pathPending ||
            agent.hasPath)
        {
            _searchRoamPathlessSeconds = 0f;
            return false;
        }

        _searchRoamPathlessSeconds += observed;
        float pathlessFor = _searchRoamPathlessSeconds;
        if (pathlessFor < SearchRoamUnpathableSeconds)
            return false;

        _searchRoamPathlessSeconds = 0f;
        string abandonedKind = _strangerDestinationOverrideKind;
        float destinationDistance = Vector3.Distance(transform.position, GetStrangerSnapshotDestination());

        if (TrySetNextStrangerSearchRoamPoint())
        {
            LogWarning(
                $"[BundySearch] roam point ({abandonedKind}, {destinationDistance:F1}m away) had no path for " +
                $"{pathlessFor:F1}s -- treating it as unpathable and advancing " +
                $"(remaining={_strangerSearchRoamPointsRemaining}).");
            return true;
        }

        // Sampling failed on both passes, so there is no replacement point to walk to and the roam
        // is over. End the search the normal way rather than standing here for the rest of the
        // window: BeginStrangerPings is what restores _nextStrangerPingAt.
        LogWarning(
            $"[BundySearch] roam point ({abandonedKind}, {destinationDistance:F1}m away) had no path for " +
            $"{pathlessFor:F1}s and no replacement roam point could be sampled -- exhausting the search " +
            "early so pings resume.");
        BeginStrangerPings("search roam point unpathable and no replacement could be sampled");
        return true;
    }

    // #289 V4. Runs in place of the perception tick while a non-target interrupt holds. Deliberately
    // narrow: no state transitions (the interrupt owns the state machine's movement decisions), no
    // destination writes, no exit lurk. Only the PING state has a scheduler to keep alive -- SEARCH's
    // window and TRACK's watchdogs are wall-clock and resolve themselves on the tick after the
    // interrupt clears, and a SEARCH window is suspended-by-design anyway.
    private void UpdateStrangerPingSchedulerDuringInterrupt()
    {
        if (!_strangerPerceptionInitialized ||
            _strangerPerceptionState != StrangerPerceptionState.Ping ||
            Time.time < _nextStrangerPingAt)
        {
            return;
        }

        if (IsStrangerTargetShelteredInClosedShip())
        {
            _nextStrangerPingAt = Time.time + GetEffectiveStrangerPingInterval();
            return;
        }

        IssueStrangerPing(knowledgeOnly: true);
    }

    private void BeginStrangerPings(string reason)
    {
        TransitionStrangerPerception(StrangerPerceptionState.Ping, reason);
        _nextStrangerPingAt = Time.time;
        if (!IsStrangerTargetShelteredInClosedShip())
            IssueStrangerPing();
    }

    // knowledgeOnly: update the estimate and the ping clock, then stop. Used while a non-target
    // interrupt owns movement (#289 V4) so that his knowledge keeps ticking without the ping also
    // clearing the standing destination override or opening an exit lurk he cannot walk to yet.
    private void IssueStrangerPing(bool knowledgeOnly = false)
    {
        if (!IsLivingPlayer(_strangerTarget))
            return;

        float noiseMeters = GetEffectiveStrangerPingNoise();
        float angle = UnityEngine.Random.Range(0f, 360f);
        float radius = Mathf.Sqrt(UnityEngine.Random.value) * noiseMeters;
        Vector3 offset = Quaternion.Euler(0f, angle, 0f) * Vector3.forward * radius;
        Vector3 noisyPosition = _strangerTarget.transform.position + offset;
        _strangerLastKnownPosition = ResolveSnapshotToNavMesh(noisyPosition);
        _strangerLastKnownAt = Time.time;
        _hasStrangerLastKnownPosition = true;
        // #307: a ping is a fresh estimate with no heading attached, and it must not inherit one. A
        // velocity sampled from a sighting several ping intervals ago would extrapolate this guess along
        // a direction that expired long before it -- error stacked on error. The recent-memory clock is
        // deliberately left alone too: PING is already the "I have lost him" tier and re-arming a
        // pursuit-memory window from inside it would make the tier unreachable.
        _strangerLastKnownVelocity = Vector3.zero;
        _strangerPredictedPursuitAt = float.NegativeInfinity;
        _lastKnownPursuitPosition = _strangerLastKnownPosition;
        _hasLastKnownPursuitPosition = true;
        float interval = GetEffectiveStrangerPingInterval();
        _nextStrangerPingAt = Time.time + interval;

        // A lurk already under way owns the destination until its commitment expires. The ping still
        // refreshed his estimate above -- he is not blind while he waits -- but without this the next
        // ping would retarget him mid-walk every interval and he would never arrive at the door.
        //
        // Ordered ABOVE the knowledgeOnly return on purpose: the re-pin is not a destination decision,
        // it is repair of one this ping just broke. The write above moved the pursuit-search seed onto
        // the refreshed estimate, and a lurk that is still committed must keep its seed pointed at the
        // door he is walking to -- otherwise a non-target interrupt would leave the fallback node
        // search hunting a route to somewhere he has decided not to go.
        // #497: the commitment is what stops a ping retargeting him mid-walk, but it was unconditional,
        // so it also survived the disappearance of its own premise -- once the refreshed estimate put
        // the target well past the door he was waiting at, he kept waiting there for the rest of the
        // commit window. This is the break: the ping that proves the lurk irrelevant is the ping that
        // ends it, and the fall-through below then treats this like any ordinary ping.
        if (ShouldAbandonCommittedExitLurk())
        {
            if (DebugLogs)
            {
                LogInfo(BundyLogCategory.Perception, 0,
                    $"[BundyPerception] state=PING reason=exit lurk abandoned (target no longer heading here) " +
                    $"lurk={FormatVector(_strangerDestinationOverride)} " +
                    $"estimate={FormatVector(_strangerLastKnownPosition)} " +
                    $"estimateToLurk={PlanarDistance(_strangerDestinationOverride, _strangerLastKnownPosition):F1}m " +
                    $"entranceId={_strangerExitLurkEntranceId}");
            }

            _strangerExitLurkCommittedUntil = float.NegativeInfinity;
            _strangerExitLurkAllowedAt = Mathf.Min(
                _strangerExitLurkAllowedAt,
                Time.time + Mathf.Max(0f, ExitLurkCooldownSeconds));
            _strangerExitLurkEntranceId = -1;
        }

        if (Time.time < _strangerExitLurkCommittedUntil &&
            _hasStrangerDestinationOverride &&
            _strangerDestinationOverrideKind == "exit-lurk")
        {
            _lastKnownPursuitPosition = _strangerDestinationOverride;
            _hasLastKnownPursuitPosition = true;

            if (DebugLogs)
            {
                LogInfo(BundyLogCategory.Perception, 0,
                    $"[BundyPerception] state=PING reason=area ping (holding exit lurk) " +
                    $"destination={FormatVector(GetStrangerSnapshotDestination())} " +
                    $"estimate={FormatVector(_strangerLastKnownPosition)} noise={noiseMeters:F1}m " +
                    $"entranceId={_strangerExitLurkEntranceId} " +
                    $"commitLeft={Mathf.Max(0f, _strangerExitLurkCommittedUntil - Time.time):F1}s");
            }
            return;
        }

        // Everything past this point is a DESTINATION decision, and while a non-target interrupt owns
        // movement Bundy is not entitled to make one: he keeps the refreshed estimate and the ping
        // clock (both written above) and nothing else.
        if (knowledgeOnly)
        {
            if (DebugLogs)
            {
                LogInfo(BundyLogCategory.Perception, 1,
                    $"[BundyPerception] state=PING reason=area ping (knowledge only; non-target interrupt) " +
                    $"estimate={FormatVector(_strangerLastKnownPosition)} noise={noiseMeters:F1}m " +
                    $"interval={interval:F1}s");
            }
            return;
        }

        // Default is the pre-3c behaviour: walk at the estimate. The lurk gets to replace it, never to
        // stack alongside it.
        ClearStrangerDestinationOverride();
        if (TryTakeUpStrangerExitLurk(_strangerLastKnownPosition))
            return;

        if (DebugLogs)
        {
            LogInfo(BundyLogCategory.Perception, 0,
                $"[BundyPerception] state=PING reason=area ping " +
                $"destination={FormatVector(_strangerLastKnownPosition)} noise={noiseMeters:F1}m " +
                $"interval={interval:F1}s escalation={GetStrangerEscalationProgress():P0}");
        }
    }

    // 3c. The Mr. X move: instead of always walking at his own noisy guess of where the target is, go
    // and stand on the way out. This is PURE DESTINATION POLICY -- it grants no knowledge, no live
    // tracking and no HUNT entry, exactly as an ordinary ping does not. IssueStrangerPing still writes
    // his estimate into _strangerLastKnownPosition; the lurk only changes where his feet go, which is
    // precisely why the knowledge/destination split had to land before this could be written.
    //
    // On the "sixth destination source" worry from the 3c scoping notes: this is not one. It writes the
    // same _strangerDestinationOverride that SEARCH roam writes, and every consumer reads it through
    // the single GetStrangerSnapshotDestination(). It REPLACES the ping destination rather than
    // competing with it.
    //
    // Gates, ordered so the cheapest rejections happen first, following the 3b house style:
    //   1. ENABLED -- kill switch; with it off the behaviour is exactly pre-3c.
    //   2. COOLDOWN -- between lurks he must still sweep, or he degenerates into a door camper.
    //   3. SIDE -- only the entrance script on his own side of the boundary is walkable from here.
    //      TryFindAssignedStrangerBoundaryEntrance already owns the crossing case and is unusable for
    //      this one: it returns false unless he and the target are on OPPOSITE sides, which is the
    //      exact situation a lurk is not. Same enumeration, opposite purpose, so the approach solver
    //      is shared and the selection is not.
    //   4. RACE -- lurking only means something if he can plausibly get there first. Committing to a
    //      race he loses is indistinguishable from walking away from the player, which is the worst
    //      thing this feature could do.
    //   5. RANGE -- and even a winnable race is not worth crossing the map for.
    // #497: the abort test for a commitment already in flight. It is measured against the lurk
    // DESTINATION rather than the entrance transform because that point is the approach the solver
    // already resolved next to the door (within AssignedStrangerEntranceApproachSearchRadius of it), so
    // no entrance lookup is needed and the number is the one he is actually walking to.
    //
    // The threshold is the admission cap times ExitLurkAbandonDistanceFactor, deliberately looser than
    // admission: hysteresis. Breaking at exactly the admission cap would let one noisy ping either side
    // of the line start and stop a lurk on alternate intervals, which reads worse than either.
    //
    // A target who has crossed the boundary trips this too, and should: the planar distance between the
    // surface and the disjoint interior coordinates is not a distance, it is an enormous artefact, and
    // the lurk's same-side premise is what has just expired. Crossing is TryFindAssignedStrangerBoundaryEntrance's job.
    private bool ShouldAbandonCommittedExitLurk()
    {
        if (Time.time >= _strangerExitLurkCommittedUntil ||
            !_hasStrangerDestinationOverride ||
            _strangerDestinationOverrideKind != "exit-lurk" ||
            !_hasStrangerLastKnownPosition)
        {
            return false;
        }

        float breakDistance = Mathf.Max(5f, ExitLurkMaxTargetDistanceMeters) *
            Mathf.Max(1f, ExitLurkAbandonDistanceFactor);
        return PlanarDistance(_strangerDestinationOverride, _strangerLastKnownPosition) > breakDistance;
    }

    private bool TryTakeUpStrangerExitLurk(Vector3 estimatedTargetPosition)
    {
        if (!ExitLurkEnabled || !IsLivingPlayer(_strangerTarget))
            return false;

        if (Time.time < _strangerExitLurkAllowedAt)
            return false;

        // SAME SIDE, and this gate is not a nicety -- run 9 proved it. Lethal Company places the
        // facility interior at world coordinates completely disjoint from the surface, so a planar
        // distance measured from an outdoor door to a target standing inside is not a distance at all;
        // it is an artefact of that layout. Run 9 lurk 1 read targetToExit=383.5m and the race gate
        // waved it through, parking him at a surface fire exit for a full 20s while the player was deep
        // in the facility. Every number this function compares is only commensurable when both ends sit
        // on the same side of the boundary. When they do not, crossing it is the job, and
        // TryFindAssignedStrangerBoundaryEntrance already owns that.
        if (isOutside != !_strangerTarget.isInsideFactory)
            return false;

        EntranceTeleport[] entrances = GetCachedEntranceTeleports();
        if (entrances == null || entrances.Length == 0)
            return false;

        // An EntranceTeleport pair has one script per side, and isEntranceToBuilding marks the one
        // standing outdoors. He can only walk to the script on the side he is currently on.
        bool wantOutdoorSide = isOutside;
        float raceMargin = Mathf.Max(0.5f, ExitLurkRaceMargin);
        float maxPath = Mathf.Max(5f, ExitLurkMaxPathMeters);
        float maxTargetDistance = Mathf.Max(5f, ExitLurkMaxTargetDistanceMeters);

        // The approach solver sweeps eight radii by up to 28 angles, each with a NavMesh sample and a
        // full path calculation, so it is far too expensive to run per entrance on a 7s ping. Everything
        // that can be decided from straight-line distance is decided first, and only the best survivor
        // is resolved properly. Straight-line distance never exceeds path length, so a candidate
        // rejected here would have been rejected after resolving too.
        //
        // #497: this used to resolve the runner-up as well, which doubled the sweep to 250 synchronous
        // path calculations and is half of the 28-46ms DoAIInterval spikes. The runner-up bought very
        // little: it only mattered when the cheapest candidate failed to resolve or lost the real-path
        // race, and in that case not lurking at all is the correct and cheaper answer -- he goes back to
        // walking at his estimate, which is the behaviour the lurk is an optional improvement over. The
        // remaining single sweep is time-sliced inside the solver itself.
        EntranceTeleport best = null;
        float bestCheap = float.PositiveInfinity;

        foreach (EntranceTeleport candidate in entrances)
        {
            if (candidate == null || candidate.isEntranceToBuilding != wantOutdoorSide)
                continue;

            Vector3 entrancePoint = GetEntrancePoint(candidate);

            // Measured against his ESTIMATE, not a real position. The lurk inherits the guess's error,
            // which is the point: a lurk built on a bad guess should be a bad lurk, not a wallhack.
            //
            // Note the asymmetry in the race test below -- the target's side is straight-line while his
            // own is eventually a real path. Since a path is never shorter than the straight line, that
            // biases every comparison against him, so he only lurks when he is winning comfortably.
            // ExitLurkRaceMargin exists to buy that back; the default is above 1.0 for this reason.
            float targetDistance = PlanarDistance(entrancePoint, estimatedTargetPosition);
            float straightSelf = PlanarDistance(transform.position, entrancePoint);

            // RELEVANCE. An exit only cuts a route the target might actually take. The race gate alone
            // cannot express this: it is a ratio, so an absurdly distant exit satisfies it trivially by
            // being distant from everyone. The same-side gate above removes the pathological version of
            // this; the cap is the backstop for a merely bad one.
            if (targetDistance > maxTargetDistance)
                continue;
            if (straightSelf > maxPath || straightSelf > targetDistance * raceMargin)
                continue;

            float cheapScore = targetDistance + straightSelf * 0.5f;
            if (cheapScore < bestCheap)
            {
                best = candidate;
                bestCheap = cheapScore;
            }
        }

        return TryCommitStrangerExitLurk(best, estimatedTargetPosition, maxPath, raceMargin);
    }

    private bool TryCommitStrangerExitLurk(
        EntranceTeleport entrance,
        Vector3 estimatedTargetPosition,
        float maxPath,
        float raceMargin)
    {
        if (entrance == null)
            return false;

        Vector3 entrancePoint = GetEntrancePoint(entrance);
        if (!TryResolveAssignedStrangerEntranceApproach(
                entrancePoint, _strangerTarget, out Vector3 approach, out string approachMode))
        {
            return false;
        }

        if (!TryCalculateCompletePath(approach, out NavMeshPath path))
            return false;

        float selfPath = EstimatePathLength(path);
        float targetDistance = PlanarDistance(entrancePoint, estimatedTargetPosition);
        if (selfPath > maxPath || selfPath > targetDistance * raceMargin)
            return false;

        SetStrangerDestinationOverride(approach, "exit-lurk");
        _strangerExitLurkCommittedUntil = Time.time + Mathf.Max(0f, ExitLurkCommitSeconds);
        _strangerExitLurkAllowedAt = _strangerExitLurkCommittedUntil + Mathf.Max(0f, ExitLurkCooldownSeconds);
        _strangerExitLurkEntranceId = entrance.entranceId;

        if (DebugLogs)
        {
            LogInfo(BundyLogCategory.Perception, 0,
                $"[BundyPerception] state=PING reason=exit lurk " +
                $"destination={FormatVector(approach)} estimate={FormatVector(estimatedTargetPosition)} " +
                $"entranceId={entrance.entranceId} main={(entrance.entranceId == 0 ? "yes" : "no")} " +
                $"selfPath={selfPath:F1}m targetToExit={targetDistance:F1}m approach={approachMode} " +
                $"commit={Mathf.Max(0f, ExitLurkCommitSeconds):F0}s");
        }

        return true;
    }

    private float GetStrangerEscalationProgress()
    {
        float start = Mathf.Max(0f, StrangerEscalationStartSeconds);
        float duration = Mathf.Max(1f, StrangerEscalationDurationSeconds);
        return Mathf.Clamp01((_strangerEscalationElapsedSeconds - start) / duration);
    }

    private float GetEffectiveStrangerPingInterval()
    {
        float start = Mathf.Max(1f, StrangerPingIntervalSeconds);
        float end = Mathf.Clamp(EscalatedStrangerPingIntervalSeconds, 1f, start);
        return Mathf.Lerp(start, end, GetStrangerEscalationProgress());
    }

    private float GetEffectiveStrangerPingNoise()
    {
        float start = Mathf.Max(0f, PingPositionNoiseMeters);
        float end = Mathf.Clamp(EscalatedPingPositionNoiseMeters, 0f, start);
        return Mathf.Lerp(start, end, GetStrangerEscalationProgress());
    }

    private void WipeStrangerKnowledgeAfterKnockdown()
    {
        if (!_strangerTargetingActive || !IsLivingPlayer(_strangerTarget))
            return;

        if (_pursuitMode == PursuitMode.NonTargetInterrupt)
        {
            _pursuitZeroTarget = null;
            _pursuitZeroAttackAttentionUntil = float.NegativeInfinity;
            _pursuitMode = PursuitMode.PrimaryStalk;
            targetPlayer = _primaryTarget;
        }

        bool wasSearch = _strangerPerceptionState == StrangerPerceptionState.Search;

        // The wipe has to clear the ESTIMATE, not merely re-open a search. It never did: it relied on
        // BeginStrangerSearch happening not to read _strangerLastKnownPosition, which made it a wipe by
        // coincidence rather than by construction -- and a direct-LOS contact writes that field
        // unjittered, so any future reader of it would send him straight back to the exact spot the
        // player knocked him down from, inverting the whole point of the knockdown reward.
        //
        // Cleared BEFORE the search opens, so nothing downstream can observe the stale value. Checked
        // readers: TryPrepareStrangerSnapshotTarget still returns true via IsAssignedStrangerTarget, so
        // pursuit is not dropped; GetStrangerSnapshotDestination falls through to the search-centre
        // override set immediately below; the search-chatter interval correctly reads as "no recent
        // knowledge". _lastKnownPursuitPosition (the pursuit-search anchor) is deliberately NOT cleared
        // here -- clearing it would be inert anyway: the next movement tick re-anchors it on the
        // snapshot destination (Navigation.UpdateMovementDestination), which is now the search centre,
        // i.e. his own position. "Start looking from where you are" is exactly right after a knockdown.
        _hasStrangerLastKnownPosition = false;
        _strangerLastKnownPosition = Vector3.zero;
        _strangerLastKnownAt = float.NegativeInfinity;
        // #307: the heading and the pursuit-memory clock are part of the estimate, so a wipe that left
        // either standing would let the prediction tier reconstitute the very knowledge the knockdown
        // was supposed to cost him.
        _strangerLastKnownVelocity = Vector3.zero;
        _strangerRecentMemoryUntil = float.NegativeInfinity;
        _strangerPredictedPursuitAt = float.NegativeInfinity;

        BeginStrangerSearch(transform.position, "knockdown stagger wiped knowledge");
        if (wasSearch && DebugLogs)
            LogInfo(BundyLogCategory.Perception, 0, "[BundyPerception] state=SEARCH reason=knockdown stagger wiped knowledge");
    }

    private PlayerControllerB FindLivingPlayerNearPosition(Vector3 position, float range)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null)
            return null;

        PlayerControllerB closest = null;
        float closestDistance = Mathf.Max(0f, range);
        foreach (PlayerControllerB player in players)
        {
            if (!IsLivingPlayer(player))
                continue;

            float distance = Vector3.Distance(position, player.transform.position);
            if (distance <= closestDistance)
            {
                closest = player;
                closestDistance = distance;
            }
        }
        return closest;
    }

    private bool IsStrangerTargetShelteredInClosedShip()
    {
        return _strangerTargetingActive &&
            IsLivingPlayer(_strangerTarget) &&
            PlayerIsAtShip(_strangerTarget) &&
            StartOfRound.Instance != null &&
            StartOfRound.Instance.hangarDoorsClosed;
    }

    private bool TryHandleStrangerShipSiege()
    {
        if (!IsStrangerTargetShelteredInClosedShip() ||
            _strangerPerceptionState == StrangerPerceptionState.Hunt)
        {
            ExitStrangerShipSiegeFallback("target no longer sheltered");
            return false;
        }

        if (TryEnterShipSiege(_strangerTarget))
        {
            ExitStrangerShipSiegeFallback("battery siege took over");
            return true;
        }

        // Job 3a. Everything below used to be StopMovement() + MotionIdle: a Bundy standing perfectly
        // still outside the ship for as long as the player cared to wait, logging "holding for TODO
        // #127" at himself every five seconds. Closed hangar doors are a routine thing for a player to
        // do, and #127's real siege depends on ShipSystems resolving AND a battery target existing, so
        // the unsieged case is not an edge case — it is the default when either is missing. A blocked
        // enemy should read as blocked, not as broken.
        //
        // This is not a sixth competing destination source (see the movement-authority note in the
        // direction plan): the siege branch already owns the tick exclusively and already writes the
        // agent via DriveBatterySabotageDestination. This fills the one path through it that wrote
        // nothing at all.
        if (ShipSiegeFallbackEnabled)
        {
            if (TryRunStrangerShipSiegeFallback(_strangerTarget))
                return true;

            // No point near the sheltered target he can even stand on, so there is no door to work.
            // Decline the siege and let the normal pursuit chain drive him — a merely wrong
            // destination still beats a frozen one.
            ExitStrangerShipSiegeFallback("no reachable anchor");
            if (Time.time >= _nextStrangerShipSiegeLogAt)
            {
                _nextStrangerShipSiegeLogAt = Time.time + StrangerShipSiegeLogIntervalSeconds;
                LogInfo(BundyLogCategory.Siege, 0,
                    $"[BundySiege] fallback declined state={_strangerPerceptionState.ToString().ToUpperInvariant()}, " +
                    "reason=no reachable anchor near the sheltered target; handing back to normal pursuit");
            }
            return false;
        }

        ExitStrangerShipSiegeFallback("fallback disabled");
        StopMovement();
        SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
        if (Time.time >= _nextStrangerShipSiegeLogAt)
        {
            _nextStrangerShipSiegeLogAt = Time.time + StrangerShipSiegeLogIntervalSeconds;
            LogInfo(BundyLogCategory.Siege, 0,
                $"[BundySiege] state={_strangerPerceptionState.ToString().ToUpperInvariant()} " +
                "reason=target sheltered in closed ship; ShipSiegeFallbackEnabled=false, holding for TODO #127");
        }
        return true;
    }

    // Job 3a: prowl and pound instead of freezing. Runs only when the real siege could not start.
    // Returns false when no anchor can be established, which is the caller's cue to decline the siege
    // outright rather than substitute a different kind of standing still.
    private bool TryRunStrangerShipSiegeFallback(PlayerControllerB target)
    {
        // Something already owns the body — a stagger, an emote, a swing still finishing. Hold the siege
        // branch and let it play out; the prowl engages on the first free tick. Reported as still
        // running so the staleness watchdog does not tear the fallback down mid-stagger.
        if (IsActionLocked() || !AgentReadyForMovement())
        {
            if (_shipSiegeFallbackActive)
                _shipSiegeFallbackLastTickAt = Time.time;
            return true;
        }

        if (!IsLivingPlayer(target) || !TryResolveShipSiegeAnchor(target, out Vector3 anchor))
            return false;

        if (!_shipSiegeFallbackActive)
        {
            _shipSiegeFallbackActive = true;
            _shipSiegePoundCount = 0;
            _shipSiegePoundHoldUntil = float.NegativeInfinity;
            _shipSiegePoundDueSince = -1f;
            _nextShipSiegePoundAt = Time.time + ShipSiegePoundIntervalSeconds;
            _nextShipSiegeOrbitFlipAt = Time.time + ShipSiegeOrbitFlipSeconds;
            LogInfo(BundyLogCategory.Siege, 0,
                $"[BundySiege] fallback engaged state={_strangerPerceptionState.ToString().ToUpperInvariant()}, " +
                $"target={PlayerLabel(target)}, anchor={FormatVector(anchor)}, anchorMode={_shipSiegeAnchorMode}, " +
                $"distance={Vector3.Distance(transform.position, anchor):F2}m");
        }

        _shipSiegeFallbackLastTickAt = Time.time;

        // Mid-swing: stand and let the clip play. Same shape as the battery siege, which parks at the
        // socket and swings from a standstill — the swing pulse does not action-lock, so nothing else
        // holds him here.
        if (Time.time < _shipSiegePoundHoldUntil)
        {
            StopMovement();
            FaceActionPoint(anchor);
            SetMotionStateIfChanged(BundyAnimatorParams.MotionIdle);
            return true;
        }

        if (_batterySiegeSwingPoseActive)
            OnBatterySiegeAnimationStopped();

        float distance = Vector3.Distance(transform.position, anchor);
        float prowlRadius = Mathf.Max(1.5f, ShipSiegeProwlRadiusMeters);

        // The loop is orbit -> close -> swing -> back out. Swinging from anywhere inside the orbit
        // radius would have him hacking at air several metres short of the door, so a due pound first
        // drives him onto the anchor and only fires within reach of it.
        bool poundDue = !_batterySiegeSwingPoseActive && Time.time >= _nextShipSiegePoundAt;
        if (poundDue)
        {
            if (_shipSiegePoundDueSince < 0f)
                _shipSiegePoundDueSince = Time.time;

            bool inReach = distance <= Mathf.Max(BasicAttackRange, StopDistance + 0.5f);
            // If he cannot actually close on the anchor — it is across a railing, up a ledge, behind a
            // prop — waiting for reach forever is the freeze this whole job exists to remove. Swing from
            // wherever he got to instead; a swing at the ship from two metres out still reads.
            bool approachTimedOut = Time.time - _shipSiegePoundDueSince >= ShipSiegePoundApproachTimeoutSeconds;
            if ((inReach || approachTimedOut) && TryPoundShipSiegeAnchor(anchor))
                return true;
        }
        else
        {
            _shipSiegePoundDueSince = -1f;
        }

        DriveShipSiegeProwl(anchor, distance, prowlRadius, closeOnAnchor: poundDue);
        return true;
    }

    // Job 3a: where he works the ship. The nearest point he can actually stand that is closest to the
    // sheltered player IS the blocked approach — with the hangar doors shut that resolves to the door
    // mouth on its own, without this code knowing anything about ship geometry, and it stays correct on
    // modded ship layouts where a hard-coded door offset would not.
    private bool TryResolveShipSiegeAnchor(PlayerControllerB target, out Vector3 anchor)
    {
        anchor = _shipSiegeAnchor;
        Vector3 targetPosition = target.transform.position;

        // A failed sweep is rate-limited on the same clock as a successful one. Failure means the caller
        // declines the siege, and the declined state is re-entered on every tick — without this the
        // no-anchor case would run the full ~130-path sweep several times a second.
        if (!_hasShipSiegeAnchor && Time.time < _nextShipSiegeAnchorRevalidateAt)
            return false;

        if (_hasShipSiegeAnchor &&
            Vector3.Distance(targetPosition, _shipSiegeAnchorTargetPosition) <= ShipSiegeAnchorTargetMoveTolerance)
        {
            if (Time.time < _nextShipSiegeAnchorRevalidateAt)
                return true;

            _nextShipSiegeAnchorRevalidateAt = Time.time + ShipSiegeAnchorRevalidateSeconds;
            if (TryCalculateCompletePath(_shipSiegeAnchor, out _))
                return true;
        }

        _nextShipSiegeAnchorRevalidateAt = Time.time + ShipSiegeAnchorRevalidateSeconds;
        _shipSiegeAnchorTargetPosition = targetPosition;

        // #300: the approach sweep is now sliced across frames. A sweep still in flight is neither a hit
        // nor a miss, so the revalidate clock is rewound instead of burned -- otherwise the 4s back-off
        // would starve the sweep it just opened, and the second (near-ship) probe would restart the
        // cursor from under the first one. The near-ship probe only runs once the near-target probe has
        // given a definitive miss.
        ReachableNavPointSearchStatus targetStatus = AdvanceReachableNavPointSearch(
            targetPosition,
            target,
            out Vector3 nearTarget,
            out string targetMode);
        if (targetStatus == ReachableNavPointSearchStatus.Pending)
        {
            _nextShipSiegeAnchorRevalidateAt = Time.time;
            return false;
        }

        if (targetStatus == ReachableNavPointSearchStatus.Resolved)
        {
            _shipSiegeAnchor = nearTarget;
            _shipSiegeAnchorMode = "near-target:" + targetMode;
            _hasShipSiegeAnchor = true;
            anchor = _shipSiegeAnchor;
            return true;
        }

        // He may not have reached the landing pad yet, in which case nothing near the player is
        // reachable and the hull itself is the destination that makes him look like he is coming.
        Transform ship = StartOfRound.Instance != null ? StartOfRound.Instance.elevatorTransform : null;
        if (ship != null)
        {
            ReachableNavPointSearchStatus shipStatus = AdvanceReachableNavPointSearch(
                ship.position,
                target,
                out Vector3 nearShip,
                out string shipMode);
            if (shipStatus == ReachableNavPointSearchStatus.Pending)
            {
                _nextShipSiegeAnchorRevalidateAt = Time.time;
                return false;
            }

            if (shipStatus == ReachableNavPointSearchStatus.Resolved)
            {
                _shipSiegeAnchor = nearShip;
                _shipSiegeAnchorMode = "near-ship:" + shipMode;
                _hasShipSiegeAnchor = true;
                anchor = _shipSiegeAnchor;
                return true;
            }
        }

        _hasShipSiegeAnchor = false;
        _shipSiegeAnchorMode = "none";
        return false;
    }

    // Job 3a: the pound. Reuses the #127 battery swing pulse verbatim — same clip selection, same
    // wall-impact cue, same ClientRpc — because a saw swung at a ship fixture is the same event whether
    // or not a battery happens to be behind it. Nothing takes damage; this is a tell, and the sheltered
    // player is meant to hear it through the hull.
    private bool TryPoundShipSiegeAnchor(Vector3 anchor)
    {
        if (_batterySiegeSwingPoseActive || Time.time < _nextShipSiegePoundAt)
            return false;

        OnBatterySiegeSwingPulse(anchor);
        if (!_batterySiegeSwingPoseActive)
            return false; // a guard inside the pulse rejected it; retry on the next tick

        _shipSiegePoundCount++;
        _shipSiegePoundDueSince = -1f;
        float impactDelay = GetBasicAttackImpactDelay(_lastBatterySiegeSwingAttackIndex);
        _shipSiegePoundHoldUntil = Time.time + impactDelay + ShipSiegePoundRecoverSeconds;
        // Measured from the end of the swing, so the configured interval is prowl time rather than
        // prowl time minus a 1.2-1.6s clip.
        _nextShipSiegePoundAt = _shipSiegePoundHoldUntil + Mathf.Max(0.5f, ShipSiegePoundIntervalSeconds);
        LogInfo(BundyLogCategory.Siege, 0,
            $"[BundySiege] fallback pound n={_shipSiegePoundCount}, attackIndex={_lastBatterySiegeSwingAttackIndex}, " +
            $"anchor={FormatVector(anchor)}, impactDelay={impactDelay:F2}s");
        return true;
    }

    private void DriveShipSiegeProwl(Vector3 anchor, float distance, float prowlRadius, bool closeOnAnchor)
    {
        Vector3 destination;
        string phase;
        if (closeOnAnchor || distance > prowlRadius + ShipSiegeArrivePaddingMeters)
        {
            destination = anchor;
            phase = closeOnAnchor ? "close" : "approach";
        }
        else
        {
            if (Time.time >= _nextShipSiegeOrbitFlipAt)
            {
                _nextShipSiegeOrbitFlipAt = Time.time + ShipSiegeOrbitFlipSeconds;
                _shipSiegeOrbitDirection = -_shipSiegeOrbitDirection;
            }

            Vector3 fromAnchor = transform.position - anchor;
            fromAnchor.y = 0f;
            Vector3 outward = fromAnchor.sqrMagnitude > 0.01f ? fromAnchor.normalized : -transform.forward;
            Vector3 orbit = Quaternion.Euler(0f, ShipSiegeOrbitStepDegrees * _shipSiegeOrbitDirection, 0f) * outward;
            destination = TryResolveNavigableDestination(anchor + orbit * prowlRadius, out Vector3 navigable)
                ? navigable
                : anchor;
            phase = _shipSiegeOrbitDirection > 0 ? "prowl:left" : "prowl:right";
        }

        bool running = distance > RunThreshold;
        ApplyGaitSpeed(running);
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
        if (!UsesManualTargetFacing)
            agent.updateRotation = !ManualPursuitControlsAgentRotation;
        else
            FaceActionPoint(anchor);

        if (agent.isOnNavMesh)
            agent.isStopped = false;

        movingTowardsTargetPlayer = false;
        moveTowardsDestination = true;
        // Siege layer, not pursuit: the ship-siege prowl deliberately keeps its own destination
        // write (movement authority, plan step 8, scope boundary).
        SetDestinationToPosition(destination);
        SetLocalBehaviourState(BehaviourStateTracking);
        SetMotionStateIfChanged(running ? BundyAnimatorParams.MotionRun : BundyAnimatorParams.MotionWalk);

        if (Time.time >= _nextShipSiegeProwlLogAt)
        {
            _nextShipSiegeProwlLogAt = Time.time + ShipSiegeProwlLogIntervalSeconds;
            LogDiagnostic(BundyLogCategory.Siege, 2,
                $"[BundySiege] fallback {phase} distance={distance:F2}m, radius={prowlRadius:F2}m, " +
                $"destination={FormatVector(destination)}, nextPoundIn={Mathf.Max(0f, _nextShipSiegePoundAt - Time.time):F2}s");
        }
    }

    // Job 3a: several DoAIInterval branches return before the siege branch (NonTargetInterrupt, an
    // unusable snapshot target, the battery route taking over), so the fallback cannot rely on its own
    // branch to notice it has stopped running. Checked unconditionally each tick instead.
    private void MaybeExpireStrangerShipSiegeFallback()
    {
        if (!_shipSiegeFallbackActive)
            return;

        if (Time.time - _shipSiegeFallbackLastTickAt <= ShipSiegeFallbackStaleSeconds)
            return;

        ExitStrangerShipSiegeFallback("fallback branch no longer running");
    }

    private void ExitStrangerShipSiegeFallback(string reason)
    {
        if (!_shipSiegeFallbackActive)
            return;

        _shipSiegeFallbackActive = false;
        _hasShipSiegeAnchor = false;
        _shipSiegeAnchorMode = "none";
        _shipSiegePoundDueSince = -1f;
        _nextShipSiegeAnchorRevalidateAt = float.NegativeInfinity;

        // Only clear the swing pose if the fallback is the one holding it. The pose flag is shared with
        // the #127 battery swing, and the commonest exit reason is precisely that the battery route took
        // over — expiring on a stale tick would then cancel a swing that route had just started.
        bool ownsSwingPose = Time.time < _shipSiegePoundHoldUntil;
        _shipSiegePoundHoldUntil = float.NegativeInfinity;
        if (ownsSwingPose)
            OnBatterySiegeAnimationStopped();

        LogInfo(BundyLogCategory.Siege, 0,
            $"[BundySiege] fallback exited reason={reason}, pounds={_shipSiegePoundCount}");
    }

    internal bool IsActivelyPursuingStrangerTarget(PlayerControllerB target)
    {
        return _strangerTargetingActive &&
            IsLivingPlayer(target) &&
            target == _strangerTarget &&
            _strangerPerceptionState == StrangerPerceptionState.Hunt &&
            CanUseLiveHuntTarget(target);
    }

    private Vector3 ResolveSnapshotToNavMesh(Vector3 position)
    {
        if (NavMesh.SamplePosition(position, out NavMeshHit hit, NavigationTargetApproachSampleDistance, NavMesh.AllAreas))
            return hit.position;

        return position;
    }

    private bool TryPrepareStrangerSnapshotTarget()
    {
        if (!_strangerTargetingActive)
            return false;

        if (!IsLivingPlayer(_strangerTarget))
        {
            _primaryTarget = null;
            targetPlayer = null;
            _hasStrangerLastKnownPosition = false;
            ClearStrangerDestinationOverride();
            ResetStrangerExitLurk();
            _strangerPerceptionInitialized = false;
            _strangerLiveTargetUntil = float.NegativeInfinity;
            _strangerHuntHoldUntil = float.NegativeInfinity;
            // #307: the target itself is gone, so the estimate refers to nothing.
            _strangerRecentMemoryUntil = float.NegativeInfinity;
            _strangerLastKnownVelocity = Vector3.zero;
            return false;
        }

        _primaryTarget = _strangerTarget;
        targetPlayer = _strangerTarget;
        TryRefreshStrangerLiveTargetKnowledge(_strangerTarget);
        return _hasStrangerLastKnownPosition ||
            CanUseLiveHuntTarget(_strangerTarget) ||
            IsAssignedStrangerTarget(_strangerTarget);
    }

    private bool TryRefreshStrangerLiveTargetKnowledge(PlayerControllerB target)
    {
        if (!_strangerTargetingActive || !IsLivingPlayer(target) || target != _strangerTarget)
            return CanUseLiveHuntTarget(target);

        if (_actionKind == ActionKind.Stagger)
            return false;
        if (_pursuitMode == PursuitMode.NonTargetInterrupt)
        {
            // #307, same principle as #289 V4 above: knowledge is not a movement decision. The interrupt
            // owns the destination, the state machine and the commitment clocks, and it keeps all three
            // -- but a target walking past in plain sight while a teammate holds him used to leave no
            // trace at all, so the interrupt was a way of making him forget as well as of stalling him.
            RefreshStrangerKnowledgeDuringInterrupt(target);
            return false;
        }

        if (CanLocallyAcquireStrangerTarget(target))
            AcquireStrangerContact(target.transform.position, "direct LOS", routineSightRefresh: true);

        return CanUseLiveHuntTarget(target);
    }

    // #307. The knowledge half of AcquireStrangerContact and nothing else: the estimate, its heading,
    // its age and the recent-memory clock. Deliberately absent, because the interrupt owns them --
    // _strangerLiveTargetUntil and _strangerHuntHoldUntil (commitment), the HUNT transition and its
    // vocal (a state decision), and ClearStrangerDestinationOverride/EndStrangerExitLurk (destinations).
    // _lastKnownPursuitPosition is written for the same reason IssueStrangerPing writes it above its own
    // knowledgeOnly return: it is the fallback node search's anchor, and leaving it stale would point
    // that search at somewhere he has already been told the player is not.
    private void RefreshStrangerKnowledgeDuringInterrupt(PlayerControllerB target)
    {
        if (!CanLocallyAcquireStrangerTarget(target))
            return;

        Vector3 resolved = ResolveSnapshotToNavMesh(target.transform.position);
        UpdateStrangerLastKnownVelocity(resolved, StrangerContactQuality.Live);
        _strangerLastKnownPosition = resolved;
        _strangerLastKnownAt = Time.time;
        _hasStrangerLastKnownPosition = true;
        _strangerPredictedPursuitAt = float.NegativeInfinity;
        _strangerRecentMemoryUntil = Time.time + Mathf.Max(0f, PursuitMemorySeconds);
        _lastKnownPursuitPosition = _strangerLastKnownPosition;
        _hasLastKnownPursuitPosition = true;
    }

    private bool CanLocallyAcquireStrangerTarget(PlayerControllerB target)
    {
        if (!IsLivingPlayer(target))
            return false;

        float distance = Vector3.Distance(transform.position, target.transform.position);
        return HasLineOfSightToPlayer(target, Mathf.Max(2f, distance + 2f));
    }

    private bool CanUseLiveHuntTarget(PlayerControllerB target)
    {
        if (!IsLivingPlayer(target))
            return false;

        if (!_strangerTargetingActive || _pursuitMode == PursuitMode.NonTargetInterrupt)
            return true;

        if (target != _strangerTarget)
            return false;

        if (_actionTarget == target || _activeBasicVictim == target || _grabbedTarget == target)
            return true;

        return _strangerPerceptionState == StrangerPerceptionState.Hunt &&
            Time.time <= _strangerLiveTargetUntil;
    }

    private bool IsAssignedStrangerTarget(PlayerControllerB target)
    {
        return _strangerTargetingActive &&
            IsLivingPlayer(target) &&
            target == _strangerTarget;
    }

    private bool IsAssignedStrangerPursuitTarget(PlayerControllerB target)
    {
        return IsAssignedStrangerTarget(target) &&
            _pursuitMode != PursuitMode.NonTargetInterrupt;
    }

    private bool HasStrangerSnapshotDestination(PlayerControllerB target)
    {
        return _strangerTargetingActive &&
            target == _strangerTarget &&
            (_hasStrangerDestinationOverride || _hasStrangerLastKnownPosition);
    }

    // The single answer to "where is he actually walking". Every consumer downstream of the perception
    // states -- pursuit destination, the run/walk threshold, remaining distance, the boundary-route
    // logging -- reads through here, so an override cannot be honoured by one of them and missed by
    // another. That failure mode is the whole reason the movement-authority fix exists.
    private Vector3 GetStrangerSnapshotDestination()
    {
        if (_hasStrangerDestinationOverride)
            return _strangerDestinationOverride;

        // #307: the single point where the recent-memory tier reaches the movement stack. An override is
        // a deliberate destination (search centre, roam point, exit lurk) and outranks a prediction, so
        // this only ever changes the raw last-known case -- TRACK, and a HUNT whose live grace lapsed.
        // Nothing that decides an ATTACK comes through here; those read CanUseLiveHuntTarget, which is
        // still the strict live clock alone.
        return TryGetPredictedPursuitPosition(out Vector3 predicted)
            ? predicted
            : _strangerLastKnownPosition;
    }

    private void SetStrangerDestinationOverride(Vector3 destination, string kind)
    {
        _strangerDestinationOverride = destination;
        _hasStrangerDestinationOverride = true;
        _strangerDestinationOverrideKind = kind;
        _lastKnownPursuitPosition = destination;
        _hasLastKnownPursuitPosition = true;
    }

    private void ClearStrangerDestinationOverride()
    {
        _hasStrangerDestinationOverride = false;
        _strangerDestinationOverrideKind = "none";
    }

    // Ends the lurk in flight WITHOUT touching the cooldown. The cooldown is what paces lurking across
    // a round, and since PING is only ever entered from a search window expiring, resetting it here
    // would hand out a fresh lurk after every single search -- exactly the door-camper behaviour that
    // gate 2 exists to prevent.
    private void EndStrangerExitLurk()
    {
        _strangerExitLurkCommittedUntil = float.NegativeInfinity;
        _strangerExitLurkEntranceId = -1;
    }

    // Full reset, for when the target itself changes and the pacing no longer refers to anything.
    private void ResetStrangerExitLurk()
    {
        EndStrangerExitLurk();
        _strangerExitLurkAllowedAt = float.NegativeInfinity;
    }

    private bool ShouldUseStrangerSnapshotDestination(PlayerControllerB target)
    {
        return HasStrangerSnapshotDestination(target) &&
            !CanUseLiveHuntTarget(target);
    }

    private PlayerControllerB SelectRandomLivingPlayer(PlayerControllerB excluded)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null || players.Length == 0)
            return null;

        var candidates = new List<PlayerControllerB>();
        foreach (PlayerControllerB player in players)
        {
            if (IsReachableHuntTarget(player) && player != excluded)
                candidates.Add(player);
        }

        if (candidates.Count == 0)
            return null;

        return candidates[UnityEngine.Random.Range(0, candidates.Count)];
    }

    private PlayerControllerB FindNonTargetInterruptPlayer()
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null)
            return null;

        PlayerControllerB closest = null;
        float closestDistance = EffectiveNonTargetInterruptRange;
        foreach (PlayerControllerB player in players)
        {
            if (!IsReachableHuntTarget(player) || player == _primaryTarget)
                continue;

            float distance = Vector3.Distance(transform.position, player.transform.position);
            if (distance > closestDistance || !HasLineOfSightToPlayer(player, EffectiveNonTargetInterruptRange))
                continue;

            if (distance <= closestDistance)
            {
                closest = player;
                closestDistance = distance;
            }
        }

        return closest;
    }

    // #307. One linecast at head height made every crate, railing and desk edge a total occluder: the
    // head is the single most easily hidden point on a player, and a body two thirds visible read as
    // no contact at all. Three samples, any of which counts, and chest first because it is the point
    // most often clear -- the short-circuit means the common case still costs one cast.
    //
    // proximityAwareness was 0, which means "the cone is the only way to see", so a player who walked
    // round behind him while touching him was invisible. Vanilla's proximity term still requires the
    // linecast to be clear, so this is not omniscience: it is his own body being in the way of his own
    // 80-degree cone. The cone stays hardcoded; only the proximity radius is tunable.
    private const float StrangerSightChestHeight = 1f;
    private const float StrangerSightHeadFallbackHeight = 1.6f;
    private const float StrangerSightHipHeight = 0.4f;

    private bool HasLineOfSightToPlayer(PlayerControllerB player, float maxDistance)
    {
        if (!IsReachableHuntTarget(player))
            return false;

        int range = Mathf.CeilToInt(Mathf.Max(1f, maxDistance + 2f));
        float proximity = Mathf.Max(0f, StrangerSightProximityMeters);
        Vector3 feet = player.transform.position;
        Vector3 head = player.gameplayCamera != null
            ? player.gameplayCamera.transform.position
            : feet + Vector3.up * StrangerSightHeadFallbackHeight;

        return CheckLineOfSightForPosition(feet + Vector3.up * StrangerSightChestHeight, 80f, range, proximity) ||
            CheckLineOfSightForPosition(head, 80f, range, proximity) ||
            CheckLineOfSightForPosition(feet + Vector3.up * StrangerSightHipHeight, 80f, range, proximity);
    }

    private PlayerControllerB FindClosestLivingPlayer(float range)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null)
            return null;

        PlayerControllerB closest = null;
        float closestDistance = range;
        foreach (PlayerControllerB player in players)
        {
            if (!IsReachableHuntTarget(player))
                continue;

            float distance = Vector3.Distance(transform.position, player.transform.position);
            if (distance <= closestDistance)
            {
                closest = player;
                closestDistance = distance;
            }
        }

        return closest;
    }

    private int CountLivingPlayersNear(Vector3 position, float range)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null)
            return 0;

        int count = 0;
        foreach (PlayerControllerB player in players)
        {
            if (IsReachableHuntTarget(player) && Vector3.Distance(position, player.transform.position) <= range)
                count++;
        }

        return count;
    }

    private bool IsLivingPlayer(PlayerControllerB player)
    {
        return player != null
            && player.isPlayerControlled
            && !player.isPlayerDead
            && player.gameObject.activeInHierarchy;
    }

    private bool IsReachableHuntTarget(PlayerControllerB player)
    {
        return IsLivingPlayer(player);
    }

    private bool IsPlayerAtHalfHealthOrLower(PlayerControllerB player)
    {
        return IsLivingPlayer(player) && player.health <= 50;
    }

    private PlayerControllerB GetCurrentHuntTarget()
    {
        return _pursuitMode == PursuitMode.NonTargetInterrupt ? _pursuitZeroTarget : _primaryTarget;
    }

    // Locomotion-only head/upper-body look-at target for BundyChainsawHandIK.
    // Returns a world point (chest/head height of the chased player) ONLY while
    // freely chasing. Suppressed during any locked action (attack/finisher/emote/
    // contextual/stagger/pull) and during paired root locks, so the look-at IK
    // never fights authored attack/finisher facing or paired playables.
    // Does not modify any state and does not touch root rotation.
    internal bool TryGetChaseLookAtPoint(out Vector3 point)
    {
        point = Vector3.zero;

        if (_pairedActionRootLockActive || IsActionLocked())
            return false;

        // #319: turn clips are classified as locomotion by the IK solver, so without this
        // the head kept tracking through the whole turn and led the body around by up to
        // the full clamp — the "owl neck" read. Same suppression UpdateManualTargetFacing
        // applies to the root; the solver's blend gate fades the look-at out rather than
        // dropping it, so the head settles into the authored turn pose.
        //
        // The root lock is read from its fields rather than through
        // IsTurnClipRootLockActive(): that helper SNAPS transform.rotation and ends the
        // lock when it expires, and this method is called from the IK LateUpdate, where
        // the contract above (no state, no root rotation) has to hold.
        if (_turnClipRootLockActive && Time.time < _turnClipRootLockUntil)
            return false;
        if (IsAnimatorInTurnState())
            return false;

        PlayerControllerB target = GetCurrentHuntTarget();
        if (!IsReachableHuntTarget(target) || !CanUseLiveHuntTarget(target))
            return false;

        point = target.transform.position + Vector3.up * 1.45f;
        return true;
    }

    internal bool TryGetBasicAttackPropAimPoint(out Vector3 point)
    {
        point = Vector3.zero;

        if (_actionKind != ActionKind.Attack || _activeBasicVictimPaired)
            return false;

        PlayerControllerB target = _activeBasicVictim ?? _actionTarget ?? GetCurrentHuntTarget();
        if (!IsLivingPlayer(target))
            return false;

        point = target.transform.position + Vector3.up * 1.1f;
        return true;
    }

}
