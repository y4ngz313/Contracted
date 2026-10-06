using System.Collections.Generic;
using UnityEngine;
using Y4NGZCompany.Facility.Interior;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 S12 U4: when an interior is mostly unreachable from its own entrance. Pure, so the
    /// native fixture pins the rule rather than restating it. "More than half" is the brief's own
    /// threshold and needs no derivation beyond itself: below it a scan that excluded some tiles is
    /// ordinary geometry, above it the facility the squad was sent into is mostly not there.
    /// </summary>
    internal static class BlacksiteInteriorReachability
    {
        internal static bool MostlyExcluded(int tilesEvaluated, int tilesReachable)
            => tilesEvaluated > 0 && tilesReachable >= 0 && tilesReachable <= tilesEvaluated
               && (tilesEvaluated - tilesReachable) * 2 > tilesEvaluated;
    }

    // #1018 S4: what is LEFT of the duty board. #1020 S6: and the three forwarders are gone too.
    //
    // The board WAS 1,052 lines that both read and planned: OrderFor's 221-line planning body, its
    // twelve-boolean replan gate, DutyIntent, the 4 s idle rule, ChooseIdleGoal, BeginIdleSweep,
    // TryIdleIntentDestination, TryCoveragePoint, TickDutyJobs' pass, TickResourceBroker, ForgetDuty
    // and the Duty plan state, with SEVEN writers of duty.Order between them (design note A.2). All
    // of that is BlacksiteSquadPlanner's now, and there is one writer.
    //
    // Four things stay here, each for a stated reason:
    //   * the interior anchor scan (TickInteriorReachability, InteriorFloors,
    //     InteriorTiles*), which is the manifest's own facility knowledge and is what the planner
    //     asks rather than owns - BlacksiteSquadPlanner.Tick calls it, and that call is what the
    //     Cecil IL assertion in CombatRecoveryChecks now pins;
    //   * the shared route-query budget (SpendCandidatePath, _candidatePathFrame,
    //     _memberCandidatePaths) and _planningBlocked, because SupportPoint and the objective half
    //     of …Jobs.cs still write them - one budget for the squad, one blocked map (fix batch 1, B2);
    //   * _idleDoorwayVisited, per-member state the planner reads through this type for the
    //     same reason;
    //   * TryGetOrder (the old OrderFor) and ClearDutyJobs, which are NOT forwarders. TryGetOrder
    //     is a one-line projection with live production callers; ClearDutyJobs is 35 lines of
    //     mission reset only the manifest can do.
    //
    // #1020 S6 deleted the forwarders (TickDutyJobs, RequestImmediateReplan and the resite
    // forwarder). Every caller now names the planner directly, which is the type that
    // owns the pass, the replan and the goal.
    internal sealed partial class BlacksiteObjectiveManifestController
    {
        private int _candidatePathFrame = -1, _candidatePaths;
        private readonly Dictionary<ulong, int> _memberCandidatePaths = new Dictionary<ulong, int>();
        // Shared by response, support and frontier planners, in addition to ordinary navigation.
        // Four route queries per member and 24 across the squad per frame; cached failures allow
        // the next decision to consider further candidates instead of hammering the same wall.
        //
        // #1018 S4 (fix batch 1, B2): ONE budget for the squad. The planner calls this rather than
        // keeping its own, because a second counter doubled the squad's path queries per frame.
        private bool SpendCandidatePath(ulong member)
        {
            if (_candidatePathFrame != Time.frameCount)
            { _candidatePathFrame = Time.frameCount; _candidatePaths = 0; _memberCandidatePaths.Clear(); }
            _memberCandidatePaths.TryGetValue(member, out int used);
            if (_candidatePaths >= 24 || used >= 4)
            { _planningBlocked[member] = BlacksiteBlockedCause.PlanningBudget; return false; }
            _candidatePaths++; _memberCandidatePaths[member] = used + 1; return true;
        }
        private readonly Dictionary<ulong, BlacksiteBlockedCause> _planningBlocked = new Dictionary<ulong, BlacksiteBlockedCause>();

        /// <summary>#1018 S4: the planner's order read (<see cref="BlacksiteSquadPlanner.TryGetOrder"/>).</summary>
        internal bool TryGetOrder(BlacksiteSoldierAI soldier, out BlacksiteMemberOrder order) => Planner.TryGetOrder(soldier, out order);

        /// <summary>
        /// #990 S16 D3: how long a doorway this member has already been sent to stays spent.
        /// SearchGoal's own stamp, because this rule is the same kind of rule against the same kind
        /// of target: "somewhere I have not tried lately", not "somewhere new forever". Without it a
        /// routeless member alternates between the two openings of a corridor and calls it progress.
        /// </summary>
        internal const float IdleDoorwayVisitedSeconds = 90f;

        // Per member, per doorway identity (the same half-metre key the entrance column uses).
        // #1018 S4 (fix batch 1, B3): the planner PROVES a doorway without writing this, and the
        // idle Action spends the stamp when it actually issues the FrontierTile successor.
        private readonly Dictionary<(ulong Member, long Doorway), float> _idleDoorwayVisited
            = new Dictionary<(ulong, long), float>();

        // #874 S12 U4: the interior anchor scan's own verdicts, read once per scan. Run 1's
        // HangarFlow (run1-2026-09-10-veld.log 52475 ff.) excluded 47 of 64 tiles as unreachable
        // from the facility entrance and the squad then produced 57 LocalRepath and 8 hard Failed
        // against goals inside them with no re-site and no line saying why. Fixing that navmesh is
        // out of scope; saying so once, and preferring goals the scan calls reachable, is not.
        private readonly BlacksiteChangeGate _interiorScanGate = new BlacksiteChangeGate();
        internal int InteriorTilesEvaluated { get; private set; }
        internal int InteriorTilesReachable { get; private set; }
        internal bool InteriorMostlyExcluded => _interiorMostlyExcluded;
        private bool _interiorMostlyExcluded;

        // #1293 R3: read by the planner pass before the first member it plans, so a pass that plans
        // nobody reads nothing; an unchanged tally (cached scan verdicts) returns at once.
        private void TickInteriorReachability()
        {
            InteriorAnchorService.GetEntranceReachabilityTally(out int tiles, out int reachable);
            if (tiles == InteriorTilesEvaluated && reachable == InteriorTilesReachable) return;
            InteriorTilesEvaluated = tiles; InteriorTilesReachable = reachable;
            _interiorMostlyExcluded = BlacksiteInteriorReachability.MostlyExcluded(tiles, reachable);
            if (!_interiorMostlyExcluded) return;
            // One line per scan verdict, not per tick: the gate re-arms only when the tally moves.
            if (!_interiorScanGate.ShouldReport(0, tiles + "/" + reachable, out int folded)) return;
            BlacksiteDiagnostics.Log("Interior",
                $"tiles={tiles}, reachable={reachable}, excluded={tiles - reachable}, floors={InteriorFloors()}, folded={folded}");
        }

        // The distinct interior floors the mission's own objectives stand on, each marked with the
        // scan's verdict for that point. A floor band is one storey of the swept body column, the
        // same measure BlacksiteResponsePolicy.OnFloor uses to decide two points share a storey.
        private string InteriorFloors()
        {
            var floors = new List<float>();
            var builder = new System.Text.StringBuilder();
            foreach (var r in Records)
            {
                if (r.Outside) continue;
                bool known = false;
                for (int i = 0; i < floors.Count; i++) if (BlacksiteResponsePolicy.OnFloor(r.Position.y, floors[i])) { known = true; break; }
                if (known) continue;
                floors.Add(r.Position.y);
                if (builder.Length > 0) builder.Append(' ');
                builder.Append(r.Position.y.ToString("0.0"))
                    .Append(InteriorAnchorService.TryGetEntrancePathDistance(r.Position, out _) ? ":reachable" : ":excluded");
            }
            return builder.Length > 0 ? builder.ToString() : "none";
        }

        /// <summary>
        /// #1018 S4: the name is kept (design note F.3.2) and the per-member half of the body is
        /// now <see cref="BlacksiteSquadPlanner.Clear"/>. What stays here is the manifest's own
        /// mission reset, including the shared per-member dictionaries <c>ForgetDuty</c> used to
        /// clear one member at a time - a new mission clears all of them outright.
        /// </summary>
        private void ClearDutyJobs()
        {
            Planner.Clear();
            _jobs.Clear(); _attempts.Clear();
            _backupGuards.Clear(); _backupResponses.Clear(); _backupReporter = null;
            // #874 S9.5: a new mission carries no earlier anchor, so the whole evidence state goes.
            _hasBackupFloor = _hadBackupFloor = _backupPointOnMesh = false; _backupFloorSource = "none";
            // #874 S12 U4: a new mission is a new facility, so the scan verdict and its line gate go.
            _interiorScanGate.Reset(); InteriorTilesEvaluated = InteriorTilesReachable = 0;
            _interiorMostlyExcluded = false;
            ClearPlayerResponses();
            // The per-member dictionaries the retired ForgetDuty cleared. They are the manifest's,
            // because SupportPoint and the objective half still write them (fix batch 1, B2).
            _supportPoints.Clear(); _supportCenters.Clear(); _supportUntil.Clear();
            _supportCursor.Clear(); _supportMisses.Clear(); _blockedSupport.Clear();
            _supportFallback.Clear();
            _patrolPoints.Clear(); _guardDuty.Clear();
            _patrolSource.Clear();
            _searchRoutes.Clear(); _idleDoorwayVisited.Clear();
            _planningBlocked.Clear(); _operatorLane.Clear();
            _memberCandidatePaths.Clear(); _candidatePathFrame = -1;
            _visited.Clear(); _insideSearchNodes = null; _outsideSearchNodes = null;
            _regroupUntil[0] = _regroupUntil[1] = 0f;
            _focusLogged.Clear();
            // #955 S12A U2: a new mission is a new facility, so the committed route's own squad
            // state goes with it. The planner itself is the mission's and rebuilds on its dungeon.
            _routeTickFrame = -1; _routeVersionSeen = -1; _routeObjectiveTile = -1; _routeEnteredFrom = -1;
            _routeRaiders.Clear(); _routeMemberPositions.Clear();
            _routeStepOffAt = 0f;
            _routeColumns.Clear(); _routeCornerClaims.Clear();
            _routeHasLeadGoal = false; _routeLeadGoal = Vector3.zero; _routeLeadGoalMissed = false; _routeLeadHolder = _routeElected = null;
        }
    }
}
