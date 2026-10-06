// #1018 S4: the idle Action's BODY - what a member with nothing actionable actually does.
//
// It replaces HoldWithoutOrder (design note D), whose rule was "every 4 s pick a point about 2 m
// behind me and walk to it": the aimless shuffle #952's log forensics measured, with no expiry, no
// purpose and no reason in the log. What stands here instead is a three-phase action, and every
// phase has an expiry:
//
//   Hold      stand at the assignment's hold point with the facing sweep armed, for
//             BlacksiteIdleActionPolicy.HoldSeconds (6 s), through the EXISTING FacingSweep lease
//             reason "idle-sweep".
//   Successor exactly one ranked BlacksiteIdleSuccessor, its own goal, its own expiry, issued
//             through SetDestinationThrottled with issuer "idle:<successor>".
//   Rally     every successor refused: HoldWithoutOrder's own rally ladder, moved here verbatim,
//             under the Idle lease's 15 s, with the planner told the member is out of candidates.
//
// Safe to enter on EVERY frame, which is what the brain's Stand continuation does: one Time.time
// compare, one HorizontalDistance and one idempotent lease re-assert per frame, with every expensive
// step (retreat-point scan, successor ranking, sweep arming) behind a phase TRANSITION. The
// successor points are read from the assignment, which the planner proved (design note C.3).
//
// This partial routes movement through the navigator rather than writing the NavMeshAgent.
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        // The idle action's own state, and the SOLE writer of it is this file. These four fields are
        // HoldWithoutOrder's _holdingGoal / _hasHoldingGoal / _holdingSince / _holdingAtGoal under
        // the names the action gives them (design note D).
        private Vector3 _idleGoal; private bool _hasIdleGoal; private float _idleSince = -1f;
        private bool _idleAtGoal;
        private BlacksiteIdlePhase _idlePhase;
        private BlacksiteIdleSuccessor _idleSuccessor;
        private float _idleExpiresAt;
        // The 5-bit mask of successors this member has tried and had refused. Cleared when the
        // assignment's Sequence moves or the phase leaves Successor (design note C.4).
        private byte _idleRejected;
        private int _idleSequence;
        // The successor the action TOOK, latched. The planner deliberately does not clear the set
        // when the member moves - moving is what taking a successor looks like - so the action holds
        // its own copy of the point and the facing it committed to and never re-reads a set that may
        // have been re-proved under it mid-walk (fix batch 1, SF6).
        private Vector3 _idleSuccessorGoal, _idleSuccessorFacing;
        private long _idleFrontierDoorway;

        // #1018 S4 (fix batch 2, S1): the issuer tokens, interned. "idle:" + _idleSuccessor built a
        // string on every frame of every walking successor, on a method that is entered every frame
        // by design. Indexed by the enum, so adding a kind without a token is a compile error's
        // width away rather than a silent allocation.
        private static readonly string[] IdleIssuers =
        {
            "idle:None", "idle:CoverStep", "idle:FrontierTile", "idle:Regroup",
            "idle:GuardPost", "idle:RallyHold"
        };

        private static string IdleIssuer(BlacksiteIdleSuccessor successor)
        {
            int index = (int)successor;
            return index >= 0 && index < IdleIssuers.Length ? IdleIssuers[index] : IdleIssuers[0];
        }

        // #1018 S4 (fix batch 2, S2): whether this PHASE has already tried to arm the sweep. Cleared
        // on every phase transition. BeginFacingSweep ends itself on a degenerate target list - a
        // member the room graph cannot place, outside, before the graph is built - and without this
        // latch the arm ran the room-graph build again on every frame for as long as that lasted.
        private bool _idleSweepArmed;

        internal BlacksiteIdlePhase IdlePhase => _idlePhase;
        internal float IdleExpiresAt => _idleExpiresAt;
        internal BlacksiteIdleSuccessor IdleSuccessor => _idleSuccessor;
        /// <summary>The point the action currently holds, for the callers that commit a goal.</summary>
        internal Vector3 IdleGoal => _idleGoal;
        internal bool HasIdleGoal => _hasIdleGoal;
        /// <summary>The <c>rejected=</c> value for <c>[UAT][Autonomy]</c> (design note E.2).</summary>
        internal string IdleRejectedToken => DescribeIdleRejected();

        /// <summary>
        /// #1018 S4: the idle Action. Replaces <c>HoldWithoutOrder</c> and is the sole writer of the
        /// idle goal.
        ///
        /// <paramref name="assignment"/> may be <c>default</c>: <c>BlacksiteNavigator.Recovery</c>'s
        /// stranded-member path and the native fixtures have no mission and no planner at all
        /// (design note G10 f), so every read of it is guarded by <c>IsValid</c> and the action then
        /// behaves exactly as the old rally hold did.
        /// </summary>
        internal bool ExecuteIdleAction(in BlacksiteSquadAssignment assignment, string cause,
            out BlacksiteIdlePhase phase, out BlacksiteIdleSuccessor successor,
            out Vector3 goal, out float until)
        {
            // HoldWithoutOrder's first line: a body off the navmesh is bound where it stands, or is
            // stranded (#1293 R2), before anything asks it to stand anywhere.
            if (_agent != null && _agent.enabled && !_agent.isOnNavMesh)
                BindNavigationInPlace("holding without order off navmesh");
            float now = Time.time;
            if (_idleSince < 0f) _idleSince = now;

            // A new assignment is a new decision: the rejection mask and the latched successor are
            // evidence about the assignment they were taken under and about nothing else.
            int sequence = assignment.IsValid ? assignment.Sequence : 0;
            if (sequence != _idleSequence)
            {
                _idleSequence = sequence;
                _idleRejected = BlacksiteIdleActionPolicy.NoneRejected;
                _idlePhase = BlacksiteIdlePhase.None;
                _idleSuccessor = BlacksiteIdleSuccessor.None;
                _idleExpiresAt = 0f;
                _idleSweepArmed = false;
            }

            if (_idlePhase == BlacksiteIdlePhase.None || now >= _idleExpiresAt) Advance(in assignment, now);

            switch (_idlePhase)
            {
                case BlacksiteIdlePhase.Hold: TickIdleHold(now); break;
                case BlacksiteIdlePhase.Successor: TickIdleSuccessor(in assignment, now); break;
                default: TickIdleRally(in assignment, now); break;
            }

            phase = _idlePhase; successor = _idleSuccessor; goal = _idleGoal; until = _idleExpiresAt;
            // #1018 S4 (fix batch 2, B2): this method emits NOTHING. It is entered every frame, and
            // a second [UAT][Autonomy] producer here carried different keys from the planner's on a
            // 1 s cadence of its own, so one area had two shapes and a reader could not tell which
            // line it was parsing. Every fact it printed is already on the planner's own Autonomy
            // line (phase, until, successor, rejected - appended per design note E.2) and on
            // [UAT][Planner]; `cause` is the Stand's, and [UAT][Brain] carries it.
            return true;
        }

        /// <summary>
        /// Death, despawn, a real order, a combat intent. Ends the sweep with it, because the sweep
        /// is the hold's and a member with an order is not holding.
        /// </summary>
        internal void ClearIdleAction()
        {
            _idleSince = -1f; _hasIdleGoal = false; _idleAtGoal = false;
            _idlePhase = BlacksiteIdlePhase.None;
            _idleSuccessor = BlacksiteIdleSuccessor.None;
            _idleExpiresAt = 0f;
            _idleRejected = BlacksiteIdleActionPolicy.NoneRejected;
            _idleSequence = 0;
            _idleFrontierDoorway = 0L;
            _idleSweepArmed = false;
            if (_facingSweeping) EndFacingSweep();
        }

        // -------------------------------------------------------------------------------------
        // The phase clock. The ONLY place a scan or a rank happens.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// The phase transition: <c>None</c> opens a hold, an expired hold ranks a successor, an
        /// expired or refused successor ranks the next one, and a rank with nothing left falls to
        /// the rally ladder. Reached on a transition only, so the ranking and the scans below cost
        /// nothing on an ordinary frame (design note C.3).
        /// </summary>
        private void Advance(in BlacksiteSquadAssignment assignment, float now)
        {
            // Every transition re-opens the one sweep arm this phase is allowed (S2).
            _idleSweepArmed = false;
            if (_idlePhase == BlacksiteIdlePhase.None)
            {
                // #957 S12A U4 / G6: the hold arms the sweep the PLANNER built the targets for, and
                // its clock runs from the assignment's own IssuedAt so a member that has already
                // been standing for four of the six seconds does not get six more.
                _idlePhase = BlacksiteIdlePhase.Hold;
                _idleSuccessor = BlacksiteIdleSuccessor.None;
                _idleGoal = assignment.IsValid && assignment.IsHold
                    ? assignment.Destination : transform.position;
                _hasIdleGoal = true; _idleAtGoal = false;
                float issuedAt = assignment.IsValid ? assignment.IssuedAt : now;
                _idleExpiresAt = Mathf.Max(now + .01f, issuedAt + BlacksiteIdleActionPolicy.HoldSeconds);
                return;
            }

            // Leaving Successor clears the mask: the successor either expired (it was given its own
            // full life and did not get there) or it is being re-ranked after a refusal, and a
            // refusal sets its own bit before we arrive here.
            if (_idlePhase != BlacksiteIdlePhase.Successor)
                _idleRejected = BlacksiteIdleActionPolicy.NoneRejected;
            RankIdleSuccessor(in assignment, now);
        }

        /// <summary>
        /// Rank once, take the answer, and set the phase. <see cref="BlacksiteIdleActionPolicy.Rank"/>
        /// is pure and total, so this cannot answer <c>None</c>; <c>RallyHold</c> is the terminal and
        /// is what puts the member into <see cref="BlacksiteIdlePhase.Rally"/>.
        /// </summary>
        private void RankIdleSuccessor(in BlacksiteSquadAssignment assignment, float now)
        {
            var set = assignment.IsValid ? assignment.Successors : default;
            // #1018 S4 (fix batch 3, item 11): one ranking input is read from a different place
            // here than in the planner's preview, and deliberately. `queuedForResource` is
            // IsWaitingForTraversal - the BODY's own wait, which is what decides whether this
            // member may leave where it is standing - while the planner's preview reads the
            // broker's lane set, because on its own tick that is the cheap and authoritative
            // answer for the whole squad and the per-frame path must not scan every lane
            // (design note G3). The two agree whenever a member's own coordinator task is the
            // reason it is queued, which is every production case; where they could differ, the
            // body's answer is the one that should decide what the body takes, and the log's
            // `successor=` is a preview. Every other input is the same value, by S6.
            var ranked = BlacksiteIdleActionPolicy.Rank(
                OnOperatorLaneForIdle(in assignment),
                set.HasRegroup,
                HasCommittedRouteForIdle(),
                IsWaitingForTraversal,
                in set, _idleRejected);

            if (ranked == BlacksiteIdleSuccessor.RallyHold
                || !set.TryGet(ranked, out Vector3 point, out Vector3 facing, out _))
            {
                BeginIdleRally(in assignment, now);
                return;
            }

            _idlePhase = BlacksiteIdlePhase.Successor;
            // #1018 S4 (fix batch 3, item 9): a re-rank IS a phase transition, so the one sweep arm
            // this phase is allowed re-opens here as well. Without it the Successor -> Successor
            // refusal path - a GuardPost refused, then another proved - kept the previous
            // successor's latch and the post that did get taken never armed its sweep.
            _idleSweepArmed = false;
            _idleSuccessor = ranked;
            _idleSuccessorGoal = point; _idleSuccessorFacing = facing;
            _idleGoal = point; _hasIdleGoal = true; _idleAtGoal = false;
            _idleExpiresAt = now + BlacksiteIdleActionPolicy.SecondsFor(ranked, in set);
            // #990 S16 D3 (fix batch 1, B3): the 90 s doorway stamp belongs to an ISSUE. The planner
            // proved the doorway without spending it; taking it here is what spends it, so a
            // doorway nobody was ever sent to stays a candidate.
            _idleFrontierDoorway = 0L;
            if (ranked == BlacksiteIdleSuccessor.FrontierTile && TryGetMission(out var mission)
                && mission.Objectives != null
                && mission.Objectives.Planner.TryGetFrontierDoorway(NetworkObjectId, out long doorway))
            {
                _idleFrontierDoorway = doorway;
                mission.Objectives.Planner.NoteFrontierTaken(NetworkObjectId, doorway);
            }
            // #1018 S4 (fix batch 2, B1): the sweep ends on taking ANY successor, a GuardPost
            // included, because a member that has been told to walk somewhere is walking - and the
            // FacingSweep lease outranks Idle, so a sweep left armed over a walk holds the body
            // against the destination it was just given. G6's "the sweep stays armed through a
            // GuardPost" is honoured by the ARRIVAL branch in TickIdleSuccessor, which re-arms it
            // once the member is standing on the post, exactly as that branch's comment says.
            if (_facingSweeping) EndFacingSweep();
        }

        /// <summary>
        /// The rally ladder, moved off <c>HoldWithoutOrder</c>: the stranded goal, then the
        /// last good navmesh position, then a sampled position. It runs HERE, on the transition,
        /// and never on a frame.
        /// </summary>
        private void BeginIdleRally(in BlacksiteSquadAssignment assignment, float now, bool resite = false)
        {
            bool entering = _idlePhase != BlacksiteIdlePhase.Rally;
            _idlePhase = BlacksiteIdlePhase.Rally;
            _idleSuccessor = BlacksiteIdleSuccessor.RallyHold;
            _idleAtGoal = false;
            _idleSweepArmed = false;
            Vector3 rally = _transportLost && _hasStrandedGoal ? _strandedGoal
                : _hasLastGoodNavPosition ? _lastGoodNavPosition : ResolveNavMeshPosition(transform.position, 4f);
            _idleGoal = rally; _hasIdleGoal = true;
            // #1018 S4 (fix batch 2, S11): a RE-SITE does not reset the phase clock. The rally
            // phase expires at its original 15 s and re-ranks then; resetting the clock on every
            // re-site meant a member persistently more than 8 m from its rally point re-ran the
            // re-site and could never reach the re-rank at all.
            if (!resite) _idleExpiresAt = now + BlacksiteIdleActionPolicy.RallySeconds;
            if (_facingSweeping) EndFacingSweep();
            if (!entering) return;
            // Design note C.4's terminal handoff, which is the one …Duties.cs:484-492 and #1009 A1's
            // RequestImmediateReplan already perform: every successor refused is a fact about the
            // member's candidates, so the planner is told and plans again now. The difference from
            // today's terminal is that the member holds at a rally point WITH AN EXPIRY while it does.
            if (!TryGetMission(out var mission) || mission.Objectives == null) return;
            mission.Objectives.Planner.NoteIdleExhausted(NetworkObjectId);
            mission.Objectives.Planner.RequestImmediateReplan(this);
        }

        // -------------------------------------------------------------------------------------
        // The per-frame halves. One compare, one distance, one idempotent re-assert.
        // -------------------------------------------------------------------------------------

        private void TickIdleHold(float now)
        {
            // #874 S4: the member is at its goal, so it is arrived; nothing holds the body. The
            // sweep only turns the head.
            if (!_facingSweeping) ArmIdleSweep();
        }

        private void TickIdleSuccessor(in BlacksiteSquadAssignment assignment, float now)
        {
            // The latched point, never a re-read of the set: the planner re-proves the set on its
            // own 4 s cadence and a member mid-walk must not be re-aimed by it (fix batch 1, SF6).
            float range = HorizontalDistance(transform.position, _idleSuccessorGoal);
            float stop = Navigator.StopDistance;
            _idleAtGoal = _idleAtGoal
                ? range <= stop + BlacksiteArrivalPolicy.MinDestinationChange
                : range <= stop;
            _idleGoal = _idleSuccessorGoal;
            if (_idleAtGoal)
            {
                // Arrived. A GuardPost keeps looking (G6); everything else stands on the point it
                // was sent to until its own expiry, which is what stops the 4 s re-pick.
                if (_idleSuccessor == BlacksiteIdleSuccessor.GuardPost && !_facingSweeping) ArmIdleSweep();
                return;
            }
            // #1018 S4 (fix batch 3, item 4): a member that is NOT at its goal must not be holding
            // a facing sweep: a post the member is displaced from reaches this line with the sweep
            // still live.
            if (_facingSweeping) EndFacingSweep();
            var verdict = SetDestinationThrottled(_idleSuccessorGoal,
                issuer: IdleIssuer(_idleSuccessor));
            if (verdict.Outcome != BlacksiteMotionOutcome.Blocked) return;
            // Design note C.4: a refused successor sets its bit and the ranking runs again IN THE
            // SAME FRAME. Bounded by the five kinds, so there is no loop that can spin: each pass
            // either takes a kind the mask has not refused or falls to the terminal.
            for (int rank = 0; rank < BlacksiteIdleActionPolicy.SuccessorKinds; rank++)
            {
                _idleRejected = BlacksiteIdleActionPolicy.Refuse(_idleRejected, _idleSuccessor);
                RankIdleSuccessor(in assignment, now);
                if (_idlePhase != BlacksiteIdlePhase.Successor) return;
                verdict = SetDestinationThrottled(_idleSuccessorGoal, issuer: IdleIssuer(_idleSuccessor));
                if (verdict.Outcome != BlacksiteMotionOutcome.Blocked) return;
            }
            BeginIdleRally(in assignment, now);
        }

        private void TickIdleRally(in BlacksiteSquadAssignment assignment, float now)
        {
            // HoldWithoutOrder's own re-site test, unchanged in every term - INCLUDING its four
            // second gate (fix batch 2, S11). The board re-sited a rally point at most once every
            // four seconds; without that gate this re-sited on every frame a member stayed out of
            // range.
            if ((!_hasIdleGoal || HorizontalDistance(transform.position, _idleGoal) > 8f)
                && now - _idleSince >= 4f)
            {
                _idleSince = now;
                BeginIdleRally(in assignment, now, resite: true);
                return;
            }
            float range = HorizontalDistance(transform.position, _idleGoal);
            float stop = Navigator.StopDistance;
            _idleAtGoal = _idleAtGoal
                ? range <= stop + BlacksiteArrivalPolicy.MinDestinationChange
                : range <= stop;
            if (!_idleAtGoal) SetDestinationThrottled(_idleGoal, issuer: IdleIssuer(BlacksiteIdleSuccessor.RallyHold));
        }

        // -------------------------------------------------------------------------------------
        // The inputs the ranking needs that are the MEMBER's, not the planner's.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// #1018 S4 (fix batch 2, S6): the <c>onOperatorLane</c> term, read off the RECORD.
        ///
        /// It is the preference's own Lane arm, which only the planner can decide, so the planner
        /// puts it on the successor set and both rankings read the same value. A first draft
        /// derived it here from "a cover point exists", which is a different question - Coverage
        /// proves a cover point too - so the planner's preview and the body's own rank could answer
        /// differently and <c>successor=</c> in the log was not always what the body took.
        /// </summary>
        private static bool OnOperatorLaneForIdle(in BlacksiteSquadAssignment assignment)
            => assignment.IsValid && assignment.Successors.CoverIsLane;

        /// <summary>
        /// The <c>hasCommittedRoute</c> term, which is
        /// <see cref="BlacksiteIdlePolicy.IssuesRoutelessDestination"/>'s own: with a route in force
        /// nothing else is walking anywhere either, so a held observation is the right answer and
        /// only a routeless squad takes a frontier.
        /// </summary>
        private bool HasCommittedRouteForIdle()
            => TryGetMission(out var mission) && mission.RoutePlanner != null
               && mission.RoutePlanner.CommittedRoute.Count > 0;

        /// <summary>
        /// #1018 S4 (fix batch 2, S2): arm the facing sweep AT MOST ONCE per phase entry.
        ///
        /// <c>BeginFacingSweep</c> ends itself when the target list is degenerate, so
        /// <c>IsFacingSweeping</c> stays false and a "arm if not sweeping" test re-armed on every
        /// frame - which meant the room-graph doorway and corner build ran at frame rate for any
        /// member the graph could not place. The latch is per phase and is cleared by every
        /// transition, so a real re-entry still re-arms.
        ///
        /// The name stays <c>BeginIdleSweep</c> and the list is built by the same rule; it is
        /// reached on the PLANNER, which is where design note F.3.3 re-points
        /// SquadRouteChecks.cs:729. #1262 P3: the elevator's next-trip wait (WaitAtStaging) arms it too.
        /// </summary>
        internal void ArmIdleSweep()
        {
            if (_idleSweepArmed) return;
            _idleSweepArmed = true;
            if (!TryGetMission(out var mission) || mission.Objectives == null) return;
            mission.Objectives.Planner.BeginIdleSweep(this);
        }

        private string DescribeIdleRejected()
        {
            if (_idleRejected == BlacksiteIdleActionPolicy.NoneRejected)
                return BlacksiteDiagnosticFormat.None;
            string joined = null;
            for (int kind = 1; kind <= BlacksiteIdleActionPolicy.SuccessorKinds; kind++)
            {
                var successor = (BlacksiteIdleSuccessor)kind;
                if (!BlacksiteIdleActionPolicy.Refused(_idleRejected, successor)) continue;
                joined = joined == null ? successor.ToString() : joined + "+" + successor;
            }
            return joined ?? BlacksiteDiagnosticFormat.None;
        }
    }
}
