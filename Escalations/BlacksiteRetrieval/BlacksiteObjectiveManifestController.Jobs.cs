using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Contracts.Payload;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteObjectiveManifestController
    {
        private readonly Dictionary<ulong, BlacksiteRaidJob> _jobs = new Dictionary<ulong, BlacksiteRaidJob>();
        private readonly Dictionary<(ulong, ulong), float> _operatorRetry = new Dictionary<(ulong, ulong), float>();
        private readonly Dictionary<ulong, Vector3> _approachTargets = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<ulong, ulong> _approachOwners = new Dictionary<ulong, ulong>();
        private readonly Dictionary<ulong, float> _approachRetry = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, string> _lastApproachRefusal = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, string> _closedApproachRefusal = new Dictionary<ulong, string>();
        private readonly Dictionary<ulong, Vector3> _supportPoints = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<ulong, Vector3> _supportCenters = new Dictionary<ulong, Vector3>();
        private readonly Dictionary<ulong, float> _supportUntil = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, Vector3> _patrolPoints = new Dictionary<ulong, Vector3>();
        // #1219 E1: each outside ship guard's duty state machine (BlacksitePatrolPolicy.Step) - the
        // waypoint it is on or walking to, its phase and its paused dwell. Replaces the old waypoint
        // index and dwell-end dictionaries.
        private readonly Dictionary<ulong, BlacksiteGuardDuty> _guardDuty = new Dictionary<ulong, BlacksiteGuardDuty>();
        // #874 S8.8: which rule produced the held post, for the [UAT][Job] issue line's source= key.
        // #874 Q6: patrol:<waypoint>/<count>, rendezvousProjected, unprojected or notDeployed.
        private readonly Dictionary<ulong, string> _patrolSource = new Dictionary<ulong, string>();
        internal string GuardPostSource(ulong member) => _patrolSource.TryGetValue(member, out var source) ? source : "none";
        // #1018 S4 (fix batch 2, S10): ONE copy of each rule, on the planner. Block 6 moved the
        // point sources onto BlacksiteSquadPlanner and the board's copies went; this forwarder is
        // the order planner's (and the native fixtures') way to the Guard duty's goal.
        // #874 Q6: the goal is the member's current patrol waypoint; the old ring's
        // TryGuardPatrolPoint forwarder had no caller left and is gone with the ring.

        private Vector3 GuardPatrolPoint(BlacksiteSoldierAI soldier) => Planner.GuardPatrolPoint(soldier);

        /// <summary>
        /// #874 Q4 (contract C1) / S3, #1219 C2: the Board order's destination - the passage link
        /// start (<c>Ship.BoardingLinkStart</c>): the ladder mount, or the rope foot (at the landing
        /// pose while a hover hull is away). Boarding IS the board crossing from there, so a member
        /// standing on the deck unboarded walks off through the drop link and boards by the ladder
        /// or rope. #1233 S3 (rule 2): the same goal in every world; from inside the route crosses
        /// on the way (#1305 G2), never a second, interior goal.
        /// </summary>
        private Vector3 BoardingApproach() => Ship.BoardingLinkStart;
        private bool CarriesPayload(ulong id)
        {
            foreach (var r in Records) if (r.CarrierId == id && r.Kind == BlacksiteObjectiveKind.Payload && r.Phase == BlacksiteObjectivePhase.Carried) return true;
            return false;
        }
        private readonly Dictionary<ulong, float> _jobObserveAt = new Dictionary<ulong, float>();
        // #1305 G4: the cart lease judge's N is BlacksiteMotionMonitor.WindowSeconds / ObserveSeconds and derived from nothing else.
        private const float ObserveSeconds = .5f;
        private bool ObserveNow(ulong objectId)
        {
            if (_jobObserveAt.TryGetValue(objectId, out float next) && Time.time < next) return false;
            _jobObserveAt[objectId] = Time.time + ObserveSeconds; return true;
        }
        private readonly Dictionary<ulong, BlacksiteObjectiveAttempts> _attempts = new Dictionary<ulong, BlacksiteObjectiveAttempts>();
        private BlacksiteObjectiveAttempts Attempts(ulong objectId)
        { if (!_attempts.TryGetValue(objectId, out var history)) _attempts[objectId] = history = new BlacksiteObjectiveAttempts(); return history; }
        // Observation accessor only: it never binds an owner, so a released attempt can never hand
        // its expired clock to the next owner. Reservation points call BeginAttempt explicitly.
        private BlacksiteRaidJob Job(BlacksiteObjectiveSnapshot record)
        {
            if (!_jobs.TryGetValue(record.ObjectId, out var job)) _jobs.Add(record.ObjectId, job = new BlacksiteRaidJob());
            // Defensive: an observed record with no open attempt means a release point was missed.
            // It opens through the manifest's own BeginAttempt so the holder motion track is
            // cleared here too: a re-open is a new attempt, and no holder may be judged against
            // samples taken from whoever held the record before it.
            if (!job.AttemptOpen)
            {
                BeginAttempt(record, record.CarrierId);
                BlacksiteDiagnostics.Log("Job", $"object={record.ObjectId}, observed with no open attempt; opened attempt={job.Generation}.");
            }
            return job;
        }
        private void BeginAttempt(BlacksiteObjectiveSnapshot record, ulong owner)
        {
            if (!_jobs.TryGetValue(record.ObjectId, out var job)) _jobs.Add(record.ObjectId, job = new BlacksiteRaidJob());
            job.BeginAttempt(owner, Time.time);
            // #874 S6.4a: a new attempt starts with an empty motion track, so no holder is judged
            // against the previous holder's samples.
            _holderTrack.Remove(record.ObjectId); _holderRoutes.Remove(record.ObjectId);
        }
        // Every abandoned or completed attempt is closed here, before any new assignment begins.
        // Only an attempt that was actually open and that the squad itself failed is charged to
        // the objective; an interruption finishes and clears the attempt without escalating it.
        private BlacksiteObjectiveAttempts CloseAttempt(ulong objectId, BlacksiteAttemptOutcome outcome)
        {
            var history = Attempts(objectId);
            bool open = _jobs.TryGetValue(objectId, out var job) && job.AttemptOpen;
            if (!open && outcome == BlacksiteAttemptOutcome.Failed) outcome = BlacksiteAttemptOutcome.Interrupted;
            if (open) job.Finish(outcome == BlacksiteAttemptOutcome.Completed);
            history.Close(outcome);
            ClearAttemptState(objectId);
            return history;
        }
        // #874 S6.1a: EVERY attempt close emits [UAT][Objective] event=closed with a named cause,
        // not only the ReleaseJob path. Six direct CloseAttempt sites closed attempts silently on
        // 2026-09-09, which is why an [UAT][Objective] grep showed six reservations into a void.
        // ReleaseJob and Drop still call the state form and log afterwards, because their line has
        // to report the record phase and retry they are about to write.
        private BlacksiteObjectiveAttempts CloseAttempt(BlacksiteObjectiveSnapshot record,
            BlacksiteAttemptOutcome outcome, BlacksiteObjectiveCloseCause cause)
        {
            _jobs.TryGetValue(record.ObjectId, out var job);
            bool open = job != null && job.AttemptOpen;
            int generation = job != null ? job.Generation : 0;
            var owner = Soldier(record.CarrierId);
            var history = CloseAttempt(record.ObjectId, outcome);
            // Only an attempt that was actually open produced a `reserved` line to pair with.
            if (open) LogObjectiveClosed(record, owner, job, generation, outcome, cause, history);
            return history;
        }
        private void ClearAttemptState(ulong objectId)
        {
            // #950 S11H U2: event=closed is written AFTER the close, so the standing point the
            // attempt actually had has to survive the clear. Absent means the approach resolver
            // never published a point for this attempt, which is the fact standingPoint= prints; the
            // holder's reach verdict then routes to the navmesh on the object's support floor. A value here
            // is never a destination; it is a record of one that was already published.
            _closedStandingPoint[objectId] = _pickupApproaches.TryGetValue(objectId, out var standing)
                ? standing : (Vector3?)null;
            _closedApproachRefusal[objectId] =
                _lastApproachRefusal.TryGetValue(objectId, out string refusal) && !string.IsNullOrWhiteSpace(refusal)
                    ? refusal : BlacksiteDiagnosticFormat.None;
            _pickupAt.Remove(objectId); _pickupApproaches.Remove(objectId); _approachTargets.Remove(objectId);
            // #874 F06: _approachBarrier dies with the attempt too. It was the one member of this
            // group that survived a close, so the next attempt's first resolution was measured
            // against the barrier the PREVIOUS attempt had last seen.
            _approachOwners.Remove(objectId); _approachRetry.Remove(objectId); _approachBarrier.Remove(objectId);
            _lastApproachRefusal.Remove(objectId); _handleAnswers.Remove(objectId);
            _pickupProbeFailures.Remove(objectId);
            // #1016 S2: the claim's leases die with the attempt, in the same statement as the three
            // parallel dictionaries that were the old bookkeeping. Before S2 nothing released the
            // objective claim at all - it was the record, and the record was freed by whichever of
            // eight paths happened to run - so a carrier that died still read as the holder to
            // everything that asked the manifest rather than the roster.
            BlacksiteResourceBroker closing = Broker;
            if (closing != null)
            {
                // ClearAttemptState knows the record, not the member, so the holder is read off the
                // lease before it goes. Forget drops the record's own lane outright - the attempt
                // is over, so nothing may still be standing in it - while the shared board is
                // released only for THIS carrier, because another record may legitimately hold it.
                //
                // #1016 S2 review batch 2, F5: the board release does NOT depend on the claim lane
                // still having a holder. A close can arrive with the Objective lease already gone -
                // #1293 R2: the ledger's stall answer releases every lease the stalled member holds
                // before the manifest closes the attempt - so keying the board release on
                // TryGetHolder could leave the board held by a carrier whose attempt was over. The
                // record's own CarrierId is the second source, and both are released because
                // either may be the live one.
                BlacksiteResourceKey claim = BlacksiteResourceKey.Objective(objectId);
                BlacksiteResourceKey boardKey = BlacksiteResourceKey.ObjectiveBoard();
                ulong released = 0UL;
                if (closing.TryGetHolder(claim, out BlacksiteResourceLease held) && held.Holder != 0UL)
                { released = held.Holder; closing.Release(boardKey, released); }
                for (int i = 0; i < Records.Count; i++)
                {
                    if (Records[i].ObjectId != objectId) continue;
                    ulong carrierId = Records[i].CarrierId;
                    if (carrierId != NoCarrier && carrierId != released) closing.Release(boardKey, carrierId);
                    break;
                }
                closing.Forget(claim);
            }
            // #874 S6.1/S6.4a: the per-attempt observation state dies with the attempt.
            _jobGoals.Remove(objectId); _jobGoalCrossings.Remove(objectId);
            _retargetLogAt.Remove(objectId); _jobGoalTargets.Remove(objectId); _jobBarriers.Remove(objectId);
            _witnessPointLogAt.Remove(objectId);
            // _holderTrack and _holderRoutes are NOT cleared here: event=closed is written after
            // the close and reports movedLast15s/closingLast10s from them. They are cleared when
            // the next attempt opens instead, so no holder is ever measured against its
            // predecessor's samples.
        }
        // #874 S6.4a: the same measurement, with the completeness the caller has to know about.
        // An incomplete route's length is its straight line - the operator score, not this
        // function, decides what an incomplete route costs. #1262 P1: the route is the route service's.
        private static float RouteDistance(Vector3 from, Vector3 goal, out bool complete)
        {
            BlacksiteRouteVerdict route = BlacksiteReachability.Route(BlacksiteTraveller.Soldier(false), from, goal);
            complete = route.Reachable;
            return complete ? route.Distance : Vector3.Distance(from, goal);
        }
        // The reporting form: the service's routed metres, NaN (printed none) with no route.
        private static float RouteDistance(Vector3 from, Vector3 goal) => BlacksiteReachability.Route(BlacksiteTraveller.Soldier(false), from, goal).ReportedRemaining;
        private static string RouteText(float metres) => float.IsNaN(metres) ? BlacksiteDiagnosticFormat.None : metres.ToString("0.00");
        /// <summary>
        /// The board's election. It keeps this exact name and this exact one signature: the
        /// BackupChecks fixture resolves it by name alone (AccessTools.Method(type,
        /// "SelectOperator")), which an overload set would make ambiguous, so the scored form
        /// below is a differently named method rather than a second SelectOperator.
        /// </summary>
        private BlacksiteSoldierAI SelectOperator(BlacksiteObjectiveSnapshot record, IEnumerable<BlacksiteSoldierAI> candidates)
            => BestOperator(record, candidates, out _);
        /// <summary>
        /// #874 F06: the same election, with the winning SCORE handed back.
        ///
        /// <see cref="ShouldTransfer"/> has to compare the challenger it is about to hand the
        /// record to against the member already holding it, and the only honest comparison is the
        /// board's own. The score is returned rather than re-derived in the caller so the two can
        /// never disagree - the same reason ObjectiveAttemptIndex has one reader.
        /// </summary>
        private BlacksiteSoldierAI BestOperator(BlacksiteObjectiveSnapshot record,
            IEnumerable<BlacksiteSoldierAI> candidates, out float bestScore)
        {
            BlacksiteSoldierAI best = null; bestScore = float.PositiveInfinity; bool owedHome = Returning && CargoOwed(record);
            // #874/#1054: the cart's own admission, resolved ONCE for the record rather than per
            // candidate. The cart refuses the handle while it is still standing in a pose whose
            // step it has measured as impossible, and until this the board could not see that: it
            // picked a fresh operator, walked him the length of the interior and had the attach
            // refused on arrival, then picked the next one. The refusal and every way out of it
            // stay the CART's (PayloadCartController.CanReserveNpc) - there is no second clock
            // here, and no operator is burned against geometry nothing has changed about.
            var cart = record.Kind == BlacksiteObjectiveKind.Payload
                ? Resolve(record.ObjectId)?.GetComponent<PayloadCartController>() : null;
            foreach (var soldier in candidates)
            {
                // A guard called in to a damage or casualty report is controlled by that response,
                // so it is not an eligible operator until the response ends. IsShipGuard is false
                // for exactly the duration of the call, which is why this is a separate test.
                if (soldier == null || soldier.isEnemyDead || soldier.IsShipGuard || GuardCalledIn(soldier.NetworkObjectId)
                    || (soldier.IsRaidWithdrawing && !owedHome) // #1305 G4: after the recall he may haul owed cargo home
                    || soldier.HasLostTransport || soldier.ReplicatedStunned || soldier.HasBoardedShip || soldier.IsObjectiveCombatActive) continue;
                if (HasPlayerResponse(soldier)) continue;
                if (_operatorRetry.TryGetValue((record.ObjectId, soldier.NetworkObjectId), out float retry) && Time.time < retry) continue;
                if (cart != null && !cart.CanReserveNpc(soldier)) continue;
                float score = OperatorScoreOf(record, soldier);
                if (score < bestScore) { best = soldier; bestScore = score; }
            }
            return best;
        }
        /// <summary>
        /// #874 S6.4a item 7, factored out: how far this member is from being able to work this
        /// record, in the board's own units.
        /// </summary>
        /// <remarks>
        /// Eligibility is deliberately NOT part of it. The four exclusions in the loop above stay
        /// exclusions, so a member the cart, a player response, a bench or its own survival refuses
        /// is never scored at all rather than being scored badly - a candidate the board must not
        /// take has no place in an ordering, whatever number it would receive.
        /// </remarks>
        private float OperatorScoreOf(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI soldier)
        {
            bool sameWorld = soldier.isOutside == ObjectiveWorld(record);
            Vector3 goal = sameWorld ? record.Position : soldier.isOutside ? _mission.SquadDirector.OutsideMainEntrance : _mission.SquadDirector.InsideMainEntrance;
            // #874 S6.4a: a candidate that can actually get there always beats one that cannot.
            float length = RouteDistance(soldier.transform.position, goal, out bool complete);
            return BlacksiteObjectivePolicy.OperatorScore(complete, length, sameWorld);
        }
        /// <summary>
        /// #874 F06: a handover, not a re-election of the man who is already doing the job.
        /// </summary>
        /// <remarks>
        /// On 2026-09-21 this closed the Shadow Drive attempt ten times in ninety-six seconds and
        /// handed it straight back to the same member every time. Member 2 held an INSIDE
        /// reservation while parked in the ship passage queue outside (stallOwner=hold:passage,
        /// movedMetresLast15s=0.00 from t=804.07 to t=900.23); eight seconds after each reservation
        /// this found some other member who was already inside, closed the attempt
        /// Interrupted/Reassigned - which by design does not bench the holder and re-arms the record
        /// one second later - and <c>AssignReservations</c> then re-elected member 2, because the
        /// board's own ranking still put him first. Every one of those ten event=closed lines reads
        /// nextOwner=2, and each close wiped the attempt's clock, its wait milestones, its
        /// best-remaining watermark and its holder motion track, so the 45 s wait cap, the 15 s
        /// immobility rule and the unreachable rule could none of them ever reach their window.
        ///
        /// The two questions were asked of different candidate sets and never compared. This one
        /// now asks the board's question: the challenger must be one the board could actually
        /// assign - not already holding a record and not fighting for its own footing, which are
        /// exactly the two terms <c>AssignReservations</c> adds to its free set and this test never
        /// had - and it must BEAT the incumbent under the same <see cref="OperatorScoreOf"/> the
        /// board ranks with, by the job's own progress epsilon so two members a few centimetres
        /// apart cannot trade the record back and forth. #1293 R2: and not stranded, the free set's
        /// third term.
        ///
        /// When nothing better exists the holder keeps the attempt. #874 S4: what bounds it then is
        /// the ledger's stalled answer, not a clock here.
        /// </remarks>
        private bool ShouldTransfer(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI owner)
        {
            var job = Job(record);
            bool recordOutside = ObjectiveWorld(record);
            if (owner.isOutside == recordOutside || Time.time - job.AssignedAt < 8f || owner.HasCommittedJobTraversal) return false;
            var challenger = BestOperator(record,
                _members.Where(s => s != owner && s.isOutside == recordOutside
                    && !IsCarrier(s.NetworkObjectId) && !IsOccupiedByOwnSurvival(s) && !s.Navigator.IsStranded),
                out float challengerScore);
            // The holder's own score costs a route query, so it is only asked once there is
            // something to compare it with: with no eligible challenger the record stays where it
            // is whatever the number would have been.
            float holderScore = challenger != null ? OperatorScoreOf(record, owner) : float.NaN;
            bool better = challenger != null
                && challengerScore < holderScore - BlacksiteObjectivePolicy.HolderClosingEpsilonMetres;
            if (!better) NoteTransferDeclined(record, owner, job, challenger, challengerScore, holderScore);
            return better;
        }
        /// <summary>
        /// #874 F06: why the record stayed with its holder, on the channel the live run already
        /// reads.
        /// </summary>
        /// <remarks>
        /// The 2026-09-21 log can say that member 2 was reassigned ten times and cannot say why any
        /// of the waits happened; the reason lived only on the [UAT][Motion] line's stallOwner=
        /// field, which nothing correlates with the objective. This is a host-side observation of
        /// host-side state written to the existing [UAT][Job] channel - no new record field,
        /// nothing replicated, and nothing a client can
        /// see. Rate limited per object so a holder waiting out a crossing prints once every two
        /// seconds rather than once per manifest tick.
        /// </remarks>
        private void NoteTransferDeclined(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI owner,
            BlacksiteRaidJob job, BlacksiteSoldierAI challenger, float challengerScore, float holderScore)
        {
            if (!BlacksiteDiagnostics.Enabled) return;
            BlacksiteDiagnostics.LogRateLimited($"job.transfer.{record.ObjectId}", 2f, "Job",
                $"object={record.ObjectId}, member={owner.MemberIndex}, attempt={job.Generation}"
                + ", event=transferDeclined"
                + $", reason={(challenger == null ? "no assignable operator in the target world" : "no operator the board would rank ahead of the holder")}"
                + $", challenger={(challenger != null ? challenger.MemberIndex.ToString() : BlacksiteDiagnosticFormat.None)}"
                + $", challengerScore={(challenger != null ? challengerScore.ToString("0.00") : BlacksiteDiagnosticFormat.None)}"
                + $", holderScore={(challenger != null ? holderScore.ToString("0.00") : BlacksiteDiagnosticFormat.None)}"
                + $", margin={BlacksiteObjectivePolicy.HolderClosingEpsilonMetres:0.00}"
                + $", phase={job.Phase}, sinceProgress={Time.time - job.LastProgressAt:0.00}.");
        }
        // #874 S4.0a: the goal kind a member was actually walking to, and whether the thing that
        // has to be open is open. Both are read-only lookups of live state, and both are what the
        // 2026-09-09 log could not answer for the six Shadow Drive releases: RC5's sealed-container
        // goal is invisible unless the line says the goal was a container lock.
        private string ObjectiveGoalKind(BlacksiteObjectiveSnapshot record, out string containerOpen)
        {
            containerOpen = "none";
            if (record.Kind == BlacksiteObjectiveKind.Payload) return "Handle";
            // #964 S14 U2: a new closed-vocabulary value on the existing goalKind= key, so
            // "the squad was walking to the witness" is greppable in event=closed and event=arrived
            // exactly the way "Handle" and "ContainerLock" already are. No new line shape.
            if (record.Kind == BlacksiteObjectiveKind.Witness) return "Witness";
            if (record.Kind != BlacksiteObjectiveKind.ShadowDrive) return "Item";
            var container = BreachContainerController.FindByNetworkId(_mission.ExpectedTargetNetworkId);
            var cache = Resolve(_mission.SecondaryTargetNetworkId)?.GetComponent<ShadowRaidCargoCrate>();
            // #874 F06: containerOpen reports the barrier that is actually in the way, not always
            // the shipping container. The drive sits behind TWO barriers in series, and on
            // 2026-09-21 attempt 12 closed at t=1030.21 printing goalKind=ContainerLock beside
            // containerOpen=True - self-contradictory, and unreadable as evidence - because the
            // container had opened 0.69 s earlier while the internal cache was still shut and this
            // reported the container's state regardless. goalKind is unchanged, and so is the
            // closed True/False/none vocabulary every score script parses; the two keys simply now
            // describe the same object.
            if (container != null && !container.IsOpen) containerOpen = "False";
            else if (cache != null && !cache.Opened) containerOpen = "False";
            else if (container != null) containerOpen = container.IsOpen.ToString();
            else if (cache != null) containerOpen = cache.Opened.ToString();
            return (container != null && !container.IsOpen) || (cache != null && !cache.Opened)
                ? "ContainerLock" : "Item";
        }
        // #1018 S4 (fix batch 2, S10, design note G10 b): an index loop. This was
        // `_members.Count(lambda)` - a closure and a boxed predicate - read inside the objective
        // half's per-record work. Same predicate, same order, nothing on the heap.
        private int AliveRaiders
        {
            get
            {
                int alive = 0;
                for (int i = 0; i < _members.Count; i++)
                {
                    var member = _members[i];
                    if (member != null && !member.IsShipGuard && !member.isEnemyDead) alive++;
                }
                return alive;
            }
        }
        private float MissionSeconds => _startedAt < 0 ? -1f : Time.time - _startedAt;

        // #950 S11H U2 / #874 S2: the unreachable rule's own state, per object.
        //
        // The 2026-09-10 March squad chased a Shadow Drive that really was at a point in rock 42 m
        // outside its own dungeon. Every attempt closed on the travel budget, the record re-armed
        // by S6.5a design, and the mission spent 295 s on six reservations with a closest approach
        // of 42.1 m.
        //
        // _closedStandingPoint is the standing point an attempt had, captured in ClearAttemptState
        // before the attempt state dies; event=closed prints it as standingPoint=.
        private readonly Dictionary<ulong, Vector3?> _closedStandingPoint = new Dictionary<ulong, Vector3?>();

        /// <summary>The standing point the just-closed attempt had, or none.</summary>
        private Vector3? ClosedStandingPoint(ulong objectId)
            => _closedStandingPoint.TryGetValue(objectId, out var point) ? point : null;
        /// <summary>The last approach ring refusal for the just-closed attempt, or none.</summary>
        private string ClosedApproachRefusal(ulong objectId)
            => _closedApproachRefusal.TryGetValue(objectId, out string refusal)
                ? refusal : BlacksiteDiagnosticFormat.None;

        /// <summary>
        /// #874 S2 / #1305 G1 (R15): the point a reachability query starts from - where the route
        /// service's route from the main entrance of the point's world arrives on the point's own
        /// island (the start of its last walk leg: the car's landing on the point's storey, a door's
        /// far side), else that entrance, sampled onto the navmesh when it can be.
        /// </summary>
        internal Vector3 ReachabilityOrigin(Vector3 point, bool outside)
        {
            Vector3 origin = outside ? _mission.SquadDirector.OutsideMainEntrance : _mission.SquadDirector.InsideMainEntrance;
            if (NavMesh.SamplePosition(origin, out NavMeshHit nav, BlacksiteObjectiveApproach.SampleRadius, NavMesh.AllAreas)) origin = nav.position;
            BlacksiteRouteVerdict route = BlacksiteReachability.Route(BlacksiteTraveller.Soldier(outside), origin, point);
            return route.Planned ? new Vector3(route.EntryX, route.EntryY, route.EntryZ) : origin;
        }

        /// <summary>
        /// #1305 C4 (R11) / G1 (R19): the unreachable rule's one route question, asked of the traveller -
        /// the releasing holder's own route from where he stands to the record's standing point (the
        /// open attempt's published one, else the navmesh on the object's support floor, the pickup ring's
        /// centre), on the route service in the objective's world (an outdoor leg under the water is
        /// refused like a member's). Only ReleaseJob asks it, for a holder in the objective's world,
        /// and the verdict with the objective's failures decides (WithdrawalCause). It used to be
        /// asked from the tile's entrance after two counted closes: on 2026-10-03 Burrow (LogOutput.log
        /// 21625-21631) two closes by a holder stalled OUTSIDE wrote the drive off on that probe while
        /// two raiders inside had complete paths to it.
        /// </summary>
        private BlacksiteRouteVerdict HolderReach(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI holder, out Vector3? standing)
        {
            standing = _pickupApproaches.TryGetValue(record.ObjectId, out var published) ? published : (Vector3?)null;
            if (!standing.HasValue && NavMesh.SamplePosition(SupportFloor(record), out NavMeshHit sampled,
                    BlacksiteObjectiveApproach.SampleRadius, NavMesh.AllAreas))
                standing = sampled.position;
            // #1305 E2 (R11): no standing point on the support floor is no route there, so it is the
            // holder's verdict. On 2026-10-03 Divide the apparatus stood 2.41 m above its floor
            // (LogOutput.log 14137 centre y -197.47, 14002 point y -195.06), the ring published none
            // and the old "no destination, no verdict" (16546) re-armed it for six minutes.
            return standing.HasValue
                ? BlacksiteReachability.Route(BlacksiteTraveller.Soldier(holder), holder.transform.position, standing.Value,
                    goalOutside: ObjectiveWorld(record))
                : BlacksiteRouteVerdict.Unsupported(BlacksiteRouteGap.NoPath);
        }

        /// <summary>
        /// #950 S11H U2: the escalation itself. An objective its holder has no complete route to is
        /// first re-resolved against the discovery scan, which is the only thing in the manifest that
        /// knows what is live: if it produces another record of the same kind that is still
        /// attainable, the mission has somewhere else to go and the squad is pointed at it. If it
        /// does not, the record stays withdrawn and the mission falls to the bounded fallback set
        /// (loot or the apparatus, and then the withdrawal rule), which is what Discover's own
        /// default arm does for a contract with no adapter. Either way the escalation is logged
        /// with the holder's verdict. It is a transition, not a repeating line, so it carries no gate.
        /// #1305 G1 (R19): a record the verdict did not withdraw re-arms, so it is its own re-resolution.
        /// </summary>
        private void EscalateUnreachable(BlacksiteObjectiveSnapshot record, BlacksiteRaidJob job, BlacksiteSoldierAI holder,
            in BlacksiteRouteVerdict route, Vector3? standing)
        {
            ulong id = record.ObjectId;
            // #1305 E2: the board holds the withdrawal before the fallback reads it - ReleaseJob's caller
            // writes it back only after this returns, and a still-Reserved apparatus settles the fallback.
            int at = RecordIndex(id); if (at >= 0) Records[at] = record;
            Discover();
            ulong next = id; bool reresolved = record.Phase != BlacksiteObjectivePhase.Unavailable;
            foreach (var other in Records)
            {
                if (reresolved || other.ObjectId == id || other.Kind != record.Kind) continue;
                if (other.Phase == BlacksiteObjectivePhase.Unavailable || other.Phase == BlacksiteObjectivePhase.Extracted) continue;
                if (Resolve(other.ObjectId) == null) continue;
                next = other.ObjectId; reresolved = true; break;
            }
            if (!reresolved) DiscoverFallbackObjectives();
            if (!BlacksiteDiagnostics.Enabled) return;
            BlacksiteDiagnostics.Log("Objective",
                $"event=unreachable, object={id}, kind={record.Kind}, member={holder.MemberIndex}"
                + $", action={(reresolved ? "reresolved" : "unavailable")}"
                + $", next={(reresolved ? next.ToString() : BlacksiteDiagnosticFormat.None)}"
                + $", reason=no complete navmesh path from the holder; {route.PathReason}"
                + $" from origin={BlacksiteDiagnosticFormat.V(holder.transform.position)}"
                + $" to standing={(standing.HasValue ? BlacksiteDiagnosticFormat.V(standing.Value) : BlacksiteDiagnosticFormat.None)}"
                + $", best={(job == null || float.IsInfinity(job.BestRemaining) || float.IsNaN(job.BestRemaining) ? BlacksiteDiagnosticFormat.None : job.BestRemaining.ToString("0.00"))}"
                + $", phase={(job != null ? job.Phase.ToString() : BlacksiteDiagnosticFormat.None)}"
                + $", aliveRaiders={AliveRaiders}, missionSeconds={MissionSeconds:0.00}");
        }
        // The attempt-close line. The [UAT][Job] line stays as it is: it is the S1.1 attempt-ledger
        // record and existing tooling reads it. This is the objective's own channel, with the cause
        // the job already computed and never printed.
        private void LogObjectiveClosed(
            BlacksiteObjectiveSnapshot record,
            BlacksiteSoldierAI owner, // null when the carrier is already gone; prints member=none.
            BlacksiteRaidJob job,
            int generation,
            BlacksiteAttemptOutcome outcome,
            BlacksiteObjectiveCloseCause cause,
            BlacksiteObjectiveAttempts history,
            in BlacksiteRouteVerdict holderVerdict = default, // read only for withdrawal=NoStandingPoint
            BlacksiteObjectiveWithdrawal withdrawal = BlacksiteObjectiveWithdrawal.None)
        {
            // This existing close hook runs once per objective attempt, so the ungated line is at
            // most one per object per close; the per-tick refusal path remains UAT-only.
            if (cause == BlacksiteObjectiveCloseCause.ApproachUnreachable
                || cause == BlacksiteObjectiveCloseCause.Unreachable)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Blacksite] Objective attempt closed: object={record.ObjectId}, kind={record.Kind}"
                    + $", cause={cause}, approachRefusal={ClosedApproachRefusal(record.ObjectId)}"
                    + $", member={(owner != null ? owner.MemberIndex.ToString() : BlacksiteDiagnosticFormat.None)}"
                    + $", missionSeconds={MissionSeconds:0.00}.");
            }
            if (!BlacksiteDiagnostics.Enabled) return;
            // #950 S11H U2: the attempt's own standing point, or none. This used to read the live
            // _pickupApproaches map, which ClearAttemptState has already emptied by the time this
            // line is written, so every close printed goal=<the object's own position>. For a
            // SEALED container that position is the point inside it that produced 133 PathInvalid
            // rejections on 2026-09-10, and printing it as the goal said the squad had somewhere to
            // stand when it had nowhere. A sealed barrier with no standing point now has no goal,
            // and everything measured against a goal reads none with it.
            Vector3? standingPoint = ClosedStandingPoint(record.ObjectId);
            bool barrierClosed = BlacksiteObjectiveApproach.TryResolveBarrier(_mission, record.Kind, out _, out _);
            bool hasGoal = standingPoint.HasValue || !barrierClosed;
            Vector3 goal = standingPoint ?? record.Position;
            string goalKind = ObjectiveGoalKind(record, out string containerOpen);
            var next = SelectOperator(record, _members);
            float retryIn = _retryAt.TryGetValue(record.ObjectId, out float at) ? at - Time.time : -1f;
            // #874 S6.1a: member=none when the carrier is already gone (object destroyed, cleanup,
            // departure). Everything measured from the owner reads none with it.
            float holderRoute = owner != null && hasGoal ? RouteDistance(owner.transform.position, goal) : float.NaN;
            string causeText = cause.ToString();
            float moved = HolderMovedLast15s(record.ObjectId, owner);
            BlacksiteDiagnostics.Log("Objective",
                $"event=closed, object={record.ObjectId}, kind={record.Kind}"
                + $", member={(owner != null ? owner.MemberIndex.ToString() : BlacksiteDiagnosticFormat.None)}, attempt={generation}"
                + $", outcome={outcome}, cause={causeText}"
                + $", phase={(job != null ? job.Phase.ToString() : "none")}"
                + $", sinceProgress={(job != null ? (Time.time - job.LastProgressAt).ToString("0.00") : "none")}"
                + $", remaining={RouteText(holderRoute)}"
                // #874 S4.2: the job's own best-remaining watermark. Infinity means the attempt
                // never observed a travel sample, which is itself the answer to "why did the
                // progress budget expire".
                + $", best={(job == null || float.IsInfinity(job.BestRemaining) || float.IsNaN(job.BestRemaining) ? "none" : job.BestRemaining.ToString("0.00"))}"
                + $", goalKind={goalKind}, goal={(hasGoal ? BlacksiteDiagnosticFormat.V(goal) : BlacksiteDiagnosticFormat.None)}"
                + $", navStatus={(owner != null ? "Path" + BlacksiteMotionPolicy.PathToken(owner.Navigator.PathState) : BlacksiteDiagnosticFormat.None)}"
                + $", containerOpen={containerOpen}"
                + $", failures={history.Failures}/{BlacksiteObjectiveAttempts.MaxFailures}"
                + $", retryIn={retryIn:0.00}"
                + $", nextOwner={(next != null ? next.MemberIndex.ToString() : "none")}"
                // #874 S6.4a. The motion pair answers "was the holder moving at all"; the route pair
                // answers "is the member being handed to actually better placed".
                + $", movedLast15s={(moved >= 0f ? moved.ToString("0.00") : BlacksiteDiagnosticFormat.None)}"
                + $", closingLast10s={HolderClosingLast10s(record.ObjectId)}"
                + $", holderRoute={RouteText(holderRoute)}"
                + $", nextRoute={(next != null && hasGoal ? RouteText(RouteDistance(next.transform.position, goal)) : BlacksiteDiagnosticFormat.None)}"
                // #950 S11H U2: appended at the END of the line, per the Stage 11H diagnostic
                // contract: did the operator ever have anywhere to stand?
                + $", standingPoint={(standingPoint.HasValue ? BlacksiteDiagnosticFormat.V(standingPoint.Value) : BlacksiteDiagnosticFormat.None)}");
            if (record.Kind == BlacksiteObjectiveKind.Loot) return;
            // #1219 A4: an exhausted mission objective is withdrawn (MissionAttemptsExhausted), so
            // the S6.5a event=exhausted re-arm line is gone with the re-arm; event=withdrawn names
            // every withdrawal and its withdrawal= token.
            if (record.Phase != BlacksiteObjectivePhase.Unavailable) return;
            bool outstanding = false;
            foreach (var other in Records)
                if (other.ObjectId != record.ObjectId && other.Kind != BlacksiteObjectiveKind.Loot
                    && (other.Phase == BlacksiteObjectivePhase.Available
                        || other.Phase == BlacksiteObjectivePhase.Reserved
                        || other.Phase == BlacksiteObjectivePhase.Carried)) { outstanding = true; break; }
            // #950 S11H U2 / #874 S2: the withdrawal names the rule that fired and the route service's
            // answer - #1305 G1 (R19): the holder's own Unsupported route. "local pickup attempts exhausted" is the
            // three-probe budget at the object.
            string withdrawalReason = withdrawal == BlacksiteObjectiveWithdrawal.NoStandingPoint
                ? $"no complete navmesh path from the holder ({holderVerdict.PathReason})"
                : withdrawal == BlacksiteObjectiveWithdrawal.MissionAttemptsExhausted || withdrawal == BlacksiteObjectiveWithdrawal.AttemptsExhausted
                    ? "attempt budget spent without haul progress"
                : withdrawal == BlacksiteObjectiveWithdrawal.CartImmovable
                    ? "the cart is stuck where no ship device can lift it; the squad leaves without it"
                : "local pickup attempts exhausted";
            BlacksiteDiagnostics.Log("Objective",
                $"event=withdrawn, object={record.ObjectId}, reason={withdrawalReason}"
                + $", failures={history.Failures}/{BlacksiteObjectiveAttempts.MaxFailures}, outstanding={outstanding}"
                + $", aliveRaiders={AliveRaiders}, missionSeconds={MissionSeconds:0.00}"
                + $", withdrawal={BlacksiteObjectivePolicy.WithdrawalToken(withdrawal)}");
        }
        // #1219 A4: a record that completed its haul (staged, loaded, delivered, witness completed)
        // clears the objective's failure history and closes its attempt Completed. Acquisition closes
        // Completed too but credits nothing: only the haul is progress.
        private void CompleteHaul(BlacksiteObjectiveSnapshot record)
        {
            Attempts(record.ObjectId).CreditHaulProgress();
            CloseAttempt(record, BlacksiteAttemptOutcome.Completed, BlacksiteObjectiveCloseCause.Completed);
        }
        // #1219 A4: a mission objective withdrawn by its spent budget or as an immovable cart is
        // written off as lost (#1066 U2, event=lost), and #1305 C4 (R12): so is one its holder's own route
        // to is Unsupported (NoStandingPoint, #1305 G1 R19). #1305 E2: the return rule reads cargo aboard, not this:
        // lost is never done. #1219 D3: CartImmovable is raised only for a stuck cart no ship device can
        // lift, so there is no pickup to request here: a cart the ship CAN lift is never written
        // off by its verdict - it stays owed, Refresh stages it within the lift reach (#1293 R2) and
        // the lift's own success Loads it. Loot and salvage are never mission objectives (ReArms),
        // so an unreachable scrap item is withdrawn without writing the mission off.
        private void WriteOffWithdrawn(BlacksiteObjectiveSnapshot record, BlacksiteObjectiveWithdrawal withdrawal)
        {
            if (!ReArms(record.Kind) || !BlacksiteObjectivePolicy.WritesOffObjective(withdrawal)) return;
            NoteObjectiveLost(record, BlacksiteObjectiveCloseCause.Abandoned);
        }
        /// <summary>
        /// #1219 D3: the mission's one answer to a cart whose stuck verdict has latched
        /// (<see cref="BlacksiteObjectivePolicy.CartStuckAnswer"/>), on the facts it stands in now:
        /// its world (B's world truth) and whether a ship device reaches its pose
        /// (<see cref="BlacksiteShipController.WithinPayloadLiftReach"/>). None for any other object.
        /// </summary>
        private BlacksiteCartStuckAnswer StuckCartAnswer(PayloadCartController cart)
        {
            if (cart == null || !cart.HasNpcStuckVerdict) return BlacksiteCartStuckAnswer.None;
            bool outside = BlacksiteWorld.Outside(cart);
            return BlacksiteObjectivePolicy.CartStuckAnswer(true, outside,
                outside && Ship != null && Ship.WithinPayloadLiftReach(cart.transform.position));
        }
        /// <summary>#1219 D3: the answer, once per answer, naming the pose.</summary>
        private void LogStuckCartAnswer(BlacksiteObjectiveSnapshot r, PayloadCartController cart, BlacksiteCartStuckAnswer answer)
        {
            if (answer == BlacksiteCartStuckAnswer.None) return;
            Vector3 pose = cart.transform.position;
            bool outside = BlacksiteWorld.Outside(cart);
            string device = Ship == null ? BlacksiteDiagnosticFormat.None : Ship.IsHovering ? "hoverCable" : "groundMagnet";
            string reason = answer == BlacksiteCartStuckAnswer.ShipLift ? "within the lift reach of the ship's " + device
                : !outside ? "inside the facility, where no ship device reaches; the squad leaves without it"
                : "outside beyond the ship's lift, the squad leaves without it";
            BlacksiteDiagnostics.Log("Objective",
                $"event=cartStuck, object={r.ObjectId}, pose={BlacksiteDiagnosticFormat.V(pose)}, outside={outside}"
                + $", device={device}, liftDistance={(Ship != null ? Vector3.Distance(pose, CargoDeliveryPoint(r)).ToString("0.00") : BlacksiteDiagnosticFormat.None)}"
                + $", answer={BlacksiteObjectivePolicy.CartStuckAnswerToken(answer)}, result={r.Phase}, reason={reason}");
        }
        // #1219 D3: Refresh's arm for an Available record whose cart already has a verdict - a player moved the
        // cart and let go, or (#1305 G4) its lease ended at the pose that latched it. #1293 R2: the write-off half
        // alone: a cart within the lift reach is Staged first; one out of it (or inside) is written off where it
        // stands, #1305 G4 through AbandonCargo (NoCartRoute). r is Refresh's current copy of the record.
        private bool AnswerStuckCartAgain(int index, BlacksiteObjectiveSnapshot r, PayloadCartController cart)
        {
            var stuck = StuckCartAnswer(cart);
            if (stuck != BlacksiteCartStuckAnswer.WriteOff) return false;
            Records[index] = r; AbandonCargo(index, BlacksiteObjectiveCloseCause.NoCartRoute, "stuck cart answer=writeOff");
            LogStuckCartAnswer(Records[index], cart, stuck);
            return true;
        }
        // #1078 rework: per-objective probe-close history. It is a BlacksiteObjectiveAttempts rather
        // than a counter so the schedule is literally the shipped one - the same 1/3/6/12/24/24
        // backoff and the same six-step bound - and cannot drift from it. It lives beside
        // _pickupFailures in lifetime: written only on a probe close, read only to defer the retry,
        // and gone with the manifest when the mission ends.
        private readonly Dictionary<ulong, BlacksiteObjectiveAttempts> _probeBackoff
            = new Dictionary<ulong, BlacksiteObjectiveAttempts>();
        // #1153: which records the board RE-ARMS for - the one classification both release paths
        // (ReleaseJob and Drop) hand WithdrawalCause as its missionObjective argument. #1078's
        // argument here was "not Loot", which made a fallback salvage target a mission objective in
        // every arm: on 2026-09-20 scrap 537 came back Available after each close and four members in
        // turn walked to the same wall (55517, 57742, 62152, 64714). Salvage is the work a raid with
        // nothing else to do invents for itself and nothing re-arms for it - which the withdrawal
        // rule has claimed for loot and salvage since S6.5a - so it answers the same as Loot in both
        // budget arms and in the probe deferral, which exists only to pace a re-arm, and a spent
        // salvage record is never written off as a lost mission objective. #1305 E2 (D11): and only a
        // mission objective holds the one ObjectiveBoard; salvage is one item per raider, in parallel.
        private static bool ReArms(BlacksiteObjectiveKind kind)
            => kind != BlacksiteObjectiveKind.Loot && kind != BlacksiteObjectiveKind.Salvage;
        // #1219 B3: releases a RESERVED record. A Carried record's custody ends only by an event
        // (Drop, TryStageOrLoad, the witness boarding) because a release neither drops the object
        // nor republishes its point: the replica then placed the drive back at its reservation-time
        // point (round 3, t=1951). Every caller is Reserved-only; a Carried record is refused here
        // with one line and left unchanged.
        private void ReleaseJob(ref BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI owner, bool localFailure, string reason,
            BlacksiteObjectiveCloseCause cause,
            BlacksiteAttemptOutcome outcome = BlacksiteAttemptOutcome.Failed)
        {
            if (record.Phase == BlacksiteObjectivePhase.Carried)
            {
                BlacksiteDiagnostics.Log("Job", $"object={record.ObjectId}, owner={owner.MemberIndex}, reason={reason}, refused=carried");
                return;
            }
            // #1305 C4 (R11): whether the holder stands in the objective's world, read while the
            // record is still Reserved to him. A holder in the other world never reached the
            // objective, so his close says nothing about it (Burrow 2026-10-03, LogOutput.log
            // 21625-21629: member 2 stalled outside, path=Complete to the main entrance, and the drive
            // inside was written off while members 0 and 1 had complete paths to it).
            bool holderInWorld = !owner.isEnemyDead && owner.isOutside == ObjectiveWorld(record);
            int attempts = _pickupFailures.TryGetValue(record.ObjectId, out int previous) ? previous : 0;
            if (localFailure) attempts++;
            if (_pickupProbeFailures.TryGetValue(record.ObjectId, out int probes) && probes >= 3) attempts = 3;
            _pickupFailures[record.ObjectId] = attempts;
            // #874 S6.4a: the 30 s bench is what stops the same wedged member re-taking the job it
            // just failed, so a squad FAILURE still benches. A deliberate handoff (Reassigned) or
            // an interruption is not that member's fault and must not cost it thirty seconds of
            // eligibility - under the old rule the ShouldTransfer handoff benched the member it
            // handed off from exactly like the six failures of the 2026-09-09 rotation.
            if (outcome == BlacksiteAttemptOutcome.Failed)
                _operatorRetry[(record.ObjectId, owner.NetworkObjectId)] = Time.time + (localFailure ? 5f : 30f);
            // #1305 C4: the member's miss still benches him, but a close by a holder outside the
            // objective's world is not the objective's: Interrupted charges it no failure.
            if (!holderInWorld && outcome == BlacksiteAttemptOutcome.Failed) outcome = BlacksiteAttemptOutcome.Interrupted;
            int generation = _jobs.TryGetValue(record.ObjectId, out var job) ? job.Generation : 0;
            // #950 S11H U2 / #874 S2 / #1305 C4 (R11): the unreachable rule, asked before the close so its
            // verdict can charge it. Only the traveller judges: a holder in the objective's world whose own
            // miss closed the attempt (ProgressBudget, the spent ring's ApproachUnreachable) is asked for his
            // own route; any but a Reachable one names the close Unreachable (the stall is the symptom), and
            // #1305 G1 (R19) only an Unsupported one at the second failure or later writes it off. A handoff
            // (Reassigned), a stranded body (Interrupted) or #1305 G4's NoDeliverablePose (a fact about the
            // cart's pose) is no report about the objective. Loot and container cargo are excluded: nothing
            // re-arms for loot, and a container record's closes are the loading attempt's.
            BlacksiteRouteVerdict holderRoute = default; Vector3? standing = null;
            bool unreachable = holderInWorld
                && (cause == BlacksiteObjectiveCloseCause.ProgressBudget || cause == BlacksiteObjectiveCloseCause.ApproachUnreachable)
                && record.Kind != BlacksiteObjectiveKind.Loot && !record.ContainerCargo
                && !(holderRoute = HolderReach(record, owner, out standing)).Reachable;
            // #1305 G1 (R19): an Unsupported route is the objective's failure whoever closed it, so the
            // three-probe close's Interrupted charges it too; a PartialFrontier close keeps its outcome.
            if (holderRoute.Reach == BlacksiteRouteReach.Unsupported) outcome = BlacksiteAttemptOutcome.Failed;
            // The attempt is closed before the record becomes assignable again. Only a squad
            // failure escalates the objective's backoff; a deliberate handoff does not.
            var history = CloseAttempt(record.ObjectId, outcome);
            // #1078 rework: the probe close's own backoff. Only a close that the local probe budget
            // produced advances it; every other close leaves it.
            bool reArms = ReArms(record.Kind);
            bool probeDeferred = BlacksiteObjectivePolicy.DefersLocalProbeClose(reArms, attempts)
                && cause == BlacksiteObjectiveCloseCause.ApproachUnreachable;
            if (probeDeferred)
            {
                if (!_probeBackoff.TryGetValue(record.ObjectId, out var probeHistory))
                    _probeBackoff[record.ObjectId] = probeHistory = new BlacksiteObjectiveAttempts();
                probeHistory.Fail();
            }
            if (unreachable) cause = BlacksiteObjectiveCloseCause.Unreachable;
            var withdrawal = BlacksiteObjectivePolicy.WithdrawalCause(reArms, history.Exhausted, attempts, holderRoute.Reach, history.Failures);
            record.Phase = BlacksiteObjectivePolicy.AvailabilityAfter(withdrawal) == BlacksiteObjectiveAvailability.Unavailable
                ? BlacksiteObjectivePhase.Unavailable : BlacksiteObjectivePhase.Available;
            record.CarrierId = NoCarrier;
            float retryAt = outcome == BlacksiteAttemptOutcome.Failed ? history.RetryAt(Time.time) : Time.time + 1f;
            // #1078 rework: a probe close is deferred on the capped attempt schedule, not on the flat
            // second. Taken as a maximum so a close that ALSO spent a squad failure keeps whichever
            // deadline is longer - no backoff a record has earned is ever shortened.
            if (probeDeferred && _probeBackoff.TryGetValue(record.ObjectId, out var deferred))
                retryAt = Mathf.Max(retryAt, deferred.RetryAt(Time.time));
            _retryAt[record.ObjectId] = retryAt;
            BlacksiteDiagnostics.Log("Job", $"object={record.ObjectId}, owner={owner.MemberIndex}, reason={reason}, attempt={generation}, outcome={outcome}, terminal={(job != null ? job.Result : BlacksiteJobResult.Invalidated)}, localAttempts={attempts}, objectiveFailures={history.Failures}/{BlacksiteObjectiveAttempts.MaxFailures}, result={record.Phase}"
                + $", withdrawal={BlacksiteObjectivePolicy.WithdrawalToken(withdrawal)}"
                + (history.Exhausted
                    ? $"; attempt budget exhausted after {history.Failures} failed attempts, record now {record.Phase}."
                    : "."));
            LogObjectiveClosed(record, owner, job, generation, outcome, cause, history, holderRoute, withdrawal);
            WriteOffWithdrawn(record, withdrawal);
            // The escalation runs after the line that explains it, so the log reads close then
            // escalation rather than the other way round.
            if (unreachable) EscalateUnreachable(record, job, owner, holderRoute, standing);
        }

        /// <summary>
        /// #1016 S2 (design note C.5): keep every live claim's lease asserted, so the broker's view of
        /// who carries what is the records'.
        ///
        /// Run from the one sweep, once per server tick, before any duty is planned. A record that is
        /// still Reserved or Carried re-asserts its carrier's lease, because a claim nobody is
        /// asserting is a claim whose holder has gone - that is what the 2 s stale window means and it
        /// is the same rule every other lane uses - and a lease found gone (the ledger's stall answer
        /// releases every lease the stalled member holds) is simply taken again: the RECORD is the
        /// claim. #1293 R2: there is no ceiling. An operator who stops progressing is the ledger's
        /// miss, and <see cref="AnswerStalledRecord"/> releases the job there; custody of a carried
        /// record ends only by an event (Drop, TryStageOrLoad, boarding, the ledger's answer).
        /// </summary>
        private void ReconcileObjectiveClaims(float now)
        {
            BlacksiteResourceBroker broker = Broker;
            if (broker == null) return;
            for (int i = 0; i < Records.Count; i++)
            {
                BlacksiteObjectiveSnapshot record = Records[i];
                if (record.CarrierId == NoCarrier) continue;
                if (record.Phase != BlacksiteObjectivePhase.Reserved
                    && record.Phase != BlacksiteObjectivePhase.Carried) continue;

                broker.Request(BlacksiteResourceKey.Objective(record.ObjectId), record.CarrierId, now, "carrier");
                if (!ReArms(record.Kind)) continue; // #1305 E2: loot and salvage hold no board

                // #1016 S2 review batch 2, F5: the board and the claim are taken together at the
                // reservation, and the carrier re-takes the board it owns beside its claim.
                BlacksiteResourceKey board = BlacksiteResourceKey.ObjectiveBoard();
                // #1153: the one place the board is KEPT is the one place it can be withheld.
                // A reserved operator who is not in the target's world has not started the job
                // - no approach ring is resolved for him either, see TryPickupApproach - so he
                // stands in no lane: it is released for him here and re-taken by this same
                // renewal on the tick he crosses. The grant AssignReservations makes is left
                // alone, because this sweep runs once per server tick (TickResourceBroker) and
                // the planner's tick follows the reservation inside the same manifest tick, so
                // no board survives a crossing. A CARRY is excluded by the rule itself.
                BlacksiteSoldierAI boardOperator = Soldier(record.CarrierId);
                if (boardOperator != null && !BlacksiteObjectivePolicy.MayWorkRecord(
                        boardOperator.isOutside == ObjectiveWorld(record),
                        record.Phase == BlacksiteObjectivePhase.Carried))
                {
                    broker.Release(board, record.CarrierId);
                    continue;
                }
                // #1016 S2 final review F-A/F-B: a carrier that is not the board's holder - a
                // handover, or (#1293 R2) a board the ledger's stall answer released - may stand in
                // a stale queue place; it is served to the front first, so its request lands as a
                // grant. A holder's request is a plain renewal.
                if (broker.TryGetLane(board, out BlacksiteResourceLane boardLane)) boardLane.Serve(record.CarrierId, now);
                broker.Request(board, record.CarrierId, now, "carrier of " + record.Kind);
            }
        }

        // #874 S6.1/S6.4a per-attempt observation state. Every one of these is cleared with the
        // attempt in ClearAttemptState, so nothing survives a rotation.
        private readonly Dictionary<ulong, Vector3> _jobGoals = new Dictionary<ulong, Vector3>();
        // #1219 B1: the holder's world-crossing count at the last observation (the planner's
        // count, bumped only by the one crossing callback). "The holder changed world between two
        // observations" is that count moving - the crossing EVENT - rather than a remembered
        // isOutside compared with the current one.
        private readonly Dictionary<ulong, int> _jobGoalCrossings = new Dictionary<ulong, int>();
        // #989 C1: the record position the last published goal was resolved against, so a goal that
        // moved because the TARGET moved can be told from one whose identity was replaced.
        private readonly Dictionary<ulong, Vector3> _jobGoalTargets = new Dictionary<ulong, Vector3>();
        // #874 F06: the barrier the job clock last observed for this record, so a stage the squad
        // actually opened can be told from a goal that merely drifted. It is the job's own memory
        // and not the approach cache's - the resolver's copy is about the point it published, this
        // one is about the observation that measured progress - and like every line above it dies
        // with the attempt in ClearAttemptState.
        private readonly Dictionary<ulong, (bool Closed, Vector3 Anchor)> _jobBarriers
            = new Dictionary<ulong, (bool, Vector3)>();
        private readonly Dictionary<ulong, float> _retargetLogAt = new Dictionary<ulong, float>();
        // #989 C2: the witnessPoint publish window, one line per second. #1271 S2: it gates the line
        // only; the route its navStatus prints is measured on every call, because it is the veto.
        private readonly Dictionary<ulong, float> _witnessPointLogAt = new Dictionary<ulong, float>();
        private readonly Dictionary<ulong, List<(float At, Vector3 Position)>> _holderTrack = new Dictionary<ulong, List<(float, Vector3)>>();
        private readonly Dictionary<ulong, List<(float At, float Route)>> _holderRoutes = new Dictionary<ulong, List<(float, float)>>();
        // The additive [UAT][Job] event line. The phase-change line keeps its own shape; this one
        // carries the bounded failure count the same way event=closed does.
        private void LogJobEvent(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI soldier, BlacksiteRaidJob job,
            BlacksiteJobPhase phase, float remaining, BlacksiteJobResult result, string tail)
        {
            if (!BlacksiteDiagnostics.Enabled) return;
            BlacksiteDiagnostics.Log("Job",
                $"object={record.ObjectId}, member={soldier.MemberIndex}, attempt={job.Generation}"
                + $", phase={phase}, remaining={remaining:0.0}, result={result}"
                + $", objectiveFailures={Attempts(record.ObjectId).Failures}/{BlacksiteObjectiveAttempts.MaxFailures}, {tail}");
        }
        // Position samples over the immobile window and route samples over the closing window, at
        // the 0.5 s observe cadence. The samples are the policy's inputs; nothing here decides.
        private void TrackHolder(ulong objectId, Vector3 position, float route)
        {
            if (!_holderTrack.TryGetValue(objectId, out var positions))
                _holderTrack[objectId] = positions = new List<(float, Vector3)>();
            positions.Add((Time.time, position));
            while (positions.Count > 1 && Time.time - positions[0].At > BlacksiteObjectivePolicy.HolderImmobileWindowSeconds)
                positions.RemoveAt(0);
            if (!_holderRoutes.TryGetValue(objectId, out var routes))
                _holderRoutes[objectId] = routes = new List<(float, float)>();
            routes.Add((Time.time, route));
            while (routes.Count > 1 && Time.time - routes[0].At > BlacksiteObjectivePolicy.HolderClosingWindowSeconds)
                routes.RemoveAt(0);
        }
        // The furthest the holder got from ANY retained sample: a member that walked out and back
        // has moved, even though its start and end positions coincide.
        private float HolderMovedLast15s(ulong objectId, BlacksiteSoldierAI owner)
        {
            if (owner == null || !_holderTrack.TryGetValue(objectId, out var positions)) return -1f;
            float moved = 0f;
            foreach (var sample in positions)
            { float d = Vector3.Distance(sample.Position, owner.transform.position); if (d > moved) moved = d; }
            return moved;
        }
        // Closing uses the job's own progress epsilon, so "the route closed in the last ten
        // seconds" and "the progress budget was refreshed" are the same event.
        private bool HolderClosingLast10s(ulong objectId)
            => _holderRoutes.TryGetValue(objectId, out var routes) && routes.Count > 1
                && routes[0].Route - routes[routes.Count - 1].Route > BlacksiteObjectivePolicy.HolderClosingEpsilonMetres;
        // #874 S6.1: the job observation, shared by both legs. The reserved leg and the carry leg
        // have different goals, different phases and different failure handling, but the
        // goal-identity restart is one piece of code called from both. Everything here runs BEFORE
        // the job's own Tick. #874 S4: the wait milestones and the wait edge went with the wait
        // clock; a crossing is the monitor's to judge.
        private void ObserveJobClock(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI soldier,
            BlacksiteRaidJob job, BlacksiteJobPhase phase, Vector3 goal, float remaining)
        {
            // The goal identity changed, so the watermark restarts. MinDestinationChange (1.45 m) is
            // the navigator's own re-issue distance: a goal that has not moved further than that is
            // not a different place to walk to. The carry leg needs this as much as the reserved leg
            // does - its goal swaps outright at the world boundary, from the inside entrance to the
            // ship's cargo loading position.
            int crossings = Planner.CrossingCount(soldier.NetworkObjectId);
            bool crossed = _jobGoalCrossings.TryGetValue(record.ObjectId, out int storedCrossings)
                && storedCrossings != crossings;
            bool hadGoal = _jobGoals.TryGetValue(record.ObjectId, out var previousGoal);
            bool goalMoved = hadGoal
                && (Vector3.Distance(previousGoal, goal) > BlacksiteArrivalPolicy.MinDestinationChange
                    || crossed);
            // #874 F06: the objective's BARRIER advanced a stage, which is a goal replacement
            // however far the two ring points happen to end up apart, and which the geometry test
            // above cannot see at all while the published point is stale. Opening a barrier is the
            // objective's own work (the case BlacksiteRaidJob.Retarget's contract names: "container
            // lock to drive"), so the stage it completes restarts the baseline exactly as a world
            // crossing or a re-probed approach does. It fires once per stage.
            var barrier = BarrierIdentity(record.Kind);
            bool hadBarrier = _jobBarriers.TryGetValue(record.ObjectId, out var previousBarrier);
            bool barrierAdvanced = hadBarrier && BarrierChanged(true, previousBarrier, barrier);
            _jobBarriers[record.ObjectId] = barrier;
            if (barrierAdvanced || goalMoved)
            {
                // #989 C1: the same objective whose POINT moved is tracked, not retargeted. A
                // witness walks, so his published point clears MinDestinationChange between two
                // observations at any pace over 0.73 m/s; declaring that as a goal replacement reset
                // LastProgressAt every tick and made the chase attempt immortal (2026-09-12: 266.7 s
                // held, released only by the squad dying, three raiders pinned in Escort behind it).
                //
                // The three terms are what "only its point moved" means. The world did not change,
                // so this is not the entrance swap the carry leg makes at the boundary; the RECORD's
                // own position moved further than the re-issue distance, so the target itself is
                // what moved; and the goal moved no further than the target did, plus that same
                // re-issue distance, so this is not a re-probed approach or a lock becoming a drive
                // riding on a small target jitter. Anything else is still a goal replacement and
                // still restarts the clock.
                bool hadTarget = _jobGoalTargets.TryGetValue(record.ObjectId, out var previousTarget);
                float targetMoved = hadTarget ? Vector3.Distance(previousTarget, record.Position) : 0f;
                bool tracked = !barrierAdvanced
                    && hadTarget
                    && !crossed
                    && targetMoved > BlacksiteArrivalPolicy.MinDestinationChange
                    && Vector3.Distance(previousGoal, goal) <= targetMoved + BlacksiteArrivalPolicy.MinDestinationChange;
                if (tracked) job.Track(Time.time, remaining);
                else job.Retarget(Time.time, remaining);
                // Route samples taken against the OLD goal cannot say whether this one is closing.
                _holderRoutes.Remove(record.ObjectId);
                if (!_retargetLogAt.TryGetValue(record.ObjectId, out float nextLog) || Time.time >= nextLog)
                {
                    _retargetLogAt[record.ObjectId] = Time.time + 1f;
                    LogJobEvent(record, soldier, job, phase, remaining, BlacksiteJobResult.Continue,
                        $"event=retarget, kind={(tracked ? "track" : barrierAdvanced ? "barrier" : "retarget")}"
                        + $", from={(hadGoal ? previousGoal.ToString("0.00") : BlacksiteDiagnosticFormat.None)}, to={goal.ToString("0.00")}"
                        + $", barrierClosed={barrier.Closed}");
                }
            }
            _jobGoals[record.ObjectId] = goal; _jobGoalCrossings[record.ObjectId] = crossings;
            _jobGoalTargets[record.ObjectId] = record.Position;
            TrackHolder(record.ObjectId, soldier.transform.position, remaining);
        }
        // #874 S6.1: the carry leg's observation. Same watermark, same goal-identity restart as the
        // reserved leg. #874 S4: it observes and closes nothing - a stalled carrier is the
        // monitor's, answered through AnswerStalledRecord.
        // #1219 A4: this is where haul progress is measured. The carrier's navmesh route to the carry
        // goal - the inside main entrance while inside, then the delivery point (CargoDeliveryPoint,
        // or the boarding point for a witness) - feeds the objective's haul ground
        // (BlacksiteObjectiveAttempts.ObserveCarry), which owns the goal identity (the carrier's
        // world plus the goal point within MinDestinationChange), keeps one set per world across
        // attempts, carries the uncredited partial across a world flip and counts only routed,
        // non-Waiting samples below the best any carry has reached in that world. Once the new
        // ground reaches BlacksiteObjectivePolicy.HaulProgressMetres the haul is progress and
        // clears the objective's failure history; the line is printed only when there was one.
        private void ObserveCarryJob(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI carrier,
            BlacksiteJobPhase phase, Vector3 goal)
        {
            var job = Job(record); var old = job.Phase;
            BlacksiteRouteVerdict route = BlacksiteReachability.Route(BlacksiteTraveller.Soldier(carrier), carrier.transform.position, goal);
            float remaining = route.ReportedRemaining;
            ObserveJobClock(record, carrier, job, phase, goal, remaining);
            job.Tick(Time.time, phase, remaining);
            if (old != phase)
                BlacksiteDiagnostics.Log("Job", $"object={record.ObjectId}, member={carrier.MemberIndex}, attempt={job.Generation}, phase={phase}, remaining={remaining:0.0}, objectiveFailures={Attempts(record.ObjectId).Failures}.");
            var history = Attempts(record.ObjectId);
            int failures = history.Failures;
            if (!history.ObserveCarry(BlacksiteWorld.Outside(carrier), goal.x, goal.y, goal.z, BlacksiteArrivalPolicy.MinDestinationChange,
                    phase == BlacksiteJobPhase.Waiting, route.Distance, route.Reachable, BlacksiteObjectivePolicy.HaulProgressMetres, out float gained)
                || failures == 0) return;
            BlacksiteDiagnostics.Log("Job", $"object={record.ObjectId}, member={carrier.MemberIndex}, attempt={job.Generation}, "
                + $"haul progress: newGround={gained:0.0}m of {BlacksiteObjectivePolicy.HaulProgressMetres:0}m, best={history.HaulBest:0.0}, "
                + $"objectiveFailures={failures}/{BlacksiteObjectiveAttempts.MaxFailures} cleared.");
        }
        private bool ObserveJob(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI soldier, Vector3 approach, bool atAccess)
        {
            if (!ObserveNow(record.ObjectId)) return false;
            bool sameWorld = soldier.isOutside == ObjectiveWorld(record);
            float distance = Vector3.Distance(soldier.transform.position, record.Position);
            // #1293 R2 / #1305 G4: every state the ledger keeps a holder for (a doorway queue, combat, a committed
            // crossing) is declared progress, so he is Waiting; in a queue the holder ahead is the ledger's to judge.
            var phase = BlacksiteObjectivePolicy.ShouldReleaseHolder(soldier.IsWaitingForTraversal, soldier.IsObjectiveCombatActive,
                    soldier.HasCommittedJobTraversal) == BlacksiteHolderRelease.Keep ? BlacksiteJobPhase.Waiting
                : !sameWorld || distance > 6f ? BlacksiteJobPhase.Travel
                : atAccess ? BlacksiteJobPhase.Interact : BlacksiteJobPhase.Approach;
            Vector3 goal = sameWorld ? approach : soldier.isOutside ? _mission.SquadDirector.OutsideMainEntrance : _mission.SquadDirector.InsideMainEntrance;
            var job = Job(record); var old = job.Phase;
            BlacksiteRouteVerdict route = BlacksiteReachability.Route(BlacksiteTraveller.Soldier(soldier), soldier.transform.position, goal);
            float remaining = route.ReportedRemaining;
            ObserveJobClock(record, soldier, job, phase, goal, remaining);
            job.Tick(Time.time, phase, remaining);
            if (old != phase)
                BlacksiteDiagnostics.Log("Job", $"object={record.ObjectId}, member={soldier.MemberIndex}, attempt={job.Generation}, phase={phase}, remaining={remaining:0.0}, objectiveFailures={Attempts(record.ObjectId).Failures}.");
            // #874 S4.0a: the arrival companion. AC2's arrival-to-acquisition time is one
            // subtraction once the moment a member reaches the object is in the objective channel;
            // the 2026-09-09 log had no arrival line at all, so "the drive was never approached"
            // could only be inferred from the absence of an Approach phase.
            if (old != phase && (phase == BlacksiteJobPhase.Approach || phase == BlacksiteJobPhase.Interact)
                && BlacksiteDiagnostics.Enabled)
            {
                string goalKind = ObjectiveGoalKind(record, out string containerOpen);
                BlacksiteDiagnostics.Log("Objective",
                    $"event=arrived, object={record.ObjectId}, member={soldier.MemberIndex}, remaining={remaining:0.00}"
                    + $", goalKind={goalKind}, containerOpen={containerOpen}"
                    // #874 S6.2: remaining is a route length, which cannot answer "did he actually get
                    // to the published point". approachError is the horizontal distance to the point
                    // the planner published, and settled is the dwell gate the access test itself uses.
                    + $", approachError={Vector3.ProjectOnPlane(soldier.transform.position - approach, Vector3.up).magnitude:0.00}"
                    + $", settled={soldier.IsSettledForInteraction}");
            }
            return true;
        }
        // #1305 G4 (R17): a Reserved cart lease ends on its own progress fact (BlacksiteRaidJob.ObservesWithoutProgress,
        // the holder's watermark to the published approach). One monitor window with no new best feeds the cart's stuck
        // verdict from where the cart stands toward its haul's goal (R11, R13) and releases the holder Failed: NoDeliverablePose,
        // else ProgressBudget (his own route is asked). The six-failure cap bounds the rest; it replaces the cart's probe charge.
        private void EndStalledCartLease(ref BlacksiteObjectiveSnapshot r, BlacksiteSoldierAI holder, PayloadCartController cart, HandleApproach handle)
        {
            int observes = Job(r).ObservesWithoutProgress;
            if (observes < BlacksiteMotionMonitor.WindowSeconds / ObserveSeconds) return;
            string refusal = _lastApproachRefusal.TryGetValue(r.ObjectId, out string last) ? last : BlacksiteDiagnosticFormat.None;
            string cartRoute = cart.NoteBlockedReleaseServer(BlacksiteWorld.Outside(cart) ? CargoDeliveryPoint(r) : _mission.SquadDirector.InsideMainEntrance);
            ReleaseJob(ref r, holder, true, $"no lease progress in {observes} observes, handle={handle}, last refusal={refusal}, cartRoute={cartRoute}",
                handle == HandleApproach.NoDeliverablePose ? BlacksiteObjectiveCloseCause.NoDeliverablePose : BlacksiteObjectiveCloseCause.ProgressBudget);
        }

        /// <summary>
        /// #874 S4: the monitor's <c>stalled</c> for a member that holds a record, answered with a
        /// different goal. A carrier drops what it carries (Failed, one objective failure, the 30 s
        /// operator bench) unless a committed traversal holds him; #1293 R2: a cart's coupled pusher
        /// is let go by the cart instead (see below). A holder
        /// of a reservation is released through <see cref="ReleaseJob"/> as a
        /// <see cref="BlacksiteObjectiveCloseCause.ProgressBudget"/> close - the traveller's own
        /// report, so a holder in the objective's world is asked for his route from where he stands
        /// and an objective he has no complete route to escalates (#1305 C4) - unless <see cref="BlacksiteObjectivePolicy.ShouldReleaseHolder"/>
        /// vetoes it on a fact that ends on its own event (a traversal wait, combat, a committed
        /// crossing), after which the ledger's next window answers again. #1293 R2 (rule 3): a
        /// <paramref name="stranded"/> holder is released without the veto, as an
        /// <see cref="BlacksiteObjectiveCloseCause.Interrupted"/> close - the ledger never answers
        /// him again, and the objective is no less reachable for his body. False when the member
        /// holds no record, so the planner answers its duty instead.
        /// </summary>
        internal bool AnswerStalledRecord(BlacksiteSoldierAI member, string reason, bool stranded)
        {
            if (_mission == null || !_mission.IsServer || member == null) return false;
            ulong id = member.NetworkObjectId;
            int carried = FindCarrier(id);
            if (carried >= 0)
            {
                if (member.HasCommittedJobTraversal) return true;
                // #1293 R2: a coupled pusher whose plan saw no cart progress within the window is the
                // ledger's miss (a cart with no plan already let him go, #1305 R23). The cart lets him go
                // as `blocked`, which feeds its stuck verdict, and Refresh's Carried arm drops the record.
                var cart = Resolve(Records[carried].ObjectId)?.GetComponent<PayloadCartController>();
                if (cart != null && cart.TryFollowNpcServer(member)) { cart.ReleaseNpcPusherServer("blocked"); return true; }
                _operatorRetry[(Records[carried].ObjectId, id)] = Time.time + 30f;
                Drop(carried, member.transform.position, "carrier " + reason);
                return true;
            }
            for (int i = 0; i < Records.Count; i++)
            {
                var record = Records[i];
                if (record.CarrierId != id || record.Phase != BlacksiteObjectivePhase.Reserved) continue;
                if (!stranded && BlacksiteObjectivePolicy.ShouldReleaseHolder(member.IsWaitingForTraversal,
                        member.IsObjectiveCombatActive, member.HasCommittedJobTraversal)
                    == BlacksiteHolderRelease.Keep)
                    return true;
                ReleaseJob(ref record, member, false, reason,
                    stranded ? BlacksiteObjectiveCloseCause.Interrupted : BlacksiteObjectiveCloseCause.ProgressBudget);
                Records[i] = record;
                return true;
            }
            return false;
        }
        private readonly Dictionary<ulong, int> _pickupProbeFailures = new Dictionary<ulong, int>();
        // #874 S6.2: the cached approach point is only valid while the state that produced it holds.
        // _approachTargets keys the cache on the object position alone, so a lock-anchor point
        // survived the container opening and the squad would have kept walking to the door of an
        // open container. The barrier's own state is part of the key.
        //
        // #874 F06: its IDENTITY is, not merely whether something is shut. A Shadow Drive sits
        // behind two barriers in series - the shipping container, then the internal cache inside it
        // - and both of them shut read as the same `true`. On 2026-09-21 the container opened at
        // t=1029.52 with the cache still closed, this key still matched, and the resolver kept
        // publishing the ring point it had resolved around the CONTAINER lock: attempt 12 closed at
        // t=1030.21 on goal=(40.37, -237.58, 92.98), 2.26 m from the point the same resolver
        // produced once both barriers were open (attempt 13, goal=(42.54, -237.53, 92.36)). Storing
        // the lock anchor beside the flag makes the handover between the two barriers a cache miss,
        // which is what it has always been in fact.
        private readonly Dictionary<ulong, (bool Closed, Vector3 Anchor)> _approachBarrier
            = new Dictionary<ulong, (bool, Vector3)>();
        /// <summary>
        /// #874 F06: how far apart two barrier anchors have to be to be different barriers. It is
        /// the staleness epsilon the approach cache already measures a moved target with (0.35 m),
        /// so "the target moved" and "the barrier changed" are one tolerance rather than two that
        /// can disagree. A container lock and the interaction point of a crate standing inside that
        /// container are metres apart; a door leaf settling as it opens is not.
        /// </summary>
        private const float BarrierIdentityMetres = .35f;
        /// <summary>
        /// #874 F06: WHICH barrier currently hides this record, as one value. Resolved through
        /// <see cref="BlacksiteObjectiveApproach.TryResolveBarrier"/> and nowhere else, so the
        /// approach cache and the job clock cannot form two opinions about it.
        /// </summary>
        private (bool Closed, Vector3 Anchor) BarrierIdentity(BlacksiteObjectiveKind kind)
            => BlacksiteObjectiveApproach.TryResolveBarrier(_mission, kind, out Vector3 anchor, out _)
                ? (true, anchor) : (false, Vector3.zero);
        /// <summary>
        /// #874 F06: whether the barrier moved on from the one a remembered answer was about.
        /// Absent is "changed", because nothing remembered cannot describe what is there now.
        /// </summary>
        private static bool BarrierChanged(bool had, (bool Closed, Vector3 Anchor) was, (bool Closed, Vector3 Anchor) now)
            => !had || was.Closed != now.Closed
                || (now.Closed && Vector3.Distance(was.Anchor, now.Anchor) > BarrierIdentityMetres);
        // #874 S6.2: one resolver, one goal, at every distance.
        //
        // The far branch (any range over six metres, or the other world) used to sample the floor
        // under the object and return it unprobed. On 2026-09-09 that published
        // (-35.18, -205.80, 113.90) - two metres behind the closed door of shipping container 111 -
        // as the reserved travel goal for the whole run. There is no near/far split any more: the
        // only thing distance still decides is whether a refusal spends a probe, because only an
        // operator that is actually standing there can have learned anything from the refusal.
        //
        // #950 S11H U2: what the out parameter means on a FALSE return. It is the record's own
        // position, and it is a MEASUREMENT baseline, never a destination: ObserveJob measures the
        // attempt's remaining route against it, and that route has to stay identified with the
        // object so the travel budget can expire instead of being re-baselined by a goal that moves
        // with the member. The only authority on whether a standing point exists is
        // _pickupApproaches, which is written on success and on nothing else, and which
        // ClearAttemptState hands to _closedStandingPoint for the close line. Every caller that
        // PUBLISHES this value as a goal must refuse on a false return; see the report for
        // PickupApproach (BlacksiteObjectiveManifestController.Raid.cs), which this unit does not own.
        private bool TryPickupApproach(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI soldier, out Vector3 point)
        {
            point = record.Position;
            // #1305 G4: a cart's walk is its handle answer (TryHandleApproach), never its own row; this
            // answers only whether it is still in play (a Live row), with record.Position as the baseline.
            if (record.Kind == BlacksiteObjectiveKind.Payload) return TryLiveRow(record.ObjectId, out _);
            // #1153: no standing point for an operator who is not in the target's world yet. This is
            // the one site every pickup point chains through - Refresh's reserved leg calls it
            // directly, the planner's order and the cargo loader reach it through PickupApproach, and
            // the witness's stand and #1148's barrier resolution begin below - so the rule is asked
            // here and in no consumer. The false return publishes record.Position, which is the
            // measurement baseline ObserveJob needs and never a destination: the goal a cross-world
            // operator actually walks is the entrance, which ObserveJob already chooses, and no probe
            // is charged because none was spent.
            if (!BlacksiteObjectivePolicy.MayWorkRecord(soldier.isOutside == ObjectiveWorld(record),
                    record.Phase == BlacksiteObjectivePhase.Carried))
                return false;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (record.Kind == BlacksiteObjectiveKind.Witness)
            {
                // #964 S14 U2 / #989 C2 / #1271 S2 (rules 3 and 4): a witness is a body on the navmesh,
                // so his standing point is where he is standing - the record's one position, his Live
                // row (#1293 R1: his transform, published by the director's pass), seen or not (a
                // sighting decides what the squad says, never where it walks). No ring: the escort's
                // gate is body to body (CustodyTakeDistance 1.2). The ring's cache holds while the same
                // operator asks and the body has moved under 0.35 m, so a still man costs no Route.
                // TryStand samples the body onto the navmesh and refuses a point the operator has no
                // complete route to: on 2026-09-29 round 2 every witnessPoint line from 21784
                // (t=411.82) to 41084 (t=670.22) was PathPartial, and the operator walked to the end
                // of that partial route and stood there. #1293 R2: a route is all it asks of a
                // moving target; there is no body-fit veto, so no refusal is a stand veto.
                ulong witnessId = record.ObjectId;
                // No Live row: he is gone, dead or held aboard (PublishWitnessRowsServer), and there is
                // nobody to stand beside. The false return keeps record.Position as the baseline.
                if (!TryLiveRow(witnessId, out var witnessRow)) return false;
                Vector3 live = witnessRow.Position;
                if (SavedApproach(witnessId, soldier, live, ref point)) return true;
                bool stood = BlacksiteObjectiveApproach.TryStand(live, soldier,
                    out Vector3 sampled, out NavMeshPathStatus? witnessStatus, out string witnessRefusal);
                if (BlacksiteDiagnostics.Enabled
                    && (!_witnessPointLogAt.TryGetValue(witnessId, out float nextWitnessLog) || Time.time >= nextWitnessLog))
                {
                    _witnessPointLogAt[witnessId] = Time.time + 1f;
                    BlacksiteDiagnostics.Log("Objective",
                        $"event=witnessPoint, object={witnessId}, member={soldier.MemberIndex}"
                        + $", source={(stood ? "live" : "refused")}"
                        + $", navStatus={(witnessStatus.HasValue ? witnessStatus.Value.ToString() : BlacksiteDiagnosticFormat.None)}"
                        + $", point={(stood ? BlacksiteDiagnosticFormat.V(sampled) : BlacksiteDiagnosticFormat.None)}"
                        + $", live={BlacksiteDiagnosticFormat.V(live)}, reason={(witnessRefusal ?? BlacksiteDiagnosticFormat.None)}");
                }
                if (stood) return PublishApproach(record, soldier, sampled, live, out point);
                // A refusal is paced by the ring's own one-second gate, so a man crossing a stair
                // costs one probe a second rather than one per resolver call.
                if (_approachRetry.TryGetValue(witnessId, out float witnessRetry) && Time.time < witnessRetry) return false;
                _approachRetry[witnessId] = Time.time + 1f;
                return RefuseApproach(record, soldier, witnessRefusal);
            }
            bool barrierClosed = BlacksiteObjectiveApproach.TryResolveBarrier(_mission, record.Kind, out var lockAnchor, out var barrierBounds);
            var barrier = (Closed: barrierClosed, Anchor: barrierClosed ? lockAnchor : Vector3.zero);
            bool hadBarrier = _approachBarrier.TryGetValue(record.ObjectId, out var cachedBarrier);
            bool barrierChanged = BarrierChanged(hadBarrier, cachedBarrier, barrier);
            if (!barrierChanged && SavedApproach(record.ObjectId, soldier, record.Position, ref point)) return true;
            // #874 F06: the retry gate paces the RING; it is not a verdict on the room.
            //
            // It used to answer false, and a false return here is what the reserved leg reads as
            // "this operator has no standing point": TryAcquireOnDwellServer drops atAccess for it,
            // which removes the 1.1 s pickup dwell and starts it again from zero. On
            // 2026-09-21 attempt 15 that is the whole 19 s failure - member 1 stood settled 0.37 m
            // from a published point with containerOpen=True and no pickupRejected line anywhere in
            // the window, while phase flipped Interact/Approach eleven times on roughly this
            // gate's own period until the local budget closed the attempt at t=1116.30. A point
            // this resolver published for this operator, about the barrier that is still there, is
            // the honest answer while the gate is shut. A barrier that has CHANGED bypasses
            // the gate outright, because the previous answer is not a second old, it is about a
            // different barrier - which is what made attempt 12 close 0.69 s after the container
            // opened. Nothing is written to _pickupApproaches here, so the standing point the
            // unreachable rule and event=closed read is still only ever a resolved one.
            if (!barrierChanged && _approachRetry.TryGetValue(record.ObjectId, out float next) && Time.time < next)
            {
                if (!_pickupApproaches.TryGetValue(record.ObjectId, out var lastResolved)) return false;
                point = lastResolved; return true;
            }
            // #874 F06: the barrier this resolution is ABOUT, recorded whether or not it finds a
            // point. It has to be written here and not in the success arm below: a refused ring
            // that recorded nothing would read as "the barrier changed" on the next call and
            // bypass the gate it just set, so a room with no standing point would spend its
            // thirty-six candidate probes every manifest tick instead of once a second.
            _approachRetry[record.ObjectId] = Time.time + 1f;
            _approachBarrier[record.ObjectId] = barrier;
            Vector3 floor = SupportFloor(record);
            int attempt = _pickupFailures.TryGetValue(record.ObjectId, out int tries) ? tries : 0;
            bool resolvedOk = BlacksiteObjectiveApproach.TryResolve(record.Position, floor, barrierClosed, lockAnchor, barrierBounds,
                soldier.transform.position, soldier.transform, ReachabilityOrigin(record.Position, ObjectiveWorld(record)),
                mask, attempt, out var resolved, out string refusal);
            return resolvedOk
                ? PublishApproach(record, soldier, resolved, record.Position, out point)
                : RefuseApproach(record, soldier, refusal);
        }
        // #1106 / #1305 E2: the floor the object stands on - the pickup ring's centre and the holder's
        // reach verdict's fallback destination (HolderReach), one answer for both.
        private Vector3 SupportFloor(BlacksiteObjectiveSnapshot record)
        {
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            var obj = Resolve(record.ObjectId); Vector3 floor = record.Position;
            float closest = float.PositiveInfinity;
            // #1106: 2026-09-19 round 2 the downward floor ray from the drive inside an opened
            // crate hit the crate body top at -225.04 while the container floor was at -225.85,
            // so every ring candidate was 0.81 m above the navmesh and refused. Ignore the
            // container and cache crate hierarchies, open or closed, resolved once per call.
            Transform containerXf = null, cacheXf = null;
            if (record.Kind == BlacksiteObjectiveKind.ShadowDrive)
            {
                containerXf = BreachContainerController.FindByNetworkId(_mission.ExpectedTargetNetworkId)?.transform;
                cacheXf = Resolve(_mission.SecondaryTargetNetworkId)?.GetComponent<ShadowRaidCargoCrate>()?.transform;
            }
            foreach (var hit in Physics.RaycastAll(record.Position + Vector3.up * .6f, Vector3.down, 6f, mask, QueryTriggerInteraction.Ignore))
            {
                if (hit.normal.y < .65f
                    || (obj != null && hit.transform.IsChildOf(obj.transform))
                    || (containerXf != null && hit.transform.IsChildOf(containerXf))
                    || (cacheXf != null && hit.transform.IsChildOf(cacheXf))
                    || hit.distance >= closest) continue;
                closest = hit.distance; floor = hit.point;
            }
            return floor;
        }
        // The standing point this operator was last published for a target that has since moved under
        // 0.35 m; the ring's and the witness's one cache. A miss leaves point (the baseline) as it was.
        // #1283 S6: a hit is the resolver's answer, so it clears an earlier refusal as a publish does.
        private bool SavedApproach(ulong objectId, BlacksiteSoldierAI soldier, Vector3 target, ref Vector3 point)
        {
            if (!_pickupApproaches.TryGetValue(objectId, out var saved)
                || !_approachOwners.TryGetValue(objectId, out var owner) || owner != soldier.NetworkObjectId
                || !_approachTargets.TryGetValue(objectId, out var savedTarget) || Vector3.Distance(savedTarget, target) >= .35f)
                return false;
            point = saved; _lastApproachRefusal[objectId] = BlacksiteDiagnosticFormat.None; return true;
        }
        // #1271 S2: the one publish every standing point takes, the ring's and the witness's. It is
        // the only writer of _pickupApproaches, which ClearAttemptState hands to _closedStandingPoint.
        private bool PublishApproach(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI soldier, Vector3 resolved,
            Vector3 target, out Vector3 point)
        {
            // #1106: the last resolution's refusal (or none on success: this publish or a SavedApproach
            // hit) is what the ungated close line prints, so a success must overwrite an earlier refusal.
            _lastApproachRefusal[record.ObjectId] = BlacksiteDiagnosticFormat.None;
            _pickupApproaches[record.ObjectId] = resolved; _approachTargets[record.ObjectId] = target;
            _approachOwners[record.ObjectId] = soldier.NetworkObjectId; point = resolved; return true;
        }
        // ...and the one refusal. Only a close operator spends a probe: a refusal measured from 190 m
        // away is a statement about the route, not about the spot.
        private bool RefuseApproach(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI soldier, string refusal)
        {
            _lastApproachRefusal[record.ObjectId] =
                string.IsNullOrWhiteSpace(refusal) ? BlacksiteDiagnosticFormat.None : refusal;
            if (!BlacksiteObjectivePolicy.RefusalIsLocal(soldier.isOutside == ObjectiveWorld(record),
                    Vector3.Distance(soldier.transform.position, record.Position)))
                return PickupRejected(record, refusal + "; not charged, operator is not local", soldier);
            int failed = _pickupProbeFailures.TryGetValue(record.ObjectId, out int oldFailures) ? oldFailures + 1 : 1;
            _pickupProbeFailures[record.ObjectId] = failed;
            return PickupRejected(record, refusal + "; probe " + failed + "/3", soldier);
        }
        // #1283 S6: the approach resolver's last verdict on this record is a refusal, measured from afar.
        private bool StandRefusedFromAfar(BlacksiteObjectiveSnapshot record, BlacksiteSoldierAI member)
            => record.Kind != BlacksiteObjectiveKind.Payload
               && _lastApproachRefusal.TryGetValue(record.ObjectId, out string refusal) && refusal != BlacksiteDiagnosticFormat.None
               && !BlacksiteObjectivePolicy.RefusalIsLocal(member.isOutside == ObjectiveWorld(record),
                   Vector3.Distance(member.transform.position, record.Position));
        // #874 S1.2 reach validation. The handle sits FollowDistance behind the cart, so the cart
        // body, a wall or a peer can occupy it. Validate it against navigation and real colliders
        // (ignoring the cart itself, which the pusher stands against) and otherwise take the
        // nearest clear point that still leaves the attach gate satisfiable from a settled stop.
        internal const float HandleStopDistance = .35f;
        internal const float PickupStopDistance = .3f;
        internal const float PickupReachDistance = .8f;
        internal const float CargoLoadStopDistance = .8f;
        internal const float CargoLoadReachDistance = 1.5f;
        // #874 S6.2: the container/cache lock's own reach contract. Opening the barrier is the FIRST
        // step of the drive job, so the duty order that carries the operator to it needs the
        // interaction's own gate, not the item pickup's 0.30/0.80.
        //
        // The gate is BlacksiteObjectiveInteraction's align range, measured to the lock anchor, and
        // it is unchanged at 1.7 m. The stop is NOT reach - slack - margin here: unlike a loose item,
        // the anchor itself can never be stood on (it is 0.13 m off the container's front face, and
        // the navmesh is baked at a 0.45 m agent radius), so the published point is always a ring
        // radius away from it and that offset has to be paid out of the same gate. The duty order
        // therefore stops the operator at exactly the stopping distance the interaction's own
        // approach phase uses, so the handover from the duty board to the interaction does not move
        // him a second time, and BlacksiteObjectiveApproach.LockRingMax takes what the gate has
        // left: 1.70 - 0.30 - 0.20 (settle) - 0.10 (sampled destination) = 1.10 m.
        internal const float ContainerLockReachDistance = 1.7f;
        internal const float ContainerLockStopDistance = BlacksiteObjectiveInteraction.OverrideStoppingDistance;
        // #874 S4.2: the player-response order's own gate, which used to be an arrival tolerance
        // with no movement contract behind it. The gate is unchanged at 0.8 m; the stop distance
        // is derived from it - 0.8 less the settle slack (0.2) less the sampled-destination
        // margin (0.1) - so a settled member is actually inside the gate it is measured against.
        internal const float PlayerResponseReachDistance = .8f;
        internal const float BlacksitePlayerResponseStopDistance =
            PlayerResponseReachDistance - BlacksiteArrivalPolicy.SettleSlack - BlacksiteArrivalPolicy.ProfileReachMargin;
        internal static BlacksiteApproachProfile HandleProfile(Vector3 facing) => HandleProfile(facing, false);
        /// <summary>
        /// #1078 rework: the handle order's profile, in one of two forms.
        ///
        /// The invariant a relocation budget exists for is
        ///   point-to-anchor + the profile's ARRIVAL FLOOR &lt;= the action's gate,
        /// where the floor is what BlacksiteArrivalPolicy.Resolve declares arrival at,
        /// <c>min(stop + SettleSlack, reach - ProfileReachMargin)</c>. For the shipped handle profile
        /// that is <c>min(.35 + .20, .85 - .10) = .55</c>, so a published point may sit at most
        /// <c>.85 - .55 = .30</c> m from the handle - which is exactly RelocationBudget, and exactly
        /// why the first pass at #1078 was wrong to publish points out to .75 m against it: a member
        /// settling short of one stood .88-1.30 m from the handle and IsWithinNpcAttachDistance refused.
        ///
        /// The repair is to issue an outer-ring point with a profile that can support it, rather than
        /// to widen a gate. Neither NpcAttachDistance nor HandleStopDistance moves. The relocated
        /// stop is BlacksiteApproachProfile's OWN floor, .15 m - the tightest stop the type accepts
        /// (it clamps with <c>Mathf.Max(.15f, stop)</c>) and the value the combat profile already
        /// ships at - so the relocated floor is <c>min(.15 + .20, .85 - .10) = .35</c> m.
        /// </summary>
        internal static BlacksiteApproachProfile HandleProfile(Vector3 facing, bool relocated) =>
            new BlacksiteApproachProfile(relocated ? HandleRelocationStopDistance : HandleStopDistance,
                PayloadCartController.NpcAttachDistance, facing);
        /// <summary>BlacksiteApproachProfile's own stop floor, which is the tightest it will accept.</summary>
        internal const float HandleRelocationStopDistance = .15f;
        /// <summary>The relocated profile's arrival floor: .15 + .20 = .35 m.</summary>
        internal const float HandleRelocationArrivalFloor =
            HandleRelocationStopDistance + BlacksiteArrivalPolicy.SettleSlack;
        /// <summary>
        /// The settled ring: HandleProfile's own RelocationBudget as a compile-time constant,
        /// .85 - .35 - .20 = .30 m, unchanged. A point inside it is one a member who stops short of
        /// it is still inside the attach gate from, under the profile the order has always carried.
        /// </summary>
        internal const float HandleSettledBudget = PayloadCartController.NpcAttachDistance
            - HandleStopDistance - BlacksiteArrivalPolicy.SettleSlack;
        /// <summary>
        /// #1078 rework: the outer ring, solved from the inequality above for the relocated profile:
        ///   NpcAttachDistance .85 - ProfileReachMargin .10 - HandleRelocationArrivalFloor .35 = .40 m.
        /// The ProfileReachMargin term is paid because the published point is itself a sampled
        /// navigation point, exactly as every other profile in this file pays it. The handle sits
        /// FollowDistance 1.4 m ahead of the cart, so a cart parked against the ship or a crate puts
        /// it inside a navmesh carve (2026-09-15: every candidate refused with "no navmesh").
        /// .40 m plus the wider SAMPLE radius below is what reaches the mesh; it is not a widened
        /// gate, it is the largest offset the arrival contract can actually deliver a body to.
        /// </summary>
        internal const float HandleRelocationBudget = PayloadCartController.NpcAttachDistance
            - BlacksiteArrivalPolicy.ProfileReachMargin - HandleRelocationArrivalFloor;
        /// <summary>
        /// Whether a published handle point needs the relocated profile. Pure, and derived from the
        /// point itself rather than from remembered state, so it cannot go stale across a round or
        /// disagree with what the ring published.
        /// </summary>
        internal static bool IsHandleApproachRelocated(PayloadCartController cart, Vector3 point)
            => cart != null && Vector3.Distance(point, cart.NpcHandlePosition) > HandleSettledBudget;
        /// <summary>
        /// #1078: ring 0 is the handle, ring 1 the settled budget on the spokes it has always used,
        /// ring 2 the relocation budget rotated half a heading so it re-tests none of them.
        /// </summary>
        private const int HandleRings = 3;
        private const int HandleHeadings = 8;
        private static float HandleRingRadius(int ring)
            => ring <= 1 ? HandleSettledBudget : HandleRelocationBudget;
        // #964 S14 U2: the escort's reach contract, on both legs of the witness job - closing on
        // him to take custody, and walking him to the ship's boarding point.
        //
        // The reach IS U1's fixed BlacksiteCustodySettings.CustodyTakeDistance 1.20 m, not a number
        // of this file's own. BlacksiteCustodyPolicy.CanBegin measures body to body against it and
        // refuses outside it, so if the duty board carried a second "close enough" the escort could
        // be declared arrived at a distance custody refuses and the record would sit in Reserved
        // spending its travel budget a hand's breadth away from the man.
        //
        // The stop is that reach less the two budgets every other profile in this file pays:
        // BlacksiteArrivalPolicy.SettleSlack 0.20, because a NavMeshAgent with autoBraking settles
        // anywhere inside its stopping distance, and BlacksiteArrivalPolicy.ProfileReachMargin
        // 0.10, because the issued destination is itself a sampled navigation point.
        // 1.20 - 0.20 - 0.10 = 0.90 m, which is exactly the shape of
        // BlacksitePlayerResponseStopDistance above; a settled escort therefore stands 1.10 m from
        // the goal, inside the 1.20 m gate rather than on its boundary. Nothing is widened: 1.20 is
        // U1's shipped constant and 0.20/0.10 are the arrival policy's.
        //
        // requiresFacing is false. #1293 R2: the witness is held at the escort's side
        // (WhistleblowerEnemyAI.CustodySeat), so an escort held to facing him would turn his back
        // on the ship; and a man is taken from any side.
        internal const float CustodyReachDistance = BlacksiteCustodySettings.CustodyTakeDistance;
        internal const float CustodyStopDistance =
            CustodyReachDistance - BlacksiteArrivalPolicy.SettleSlack - BlacksiteArrivalPolicy.ProfileReachMargin;
        internal static BlacksiteApproachProfile CustodyProfile(Vector3 facing) =>
            new BlacksiteApproachProfile(CustodyStopDistance, CustodyReachDistance, facing, requiresFacing: false);
        // #1305 G4: the handle's answer. Pose, a validated stand; Pending, none now but a candidate passed every pose
        // gate and failed only an operator-side one (a squadmate on it, no complete path from where he stands) - its
        // nav position is the point, so the walk there is measurable; NoDeliverablePose, every candidate failed a pose
        // gate (the point is the raw handle). The cart has ONE soldier push pose (NpcHandlePosition).
        internal enum HandleApproach { Pose, Pending, NoDeliverablePose }
        private readonly Dictionary<ulong, (HandleApproach Answer, Vector3 At)> _handleAnswers = new Dictionary<ulong, (HandleApproach, Vector3)>();
        private HandleApproach TryHandleApproach(BlacksiteObjectiveSnapshot record, PayloadCartController cart, BlacksiteSoldierAI soldier, out Vector3 point)
        {
            Vector3 handle = cart.NpcHandlePosition;
            point = handle;
            ulong id = record.ObjectId;
            bool cached = _pickupApproaches.TryGetValue(id, out var saved)
                && _approachOwners.TryGetValue(id, out var owner) && owner == soldier.NetworkObjectId;
            // The handle only moves when the cart moves. Re-probe when it does; otherwise the
            // validated spot stands, so this costs no queries on the ordinary tick.
            bool throttled = _approachRetry.TryGetValue(id, out float next) && Time.time < next;
            if (cached && (throttled || (_approachTargets.TryGetValue(id, out var target) && Vector3.Distance(target, handle) < .2f)))
            { point = saved; return HandleApproach.Pose; }
            if (throttled)
            { if (!_handleAnswers.TryGetValue(id, out var last)) return HandleApproach.Pending; point = last.At; return last.Answer; }
            _approachRetry[id] = Time.time + .25f;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            System.Predicate<Collider> ignoreCart = c => c != null && c.transform.IsChildOf(cart.transform);
            string refusal = "no navmesh"; Vector3? waitAt = null;
            // #1078: the outer rings' best candidate, published only if no settled one resolved.
            bool haveOuter = false; Vector3 outer = handle;
            for (int ring = 0; ring < HandleRings; ring++)
                for (int i = 0; i < (ring == 0 ? 1 : HandleHeadings); i++)
                {
                    // Ring 0 is the handle itself; ring 1 is the settled budget on the same spokes it
                    // has always used; ring 2 is the relocation budget, rotated half a heading so it
                    // re-tests none of the spokes the ring inside it already refused.
                    Vector3 candidate = ring == 0 ? handle
                        : handle + Quaternion.Euler(0, i * (360f / HandleHeadings) + (ring - 1) * (180f / HandleHeadings), 0)
                            * Vector3.forward * HandleRingRadius(ring);
                    if (!NavMesh.SamplePosition(candidate, out var nav, BlacksiteObjectiveApproach.SampleRadius, NavMesh.AllAreas)
                        || Mathf.Abs(nav.position.y - handle.y) > .8f) { refusal = "no navmesh"; continue; }
                    // #1078: the first veto is the cart's own production attach test, not a ring literal: the
                    // old .35 m rule spoke about the duty profile's relocation budget, not about whether a body
                    // standing there can take the handle (2026-09-15: "no navmesh" for mesh beyond the ring).
                    if (!cart.IsWithinNpcAttachDistance(nav.position)) { refusal = "outside attach gate"; continue; }
                    // #1078 rework: and the second is the arrival contract. A body standing ON the
                    // point can take the handle, but the order has to be able to DELIVER him to it:
                    // the sample radius reaches further than the ring does, and a point past the
                    // relocation budget is one no profile this order may carry can settle inside the
                    // gate from. See HandleProfile for the inequality.
                    if (Vector3.Distance(nav.position, handle) > HandleRelocationBudget)
                    { refusal = "outside handle relocation budget"; continue; }
                    if (!BlacksitePhysicalStep.CanOccupy(soldier.transform, nav.position, mask, ignoreCart))
                    { refusal = "occupied"; continue; }
                    string operatorGate = _members.Any(m => m != soldier && !m.isEnemyDead && Vector3.Distance(m.transform.position, nav.position) < .8f) ? "squadmate within 0.8 m"
                        : !BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(soldier), soldier.transform.position, nav.position).Reachable ? "no complete path" : null;
                    if (operatorGate != null) { refusal = operatorGate; waitAt = waitAt ?? nav.position; continue; }
                    if (Vector3.Distance(nav.position, handle) > HandleSettledBudget)
                    { if (!haveOuter) { haveOuter = true; outer = nav.position; } continue; }
                    _lastApproachRefusal[id] = BlacksiteDiagnosticFormat.None; _handleAnswers.Remove(id);
                    point = nav.position; _operatorLane[soldier.NetworkObjectId] = nav.position;
                    _pickupApproaches[id] = nav.position; _approachTargets[id] = handle; _approachOwners[id] = soldier.NetworkObjectId;
                    return HandleApproach.Pose;
                }
            // #1078: nothing settled resolved, so publish the outer point rather than nothing. It is inside
            // the cart's own attach gate AND the relocation budget, so the order that carries it
            // (HandleProfile(facing, relocated: true), chosen by IsHandleApproachRelocated) settles the
            // member inside the gate rather than 0.88-1.30 m outside it.
            if (haveOuter)
            {
                _lastApproachRefusal[id] = BlacksiteDiagnosticFormat.None; _handleAnswers.Remove(id);
                point = outer; _operatorLane[soldier.NetworkObjectId] = outer;
                _pickupApproaches[id] = outer; _approachTargets[id] = handle; _approachOwners[id] = soldier.NetworkObjectId;
                return HandleApproach.Pose;
            }
            _lastApproachRefusal[id] = refusal;
            _operatorLane.Remove(soldier.NetworkObjectId);
            _pickupApproaches.Remove(id); _approachOwners.Remove(id);
            // #1305 G4: no probe is charged (R4: EndStalledCartLease ends the lease); the answer and last refusal are logged, throttled.
            var answer = waitAt.HasValue ? HandleApproach.Pending : HandleApproach.NoDeliverablePose;
            point = waitAt ?? handle; _handleAnswers[id] = (answer, point);
            PickupRejected(record, $"cart handle answer={answer}, last refusal={refusal}", soldier);
            return answer;
        }
        // Reserved operator standing points only. Support-point usefulness is owned elsewhere;
        // this is the minimum needed so a helper cannot claim the lane the operator must occupy.
        private readonly Dictionary<ulong, Vector3> _operatorLane = new Dictionary<ulong, Vector3>();
        private bool InOperatorLane(ulong member, Vector3 point)
        {
            foreach (var lane in _operatorLane)
                if (lane.Key != member && Vector3.Distance(lane.Value, point) < 1.4f) return true;
            return false;
        }
        // The blocked cause of the last completed pass, replayed while its result is cached: a
        // cached deferral must not read as a successful support slot at the member's own feet.
        private readonly Dictionary<ulong, BlacksiteBlockedCause> _blockedSupport = new Dictionary<ulong, BlacksiteBlockedCause>();
        private readonly Dictionary<ulong, int> _supportCursor = new Dictionary<ulong, int>();
        // Candidates rejected in a row since the last usable one. Only four route queries fit in a
        // member's frame budget, so a pass that runs out is normally deferred; once a whole circle
        // of candidates has been refused it is the geometry, not the budget, that is saying no.
        private readonly Dictionary<ulong, int> _supportMisses = new Dictionary<ulong, int>();
        // #1018 S4 (fix batch 2, S10, design note G10 b): an index loop. SupportPoint asks this
        // once per candidate over twelve bearings per member per plan, and the LINQ form allocated
        // a closure and boxed a predicate every time. Same predicate, same 1.2 m separation
        // against both a body and another member's reserved point.
        private bool Occupied(BlacksiteSoldierAI soldier, Vector3 point)
        {
            for (int i = 0; i < _members.Count; i++)
            {
                var m = _members[i];
                if (m == null || m == soldier || m.isEnemyDead) continue;
                if (Vector3.Distance(m.transform.position, point) < 1.2f) return true;
                if (_supportPoints.TryGetValue(m.NetworkObjectId, out var reserved)
                    && Vector3.Distance(reserved, point) < 1.2f) return true;
            }
            return false;
        }
        // #872 D8 / #1018 S4 (design note C.5): what the LAST completed pass answered with, per
        // member, so a cached answer replays as the kind of answer it was. Before this the cached
        // branch below could replay a HoldWhereItStands terminal - the member's own feet - as though
        // it were a support slot, for up to three seconds, and nothing downstream could tell.
        private readonly Dictionary<ulong, BlacksiteSupportFallback> _supportFallback
            = new Dictionary<ulong, BlacksiteSupportFallback>();

        // floorY names the storey a candidate must stand on. For local security that is the
        // member's own height; for a response it is the REPORTED point's supporting floor, so a
        // fight six metres up a staircase is answered by a stairs route instead of having every
        // candidate rejected against the guard's current height before any path is computed.
        private Vector3 SupportPoint(BlacksiteSoldierAI soldier, Vector3 center, Vector3 forward, float floorY)
            => SupportPoint(soldier, center, forward, floorY, out _);

        /// <summary>
        /// #1018 S4 / #872 D8 (design note C.5): the same pass, saying WHICH of its two answers it
        /// gave - <c>Ring</c>, one of the twelve bearings, or <c>HoldWhereItStands</c>, the member's
        /// own position when no bearing stood or the frame budget deferred the pass. The twelve
        /// bearings and the 3 s cache are unchanged. #1283 S6: the tethered terminal (the anchor
        /// slot, then a hold at the anchor) is deleted; the escort caller (Orders.cs) walks only a
        /// <c>Ring</c> answer and otherwise takes the squad route, never a Partial walk to the anchor.
        ///
        /// Why the caller needs it: <c>HoldWhereItStands</c> returns the member's own position, and
        /// a destination that happens to equal the member's feet is indistinguishable from a
        /// support slot the member is already standing on. The duty board measured the first as
        /// <c>remaining=Infinity</c> through <c>NoReachableCandidate</c>, which the progress
        /// budget's closure epsilon can never satisfy - the 2026-09-12 run's 25 s Reassign cycle,
        /// fifteen to twenty-five metres behind the operator, for four minutes. With the fallback in
        /// hand the planner says the hold out loud instead
        /// (<see cref="BlacksiteAssignmentGoalKind.Hold"/>) and the budget observes a hold.
        /// </summary>
        private Vector3 SupportPoint(BlacksiteSoldierAI soldier, Vector3 center, Vector3 forward, float floorY,
            out BlacksiteSupportFallback fallback)
        {
            ulong id = soldier.NetworkObjectId;
            if (_supportUntil.TryGetValue(id, out float until) && Time.time < until
                && _supportCenters.TryGetValue(id, out var oldCenter) && Vector3.Distance(oldCenter, center) < 2f)
            {
                if (_blockedSupport.TryGetValue(id, out var held)) _planningBlocked[id] = held;
                // #1018 S4 (design note C.5, point 2): the cached FALLBACK is replayed with the
                // cached point, so a cached refusal reads as a refusal for the whole 3 s rather
                // than as a ring answer at the member's feet.
                fallback = _supportFallback.TryGetValue(id, out var cachedFallback)
                    ? cachedFallback : BlacksiteSupportFallback.Ring;
                return _supportPoints[id];
            }
            Vector3 result = soldier.transform.position; float best = float.PositiveInfinity;
            _planningBlocked.Remove(id);
            bool sameCenter = _supportCenters.TryGetValue(id, out var previous) && Vector3.Distance(previous, center) < 2f;
            int cursor = sameCenter && _supportCursor.TryGetValue(id, out int saved) ? saved : 0;
            int misses = sameCenter && _supportMisses.TryGetValue(id, out int rejected) ? rejected : 0;
            bool deferred = false;
            for (int step = 0; step < 12; step++)
            {
                int i = (cursor + step) % 12;
                _supportCursor[id] = (i + 1) % 12;
                Vector3 candidate = center + Quaternion.Euler(0, soldier.MemberIndex * 75f + i * 30f, 0) * Vector3.forward * (i < 6 ? 2.6f : 4.2f);
                // Anti-floor-snap: a sample that landed on a different storey than the one being
                // supported is refused, whichever storey that is.
                if (!NavMesh.SamplePosition(candidate, out var nav, .65f, NavMesh.AllAreas)
                    || !BlacksiteResponsePolicy.OnFloor(nav.position.y, floorY)
                    || Occupied(soldier, nav.position)) { misses++; continue; }
                if (InOperatorLane(id, nav.position)) { misses++; continue; } // #874 S1.2: keep the operator's approach lane clear.
                int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
                if (!BlacksitePhysicalStep.CanOccupy(soldier.transform, nav.position, mask)) { misses++; continue; }
                if (!SpendCandidatePath(id)) { _supportCursor[id] = i; deferred = true; break; }
                if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(soldier), soldier.transform.position, nav.position).Reachable) { misses++; continue; }
                float score = Vector3.Distance(soldier.transform.position, nav.position);
                if (score < 1.8f) { misses++; continue; }
                misses = 0;
                if (score < best) { best = score; result = nav.position; }
            }
            // A deferred pass is a frame-budget answer only while untried candidates remain;
            // a whole circle refused in a row is the geometry, and saying so lets the caller
            // move on to its bounded alternatives instead of deferring forever.
            bool budget = deferred && misses < 12;
            fallback = float.IsPositiveInfinity(best) ? BlacksiteSupportFallback.HoldWhereItStands : BlacksiteSupportFallback.Ring;
            _supportPoints[id] = result; _supportCenters[id] = center; _supportUntil[id] = Time.time + (deferred ? .2f : 3f);
            _supportMisses[id] = misses;
            // The cache and the fallback are written together, always, so they cannot disagree.
            _supportFallback[id] = fallback;
            if (float.IsPositiveInfinity(best))
            {
                _planningBlocked[id] = _blockedSupport[id] = budget
                    ? BlacksiteBlockedCause.PlanningBudget : BlacksiteBlockedCause.NoReachableCandidate;
            }
            else { _planningBlocked.Remove(id); _blockedSupport.Remove(id); _supportMisses.Remove(id); }
            return result; // Explicit blocked result is budgeted by the duty board.
        }
    }
}
