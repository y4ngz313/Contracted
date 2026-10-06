using System;
using System.Collections.Generic;
using System.Text;
using DunGen;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Facility.Interior;
using Y4NGZCompany.Facility.Interior.Placement;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #954 S12A U1. The host side of the squad route: one planner per mission that turns the live
    /// dungeon into a <see cref="BlacksiteRoomGraph"/> and publishes ONE committed room route for
    /// the whole squad to follow. The route is shared; the bodies are not, so nothing here hands a
    /// member a point - it publishes the lead's waypoint, the next room, and the doorway and corner
    /// targets of a tile, and U2 (#955) and U4 (#957) place bodies against them.
    ///
    /// The rule this replaces is the nearest unvisited AI node with a ninety second stamp
    /// (BlacksiteObjectiveManifestController.Raid.cs SearchGoal). That rule keeps no route and no
    /// permanent explored set, so six members produce six goals and a finished branch comes back a
    /// minute and a half later. Until #955 lands, SearchGoal still issues the goal: this unit only
    /// builds the route and logs it.
    ///
    /// Host authority: every entry point returns without effect unless the mission is the server.
    /// The build is keyed on the live Dungeon instance, so a new round rebuilds the graph on first
    /// use and the previous round's tiles are dropped with it.
    /// </summary>
    internal sealed class BlacksiteSquadRoutePlanner
    {
        // A hop the NavMesh refuses right now is usually a closed door, not a hop that can never be
        // walked. (The mineshaft car is no such hop: the reachability oracle rides it between its
        // two landings wherever it stands, #874 N6.) The door resolves inside one interaction
        // envelope: the leaf's own travel after the hand operates it (#1233 S5: no confirm clock,
        // the leaf takes what it takes), which the progress ledger bounds - a member held at a leaf
        // that has not let him advance is answered after BlacksiteMotionMonitor.WindowSeconds 8 -
        // plus the frontier search's own 2 s re-pick: 8 + 2 = 10. Retrying sooner would spend a
        // NavMesh path per squad tick on a door that has not finished opening.
        internal const float HopRetrySeconds = 10f;

        // The tile's own bounds centre is sampled at 2 m, the same radius InteriorAnchorService
        // uses to put an anchor on the floor of a tile, because a tile centre can sit above a
        // stair or a pit and still belong to a walkable room.
        private const float RepresentativeSampleRadius = 2f;

        // A bounds corner is inside the wall by construction. Insetting by one agent diameter
        // (BlacksiteArrivalPolicy.AgentRadius 0.45 doubled) makes a corner target a place a soldier
        // can actually stand, and the same 2 m sample then puts it on the floor.
        private const float CornerInsetMetres = BlacksiteArrivalPolicy.AgentRadius * 2f;

        private const string EventBuilt = "built";
        private const string EventPlanned = "planned";
        private const string EventAdvanced = "advanced";
        private const string EventDeadEnd = "deadEnd";
        private const string EventReplanned = "replanned";
        // #989 C3: the routeless report. It is not a route state change, so it is not a LogRoute
        // event; it is the standing answer to "why is there no plan", rate-limited so five minutes
        // of routelessness cannot be invisible the way the 2026-09-12 run's was.
        private const string EventNoRoute = "noRoute";
        // #989 C3: how often the routeless report repeats. One HopRetrySeconds, because that is
        // exactly how often the only thing that can change the answer - a refused hop coming back -
        // is re-asked.
        private const float NoRouteReportSeconds = HopRetrySeconds;

        private const string ReasonObjectiveKnown = "objectiveKnown";
        private const string ReasonFrontier = "frontier";
        private const string ReasonLeadEntered = "leadEntered";
        private const string ReasonDegreeExhausted = "degreeExhausted";
        private const string ReasonObjectiveMoved = "objectiveMoved";
        private const string ReasonHopUnproven = "hopUnproven";
        private const string ReasonNoRoute = "noRoute";
        // #989 C3: a frontier planned toward a known but unroutable objective tile.
        private const string ReasonFrontierToward = "frontierToward";

        // ---- #950 A4 (O2): the frontier-room sweep. ----
        //
        // A frontier room opened toward an objective the proven hops cannot reach is the one room
        // the squad has a reason to LOOK at, and until this landed the graph wrote it off the frame
        // the lead's body crossed its boundary: Observe marked the lead's tile explored on entry, so
        // a 40 m cave tile was "explored" from its doorway and the permanent explored set meant the
        // squad never came back. On 2026-09-12 that produced explored=7/86 over five minutes with
        // the crate in a room nobody had looked into. The room is now held open until the squad's
        // bodies have covered its published doorway and corner targets, or the budget expires.
        //
        // It applies ONLY to a frontierToward destination. Every other tile the lead walks through
        // is marked exactly as before, so ordinary transit is unchanged.
        private const string EventSweep = "sweep";
        private const string EventSwept = "swept";
        // One interaction envelope, the same bound a refused hop is re-asked on. It is the ceiling,
        // not the cost: a room whose targets are all covered is finished the moment they are.
        private const float SweepBudgetSeconds = HopRetrySeconds;
        // A target is covered when a body is within one destination-change grain of it
        // (BlacksiteArrivalPolicy.MinDestinationChange 1.45 m). That is the distance at which the
        // navigator stops treating a goal as a different place, so a member inside it has been there.
        private const float SweepCoverMetres = BlacksiteArrivalPolicy.MinDestinationChange;

        private static readonly Vector3[] NoPoints = new Vector3[0];

        private readonly BlacksiteRetrievalMissionController _mission;
        private readonly Func<int, int, bool> _hopProven;
        // #950 A4 (O2): the frontier rank, and the objective it is measured to. Cached delegate and
        // field rather than a closure so a plan allocates nothing.
        private readonly Func<int, float> _frontierWorldDistance;
        private Vector3 _frontierObjective;
        // #950 A4 (O2): the room being swept, the targets still uncovered in it, and the budget.
        // -1 means no sweep is open, which is every tick outside a frontierToward arrival.
        private readonly List<Vector3> _sweepTargets = new List<Vector3>();
        private int _sweepTile = -1;
        private float _sweepDeadline;
        private readonly Dictionary<long, HopVerdict> _hops = new Dictionary<long, HopVerdict>();
        private readonly List<int> _route = new List<int>();
        private readonly HashSet<int> _deadEndLogged = new HashSet<int>();
        private readonly HashSet<int> _occupied = new HashSet<int>();
        private readonly HashSet<int> _occupiedNow = new HashSet<int>();
        private readonly List<int> _exited = new List<int>();

        private BlacksiteRoomGraph _graph;
        private Dungeon _dungeon;
        private Tile[] _tiles;
        private Bounds[] _bounds;
        private Vector3[] _representative;
        private bool[] _walkable;
        private TileRoomKind[] _kind;
        private List<Vector3>[] _doorways;
        private List<Vector3>[] _corners;

        private Vector3 _leadPosition;
        private Vector3? _objective;
        private string _replanCause;
        private bool _failedPlan;
        // #989 C3: the committed route is a frontier planned toward an objective tile the proven
        // hops cannot reach, not a route to the objective. It ends at an unexplored tile, so the two
        // rules that keep a route alive while the objective is known - the keep check below and
        // Observe's spent-plan check - have to treat it as the frontier plan it is.
        private bool _frontierToward;
        // #989 C3: the first refused hop out of the lead's tile is dumped once per graph, because it
        // is the one datum the 2026-09-12 log could not answer: why the very first plan failed.
        private bool _noRouteHopDumped;
        private float _nextPlanAttemptAt;
        private int _currentTile = -1;
        private int _objectiveTile = -1;
        private int _routeVersion;
        // #955 S12A U2: the fixture seam. A synthetic planner owns its graph outright, so it is
        // host-authoritative and already built; nothing else about it differs from a live one.
        private bool _synthetic;

        internal BlacksiteSquadRoutePlanner(BlacksiteRetrievalMissionController mission)
        {
            _mission = mission;
            _hopProven = HopProven;
            _frontierWorldDistance = FrontierWorldDistance;
        }

        /// <summary>
        /// #950 A4 (O2): how far a tile's representative point is from the objective the frontier is
        /// being planned toward. This is the rank PlanFrontierToward uses; the graph itself stays
        /// free of UnityEngine types, so the measurement lives here. A tile with no representative
        /// point of its own is unmeasurable and is ranked last rather than refused.
        /// </summary>
        private float FrontierWorldDistance(int tile)
        {
            if (!InRange(tile) || _representative == null || _walkable == null || !_walkable[tile])
                return float.MaxValue;
            return Vector3.Distance(_representative[tile], _frontierObjective);
        }

        /// <summary>
        /// #955 S12A U2: build a planner over a graph the caller already has, for a fixture that
        /// has a baked NavMesh but no DunGen dungeon. The hop proof, the route, the explored set,
        /// the dead-end rule, the waypoint and the logging are the live ones; only the dungeon walk
        /// that produces the tiles, bounds, representative points and doorways is supplied.
        /// </summary>
        internal static BlacksiteSquadRoutePlanner FromSynthetic(
            BlacksiteRoomGraph graph,
            Tile[] tiles,
            Bounds[] bounds,
            Vector3[] representative,
            TileRoomKind[] kinds,
            List<Vector3>[] doorways)
        {
            var planner = new BlacksiteSquadRoutePlanner(null);
            int count = graph.Count;
            planner._graph = graph;
            planner._tiles = tiles;
            planner._bounds = bounds;
            planner._representative = representative;
            planner._kind = kinds;
            planner._doorways = doorways;
            planner._corners = new List<Vector3>[count];
            planner._walkable = new bool[count];
            for (int i = 0; i < count; i++)
                planner._walkable[i] = tiles[i] != null;
            planner._synthetic = true;
            return planner;
        }

        /// <summary>The committed route, lead's tile first and the destination last. Empty means no plan.</summary>
        internal IReadOnlyList<int> CommittedRoute => _route;

        /// <summary>The tile the lead was last observed in, or -1 before the first observation.</summary>
        internal int CurrentTile => _currentTile;

        /// <summary>The next tile on the committed route, or -1 when the route is empty or finished.</summary>
        internal int NextRoom => _route.Count >= 2 ? _route[1] : -1;

        /// <summary>Increments on every planned and replanned event so a consumer can gate on it.</summary>
        internal int RouteVersion => _routeVersion;

        /// <summary>
        /// Where the lead walks: the representative point of <see cref="NextRoom"/>, the objective
        /// itself once the lead is in the objective's tile, and the lead's own position when there
        /// is no route. Never a stale waypoint.
        /// </summary>
        internal Vector3 LeadWaypoint
        {
            get
            {
                if (_route.Count == 0)
                    return _leadPosition;
                // #989 C3: a frontierToward route does not end at the objective, so its last tile is
                // a room to open and never the objective point the proven hops just refused.
                bool objectiveRoute = _objective.HasValue && _objectiveTile >= 0 && !_frontierToward;
                if (objectiveRoute && _currentTile == _objectiveTile)
                    return _objective.Value;
                int next = NextRoom;
                if (next < 0)
                    return objectiveRoute ? _objective.Value : _leadPosition;
                return _walkable != null && next < _walkable.Length && _walkable[next]
                    ? _representative[next]
                    : _leadPosition;
            }
        }

        /// <summary>
        /// Plans, or keeps, the committed route. A known objective plans to the objective's tile; a
        /// null objective plans to the nearest unexplored tile. Safe every squad tick: a proven route is
        /// kept silently, and a refused plan is not re-asked for one HopRetrySeconds (#1305 S: the one latch).
        /// </summary>
        internal bool TryPlan(Vector3 leadPosition, Vector3? objective, out string reason)
        {
            reason = ReasonNoRoute;
            if (Time.time < _nextPlanAttemptAt || !HostAuthority || !EnsureBuilt())
                return false;

            _leadPosition = leadPosition;
            _objective = objective;
            int lead = TileAt(leadPosition);
            if (lead >= 0)
            {
                _currentTile = lead;
                // #950 A4 (O2): a room still being swept is not explored yet. Everything else is
                // marked on entry exactly as it was.
                if (_sweepTile != lead)
                    _graph.MarkExplored(lead);
            }

            int objectiveTile = objective.HasValue ? TileAt(objective.Value) : -1;
            bool known = objectiveTile >= 0;

            if (_replanCause == null && _route.Count > 0 && _route[0] == _currentTile
                && objectiveTile == _objectiveTile
                // #989 C3: a frontierToward route ends at an unexplored tile, so it is spent the
                // moment the lead explores that tile, exactly as an ordinary frontier route is.
                && ((known && !_frontierToward) || !_graph.IsExplored(_route[_route.Count - 1]))
                && (_route.Count == 1 || HopProven(_route[0], _route[1])))
            {
                reason = _frontierToward ? ReasonFrontierToward : known ? ReasonObjectiveKnown : ReasonFrontier;
                return true;
            }

            bool first = _routeVersion == 0;
            string cause = first
                ? (known ? ReasonObjectiveKnown : ReasonFrontier)
                : (known && objectiveTile != _objectiveTile
                    ? ReasonObjectiveMoved
                    : (_replanCause ?? ReasonFrontier));
            _replanCause = null;
            _objectiveTile = objectiveTile;

            List<int> planned;
            bool routed = known
                ? _graph.PlanRoute(_currentTile, objectiveTile, _hopProven, out planned)
                : _graph.PlanFrontier(_currentTile, _hopProven, out planned, out _);
            _frontierToward = false;

            // #989 C3: the frontier fall-through. `known` is always true for a Whistleblower raid -
            // TileAt falls back to NearestCentreTile, so an objective tile is never -1 while a
            // record exists - so PlanFrontier was dead code for the whole 2026-09-12 mission and a
            // failed PlanRoute cleared the committed route outright. A squad that cannot reach him
            // opens the next unexplored room toward him instead of falling back to the pre-#954
            // nearest-unvisited-node wander, which explored 11 of 58 tiles in five minutes.
            if (known && (!routed || planned == null || planned.Count == 0))
            {
                NoteNoRoute(objectiveTile);
                // #950 A4 (O2): rank the candidate rooms by how far they are from HIM in the world,
                // with the doorway hop count only breaking ties. See PlanFrontierToward.
                _frontierObjective = objective.Value;
                if (_graph.PlanFrontierToward(_currentTile, objectiveTile, _hopProven, out planned, out _,
                        _frontierWorldDistance)
                    && planned != null && planned.Count > 0)
                {
                    routed = true;
                    _frontierToward = true;
                    cause = ReasonFrontierToward;
                }
            }

            if (!routed || planned == null || planned.Count == 0)
            {
                if (!known) NoteNoRoute(objectiveTile);
                // The cross-cutting rule: a plan that finds nothing says so, and LeadWaypoint falls
                // back to the lead's own position rather than to a stale waypoint. The latch keeps
                // the line a transition: a squad that is boxed in says so once, not once per tick.
                // The only thing that can change the answer is a refused hop coming back, so the
                // next automatic attempt waits exactly one HopRetrySeconds.
                bool transition = _route.Count > 0 || !_failedPlan;
                _route.Clear();
                _failedPlan = true;
                _nextPlanAttemptAt = Time.time + HopRetrySeconds;
                reason = ReasonNoRoute;
                if (transition)
                {
                    _routeVersion++;
                    LogRoute(EventReplanned, reason, _currentTile, _currentTile, -1, true);
                }
                return false;
            }

            _failedPlan = false;
            _nextPlanAttemptAt = 0f;
            reason = cause;
            if (SameAsCommitted(planned))
                return true;

            _route.Clear();
            _route.AddRange(planned);
            _routeVersion++;
            LogRoute(first ? EventPlanned : EventReplanned, reason, _currentTile, Destination, NextRoom, true);
            return true;
        }

        private bool SameAsCommitted(List<int> planned)
        {
            if (planned.Count != _route.Count)
                return false;
            for (int i = 0; i < planned.Count; i++)
                if (planned[i] != _route[i])
                    return false;
            return true;
        }

        /// <summary>
        /// One squad tick of observation. Marks the lead's tile explored on entry, advances the
        /// route when the lead enters <see cref="NextRoom"/>, logs a dead end once when the squad
        /// leaves a tile whose doorways are all explored, and re-plans when the route is spent, the
        /// next hop stops proving or a dead end retires the branch. Returns true when the route
        /// advanced on this call.
        /// </summary>
        internal bool Observe(Vector3 leadPosition, IReadOnlyList<Vector3> memberPositions)
        {
            if (!HostAuthority || !EnsureBuilt())
                return false;

            _leadPosition = leadPosition;
            bool advanced = false;
            int lead = TileAt(leadPosition);
            if (lead >= 0)
            {
                if (lead != _currentTile)
                {
                    // #950 A4 (O2): the lead has left the room it was sweeping, so the sweep is
                    // over however much of it was covered, and the room is written off now.
                    if (_sweepTile >= 0 && _sweepTile != lead)
                        FinishSweep("left");
                    // The destination of a frontierToward route is the room the squad opened in
                    // order to LOOK at it. It is swept before it is written off; every other tile
                    // is marked on entry as before.
                    if (_frontierToward && _route.Count > 0 && lead == _route[_route.Count - 1]
                        && !_graph.IsExplored(lead))
                        BeginSweep(lead);
                    else
                        _graph.MarkExplored(lead);
                }
                if (lead == NextRoom)
                {
                    // #989 C5 (round 2): the lead just walked this hop with its body, which is a
                    // stronger fact than the NavMesh path HopProven caches. Record it as such so a
                    // single member's partial path can never refuse a doorway the squad has used.
                    NoteHopWalked(_currentTile, lead);
                    _route.RemoveAt(0);
                    advanced = true;
                }
                _currentTile = lead;
            }

            if (advanced)
                LogRoute(EventAdvanced, ReasonLeadEntered, _currentTile, Destination, NextRoom, true);

            // #950 A4 (O2): strike the covered targets off the open sweep before the spent-plan
            // rules run, so a room whose targets are all covered finishes on the same tick.
            TickSweep(leadPosition, memberPositions);

            string cause = NoteExitedDeadEnds(lead, memberPositions);
            if (cause == null && NextRoom >= 0 && !HopProven(_currentTile, NextRoom))
                cause = ReasonHopUnproven;
            // No route at all, or a frontier route whose frontier the lead has just explored. A
            // committed route to a KNOWN objective that is down to its last tile is an arrival, not
            // a spent plan, so it does not re-plan and does not log.
            // #989 C3: a frontierToward route is a frontier route, so its last tile is spent too.
            if (cause == null && (_route.Count == 0 || (_route.Count == 1 && (_objectiveTile < 0 || _frontierToward))))
                cause = ReasonFrontier;
            // #950 A4 (O2): a room being swept is not a spent plan. The sweep IS the plan, and
            // re-planning here would only re-pick the unexplored room the squad is standing in.
            // A dead end or an unproven hop still wins, because both are facts about the graph.
            if (cause == ReasonFrontier && _sweepTile >= 0)
                cause = null;

            if (cause != null && Time.time >= _nextPlanAttemptAt)
            {
                _replanCause = cause;
                TryPlan(leadPosition, _objective, out _);
            }

            return advanced;
        }

        /// <summary>
        /// #950 A4 (O2): open the sweep of a frontier room.
        ///
        /// The targets are the ones the room already publishes - its connected doorways and its
        /// inset floor corners - because those are the points the role actions already walk bodies
        /// to (BlacksiteObjectiveManifestController.Raid.cs TryRouteLocalPoint: the pointman peeks a
        /// doorway, the riflemen claim corners). The squad order for a member following a committed
        /// route is already <c>SearchLastKnown</c>, issued at Raid.cs where the route order is
        /// built, so opening a sweep only has to say so out loud: the Searching voice line, through
        /// the manifest's own announcement, which is the line that already means "we are looking".
        ///
        /// A room with nothing to sweep is marked at once; nothing here can leave a tile unexplored
        /// for good, because the budget always ends the sweep and leaving the room ends it too.
        /// </summary>
        private void BeginSweep(int tile)
        {
            _sweepTargets.Clear();
            IReadOnlyList<Vector3> doorways = DoorwayTargets(tile);
            for (int i = 0; i < doorways.Count; i++)
                _sweepTargets.Add(doorways[i]);
            IReadOnlyList<Vector3> corners = CornerTargets(tile);
            for (int i = 0; i < corners.Count; i++)
                _sweepTargets.Add(corners[i]);
            if (_sweepTargets.Count == 0)
            {
                _graph.MarkExplored(tile);
                return;
            }
            _sweepTile = tile;
            _sweepDeadline = Time.time + SweepBudgetSeconds;
            // The existing Searching line, through the squad channel rather than one member's, with
            // the same volume BlacksiteSoldierAI.AnnounceSearch asks for. It is a transition - one
            // line per room opened, not one per tick - and the director's own suppression rules
            // decide whether the channel is free to say it.
            if (_mission != null && _mission.SquadDirector != null)
                _mission.SquadDirector.RequestVoice(
                    BlacksiteVoiceEvent.Searching,
                    transition: true,
                    transitionToken: tile + 1,
                    volume: .7f);
            if (BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.Log("Route",
                    $"event={EventSweep}, tile={tile}, targets={_sweepTargets.Count}"
                    + $", budget={SweepBudgetSeconds:0.00}"
                    + $", explored={_graph.ExploredCount}/{_graph.Count}");
        }

        /// <summary>
        /// #950 A4 (O2): strike every open sweep target a body has covered this tick, and close the
        /// sweep when none are left or the budget is spent. Measured against the lead and against
        /// every member position the caller collected, because a corner a rifleman is standing in
        /// has been swept whether or not the lead ever went near it.
        /// </summary>
        private void TickSweep(Vector3 leadPosition, IReadOnlyList<Vector3> memberPositions)
        {
            if (_sweepTile < 0)
                return;
            for (int i = _sweepTargets.Count - 1; i >= 0; i--)
            {
                Vector3 target = _sweepTargets[i];
                bool covered = Vector3.Distance(leadPosition, target) <= SweepCoverMetres;
                for (int m = 0; !covered && memberPositions != null && m < memberPositions.Count; m++)
                    covered = Vector3.Distance(memberPositions[m], target) <= SweepCoverMetres;
                if (covered)
                    _sweepTargets.RemoveAt(i);
            }
            if (_sweepTargets.Count == 0)
            { FinishSweep("covered"); return; }
            if (Time.time >= _sweepDeadline)
                FinishSweep("budget");
        }

        /// <summary>
        /// #950 A4 (O2): close the sweep and write the room off. The reason names which of the three
        /// endings it was, so a log can tell a room the squad actually walked from one the budget
        /// gave up on.
        /// </summary>
        private void FinishSweep(string reason)
        {
            int tile = _sweepTile;
            if (tile < 0)
                return;
            int uncovered = _sweepTargets.Count;
            _sweepTile = -1;
            _sweepTargets.Clear();
            _graph.MarkExplored(tile);
            if (BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.Log("Route",
                    $"event={EventSwept}, tile={tile}, reason={reason}, uncovered={uncovered}"
                    + $", explored={_graph.ExploredCount}/{_graph.Count}");
        }

        /// <summary>World positions of this tile's connected doorways, for the pointman's peek (U4).</summary>
        internal IReadOnlyList<Vector3> DoorwayTargets(int tile)
        {
            if (!EnsureBuilt() || !InRange(tile))
                return NoPoints;
            return (IReadOnlyList<Vector3>)_doorways[tile] ?? NoPoints;
        }

        /// <summary>
        /// The tile's floor corners projected to the NavMesh, for the corner sweep (U4). Built on
        /// first request and cached, so a level's worth of corner sampling is never paid for tiles
        /// the squad never enters.
        /// </summary>
        internal IReadOnlyList<Vector3> CornerTargets(int tile)
        {
            if (!EnsureBuilt() || !InRange(tile) || _tiles[tile] == null)
                return NoPoints;
            return _corners[tile] ?? (_corners[tile] = BuildCorners(_bounds[tile]));
        }

        /// <summary>
        /// #990 S16 D3: has the squad been in this tile? The graph already tracks it - every
        /// <c>Observe</c> marks the lead's tile explored - and the routeless idle rule needs to tell
        /// a doorway that opens onto somewhere new from one that opens onto the room the squad just
        /// left. Read-only; nothing here marks anything.
        /// </summary>
        internal bool IsTileExplored(int tile) => EnsureBuilt() && _graph != null && _graph.IsExplored(tile);

        /// <summary>
        /// #1016 S2: how many tiles the squad has opened. <c>BlacksiteRoomGraph.ExploredCount:42</c>
        /// has always known it and the planner already prints it on its own lines (<c>:470</c>,
        /// <c>:515</c>, <c>:941</c>); this is the same number published so the blackboard's read
        /// facade can answer without reaching into <c>_graph</c>. Read-only; nothing here builds or
        /// marks anything, so an unbuilt planner answers 0 rather than forcing a graph walk.
        /// </summary>
        internal int ExploredCount => _graph != null ? _graph.ExploredCount : 0;

        /// <summary>
        /// #990 S16 D3: the tile on the far side of one of <paramref name="tile"/>'s doorways, or
        /// -1 when the opening is a dead end (no other tile records it).
        ///
        /// Two doorway records of the same opening are the SAME world position on both sides - the
        /// duty board's idle sweep already relies on that and calls it "an identity test, not a
        /// tolerance to tune" - so this is a lookup, not a geometric probe past the threshold, and
        /// it cannot pick the wrong room on a doorway the squad is standing in.
        /// </summary>
        internal int TileAcrossDoorway(int tile, Vector3 doorway)
        {
            if (!EnsureBuilt() || !InRange(tile) || _doorways == null) return -1;
            for (int other = 0; other < _doorways.Length; other++)
            {
                if (other == tile || _doorways[other] == null) continue;
                var list = _doorways[other];
                for (int i = 0; i < list.Count; i++)
                    if (Vector3.SqrMagnitude(list[i] - doorway) <= DoorwayIdentityMetres * DoorwayIdentityMetres)
                        return other;
            }
            return -1;
        }

        /// <summary>#990: how close two records of one opening are. The duty board's own 0.05 m.</summary>
        internal const float DoorwayIdentityMetres = .05f;

        internal TileRoomKind KindOf(int tile)
        {
            if (!EnsureBuilt())
                return TileRoomKind.Hallway;
            return KindAt(tile);
        }

        /// <summary>The tile a world point is in: bounds contains, else the nearest bounds centre.</summary>
        internal int TileOf(Vector3 position)
        {
            return EnsureBuilt() ? TileAt(position) : -1;
        }

        private bool HostAuthority => _synthetic || (_mission != null && _mission.IsServer);

        private int Destination => _route.Count > 0 ? _route[_route.Count - 1] : _currentTile;

        private bool InRange(int tile) => _tiles != null && tile >= 0 && tile < _tiles.Length;

        private TileRoomKind KindAt(int tile) => InRange(tile) ? _kind[tile] : TileRoomKind.Hallway;

        private bool EnsureBuilt()
        {
            // #955 S12A U2: a synthetic planner owns its graph and is never rebuilt from a dungeon.
            if (_synthetic)
                return true;
            Dungeon dungeon = RoundManager.Instance != null && RoundManager.Instance.dungeonGenerator != null
                && RoundManager.Instance.dungeonGenerator.Generator != null
                ? RoundManager.Instance.dungeonGenerator.Generator.CurrentDungeon
                : null;
            if (dungeon == null || dungeon.AllTiles == null || dungeon.AllTiles.Count == 0)
                return false;
            if (_graph != null && ReferenceEquals(_dungeon, dungeon))
                return true;
            Build(dungeon);
            return _graph != null;
        }

        private void Build(Dungeon dungeon)
        {
            var all = dungeon.AllTiles;
            int count = all.Count;
            _tiles = new Tile[count];
            _bounds = new Bounds[count];
            _representative = new Vector3[count];
            _walkable = new bool[count];
            _kind = new TileRoomKind[count];
            _doorways = new List<Vector3>[count];
            _corners = new List<Vector3>[count];

            var index = new Dictionary<Tile, int>(count);
            for (int i = 0; i < count; i++)
            {
                Tile tile = all[i];
                _kind[i] = TileRoomKind.Hallway;
                if (tile == null)
                    continue;
                _tiles[i] = tile;
                _bounds[i] = tile.Bounds;
                _kind[i] = TileRoomClassifierRuntime.Classify(tile).Kind;
                if (!index.ContainsKey(tile))
                    index[tile] = i;
            }

            var binned = new List<Vector3>[count];
            int nodes = 0;
            int unbinned = 0;
            GameObject[] aiNodes = RoundManager.Instance != null ? RoundManager.Instance.insideAINodes : null;
            if (aiNodes != null)
                for (int i = 0; i < aiNodes.Length; i++)
                {
                    GameObject node = aiNodes[i];
                    if (node == null)
                        continue;
                    nodes++;
                    Vector3 point = node.transform.position;
                    int tile = ContainingTile(point);
                    if (tile < 0)
                    {
                        unbinned++;
                        tile = NearestCentreTile(point);
                    }
                    if (tile < 0)
                        continue;
                    (binned[tile] ?? (binned[tile] = new List<Vector3>(4))).Add(point);
                }

            for (int i = 0; i < count; i++)
            {
                if (_tiles[i] == null)
                    continue;
                Vector3 centre = _bounds[i].center;
                _representative[i] = centre;

                // The binned AI node nearest the bounds centre is the best representative there is:
                // it is already a point the vanilla AI walks to.
                List<Vector3> points = binned[i];
                float bestSqr = float.PositiveInfinity;
                for (int p = 0; points != null && p < points.Count; p++)
                {
                    float sqr = (points[p] - centre).sqrMagnitude;
                    if (sqr >= bestSqr)
                        continue;
                    bestSqr = sqr;
                    _representative[i] = points[p];
                    _walkable[i] = true;
                }

                if (!_walkable[i])
                {
                    if (NavMesh.SamplePosition(centre, out NavMeshHit hit, RepresentativeSampleRadius, NavMesh.AllAreas))
                    {
                        _representative[i] = hit.position;
                        _walkable[i] = true;
                    }
                }
            }

            var graph = new BlacksiteRoomGraph(count);
            int edges = 0;
            for (int i = 0; i < count; i++)
            {
                Tile tile = _tiles[i];
                if (tile == null || tile.UsedDoorways == null)
                    continue;
                for (int d = 0; d < tile.UsedDoorways.Count; d++)
                {
                    Doorway doorway = tile.UsedDoorways[d];
                    Doorway connected = doorway != null ? doorway.ConnectedDoorway : null;
                    if (connected == null || connected.Tile == null)
                        continue;
                    if (!index.TryGetValue(connected.Tile, out int other) || other == i)
                        continue;

                    (_doorways[i] ?? (_doorways[i] = new List<Vector3>(4))).Add(doorway.transform.position);

                    // A tile with no walkable representative has no proven hop either, so it gets
                    // no edges rather than an edge the route could never walk.
                    if (!_walkable[i] || !_walkable[other])
                        continue;
                    int before = graph.Degree(i);
                    graph.AddEdge(i, other);
                    if (graph.Degree(i) != before)
                        edges++;
                }
            }

            _graph = graph;
            _dungeon = dungeon;
            _hops.Clear();
            _route.Clear();
            _deadEndLogged.Clear();
            _occupied.Clear();
            _currentTile = -1;
            _objectiveTile = -1;
            _routeVersion = 0;
            _replanCause = null;
            _failedPlan = false;
            _nextPlanAttemptAt = 0f;
            _frontierToward = false;
            _noRouteHopDumped = false;
            // #950 A4 (O2): a sweep belongs to a room of the old graph.
            _sweepTile = -1;
            _sweepTargets.Clear();
            _sweepDeadline = 0f;

            if (BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.Log("Route",
                    $"event={EventBuilt}, tiles={count}, edges={edges}, nodes={nodes}, unbinned={unbinned}");
        }

        private List<Vector3> BuildCorners(Bounds bounds)
        {
            var corners = new List<Vector3>(4);
            float y = bounds.min.y;
            float minX = Mathf.Min(bounds.min.x + CornerInsetMetres, bounds.center.x);
            float maxX = Mathf.Max(bounds.max.x - CornerInsetMetres, bounds.center.x);
            float minZ = Mathf.Min(bounds.min.z + CornerInsetMetres, bounds.center.z);
            float maxZ = Mathf.Max(bounds.max.z - CornerInsetMetres, bounds.center.z);
            AddCorner(corners, new Vector3(minX, y, minZ));
            AddCorner(corners, new Vector3(maxX, y, minZ));
            AddCorner(corners, new Vector3(minX, y, maxZ));
            AddCorner(corners, new Vector3(maxX, y, maxZ));
            return corners;
        }

        private void AddCorner(List<Vector3> corners, Vector3 point)
        {
            if (NavMesh.SamplePosition(point, out NavMeshHit hit, RepresentativeSampleRadius, NavMesh.AllAreas))
                corners.Add(hit.position);
        }

        private int ContainingTile(Vector3 position)
        {
            for (int i = 0; i < _tiles.Length; i++)
                if (_tiles[i] != null && _bounds[i].Contains(position))
                    return i;
            return -1;
        }

        private int NearestCentreTile(Vector3 position)
        {
            int nearest = -1;
            float nearestSqr = float.PositiveInfinity;
            for (int i = 0; i < _tiles.Length; i++)
            {
                if (_tiles[i] == null)
                    continue;
                float sqr = (_bounds[i].center - position).sqrMagnitude;
                if (sqr >= nearestSqr)
                    continue;
                nearestSqr = sqr;
                nearest = i;
            }
            return nearest;
        }

        private int TileAt(Vector3 position)
        {
            int contained = ContainingTile(position);
            return contained >= 0 ? contained : NearestCentreTile(position);
        }

        /// <summary>
        /// A hop is proven when a complete NavMesh path joins the two tiles' representative points,
        /// or, #874 N6, when the two points are joined by walking to one landing of the mineshaft
        /// car and on from the other. The car is not a NavMesh link, so the start tile's hop
        /// down to the caves never proved. On Vow (log 11103, refusedHop=0->1 from -182 to -219) the
        /// planner stayed routeless (explored=1/98) for as long as the lead was on the top floor.
        /// The verdict is cached for the mission; a refusal is re-asked after
        /// <see cref="HopRetrySeconds"/> so a closed door does not retire a doorway for good.
        /// </summary>
        private bool HopProven(int a, int b)
        {
            if (!InRange(a) || !InRange(b) || !_walkable[a] || !_walkable[b])
                return false;
            if (a == b)
                return true;

            long key = HopKey(a, b);
            if (_hops.TryGetValue(key, out HopVerdict verdict))
            {
                if (verdict.Proven)
                    return true;
                if (Time.time < verdict.RetryAt)
                    return false;
            }

            InteriorAnchorService.RecordNavMeshPathCalculation();
            bool proven = BlacksiteReachability.Route(BlacksiteTraveller.Soldier(false), _representative[a], _representative[b]).Reachable;
            _hops[key] = new HopVerdict(proven, proven ? 0f : Time.time + HopRetrySeconds);
            return proven;
        }

        /// <summary>
        /// Records which tiles hold a body this tick and reports whether any tile the squad has
        /// just left has its doorways all explored. Each such tile logs one deadEnd line for the
        /// mission, lowest tile id first; the squad never walks back in because the explored set
        /// never expires.
        /// </summary>
        private string NoteExitedDeadEnds(int lead, IReadOnlyList<Vector3> memberPositions)
        {
            _occupiedNow.Clear();
            if (lead >= 0)
                _occupiedNow.Add(lead);
            for (int i = 0; memberPositions != null && i < memberPositions.Count; i++)
            {
                int tile = TileAt(memberPositions[i]);
                if (tile >= 0)
                    _occupiedNow.Add(tile);
            }

            _exited.Clear();
            foreach (int tile in _occupied)
                if (!_occupiedNow.Contains(tile))
                    _exited.Add(tile);

            _occupied.Clear();
            foreach (int tile in _occupiedNow)
                _occupied.Add(tile);

            string cause = null;
            _exited.Sort();
            for (int i = 0; i < _exited.Count; i++)
            {
                int tile = _exited[i];
                if (!_graph.IsDeadEnd(tile) || !_deadEndLogged.Add(tile))
                    continue;
                LogRoute(EventDeadEnd, ReasonDegreeExhausted, tile, tile, -1, false, tile);
                cause = ReasonFrontier;
            }

            return cause;
        }

        private static long HopKey(int a, int b)
            => a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;

        /// <summary>
        /// #989 C5 (round 2). The lead crossed this doorway on its own legs, so the hop is proven
        /// for the mission.
        /// </summary>
        private void NoteHopWalked(int a, int b)
        {
            if (!InRange(a) || !InRange(b) || a == b)
                return;
            _hops[HopKey(a, b)] = new HopVerdict(true, 0f);
        }

        /// <summary>
        /// #989 C3. The routeless report. It replaces the one-shot transition latch that made the
        /// 2026-09-12 run's five minutes of routelessness invisible: the planner failed its first
        /// and only plan at t=105.67 and the latch suppressed every later line, so the squad being
        /// routeless could only be inferred from the ABSENCE of an event=planned line.
        ///
        /// It names the objective tile, its representative point and its walkability, and on the
        /// first failure per graph it dumps the first refused hop out of the lead's own tile with
        /// both representative points - which is the one datum that log could not answer.
        /// </summary>
        private void NoteNoRoute(int objectiveTile)
        {
            if (!BlacksiteDiagnostics.Enabled)
                return;

            string hop = BlacksiteDiagnosticFormat.None;
            if (!_noRouteHopDumped && InRange(_currentTile))
            {
                _noRouteHopDumped = true;
                IReadOnlyList<int> neighbours = _graph.Neighbours(_currentTile);
                for (int i = 0; i < neighbours.Count; i++)
                {
                    int next = neighbours[i];
                    if (HopProven(_currentTile, next))
                        continue;
                    hop = $"{_currentTile}->{next} from={BlacksiteDiagnosticFormat.V(_representative[_currentTile])}"
                        + $" to={(InRange(next) ? BlacksiteDiagnosticFormat.V(_representative[next]) : BlacksiteDiagnosticFormat.None)}"
                        + $" walkable={(InRange(next) && _walkable[next])}";
                    break;
                }
                if (hop == BlacksiteDiagnosticFormat.None)
                    hop = neighbours.Count == 0 ? "none: the lead's tile has no edges" : "none: every hop out of the lead's tile proves";
            }

            BlacksiteDiagnostics.LogRateLimited("route.noRoute", NoRouteReportSeconds, "Route",
                $"event={EventNoRoute}, tile={(objectiveTile >= 0 ? objectiveTile.ToString() : BlacksiteDiagnosticFormat.None)}"
                + $", point={(InRange(objectiveTile) ? BlacksiteDiagnosticFormat.V(_representative[objectiveTile]) : BlacksiteDiagnosticFormat.None)}"
                + $", walkable={(InRange(objectiveTile) && _walkable[objectiveTile])}"
                + $", from={(_currentTile >= 0 ? _currentTile.ToString() : BlacksiteDiagnosticFormat.None)}"
                + $", refusedHop={hop}"
                + $", explored={_graph.ExploredCount}/{_graph.Count}");
        }

        private void LogRoute(string logEvent, string reason, int from, int to, int next, bool leadKnown,
            int singleTile = -1)
        {
            if (!BlacksiteDiagnostics.Enabled)
                return;

            var builder = new StringBuilder(160);
            builder.Append("event=").Append(logEvent).Append(", tiles=[");
            if (singleTile >= 0)
            {
                builder.Append(singleTile);
            }
            else
            {
                for (int i = 0; i < _route.Count; i++)
                {
                    if (i > 0)
                        builder.Append(',');
                    builder.Append(_route[i]);
                }
            }

            builder.Append("], reason=").Append(reason)
                .Append(", from=").Append(from >= 0 ? from.ToString() : BlacksiteDiagnosticFormat.None)
                .Append(", to=").Append(to >= 0 ? to.ToString() : BlacksiteDiagnosticFormat.None)
                .Append(", next=").Append(next >= 0 ? next.ToString() : BlacksiteDiagnosticFormat.None)
                .Append(", lead=").Append(leadKnown ? BlacksiteDiagnosticFormat.V(_leadPosition) : BlacksiteDiagnosticFormat.None)
                .Append(", kind=").Append(KindAt(next >= 0 ? next : from).ToString())
                .Append(", explored=").Append(_graph.ExploredCount).Append('/').Append(_graph.Count);
            BlacksiteDiagnostics.Log("Route", builder.ToString());
        }

        private readonly struct HopVerdict
        {
            internal HopVerdict(bool proven, float retryAt)
            {
                Proven = proven;
                RetryAt = retryAt;
            }

            internal bool Proven { get; }

            internal float RetryAt { get; }
        }
    }
}
