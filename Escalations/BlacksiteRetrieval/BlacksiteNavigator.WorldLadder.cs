using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        internal sealed partial class BlacksiteNavigator
        {
            private BlacksiteWorldLadder _worldLadder;
            private BlacksiteWorldLadderMotor _worldClimb;
            private Vector3 _worldLadderEntry;
            private float _worldLadderRetry;
            // #1293 R3: the decision that last asked for this ladder, the admitted foot, and the
            // facts TickWorldLadderWait raises a decision on when they change.
            private int _worldLadderAskedDecision;
            private Vector3 _worldLadderFoot;
            private bool _worldLadderQueued, _worldLadderQueueAdmitted, _worldLadderFootAdmitted, _worldLadderAtFoot, _worldLadderRetryHeld;
            // How near the foot the body mounts from: inside it the climb begins where the agent stopped.
            private const float WorldLadderMountRadius = .35f;
            // #874 S4.0b diagnostics: the log cadence fields, and when the member began waiting at
            // the foot (waitedFor= on the wait and begin lines). #1293 R2: diagnostics only - the
            // wait at the foot has no bound.
            private float _worldLadderNextWaitLogAt, _worldLadderWaitedFor;
            private float _worldLadderWaitSince;
            private float _worldLadderClimbStartedAt;
            private bool _worldLadderUp;
            // Cached once per navigator: the motor asks this every frame of a climb and a fresh
            // closure per frame per member is not free.
            private Func<bool> _worldLadderCanRetreat;
            private Func<Vector3,bool> _worldLadderSpaceFree;
            private static readonly List<BlacksiteSoldierAI> WorldLadderScratch = new List<BlacksiteSoldierAI>();
            internal bool IsWorldClimbing => _worldClimb != null;
            internal BlacksiteLadderPhase LadderPhase
                => _jump?.LadderPhase ?? _worldClimb?.PhaseAt(_soldier.transform.position) ?? BlacksiteLadderPhase.None;
            // #874 S5.1: how long this member has been standing at the foot of a ladder it has not
            // been admitted to. Zero while it is still walking there.
            private float WorldLadderWaitedFor
                => _worldLadderWaitSince > 0f ? Time.time - _worldLadderWaitSince : 0f;
            // #874 S4: the dwell a member stands under at a ladder foot. This file ends only the
            // dwell it raised.
            private const string LadderQueueDwell = "ladder-queue";
            // #1293 R3: ending the wait at the foot is a decision for the member.
            private void EndLadderQueueDwell()
            {
                if (_dwellReason != LadderQueueDwell) return;
                EndDwell();
                _soldier.RequestDecision(BlacksiteDecisionCause.Outcome);
            }
            private bool HandleWorldLadder(BlacksiteShipController ship, bool outbound, ref Vector3 destination)
            {
                if (!ship.TryUpcomingWorldLadder(_soldier.transform.position, outbound, PhysicalMask, out var ladder, out var points))
                { ReleaseWorldLadder(); return false; }
                if (_worldLadder != ladder) { ReleaseWorldLadder(); _worldLadder = ladder; }
                _worldLadderAskedDecision = _soldier.Brain.Decisions;
                bool up = points[3].y > points[0].y;
                // A retry is a decision to look elsewhere for a route, not a reason to stand at the
                // foot: on 2026-09-09 the retry branch froze the member exactly like the queue
                // branch did, and neither was visible to the stall watchdog.
                if (Time.time < _worldLadderRetry)
                {
                    _worldLadderRetryHeld = true;
                    LogWorldLadderWait(ladder, points, up, false, $"retry held for {_worldLadderRetry - Time.time:0.00}s");
                    return false;
                }
                if (!TryAdmitToWorldLadder(ladder, points, up, out string refusal))
                {
                    _worldLadderFootAdmitted = false;
                    // Wait outside the column so a climber coming the other way can dismount.
                    bool held = Vector3.Distance(_soldier.transform.position, points[0]) <= 3.5f;
                    if (held && _worldLadderWaitSince <= 0f) _worldLadderWaitSince = Time.time;
                    LogWorldLadderWait(ladder, points, up, held, refusal);
                    // #1293 R2/R3: the wait at the foot has no bound: a dwell re-armed on every refusal
                    // for the lane's stale window, whose expiry is the Outcome that asks again.
                    if (held) Dwell(BlacksiteResourceLeasePolicy.StaleSeconds, LadderQueueDwell);
                    return false;
                }
                destination = points[0];
                _worldLadderFoot = points[0];
                _worldLadderFootAdmitted = true;
                if (Vector3.Distance(_soldier.transform.position, points[0]) > WorldLadderMountRadius) return false;
                // #874 S4: a refused climb dwells at the foot while its five-second retry runs.
                if (!ladder.Validate(PhysicalMask, out var fresh, out var reason))
                {
                    BlacksiteDiagnostics.Log("WorldLadder", "climb refused: " + reason);
                    ReleaseWorldLadder(); _worldLadderRetry = Time.time + 5f;
                    Dwell(_worldLadderRetry - Time.time, LadderQueueDwell);
                    return false;
                }
                if (points[3].y < points[0].y) System.Array.Reverse(fresh);
                points = fresh;
                if (Vector3.Distance(_soldier.transform.position, points[0]) > WorldLadderMountRadius) { destination = points[0]; return false; }
                _worldLadderEntry = points[0];
                // Start exactly where the navigator stopped. Include the last supported approach
                // instead of snapping the body onto the first authored point.
                var motion = new Vector3[] { _soldier.transform.position, points[1], points[2], points[3] };
                _worldClimb = new BlacksiteWorldLadderMotor(motion, ladder.RungFacing);
                EndLadderQueueDwell();
                BeginCrossing("ladder"); _agent.updatePosition = false; _agent.updateRotation = false;
                _worldLadderUp = points[3].y > points[0].y;
                _worldLadderClimbStartedAt = Time.time;
                _worldLadderWaitedFor = WorldLadderWaitedFor;
                _worldLadderWaitSince = 0f; _worldLadderNextWaitLogAt = 0f;
                // #874 S5.1: admission ends on mount. The FIFO queue releases its head so the next
                // member is admitted after the headway; occupancy from here on belongs to the
                // column. Never calling Advance is what made this ladder single occupancy on 2026-09-09.
                ladder.Queue.Advance(_soldier.NetworkObjectId, Time.time);
                ladder.Column.Report(_soldier.NetworkObjectId, _soldier.transform.position.y, Time.time);
                _soldier.NoteTraversalMilestone("ladder:mounted"); // #874 S6.1 wait milestone.
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("WorldLadder",
                        $"event=begin, member={_soldier.MemberIndex}, direction={(_worldLadderUp ? "up" : "down")}, "
                        + $"rise={Mathf.Abs(points[3].y - points[0].y):0.00}, begin={BlacksiteDiagnosticFormat.V(_worldLadderEntry)}, "
                        + $"end={BlacksiteDiagnosticFormat.V(points[3])}, occupants={ladder.Column.Occupants}, "
                        + $"waiting={ladder.Queue.WaitingCount}, headwayToLead={DescribeHeadwayToLead(ladder, _worldLadderUp, _soldier.transform.position.y)}, "
                        + $"requiredHeadway={ladder.Column.RequiredHeadway:0.00}, waitedFor={_worldLadderWaitedFor:0.00}, "
                        + $"animator={DescribeAnimatorState()}, ropeDirection={_soldier._ropeDirection.Value}, authority=host, facing={BlacksiteDiagnosticFormat.V(ladder.RungFacing)}");
                return true;
            }

            // #874 S5.1: the whole admission rule, in the order the refusals should be reported.
            // The vanilla Occupied test (a player on the InteractTrigger) is unchanged; the FIFO
            // queue orders the waiting members; the column decides how many heads the headway allows.
            private bool TryAdmitToWorldLadder(
                BlacksiteWorldLadder ladder, Vector3[] points, bool up, out string refusal)
            {
                if (ladder.Occupied) { refusal = "ladder reports an occupant"; return false; }
                // #1016 S2: a world ladder is a scene object that does not know its mission, so its
                // queue is bound on the first request that does.
                ladder.Queue.Bind(_soldier.ResourceBroker, BlacksiteResourceKey.WorldLadder(ladder));
                BlacksiteBrokerVerdict verdict =
                    ladder.Queue.RequestVerdict(_soldier.NetworkObjectId, Time.time);
                _worldLadderQueued = true;
                _worldLadderQueueAdmitted = verdict.Admitted;
                if (!verdict.Admitted)
                {
                    refusal = "queue admission refused";
                    return false;
                }
                return ladder.Column.TryAdmit(_soldier.NetworkObjectId, up, points[0].y, Time.time, out refusal);
            }
            internal bool TickWorldLadder()
            {
                if (_worldClimb == null) { TickWorldLadderWait(); return false; }
                // #874 S4: the climb is a crossing until ReleaseWorldLadder ends it.
                _worldLadder.Queue.Request(_soldier.NetworkObjectId, Time.time);
                // The column follows the body up the rungs: the headway the next member must clear
                // is measured against where this one actually is, not where it mounted.
                _worldLadder.Column.Report(_soldier.NetworkObjectId, _soldier.transform.position.y, Time.time);
                if (_worldLadderCanRetreat == null)
                    _worldLadderCanRetreat = () => _worldLadder == null
                        || _worldLadder.Column.CanRetreat(_soldier.NetworkObjectId);
                // The column's own body test is the spacing: a follower closing on a stalled
                // climber stops at the 1.96 m capsule column.
                if (_worldLadderSpaceFree == null)
                    _worldLadderSpaceFree = next => _worldLadder == null || _worldLadder.Column.SpaceFree(_soldier.NetworkObjectId, next.y);
                var result = _worldClimb.Tick(_soldier.transform, Time.deltaTime, PhysicalMask,
                    _soldier.ReplicatedStunned || _soldier.stunNormalizedTimer > 0, _worldLadderSpaceFree,
                    _worldLadderCanRetreat);
                FaceCombatPoint(_soldier.transform.position + _worldClimb.Facing, FacingTurnDegreesPerSecond);
                _soldier.SyncPositionToClients();
                // #874 P07: Moving includes the settle - the walk back down a cancelled climb owes
                // the body - and this early return is what keeps its reservations alive for it. The
                // queue request and the column report above are renewed on every settling frame,
                // so no peer is admitted into a column a member is still physically inside, and the
                // slot is handed back on the frame the body arrives.
                if (result == BlacksitePassageResult.Moving) return true;
                bool complete = result == BlacksitePassageResult.Complete;
                // #874 F07: a retreat and an abort are two different endings and the log printed
                // one for the other. `reason` is set by Retreat AND by the spent-budget abort
                // below it, so keying result= on it alone called an aborted climb "retreated".
                // The motor's own Returning is the fact, taken before the motor is dropped.
                // #874 P07: and Returning is now true for a settle as well, which is an ABORT that
                // retreats - so the motor's own Aborted is what separates the two.
                bool retreated = _worldClimb.Returning;
                bool aborted = _worldClimb.Aborted;
                string reason = _worldClimb.Reason;
                float pausedSeconds = _worldClimb.PausedSeconds, blockedSeconds = _worldClimb.BlockedSeconds;
                Vector3 current = _soldier.transform.position;
                float climbSeconds = Time.time - _worldLadderClimbStartedAt;
                BlacksiteWorldLadder ladder = _worldLadder;
                // #874 F07: ONE owner. ReleaseWorldLadder hands back the queue slot, the column
                // slot, the queue dwell, the agent flags the mount wrote and the crossing. This tick
                // adds only the thing an abort must never do - put the member back on navigation at
                // the point the climb physically ended.
                ReleaseWorldLadder();
                // #1293 R2: a dismount that cannot bind where it ended is stranded, never moved.
                if (!TryBindAtClimbExit(current))
                    BindInPlace("world ladder ended off the navmesh");
                RequestImmediateDestinationRefresh();
                if (!complete) _worldLadderRetry = Time.time + 5f;
                else _soldier.NoteTraversalMilestone("ladder:dismounted"); // #874 S6.1 wait milestone.
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("WorldLadder",
                        $"event=end, member={_soldier.MemberIndex}, direction={(_worldLadderUp ? "up" : "down")}, "
                        + $"climbSeconds={climbSeconds:0.00}, position={BlacksiteDiagnosticFormat.V(current)}, "
                        + $"occupants={(ladder != null ? ladder.Column.Occupants : 0)}, "
                        + $"pausedSeconds={pausedSeconds:0.00}, blockedSeconds={blockedSeconds:0.00}, "
                        + $"result={(complete ? "dismounted" : aborted ? "aborted" : retreated ? "retreated" : "aborted")}, "
                        + $"reason={reason ?? BlacksiteDiagnosticFormat.None}, "
                        + $"animator={DescribeAnimatorState()}, ropeDirection={_soldier._ropeDirection.Value}, authority=host");
                return true;
            }

            /// <summary>
            /// #1293 R3: the wait for a ladder, every frame, while this decision or the previous one
            /// asked for it. Keeps the FIFO place by presence and raises Outcome when the queue admits
            /// the member, the body reaches the admitted foot, or the retry deadline passes.
            /// </summary>
            private void TickWorldLadderWait()
            {
                if (_worldLadder == null) return;
                if (_soldier.Brain.Decisions - _worldLadderAskedDecision > 1) { ReleaseWorldLadder(); return; }
                bool admitted = _worldLadderQueued && _worldLadder.Queue.Request(_soldier.NetworkObjectId, Time.time);
                bool atFoot = _worldLadderFootAdmitted
                    && Vector3.Distance(_soldier.transform.position, _worldLadderFoot) <= WorldLadderMountRadius;
                bool retryEnded = _worldLadderRetryHeld && Time.time >= _worldLadderRetry;
                if ((admitted && !_worldLadderQueueAdmitted) || (atFoot && !_worldLadderAtFoot) || retryEnded)
                    _soldier.RequestDecision(BlacksiteDecisionCause.Outcome);
                _worldLadderQueueAdmitted = admitted;
                _worldLadderAtFoot = atFoot;
                if (retryEnded) _worldLadderRetryHeld = false;
            }

            /// <summary>#1293 R3: a forgotten goal takes the ladder wait it was routed through; a climb is the crossing's own.</summary>
            internal void ReleaseWorldLadderWait()
            {
                if (_worldClimb == null) ReleaseWorldLadder();
            }

            /// <summary>
            /// #874 F07: the dismount's hand-back to ordinary navigation, on the navigator's own
            /// bind rule rather than a looser one of its own.
            ///
            /// It was <c>TryNavPoint(current, PhysicalMask, out nav, .6f, .6f)</c> followed by
            /// <c>_agent.Warp(nav)</c>. The second <c>.6f</c> is TryNavPoint's
            /// <c>groundNavTolerance</c>, so this accepted a mesh point up to 0.60 m from the floor
            /// under the member and then warped the BODY onto it - while every other rebind gate in
            /// this navigator accepts only
            /// <see cref="BlacksiteRecoveryLadder.GroundNavTolerance"/> (0.25 m) and warps to the
            /// position the member physically reached. A dismount whose nearest mesh sat between
            /// those two numbers was declared rebound here and judged unbound by the recovery
            /// ladder on the next think - the false recovery the F07 trace names - and one beyond
            /// both moved the body up to 0.60 m off the catwalk it had just climbed onto.
            /// </summary>
            private bool TryBindAtClimbExit(Vector3 current)
                => _agent != null && _agent.enabled
                    && NavMesh.SamplePosition(current, out var nav, BlacksiteRecoveryLadder.GroundNavTolerance, NavMesh.AllAreas)
                    && Mathf.Abs(nav.position.y - current.y) < BlacksiteRecoveryLadder.GroundNavTolerance
                    && _agent.Warp(current);

            /// <summary>
            /// #874 F07: the ONE place a world-ladder climb is unwound, whatever ended it.
            ///
            /// Death, capture, a teleport, a round teardown and every ordinary dismount arrive
            /// here. The column keeps the order of the survivors, so a casualty in the middle of a
            /// stack does not reshuffle who may back down next.
            ///
            /// It releases the queue, the column, the queue dwell, the agent flags the mount wrote
            /// (<c>updatePosition = false</c>, <c>updateRotation = false</c>) and the crossing,
            /// exactly as <c>ReleaseJump</c> does for the jump leg, and NEVER moves the transform -
            /// the caller that ended the climb owns where the body is.
            /// </summary>
            internal void ReleaseWorldLadder()
            {
                bool ownedBody = _worldClimb != null;
                _worldLadder?.Queue.Release(_soldier.NetworkObjectId);
                _worldLadder?.Column.Release(_soldier.NetworkObjectId);
                // #1016 S2: the WAIT's dwell goes with the wait, so a released member walks at once.
                EndLadderQueueDwell();
                _worldLadder = null; _worldClimb = null;
                _worldLadderWaitSince = 0f; _worldLadderNextWaitLogAt = 0f;
                _worldLadderQueued = _worldLadderQueueAdmitted = _worldLadderFootAdmitted = _worldLadderAtFoot = _worldLadderRetryHeld = false;
                if (!ownedBody) return;
                // The mount's two writes, given back directly, exactly as ReleaseJump does: a seat
                // that took the body since the mount leaves the crossing's end a no-op, never the flags.
                if (_agent != null && _agent.enabled)
                {
                    _agent.updateRotation = true;
                    _agent.updatePosition = true;
                }
                EndCrossing(_soldier.transform.position);
            }

            // The wait branch was silent, so the idle member-seconds four raiders spent at the foot
            // of one ladder had to be reconstructed from unrelated fields. One line on entry, then
            // one per second while the wait is held. #874 S5.1: occupants, lead, headwayToLead and
            // requiredHeadway are the column's real state. #1293 R2: budget= is gone with the bound,
            // and (#1293 R3) waitExpiresIn=, which printed that bound.
            private void LogWorldLadderWait(BlacksiteWorldLadder ladder, Vector3[] points, bool up, bool held, string refusal)
            {
                if (!BlacksiteDiagnostics.Enabled) return;
                bool entering = _worldLadderNextWaitLogAt <= 0f;
                if (!entering && (!held || Time.time < _worldLadderNextWaitLogAt)) return;
                _worldLadderNextWaitLogAt = Time.time + 1f;
                bool hasLead = ladder.Column.TryLeadAhead(_soldier.NetworkObjectId, up, points[0].y, out ulong lead, out float headway);
                BlacksiteDiagnostics.Log("WorldLadder",
                    $"event=wait, member={_soldier.MemberIndex}, held={held}, "
                    + $"ladder={BlacksiteDiagnosticFormat.V(points[0])}, occupants={ladder.Column.Occupants}, "
                    + $"waiting={ladder.Queue.WaitingCount}, lead={(hasLead ? DescribeMemberToken(lead) : BlacksiteDiagnosticFormat.None)}, "
                    + $"headwayToLead={(hasLead ? headway.ToString("0.00") : BlacksiteDiagnosticFormat.None)}, "
                    + $"requiredHeadway={ladder.Column.RequiredHeadway:0.00}, "
                    + $"waitedFor={WorldLadderWaitedFor:0.00}, "
                    + $"refusal={refusal ?? BlacksiteDiagnosticFormat.None}");
            }

            // Vertical separation to the nearest body ahead of this member on the column, which is
            // the headway the column enforces. "none" while nothing is ahead of it.
            private string DescribeHeadwayToLead(BlacksiteWorldLadder ladder, bool up, float footY)
                => ladder != null && ladder.Column.TryLeadAhead(_soldier.NetworkObjectId, up, footY, out _, out float headway)
                    ? headway.ToString("0.00")
                    : BlacksiteDiagnosticFormat.None;

            private string DescribeMemberToken(ulong token)
            {
                if (token == 0UL) return BlacksiteDiagnosticFormat.None;
                BlacksiteSoldierAI member = ResolveMemberToken(token);
                return member != null ? member.MemberIndex.ToString() : "token:" + token;
            }

            private static BlacksiteSoldierAI ResolveMemberToken(ulong token)
            {
                WorldLadderScratch.Clear();
                Y4NGZCompany.Escalations._Shared.MoonEscalationState.CollectActiveBlacksiteSoldiers(WorldLadderScratch);
                for (int i = 0; i < WorldLadderScratch.Count; i++)
                    if (WorldLadderScratch[i] != null && WorldLadderScratch[i].NetworkObjectId == token)
                        return WorldLadderScratch[i];
                return null;
            }

            private string DescribeAnimatorState()
            {
                Animator animator = _soldier._animator;
                if (animator == null || animator.runtimeAnimatorController == null || animator.layerCount == 0)
                    return BlacksiteDiagnosticFormat.None;
                return animator.GetCurrentAnimatorStateInfo(0).shortNameHash.ToString();
            }
        }
    }
}
