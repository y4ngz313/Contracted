using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteObjectiveManifestController
    {
        // One response record per called guard. Progress and the resolved goal are per guard; the
        // post itself - point, floor and quiet clock - is the squad's, and only the squad ends it.
        private sealed class BackupResponse
        {
            internal BlacksiteBackupPhase Phase;
            internal BlacksiteResponseGoal Kind;
            internal Vector3 Goal, Center;
            internal bool HasGoal;
            internal float NextPlan;
        }
        private readonly HashSet<ulong> _backupGuards = new HashSet<ulong>();
        private readonly Dictionary<ulong, BackupResponse> _backupResponses = new Dictionary<ulong, BackupResponse>();
        private BackupResponse Response(ulong id)
        { if (!_backupResponses.TryGetValue(id, out var r)) _backupResponses[id] = r = new BackupResponse(); return r; }
        private void EndGuardBackup(ulong id, string reason)
        {
            var phase = _backupResponses.TryGetValue(id, out var response) ? response.Phase : BlacksiteBackupPhase.Dispatched;
            _backupGuards.Remove(id); _backupResponses.Remove(id);
            _supportUntil.Remove(id); _blockedSupport.Remove(id); _planningBlocked.Remove(id);
            // The guard returns to its rendezvous post; the duty board opens a fresh Guard attempt
            // because Backup and Guard are different duty classes.
            BlacksiteDiagnostics.Log("Backup", $"memberId={id}, phase={phase}, response ended: {reason}; return to rendezvous.");
        }
        private void SetBackupPhase(BlacksiteSoldierAI guard, BackupResponse response, BlacksiteBackupPhase phase, string why)
        {
            if (response.Phase == phase) return;
            BlacksiteDiagnostics.Log("Backup", $"member={guard.MemberIndex}, phase {response.Phase} -> {phase}: {why}; floorY={_backupFloor.y:0.0}, outside={_backupOutside}.");
            response.Phase = phase; response.HasGoal = false; response.NextPlan = 0f;
        }
        private Vector3 _backupPoint, _backupFloor;
        // #1219 B1: _backupOutside is a MEMO of the report's world, written from
        // BlacksiteWorld.Outside(reporter) when the report is made; it is where a called-in guard
        // goes (RequiresInterior) and is never read as any entity's current world.
        private bool _backupOutside, _hasBackupFloor, _hadBackupFloor, _backupPointOnMesh;
        // Which evidence resolved the current anchor, verbatim in the diagnostic line, so a live
        // log names the storey's source instead of leaving an implausible height unexplained.
        private string _backupFloorSource = "none";
        private float _backupBegan, _backupLastThreat;
        // #1271 S3b: whom the post follows - the last report's reporter; a dead one leaves the last point.
        private BlacksiteSoldierAI _backupReporter;
        internal bool GuardCalledIn(ulong id) => _backupGuards.Contains(id);
        // The reported point's own supporting floor, resolved once per target. Every candidate is
        // judged against THIS height; a NavMesh sample further away than the anchor tolerance is a
        // different storey, so the raw reported height is kept rather than snapped downstairs.
        //
        // #874 S9.5: in run 2 the reporter was taking melee on a stair at y=-178.8 while its floor
        // was y=-182.1. The sample failed there (the same off-mesh column the stair recovery owns)
        // and the old else branch adopted the RAW AIRBORNE height, so every support candidate,
        // every floor node and the entrance hold were judged against a storey that does not exist
        // and both rifle guards ended 12 s after dispatch with "reported floor y=-179.8 has no
        // reachable standing point after 4 bounded alternatives" while the fight was on the top
        // floor 6-20 m from the entrance (run2-adamance.log t=640.13..652.37). The anchor is now
        // pinned to evidence, in order: a point the navigator itself last stood on, a NavMesh
        // sample, the supporting ground within one body height under the report, the previous
        // anchor. The raw report is the anchor only on a FIRST report with no other evidence at
        // all, because there is then nothing else to answer with.
        private Vector3 ReportedFloor()
        {
            if (_hasBackupFloor) return _backupFloor;
            if (TryReportedFloor(_backupPoint, out var floor, out string source))
            { _backupFloor = floor; _backupFloorSource = source; _hasBackupFloor = _hadBackupFloor = true; return _backupFloor; }
            if (_hadBackupFloor)
            {
                // The report carries no evidence about a storey. Refuse it: the anchor and the
                // response both stand, and the contact point still tracks the fight.
                _backupFloorSource = "previous"; _hasBackupFloor = true;
                BlacksiteDiagnostics.LogRateLimited("backup.floorRefused", 2f, "Backup",
                    $"report at {_backupPoint} carries no on-mesh evidence; anchor held at floorY={_backupFloor.y:0.0}, floorSource=previous.");
                return _backupFloor;
            }
            _backupFloor = _backupPoint; _backupFloorSource = "raw"; _hasBackupFloor = _hadBackupFloor = true;
            return _backupFloor;
        }
        // The evidence order itself, so the report sites and the anchor share one definition.
        private bool TryReportedFloor(Vector3 point, out Vector3 floor, out string source)
        {
            floor = point; source = "raw";
            // The navigator only refreshes its last good position while unblocked and able to
            // occupy the spot, so a report taken from it is already standing ON the mesh.
            if (_backupPointOnMesh) { source = "lastGoodNav"; return true; }
            if (NavMesh.SamplePosition(point, out var nav, BlacksiteResponsePolicy.ReportSampleRadius, NavMesh.AllAreas)
                && Mathf.Abs(nav.position.y - point.y) <= BlacksiteResponsePolicy.FloorAnchorTolerance)
            { floor = nav.position; source = "sample"; return true; }
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            // The ray starts a finger above the report so a surface exactly under the foot is hit
            // rather than started inside; the drop it may accept is still one body height.
            const float RayLift = .05f;
            if (BlacksitePhysicalStep.TrySupportingGround(point + Vector3.up * RayLift, point.y, mask, out var ground, out _,
                BlacksiteResponsePolicy.ReportDropMetres + RayLift, BlacksiteResponsePolicy.ReportDropMetres))
            { floor = new Vector3(point.x, ground.point.y, point.z); source = "supportingGround"; return true; }
            return false;
        }
        // #874 S9.5: a soldier taking melee on a railing is precisely when its transform is least
        // trustworthy, and the transform is what the response used to publish. The navigator's own
        // last good navmesh position is on-mesh by construction; it is a better reading of the SAME
        // place while it is within one body height vertically and the sample radius horizontally,
        // and anything further away is a different place rather than a cleaner reading of this one.
        private Vector3 ReportPoint(BlacksiteSoldierAI reporter, out bool onMesh)
        {
            onMesh = false;
            Vector3 point = reporter.transform.position;
            var navigator = reporter.Navigator;
            if (navigator == null || !navigator.HasLastGoodPosition) return point;
            Vector3 good = navigator.LastGoodPosition;
            if (Mathf.Abs(good.y - point.y) > BlacksiteResponsePolicy.ReportDropMetres
                || Vector3.ProjectOnPlane(good - point, Vector3.up).magnitude > BlacksiteResponsePolicy.ReportSampleRadius)
                return point;
            onMesh = true; return good;
        }
        internal void RequestGuardBackup(BlacksiteSoldierAI reporter, bool casualty)
        {
            // #974: a guard answers a contact because it is standing on the same moon as the man who
            // called it; nothing in a backup run touches the ship's cable.
            if (!_mission.IsServer || Returning || reporter == null || Ship == null || !Ship.SquadOnMoon) return;
            _backupLastThreat = Time.time; _backupReporter = reporter;
            // A casualty call is answered at the casualty's own position; a damage call at the
            // reporter's contact point. Both dispatch.
            Vector3 point = ReportPoint(reporter, out bool reportOnMesh); bool outside = BlacksiteWorld.Outside(reporter);
            // #1271 S3b: backup is a post (pass rule 8). The first report opens it; a later one only
            // moves it and renews its quiet clock, so a report never dispatches an open post again.
            if (_backupGuards.Count > 0) { Retarget(point, outside, reportOnMesh); return; }
            _backupPoint = point; _backupOutside = outside; _backupPointOnMesh = reportOnMesh; _hasBackupFloor = false;
            _mission.SquadDirector.CollectLivingMembers(_members);
            bool dispatched = false;
            // A guard already carrying an objective keeps its cargo protection and is not called.
            // #1293 R2: nor is a stranded guard, who cannot walk to the post.
            foreach (var guard in _members.Where(s => s.IsRifleGuard && !s.HasBoardedShip && !s.HasLostTransport && !IsCarrier(s.NetworkObjectId)
                && !s.Navigator.IsStranded))
                if (_backupGuards.Add(guard.NetworkObjectId))
                {
                    dispatched = true; guard.AnnounceBackup(true);
                    Response(guard.NetworkObjectId).Phase = BlacksiteBackupPhase.Dispatched;
                    ReleaseReservationForBackup(guard);
                }
            if (!dispatched) return;
            _backupBegan = Time.time;
            var caller = !casualty ? reporter : _members.Where(s => s != reporter && !s.isEnemyDead).OrderBy(s => Vector3.Distance(s.transform.position, reporter.transform.position)).FirstOrDefault();
            caller?.AnnounceBackup(false);
            BlacksiteDiagnostics.Log("Backup", $"dispatch guards={_backupGuards.Count}, reporter={reporter.MemberIndex}, casualty={casualty}, outside={_backupOutside}, observed={_backupPoint}, floorY={ReportedFloor().y:0.0}, floorSource={_backupFloorSource}.");
        }
        // A changing contact moves the target without resetting progress. The floor is re-derived
        // once the point has left the anchor's storey, and only a materially different storey - or
        // the other world - is different travel and restarts it.
        //
        // #874 S9.5: the anchor is cleared only when the storey itself changed. Clearing it on
        // EVERY report is what let the run's anchor walk -182.1 -> -181.3 -> -179.8 while the
        // reporter was being knocked about on a stair: none of those steps was a material floor
        // change, so none of them was different travel, yet each one re-derived the storey every
        // candidate is judged against.
        //
        // #1271 S3b: the post follows its reporter every tick, so the storey test is against the
        // ANCHOR: from the previous point a walked stair never moves it, and three metres from the
        // anchor re-anchors mid-stair and stays there on a six-metre storey.
        private void Retarget(Vector3 point, bool outside, bool onMesh)
        {
            float was = ReportedFloor().y; bool otherWorld = outside != _backupOutside;
            _backupPoint = point; _backupOutside = outside;
            if (!otherWorld && BlacksiteResponsePolicy.OnFloor(point.y, was)) return;
            _backupPointOnMesh = onMesh; _hasBackupFloor = false;
            if (!otherWorld && !BlacksiteResponsePolicy.MaterialFloorChange(was, ReportedFloor().y)) return;
            foreach (var guard in _members.Where(s => _backupGuards.Contains(s.NetworkObjectId)))
            {
                var response = Response(guard.NetworkObjectId);
                response.Phase = BlacksiteBackupPhase.Dispatched; response.HasGoal = false; response.NextPlan = 0f;
                _supportUntil.Remove(guard.NetworkObjectId);
            }
            BlacksiteDiagnostics.Log("Backup", $"contact moved to a different floor: point={point}, outside={outside}, floorY={ReportedFloor().y:0.0}, floorSource={_backupFloorSource}; travel restarted.");
        }
        // A dispatched guard is no longer an eligible operator, so an existing reservation is
        // closed explicitly as an interruption: the squad did not fail the objective.
        private void ReleaseReservationForBackup(BlacksiteSoldierAI guard)
        {
            for (int i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                if (r.CarrierId != guard.NetworkObjectId || r.Phase != BlacksiteObjectivePhase.Reserved) continue;
                ReleaseJob(ref r, guard, false, "guard dispatched to backup", BlacksiteObjectiveCloseCause.Reassigned, BlacksiteAttemptOutcome.Interrupted);
                Records[i] = r;
            }
        }
        private void TickGuardBackup()
        {
            _backupGuards.RemoveWhere(id => !_members.Any(s => s.NetworkObjectId == id && !s.isEnemyDead));
            foreach (ulong id in _backupResponses.Keys.Where(id => !_backupGuards.Contains(id)).ToArray()) _backupResponses.Remove(id);
            if (_backupGuards.Count == 0) return;
            // #1271 S3b: the post follows the squad - its point is the living reporter's own.
            if (_backupReporter != null && !_backupReporter.isEnemyDead)
                Retarget(ReportPoint(_backupReporter, out bool onMesh), BlacksiteWorld.Outside(_backupReporter), onMesh);
            if (_members.Any(s => s.isOutside == _backupOutside && s.IsObjectiveCombatActive
                && Vector3.Distance(s.transform.position, _backupPoint) < 18f)) _backupLastThreat = Time.time;
            Vector3 floor = ReportedFloor();
            foreach (var guard in _members.Where(s => _backupGuards.Contains(s.NetworkObjectId)).ToArray())
            {
                ulong id = guard.NetworkObjectId;
                if (Returning) { EndGuardBackup(id, "extraction"); continue; }
                var response = Response(id);
                bool sameWorld = guard.isOutside == _backupOutside;
                bool arrived = sameWorld && Vector3.Distance(guard.transform.position, _backupPoint) < BlacksiteResponsePolicy.ArrivalMetres;
                bool combat = guard.IsObjectiveCombatActive;
                // Reaching the reported floor means standing on its storey inside the reported
                // area; the last stretch to the chosen support slot is its own stage.
                bool onFloor = arrived && BlacksiteResponsePolicy.OnFloor(guard.transform.position.y, floor.y);
                bool atSupport = onFloor && (!response.HasGoal
                    || Vector3.Distance(guard.transform.position, response.Goal) < BlacksiteResponsePolicy.SupportArrivalMetres);
                var next = BlacksiteResponsePolicy.NextPhase(response.Phase, sameWorld, onFloor, atSupport, combat,
                    Time.time - _backupLastThreat > BlacksiteResponsePolicy.PostQuietSeconds);
                if (next == BlacksiteBackupPhase.Released) { EndGuardBackup(id, "area checked"); continue; }
                SetBackupPhase(guard, response, next, combat ? "engaged" : arrived ? "reached the reported area" : "advancing");
                // The duty board owns this guard's one progress budget, including traversal and
                // combat deferral. Each phase is a distinct duty stage there, so it gets its own
                // deadline and the arrival of a different guard cannot extend it.
            }
        }
        private bool TryBackupOrder(BlacksiteSoldierAI soldier, out BlacksiteMemberOrder order)
        {
            order = default;
            ulong id = soldier.NetworkObjectId;
            if (Returning || !GuardCalledIn(id) || soldier.IsAboardShip) return false;
            var response = Response(id);
            if (soldier.isOutside != _backupOutside)
            {
                // #874 A4: dropping the goal and zeroing the cadence go together everywhere now
                // (here and SetBackupPhase), because the cadence is what gates the re-selection
                // rather than what causes it: a guard that crosses back has no goal, so it must be
                // allowed to choose one on the tick it arrives.
                response.HasGoal = false; response.NextPlan = 0f;
                Vector3 entrance = Planner.CrossingFor(soldier);
                order = BlacksiteSquadPlanner.MakeOrder(soldier.isOutside ? BlacksiteSquadOrderKind.Infiltrate : BlacksiteSquadOrderKind.Extract, entrance, _backupPoint);
                return true;
            }
            Vector3 floor = ReportedFloor();
            // #874 A4: a re-selection needs a REASON. A bare 1 s cadence re-ran SelectBackupGoal
            // while the guard already held a perfectly good slot, and SupportPoint's cursor hands
            // out a different one of the twelve rungs each pass, so round 3 of the 2026-09-22 run
            // cycled one guard's goal among five support points 1-7 m apart once a second for
            // 158 s (full log 48973-67039). The cadence still GATES the work - one selection per
            // second at most, and the 0.2 s deferrals (PlanningBudget, SpendCandidatePath) and
            // SetBackupPhase's NextPlan=0/HasGoal=false reset still force the next one - but it no
            // longer causes it: a held goal is kept until the report's floor moves or a refusal is
            // on record.
            if (Time.time >= response.NextPlan
                && (!response.HasGoal || Vector3.Distance(response.Center, floor) > 2f
                    || _planningBlocked.ContainsKey(id)))
            {
                response.NextPlan = Time.time + 1f; response.Center = floor;
                if (!TryFloorAnchorGoal(soldier, response, floor)) SelectBackupGoal(soldier, response, floor);
            }
            order = BlacksiteSquadPlanner.MakeOrder(BlacksiteSquadOrderKind.SearchLastKnown,
                response.HasGoal ? response.Goal : soldier.transform.position, _backupPoint);
            return true;
        }
        // #874 S7.6: the reported floor may be a STOREY this guard cannot walk to. In the repaired
        // run the two rifle guards entered at y=-182.16 and the call was on the mineshaft's bottom
        // floor at y=-219.3, reachable only by the elevator: every support candidate, every floor
        // node and the entrance hold were judged against that floor from the storey above, all
        // four bounded alternatives were spent and the response ended with the guard standing
        // still (LogOutput.log L9613, L9890, L9979 - movedMetresLast5s=0.00, path=False).
        //
        // Off that storey with no walking route to it the order IS the reported floor anchor, the
        // same rule S6.4b gives the squad's own off-storey members in SupportOrSearchOrder: the
        // elevator, the stairs or a world ladder then owns the route, because an ordinary
        // SearchLastKnown destination on another storey is exactly what the interaction
        // coordinator turns into a mineshaft trip. Nothing here creates an elevator task.
        //
        // The walking-route question is asked, not assumed. A storey the guard can already reach -
        // six metres up a staircase - keeps the S1.4 behaviour, where each guard takes a separately
        // reserved support slot judged against the reported floor, instead of both guards being
        // sent to the identical reported point. One route query per planning pass, on the same
        // per-member frame budget every other candidate route is drawn from.
        private bool TryFloorAnchorGoal(BlacksiteSoldierAI soldier, BackupResponse response, Vector3 floor)
        {
            if (!BlacksiteResponsePolicy.OffStorey(soldier.transform.position.y, floor.y)) return false;
            // The same tolerance ReportedFloor uses to decide a sample belongs to the reported
            // point: further than that is a different storey and the raw height is the anchor.
            Vector3 anchor = NavMesh.SamplePosition(floor, out var nav, BlacksiteResponsePolicy.FloorAnchorTolerance, NavMesh.AllAreas)
                ? nav.position : floor;
            if (!SpendCandidatePath(soldier.NetworkObjectId))
            { response.NextPlan = Time.time + .2f; return true; } // unasked, not answered: hold and retry next frame
            if (BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(soldier), soldier.transform.position, anchor).Reachable) return false;
            if (!response.HasGoal || response.Kind != BlacksiteResponseGoal.FloorAnchor
                || Vector3.Distance(response.Goal, anchor) > 1.8f)
                BlacksiteDiagnostics.Log("Backup", $"member={soldier.MemberIndex}, phase={response.Phase}, goal={anchor}, floorY={floor.y:0.0}, reason={BlacksiteResponseGoal.FloorAnchor}.");
            response.Goal = anchor; response.Kind = BlacksiteResponseGoal.FloorAnchor; response.HasGoal = true;
            // Arriving on the storey is a phase change, and SetBackupPhase zeroes NextPlan, so the
            // support slot is chosen on the next tick rather than a second later.
            return true;
        }
        // Bounded alternatives, in order: a support slot on the reported floor, the nearest
        // reachable AI node on that floor, then an entrance-side hold. With none of them - or with a
        // spent frame budget - the guard holds its previous goal, or where it stands, and keeps
        // re-planning: backup is a post, so an unavailable destination does not end it (#1271 S3b).
        private void SelectBackupGoal(BlacksiteSoldierAI soldier, BackupResponse response, Vector3 floor)
        {
            ulong id = soldier.NetworkObjectId;
            Vector3 goal = SupportPoint(soldier, floor, floor - soldier.transform.position, floor.y);
            var kind = BlacksiteResponseGoal.SupportSlot;
            var cause = _planningBlocked.TryGetValue(id, out var blocked) ? blocked : BlacksiteBlockedCause.None;
            if (cause == BlacksiteBlockedCause.PlanningBudget) { response.NextPlan = Time.time + .2f; return; }
            if (cause == BlacksiteBlockedCause.NoReachableCandidate)
            {
                if (TryReportedFloorNode(soldier, floor, out goal)) { kind = BlacksiteResponseGoal.FloorNode; cause = BlacksiteBlockedCause.None; }
                else if (TryEntranceHold(soldier, out goal)) { kind = BlacksiteResponseGoal.EntranceHold; cause = BlacksiteBlockedCause.None; }
                else kind = BlacksiteResponseGoal.Blocked;
                // The support slot's own cached refusal is left in place: it is what keeps the
                // alternatives affordable inside the per-member route budget until it expires.
                if (cause == BlacksiteBlockedCause.None) _planningBlocked.Remove(id);
            }
            if (kind == BlacksiteResponseGoal.Blocked) return;
            if (!response.HasGoal || response.Kind != kind || Vector3.Distance(response.Goal, goal) > 1.8f)
                BlacksiteDiagnostics.Log("Backup", $"member={soldier.MemberIndex}, phase={response.Phase}, goal={goal}, floorY={floor.y:0.0}, reason={kind}.");
            response.Goal = goal; response.Kind = kind; response.HasGoal = true;
        }
        // #874 S7.6: the node loop and the entrance hold draw from ONE four-route-per-member frame
        // budget (SpendCandidatePath). A real facility floor carries hundreds of AI nodes, so the
        // loop spent every query and the hold - the alternative that certainly has a route - was
        // never asked at all; the run counted that spent budget as a blocked pass and ended the
        // response on it (LogOutput.log L9979). The hold needs exactly one query, so the loop may
        // spend at most the other three. Bounding the loop rather than giving the hold a query of
        // its own keeps the per-member budget the single authority on route cost.
        private const int NodeRouteQueries = 3;
        // The nearest AI node standing on the reported floor with a complete route from the guard.
        private bool TryReportedFloorNode(BlacksiteSoldierAI soldier, Vector3 floor, out Vector3 goal)
        {
            goal = default;
            int spent = 0;
            var nodes = _backupOutside
                ? (_outsideSearchNodes ?? (_outsideSearchNodes = GameObject.FindGameObjectsWithTag("OutsideAINode")))
                : (_insideSearchNodes ?? (_insideSearchNodes = GameObject.FindGameObjectsWithTag("AINode")));
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            foreach (var node in nodes.Where(n => n != null && BlacksiteResponsePolicy.OnFloor(n.transform.position.y, floor.y))
                .OrderBy(n => Vector3.Distance(n.transform.position, floor)))
            {
                if (BlacksiteHazardAwareness.Nearby(node.transform.position, 12f) != null) continue;
                if (!NavMesh.SamplePosition(node.transform.position, out var nav, 1f, NavMesh.AllAreas)
                    || !BlacksiteResponsePolicy.OnFloor(nav.position.y, floor.y)
                    || !BlacksitePhysicalStep.CanOccupy(soldier.transform, nav.position, mask)) continue;
                if (spent >= NodeRouteQueries || !SpendCandidatePath(soldier.NetworkObjectId)) return false;
                spent++;
                if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(soldier), soldier.transform.position, nav.position).Reachable) continue;
                goal = nav.position; return true;
            }
            return false;
        }
        // Last resort: hold on the reported side of the entrance, where a route certainly exists.
        private bool TryEntranceHold(BlacksiteSoldierAI soldier, out Vector3 goal)
        {
            goal = _backupOutside ? _mission.SquadDirector.OutsideMainEntrance : _mission.SquadDirector.InsideMainEntrance;
            Vector3 offset = goal + Quaternion.Euler(0, soldier.MemberIndex * 72f, 0) * Vector3.forward * 3f;
            if (!NavMesh.SamplePosition(offset, out var nav, 2f, NavMesh.AllAreas)
                || !BlacksiteResponsePolicy.OnFloor(nav.position.y, goal.y)) return false;
            if (!SpendCandidatePath(soldier.NetworkObjectId)) return false;
            if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(soldier), soldier.transform.position, nav.position).Reachable) return false;
            goal = nav.position; return true;
        }
    }
    public sealed partial class BlacksiteSoldierAI
    {
        // #1271 S8: the transition wrappers (no token: one line per occurrence, SquadDirector.RequestVoice)
        // are queued rather than dropped by the ordinary lock; AnnounceBoarded is plain on purpose, so
        // the lock may drop it. The director re-routes leader calls (BlacksiteVoiceEventRules.IsLeaderCall)
        // to the Leader.
        internal void AnnounceCargo() => RequestSquadVoice(BlacksiteVoiceEvent.CargoDeposited, transition: true, volume: .6f);
        internal void AnnounceObjectiveSighted() => RequestSquadVoice(BlacksiteVoiceEvent.ObjectiveSighted, transition: true, volume: .7f);
        internal void AnnounceWitnessSighted() => RequestSquadVoice(BlacksiteVoiceEvent.WitnessSighted, transition: true, volume: .8f);
        internal void AnnounceWitnessTaken() => RequestSquadVoice(BlacksiteVoiceEvent.WitnessTaken, transition: true, volume: .78f);
        internal void AnnounceObjectiveTaken() => RequestSquadVoice(BlacksiteVoiceEvent.ObjectiveTaken, transition: true, volume: .7f);
        internal void AnnounceObjectiveLost() => RequestSquadVoice(BlacksiteVoiceEvent.ObjectiveLost, transition: true, volume: .75f);
        internal void AnnounceBoarded() => RequestSquadVoice(BlacksiteVoiceEvent.MemberBoarded, volume: .65f);
        internal void AnnounceSearch(bool reacquired) => RequestSquadVoice(reacquired ? BlacksiteVoiceEvent.Reacquired : BlacksiteVoiceEvent.Searching, volume: .7f);
        internal void AnnounceBackup(bool acknowledgement) => RequestSquadVoice(acknowledgement ? BlacksiteVoiceEvent.BackupAcknowledged : BlacksiteVoiceEvent.BackupRequested, volume: .75f);
    }
}
