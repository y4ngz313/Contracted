using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // One sliced search shared by runtime and extracted-moon checks. Hull centres need physical
    // support, while the exit/ladder and the entire approach/return must have usable navigation.
    // #1305: the one landing judge. It completes with a sealed plan (a ground pose, else a hover
    // over the rappel foot nearest the entrance by walking whose corridor is clear, else over the
    // nearest complete foot at MaxAltitude), or with none only when the entrance is unresolved or
    // no foot exists on the connected outside navmesh.
    internal sealed class BlacksiteLandingSearch
    {
        private readonly Vector3 _main, _ship;
        // #874 plan B: the job's one entrance approach, resolved before this search exists. Every
        // pose's route ends at it; when it is unresolved the search is complete before its first
        // candidate, with that refusal as its only reason.
        private readonly BlacksiteEntranceApproach _entrance;
        private readonly string _entranceRefusal;
        private readonly int _mask, _members;
        private readonly float _footToDeck;
        private readonly IEnumerator<Vector3> _seeds;
        private readonly bool _allowHover;
        private bool _hover;
        private long _groundWork = -1; // #1271 S5: Work when the ground phase ended; -1 while it runs.
        private bool _groundShareExpired;
        private string _groundSummary;
        private readonly List<Vector3> _candidates = new List<Vector3>();
        private readonly Dictionary<(int, int), List<Vector3>> _cells = new Dictionary<(int, int), List<Vector3>>();
        private readonly Dictionary<string, int> _reasons = new Dictionary<string, int>();
        // #951 S11H U3 (e) / #1233 S8: the egress and ladder proof's phase for the current heading.
        // 0 not started; 1 handed its own slice; 2 egress slots proven; 3 ladder ground route proven
        // (the leg has passed). Reset wherever the heading changes.
        private bool _gathering = true, _yieldFrame;
        private byte _accessPhase;
        // #1233 S8: the ladder clearance variant the next ladder Step tries, and the last refusal
        // a variant gave (an unsupported variant gives none), reported if all four refuse.
        private int _ladderVariant;
        private string _ladderRefusal;
        // #1233 S8: the banked second pose the next Step tries, after the level pose refused for
        // gear spread; and whether the next Step is the boarding access of a pose that passed.
        // Both cleared by every heading change with the access phase.
        private bool _bankPending, _boardingPending;
        // #1233 S7: the corridor altitude proven for the placed pose, carried to its boarding Step.
        private float _provenAltitude;
        private Quaternion _bankedRotation;
        private int _candidate, _heading, _approachesPassed, _nearDepth = -1;
        // #873 S12 U5 (2): consecutive support refusals on the current candidate, and how many
        // candidates gave up their remaining headings because of them.
        private int _supportFailures, _abandonedCandidates;
        // #1305: the hover phase. A breadth-first flood of the outside navmesh from the entrance
        // approach, so the frontier is in walking-distance order; each foot it dequeues is judged
        // in its own Steps (_hoverStage: Frontier, Foot, StepOff, Pose, RoutePath, Egress). The
        // corridor is preferred across feet: an obstructed foot is only the nearest one's last resort.
        private readonly Queue<Vector3> _frontier = new Queue<Vector3>();
        private readonly HashSet<(int, int, int)> _visited = new HashSet<(int, int, int)>();
        private StepKind _hoverStage = StepKind.Frontier;
        private Vector3 _foot;
        private Quaternion _facing;
        private int _pose, _footPoses, _feet, _posesTried;
        // The first hull- and column-clear pose of the current foot, routed when no pose has a clear
        // corridor and no last resort is kept yet.
        private bool _hasPose, _corridorClear;
        private Vector3 _poseOrigin;
        private Quaternion _poseRotation;
        private BlacksiteLandingAccess _poseAccess;
        // The nearest foot whose whole chain completed obstructed, sealed if no foot completes clear.
        private (Vector3 origin, Quaternion rotation, BlacksiteLandingAccess access, Vector3 exit, Vector3[] egress, string detail, BlacksiteLandingApproach route)? _lastResort;
        private Vector3[] _egress; // the plan's: ground's proven fan, hover's best effort
        private Vector3 _origin, _exit;
        private Quaternion _rotation;
        private BlacksiteLandingAccess _access;
        private readonly (Bounds bounds, string name)[] _carvers;
        private BlacksiteLandingApproach _route;
        private string _detail, _nearMiss;
        internal bool Complete { get; private set; }
        internal bool Found { get; private set; }
        internal string Detail { get; private set; }
        internal float RouteMetres { get; private set; }
        // #874 N1: the accepted pose's lip-to-deck-navigation gap on the probe bake; NaN for hover.
        internal float LipNavGap { get; private set; } = float.NaN;
        internal BlacksiteLandingPlan Plan { get; private set; }
        internal int CandidateCount => _candidates.Count;
        internal string Progress => _hover
            ? $"hover: feet={_feet}, frontier={_frontier.Count}, poses={_posesTried}, routeActive={_route != null}"
            : "ground: " + (_gathering ? "gathering sites" : $"site={_candidate + 1}/{CandidateCount}, heading={_heading + 1}/72, routeActive={_route != null}");
        internal int Rejections { get; private set; }
        internal int Slices { get; private set; }
        internal double MaxSliceMilliseconds { get; private set; }
        // #1233 S8: every Step is one of these legs, and the slice loop reads the leg the NEXT Step
        // would run (PeekKind) against that leg's last-observed cost before it starts it, so a
        // slice stops short of its budget rather than overrunning it by one whole leg. The longest
        // Step and its leg are reported (maxStepMs=/longestStep=); the loop cannot yield inside
        // one, so a leg's cost is the floor under any slice budget.
        internal enum StepKind : byte { None, Gather, Sort, RoutePath, RouteSample, Egress, Ladder, Deck, Placement, PlacementBank, Boarding, Frontier, Foot, StepOff, Pose }
        internal double MaxStepMilliseconds { get; private set; }
        internal StepKind LongestStep { get; private set; }
        private StepKind _stepKind;
        private readonly double[] _stepCost = new double[15];
        private bool _sortPending;
        internal int SupportRays { get; private set; }
        internal int HullQueries { get; private set; }
        internal int MaximumPoses { get; private set; }
        // #873 S12 U5: what the [UAT][Landing] line reads off the job.
        internal int Approaches => _approachesPassed;
        /// <summary>Whether the ground phase was ended by its work share rather than by running out
        /// of sites. A hover landing on a moon where this is true is a moon whose ground search was
        /// still working when its six seconds of work ran out.</summary>
        internal bool GroundShareExpired => _groundShareExpired;
        // #1293 P2: when hover took over, the ground phase's work (nominal s) and the sites it never tried (0 = all tried).
        internal float GroundWorkSeconds => _groundWork < 0 ? float.NaN : (float)(_groundWork / GroundWorkPerSecond);
        internal int GroundSitesLeft { get; private set; }
        /// <summary>The reason a completed search has no site: the entrance refusal, else the most
        /// common refusal. <c>null</c> when a site was found.</summary>
        internal string FailureReason => Found ? null : _entranceRefusal
            ?? (_hover ? "no rappel foot on the connected outside navmesh: " + (TopReason ?? "no outside navigation at the entrance")
            : TopReason ?? "no candidate sites were gathered");
        private string TopReason => _reasons.Count == 0 ? null : _reasons.OrderByDescending(p => p.Value).First().Key;
        internal string Summary => $"mode={(_hover ? "hover" : "ground")}, candidates={CandidateCount}, approaches={_approachesPassed}, rejected={Rejections}, slices={Slices}, maxSliceMs={MaxSliceMilliseconds:0.00}, maxStepMs={MaxStepMilliseconds:0.00}, longestStep={LongestStep}, supportRays={SupportRays}, hullQueries={HullQueries}, workSeconds={WorkSeconds:0.00}, maxPoses={MaximumPoses}, abandoned={_abandonedCandidates}, deckProofMs={BlacksiteShipNavigation.LastDeckProofMilliseconds:0.00}, reasons=[{(_groundShareExpired && !_hover ? GroundShareReason + ", " : "")}{string.Join(", ", _reasons.OrderByDescending(p => p.Value).Take(8).Select(p => p.Key + ":" + p.Value))}], nearMiss=[{_nearMiss}]"
            + (_hover ? $", feet={_feet}, frontier={_frontier.Count}, posesTried={_posesTried}" : _entranceRefusal != null ? "" : ", entrance=[" + _entrance + "]")
            + (_groundSummary == null ? "" : ", groundWork=" + GroundWorkSeconds.ToString("0.00") + "s, groundSitesLeft=" + GroundSitesLeft + ", groundSearch=[" + _groundSummary + "]");
        // #1271 S5: the clock the ground share reads, zero when the job starts: the ground phase's
        // own Work priced in nominal seconds (derivation: docs/blacksite/01-transport-and-
        // deployment.md), so the accepted site is the same at any slice budget or frame rate. #1305:
        // the hover phase is on no clock; it ends with a foot or an empty frontier, and Summary
        // counts its cost (feet=, posesTried=, supportRays=, hullQueries=).
        internal float WorkSeconds => (float)((_groundWork < 0 ? Work : _groundWork) / GroundWorkPerSecond);
        private const double CalibrationSeconds = 12d; // #1293 P2: the 12 s live ground phase the clock is priced on, not the share
        internal const double GroundWorkPerSecond = (351831d + 600143d) / CalibrationSeconds;
        // #1293 P2: the ground phase's work share (Clock seconds) before the search moves to hover. A
        // moon with a ground site accepts it well inside it on the cached fixture moons (Vow 2.17 s,
        // Adamance 0.76 s, Rend 1.89 s), so a moon still searching ground at 6 s hovers instead.
        internal const float GroundShareSeconds = 6f;
        internal const string GroundShareReason = "ground search used its 6s work share; trying hover";
        // #873 S12 U5 (2): consecutive support refusals that abandon a candidate's 72 headings. Eight
        // headings at 5 degrees is 40 degrees of yaw, wider than the 30 degree band inside which a
        // neighbouring heading can still pass continuous support, so the ground is the problem.
        internal const int ConsecutiveSupportFailuresToAbandon = 8;
        // #1305: the rappel poses fitted to a foot, lowest first: 18 yaws 20 degrees apart from the
        // entrance heading at each rope height inside BlacksiteRappelGeometry's 6-18 m band.
        private static readonly float[] PoseHeights = { 6f, 10f, 14f, 18f };
        private const int PoseYaws = 18;
        private const int OutsideArea = NavMesh.AllAreas & ~BlacksiteShipNavigation.DeckAreaMask;
        private long Work => (long)SupportRays + HullQueries;
        internal BlacksiteLandingSearch(BlacksiteEntranceApproach entrance, Vector3 ship, Vector3[] nodes, int mask, float footToDeck, int members,
            bool allowHover = true)
        {
            _entrance = entrance; _main = entrance.Entrance; _ship = ship; _mask = mask; _footToDeck = footToDeck; _members = members;
            _allowHover = allowHover; _carvers = BlacksiteLandingGeometry.WorldCarvers();
            _seeds = Seeds(_main, ship, nodes ?? Array.Empty<Vector3>()).GetEnumerator();
            // Checked before any candidate, hull or cable work: every pose's route would end at a
            // door that has no approach, so none is asked. Not counted through Reject, whose category
            // split would cut the refusal at its " at " and drop the segment's location.
            if (!entrance.Resolved)
            {
                _entranceRefusal = entrance.Refusal; _reasons[_entranceRefusal] = 1; Rejections = 1;
                _gathering = false; Complete = true;
            }
        }
        private static IEnumerable<Vector3> Seeds(Vector3 main, Vector3 ship, Vector3[] nodes)
        {
            // Start near the entrance so a dense node array cannot crowd this plateau out of the
            // bounded search. ClearUsableVolume, not an arbitrary 30m exclusion, protects the door.
            for (int ring = 4; ring <= 12; ring++)
                for (int x = -ring; x <= ring; x++) for (int z = -ring; z <= ring; z++)
                    if (Mathf.Max(Mathf.Abs(x), Mathf.Abs(z)) == ring) yield return main + new Vector3(x * 3f, 0, z * 3f);
            foreach (var node in nodes)
                foreach (var offset in NodeOffsets) yield return node + offset;
            float basis = Mathf.Atan2((ship - main).z, (ship - main).x);
            foreach (float radius in new[] { 20f, 28f, 36f, 44f, 52f, 60f, 72f, 84f, 98f, 120f })
                for (int i = 0; i < 48; i++)
                { float angle = basis + i * Mathf.PI / 24f; yield return main + new Vector3(Mathf.Cos(angle), 0, Mathf.Sin(angle)) * radius; }
        }
        // #1233 S8: one array for every node, not one per node; the gather Step is asked up to
        // 2048 times per phase and its garbage was the only thing that could make it long.
        private static readonly Vector3[] NodeOffsets =
        {
            Vector3.zero, Vector3.right * 6, Vector3.left * 6, Vector3.forward * 6, Vector3.back * 6,
            new Vector3(3, 0, 3), new Vector3(-3, 0, 3), new Vector3(3, 0, -3), new Vector3(-3, 0, -3)
        };
        // #951 S11H U3 (e): the budget a slice is allowed while the local player is outside the
        // ship, where the frame is drawing the world rather than the ship interior and the search
        // is competing with everything the walk to the entrance costs.
        //
        // Derivation: the ship search runs during that walk. 2 ms is half the 4 ms default slice,
        // which at Test 3's measured 91 fps (11.0 ms per frame) leaves the frame at 13.0 ms, or
        // 77 fps worst case if every frame were a full slice, and in practice above 80 because the
        // search completes inside its budget on most slices. The full 4 ms is kept while the player
        // is still inside the ship, which is where the search actually starts.
        internal const double OutsideShipSliceMilliseconds = 2d;

        internal void Advance(double milliseconds = 4)
        {
            if (Complete) return;
            Slices++; _yieldFrame = false;
            double budget = LocalPlayerOutsideShip() ? Math.Min(milliseconds, OutsideShipSliceMilliseconds) : milliseconds;
            var watch = Stopwatch.StartNew();
            double last = 0d;
            // #951 S11H U3 (e): one Step always runs, so a search cannot starve on a late frame.
            // #1233 S8: the loop stops when the NEXT leg's last-observed cost would not fit in what
            // is left; a leg never measured runs first in the next slice. (27 September: 15.87 ms
            // slices on a 4 ms budget, because the loop only asked whether it was ALREADY spent.)
            Step(); Measure();
            while (!Complete && !_yieldFrame)
            {
                double next = _stepCost[(int)PeekKind()];
                if (next <= 0d || watch.Elapsed.TotalMilliseconds + next >= budget) break;
                Step(); Measure();
            }
            MaxSliceMilliseconds = Math.Max(MaxSliceMilliseconds, watch.Elapsed.TotalMilliseconds);
            void Measure()
            {
                double now = watch.Elapsed.TotalMilliseconds, step = now - last; last = now;
                // A leg is priced at its decayed maximum: a NavMesh path or a hull pose varies with
                // the site, and one long instance must keep the next few from starting late.
                _stepCost[(int)_stepKind] = Math.Max(step, _stepCost[(int)_stepKind] * .8d);
                if (step > MaxStepMilliseconds) { MaxStepMilliseconds = step; LongestStep = _stepKind; }
            }
        }
        /// <summary>The leg the next <see cref="Step"/> runs, from the same state Step reads.</summary>
        private StepKind PeekKind()
        {
            if (_hover) return _hoverStage != StepKind.RoutePath ? _hoverStage
                : _route.NextAdvanceBuildsLeg ? StepKind.RoutePath : StepKind.RouteSample;
            if (_gathering) return StepKind.Gather;
            if (_sortPending) return StepKind.Sort;
            if (_candidate >= _candidates.Count) return StepKind.None;
            if (_route != null)
            {
                if (!_route.Complete) return _route.NextAdvanceBuildsLeg ? StepKind.RoutePath : StepKind.RouteSample;
                if (_accessPhase >= 3) return StepKind.Deck;
                return _accessPhase == 0 ? StepKind.None : _accessPhase == 1 ? StepKind.Egress : StepKind.Ladder;
            }
            return _boardingPending ? StepKind.Boarding : _bankPending ? StepKind.PlacementBank : StepKind.Placement;
        }

        // #951 S11H U3 (e): whether the local player is outside the ship, asked for by NAME for the
        // same reason NestProbe below is: this file is compiled a second time by the extracted-moon
        // checks into a project with no game assembly in it, where StartOfRound does not exist. The
        // members are resolved once; the probe is null in the extracted project and the search
        // there keeps the full 4 ms slice, which is correct - that project has no player and no
        // frame to protect.
        private static readonly Func<bool> OutsideShipProbe = BindOutsideShipProbe();

        private static Func<bool> BindOutsideShipProbe()
        {
            try { return ResolveOutsideShipProbe(); }
            // A bind that throws must not take the type's static constructor with it: the search
            // still has to run, it just runs at the full slice.
            catch { return null; }
        }

        private static Func<bool> ResolveOutsideShipProbe()
        {
            Type start = Type.GetType("StartOfRound, Assembly-CSharp", false);
            // A null here means the slice silently stays at 4 ms for the life of the process, so
            // the loaded assemblies are swept too rather than trusting one qualified name.
            if (start == null)
                foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                { start = assembly.GetType("StartOfRound", false); if (start != null) break; }
            PropertyInfo instance = start?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public);
            FieldInfo instanceField = instance == null ? start?.GetField("Instance", BindingFlags.Static | BindingFlags.Public) : null;
            FieldInfo local = start?.GetField("localPlayerController", BindingFlags.Instance | BindingFlags.Public);
            FieldInfo inShip = local?.FieldType.GetField("isInHangarShipRoom", BindingFlags.Instance | BindingFlags.Public);
            if ((instance == null && instanceField == null) || local == null || inShip == null) return null;
            return () =>
            {
                object round = instance != null ? instance.GetValue(null) : instanceField.GetValue(null);
                if (round == null) return false;
                object player = local.GetValue(round);
                // A destroyed body throws out of the field read below and LocalPlayerOutsideShip
                // answers "inside", which is the conservative answer: the full slice, never a
                // starved search.
                return player != null && !(bool)inShip.GetValue(player);
            };
        }

        internal static bool LocalPlayerOutsideShip()
        {
            try { return OutsideShipProbe != null && OutsideShipProbe(); }
            catch { return false; }
        }
        private void Step()
        {
            _stepKind = StepKind.None;
            if (_hover) { HoverStep(); return; }
            if (_gathering)
            {
                _stepKind = StepKind.Gather;
                if (_candidates.Count < 2048 && _seeds.MoveNext()) { Add(_seeds.Current); return; }
                _seeds.Dispose(); _gathering = false; _sortPending = true; return;
            }
            if (_sortPending)
            {
                // Prefer the entrance's ground elevation over roofs selected by a tall probe.
                // #1233 S8: its own Step, and the key is computed once per site rather than twice
                // per comparison (3.4 ms for 1,500 sites became the leg's whole cost).
                _stepKind = StepKind.Sort; _sortPending = false;
                var keys = new float[_candidates.Count];
                for (int i = 0; i < keys.Length; i++) keys[i] = FlatDistance(_candidates[i], _main) + Mathf.Abs(_candidates[i].y - _main.y) * 8f;
                var order = _candidates.ToArray();
                Array.Sort(keys, order);
                _candidates.Clear(); _candidates.AddRange(order);
                return;
            }
            // #873 S12 U5 (1) / #1271 S5: the ground-to-hover handoff when the ground phase has found
            // no site inside its work share; a found pose completes at acceptance.
            if (_allowHover && _route == null && WorkSeconds > GroundShareSeconds) _groundShareExpired = true;
            if (_candidate >= _candidates.Count || _groundShareExpired)
            {
                if (!_allowHover) { Complete = true; return; }
                GroundSitesLeft = _candidates.Count - _candidate; _groundSummary = Summary; _groundWork = Work; _hover = true;
                _candidates.Clear(); _cells.Clear(); _reasons.Clear(); _nearDepth = -1; _nearMiss = null;
                // #1233 S8: a hover leg is priced by the hover phase. A ground route was a walk
                // from a hull on the apron; the hover route starts at a rope foot on ground the
                // ground phase never routed from, and the one hover route of a search is built as
                // a slice's first leg, not appended to a hover pose on the ground phase's price.
                Array.Clear(_stepCost, 0, _stepCost.Length);
                // #1305: the flood starts at the entrance approach's own navigation.
                if (NavMesh.SamplePosition(_entrance.Nav, out var start, 1.5f, OutsideArea)) Visit(start.position);
                return;
            }
            if (_route != null)
            {
                // #1233 S8: one Step is ONE physical route sample, not a batch of twelve; the batch
                // only decided how far past the budget a slice ran (Vow: an 11.26 ms step behind an
                // 11.74 ms slice). Yielding the frame per batch was measured and rejected in #951.
                _stepKind = _route.NextAdvanceBuildsLeg ? StepKind.RoutePath : StepKind.RouteSample;
                _route.Advance(1);
                if (!_route.Complete) return;
                if (!_route.Success) { Reject(_route.Reason, 3); NextHeading(); return; }
                // Only build the six staging paths and the long walk around the hull once
                // the site's entrance route is known to work. All acceptance gates remain.
                //
                // #951 S11H U3 (e): and never on top of a slice that has already spent its budget.
                // #1233 S8: the leg is deferred to the start of the next slice and then stepped - the
                // six egress slots as one Step, then ONE ladder clearance variant per Step, first
                // success wins (touchdown's TryLadderGroundRoute still walks all four for the
                // shortest) - so the 6.66 ms "access" step is at most five of about 2 ms.
                if (_accessPhase < 3)
                {
                    if (_accessPhase == 0) { _accessPhase = 1; _yieldFrame = true; return; }
                    _yieldFrame = true;
                    if (_accessPhase == 1)
                    {
                        _stepKind = StepKind.Egress;
                        if (!BlacksiteShipNavigation.TryEgressSlots(_rotation, _exit, _members, _mask, out _egress, out var egressReason))
                        { Reject(egressReason, 2); NextHeading(); return; }
                        _accessPhase = 2; _ladderVariant = 0; _ladderRefusal = null; return;
                    }
                    _stepKind = StepKind.Ladder;
                    if (BlacksiteShipNavigation.TryLadderGroundRouteVariant(_origin, _rotation, BlacksitePhysicalStep.ProbeTransform,
                            _exit, _access, _mask, _ladderVariant, out _, out _, out var ladderReason))
                    { _accessPhase = 3; return; }
                    if (ladderReason != null) _ladderRefusal = ladderReason;
                    if (++_ladderVariant < BlacksiteShipNavigation.LadderRouteClearances.Length) return;
                    Reject(_ladderRefusal ?? "no ground approach to ladder", 2); NextHeading(); return;
                }
                _stepKind = StepKind.Deck;
                if (!BlacksiteShipNavigation.AdvanceCandidateDeck(_rotation, _access, out bool proofComplete, out var deckReason, out float lipNavGap))
                { if (proofComplete) { Reject(deckReason, 4); NextHeading(); } else _yieldFrame = true; return; }
                Accept(true, lipNavGap); return;
            }
            if (_boardingPending)
            {
                // #1233 S8: the boarding access of the pose the last Step placed, on its own so the
                // placement leg is priced as a placement and this one as itself. Asked before the
                // heading's rotation is recomputed below, which would discard a banked pose.
                _boardingPending = false; _stepKind = StepKind.Boarding;
                if (!BlacksiteBoardingGeometry.TryAccess(_origin, _rotation, _mask, _footToDeck, out _access, out _exit, out var accessReason, members: _members, deferGroundRoutes: true))
                { Reject(accessReason, 2); NextHeading(); return; }
                _access.Altitude = _provenAltitude;
                _route = new BlacksiteLandingApproach(_origin, _rotation, _exit, _entrance, _mask);
                return;
            }
            var candidate = _candidates[_candidate];
            var look = Vector3.ProjectOnPlane(_main - candidate, Vector3.up);
            _rotation = Quaternion.LookRotation(look.sqrMagnitude > .01f ? look.normalized : Vector3.forward) * Quaternion.Euler(0, _heading * 5f, 0);
            // #948 S12 U3: a ground site inside a sapsucker's nest ring is refused before any
            // placement work is done. Run 2 put the ship 4.3 m from a nest (line 53272) and then
            // deployed the squad into the one piece of ground the raid is routed around. The ring
            // is the nest footprint plus the existing ship-guard leash, so a posted guard is never
            // standing on it. The reason goes through the search's own Reject vocabulary, so it is
            // counted and can win the nearMiss the same way every other refusal can.
            // The hover phase deliberately does not ask: a rappel foot is chosen for its drop, the
            // ship never touches the ground, and hover is the search's last resort.
            if (!_bankPending && NestBlocksLanding(candidate, out string nestReason))
            { Reject(nestReason, 0); NextHeading(); return; }
            // #873 S12 U5 (2): the corner rays before any placement work. A candidate with no ground
            // under one of its four gear feet is refused after two rays at that foot instead of
            // twelve support rays, a stability solve and the hull-query pass inside TrySupport, and
            // the filter refuses only what TrySupport provably refuses (see TryCornerSupport).
            // Rejected at depth 0 so a corner miss can never win the near-miss report from a real
            // placement attempt.
            // #1233 S8: the placement is two Steps, one TrySupport each. The level pose runs with
            // the corner filter; a gear-spread refusal (which returns before the hull-volume pass)
            // computes the banked pose from the heights just measured, no physics, and hands it to
            // the NEXT Step, so the slice clock is read between the two support solves.
            bool bank = _bankPending; _bankPending = false;
            if (!bank)
            {
                BlacksiteLandingGeometry.LastTotalSupportRays = 0;
                _stepKind = StepKind.Placement;
                bool corners = BlacksiteLandingGeometry.TryCornerSupport(candidate, _rotation, _mask, _footToDeck, out string cornerReason);
                SupportRays += BlacksiteLandingGeometry.LastTotalSupportRays;
                if (!corners)
                { Reject(cornerReason, 0); NextHeadingAfterSupportFailure(); return; }
                // #1293 P2: before the twelve support rays, refuse a heading the level cabin test refuses at every deck height.
                if (BlacksiteLandingGeometry.CabinBlockedAtEveryDeck(candidate, _rotation, _mask, out string cabinReason))
                { _origin = candidate; Reject(cabinReason, 1); NextHeading(); return; }
            }
            else _stepKind = StepKind.PlacementBank;
            bool placed = BlacksiteLandingGeometry.TrySupport(candidate, bank ? _bankedRotation : _rotation, _mask, _footToDeck, out _origin, out string reason);
            SupportRays += BlacksiteLandingGeometry.LastSupportRays;
            HullQueries += BlacksiteLandingGeometry.LastHullQueries;
            MaximumPoses = Math.Max(MaximumPoses, bank ? 2 : 1);
            if (!bank && !placed && BlacksiteLandingGeometry.TryBankedRotation(_rotation, reason, out _bankedRotation))
            { _bankPending = true; return; }
            if (bank && placed)
            {
                _rotation = _bankedRotation;
                BlacksiteLandingGeometry.LastSupportDetail += " tilt=" + Vector3.Angle(Vector3.up, _rotation * Vector3.up).ToString("0.0");
            }
            if (!placed)
            {
                // #873 S12 U5 (2): "lacks continuous support" and the corner-ray refusal are the same
                // fact measured at two costs, so both advance the abandon counter; every other
                // refusal (tilt, spread, cabin, hull, route) resets it, because those are
                // heading-specific and a neighbouring heading really can pass them.
                bool support = reason != null && reason.EndsWith("lacks continuous support", StringComparison.Ordinal);
                Reject(reason, 1);
                if (support) NextHeadingAfterSupportFailure(); else NextHeading();
                return;
            }
            _detail = BlacksiteLandingGeometry.LastSupportDetail;
            // #1305 C7: the probe deck stands far from the moon, so a world carver on this hull (Burrow's cactus on the starboard catwalk) is refused here.
            if (BlacksiteLandingGeometry.DeckCarved(_origin, _rotation, _carvers, out reason)) { Reject(reason, 1); NextHeading(); return; }
            // #1233 S7: a hull that fits here must also be able to arrive and leave through proven air.
            if (!BlacksiteFlightCorridor.Clear(_origin, _rotation, _mask, null, out _provenAltitude, out reason))
            { Reject(reason, 1); NextHeading(); return; }
            _boardingPending = true;
        }
        // #948 S12 U3 A5: the nest rule, asked for by NAME rather than referenced.
        //
        // This file is compiled twice: once into the mod, and once on its own by the extracted-moon
        // checks (tests/BlacksiteLandingCombatChecks/Run.ps1), which build a bare Unity project out
        // of a handful of policy files and nothing else - no game assembly and no hazard stack. So
        // the rule itself lives in BlacksiteHazardAwareness, where the bird and its nest can be
        // named properly, and this file binds to it once through its own assembly. In the extracted
        // project the bind fails, the probe is null and the rule is correctly inert, because nothing
        // spawns there. Bound once into a delegate, never invoked reflectively per candidate.
        private delegate bool NestProbeDelegate(Vector3 origin, out string reason);
        private static readonly NestProbeDelegate NestProbe = BindNestProbe();

        private static NestProbeDelegate BindNestProbe()
        {
            MethodInfo method = typeof(BlacksiteLandingSearch).Assembly
                .GetType("Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteHazardAwareness", false)
                ?.GetMethod("NestBlocksLanding", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            return method == null
                ? null
                : (NestProbeDelegate)Delegate.CreateDelegate(typeof(NestProbeDelegate), method, false);
        }

        /// <summary>
        /// Whether a candidate stands inside a sapsucker's nest ring, with the reason this search's
        /// own Reject vocabulary will count.
        /// </summary>
        internal static bool NestBlocksLanding(Vector3 origin, out string reason)
        {
            reason = null;
            return NestProbe != null && NestProbe(origin, out reason);
        }

        private void NextHeading()
        {
            _route = null; _accessPhase = 0; _bankPending = _boardingPending = false; _supportFailures = 0;
            if (++_heading >= 72) { _heading = 0; _candidate++; }
        }
        // #873 S12 U5 (2): the same heading advance, but counting consecutive support refusals. At
        // ConsecutiveSupportFailuresToAbandon the candidate gives up its remaining headings: forty
        // degrees of yaw have all found the same missing ground, so the ground is the problem.
        private void NextHeadingAfterSupportFailure()
        {
            _route = null; _accessPhase = 0; _bankPending = _boardingPending = false;
            if (++_supportFailures >= ConsecutiveSupportFailuresToAbandon)
            { _supportFailures = 0; _abandonedCandidates++; _heading = 0; _candidate++; return; }
            if (++_heading >= 72) { _heading = 0; _candidate++; _supportFailures = 0; }
        }
        // #1305 (R3): the hover phase, one foot at a time in the flood's walking-distance order. A
        // foot needs one fact: supported outside navigation the squad can step off from, under a
        // rope pose the hull fits, with a walk to the entrance and back. Nothing else chooses it but
        // the corridor: the first foot that completes clear is the plan; the nearest foot that
        // completes obstructed is kept, and sealed at MaxAltitude only when the frontier empties.
        private void HoverStep()
        {
            _stepKind = _hoverStage;
            switch (_hoverStage)
            {
                case StepKind.Frontier:
                    if (_frontier.Count == 0)
                    {
                        if (_lastResort == null) { Complete = true; return; }
                        (_origin, _rotation, _access, _exit, _egress, _detail, _route) = _lastResort.Value;
                        Accept(false, float.NaN); return;
                    }
                    _foot = _frontier.Dequeue();
                    for (int dx = -1; dx <= 1; dx++)
                        for (int dz = -1; dz <= 1; dz++)
                            if ((dx != 0 || dz != 0) && NavMesh.SamplePosition(_foot + new Vector3(dx * 2f, 0, dz * 2f), out var hit, 1.5f, OutsideArea)
                                && !_visited.Contains(Cell(hit.position)) && !NavMesh.Raycast(_foot, hit.position, out _, OutsideArea)) Visit(hit.position);
                    _feet++; _origin = _foot; _rotation = Quaternion.identity; _detail = "foot=" + _foot.ToString("F2");
                    _hoverStage = StepKind.Foot; return;
                case StepKind.Foot:
                    SupportRays++;
                    if (!BlacksiteLandingGeometry.ProbeGround(_foot, 1f, _mask, null, out var under, out _)
                        || !BlacksiteLandingGeometry.TryFloorNav(under.point, _mask, out _foot, .6f))
                    { Reject("rappel foot has no supported navigation", 0); NextFoot(); return; }
                    if (!BlacksitePhysicalStep.CanOccupy(BlacksitePhysicalStep.ProbeTransform, _foot, _mask, out var blocker))
                    { Reject("rappel foot blocked by " + (blocker != null ? blocker.name : "saturated query"), 0); NextFoot(); return; }
                    _hoverStage = StepKind.StepOff; return;
                case StepKind.StepOff:
                    // Room to step off for sequential deployment: the first of eight directions,
                    // entrance-ward first, whose floor the squad reaches by touchdown's own leg.
                    var toward = Vector3.ProjectOnPlane(_entrance.Nav - _foot, Vector3.up);
                    _facing = Quaternion.LookRotation(toward.sqrMagnitude > .01f ? toward.normalized : Vector3.forward);
                    string stepWhy = "rappel step-off has no supported ground";
                    for (int i = 0; i < 8; i++)
                        if (BlacksiteLandingGeometry.TryFloorNav(_foot + _facing * Quaternion.Euler(0, i * 45f, 0) * Vector3.forward * 2f, _mask, out _exit, .6f)
                            && BlacksiteShipNavigation.TryDropToStaging(_foot, _exit, _mask, out stepWhy))
                        { _pose = _footPoses = 0; _hasPose = false; _hoverStage = StepKind.Pose; return; }
                    Reject(stepWhy, 1); NextFoot(); return;
                case StepKind.Pose:
                    HoverPose(); return;
                case StepKind.RoutePath:
                    _stepKind = _route.NextAdvanceBuildsLeg ? StepKind.RoutePath : StepKind.RouteSample;
                    _route.Advance(1);
                    if (!_route.Complete) return;
                    // The route starts at the step-off, not the hull, so no other pose can mend it.
                    if (!_route.Success) { Reject(_route.Reason, 3); NextFoot(); return; }
                    _hoverStage = StepKind.Egress; return;
                default:
                    // Best effort, never a refusal: the proven fan when it fits, else the proven route.
                    if (!BlacksiteShipNavigation.TryEgressSlots(_rotation, _exit, _members, _mask, out _egress, out _))
                        _egress = RouteEgress(_route.Waypoints);
                    if (_corridorClear) { Accept(true, float.NaN); return; }
                    // Only the first complete foot gets here obstructed (HoverPose): kept, and the flood goes on.
                    _lastResort = (_origin, _rotation, _access, _exit, _egress, _detail, _route);
                    NextFoot(); return;
            }
        }
        // One pose per Step, every height's yaws before the next height. A pose whose hull would
        // hang over the crew ship is skipped untested: that is the air pose's flight safety, not a
        // property of the foot. The corridor is preferred, never a reason the foot does not exist:
        // with no clear rung the first hull- and column-clear pose is routed as the last resort until
        // one is kept; after that a foot with no clear rung is passed over.
        private void HoverPose()
        {
            while (_pose < PoseHeights.Length * PoseYaws)
            {
                int pose = _pose++;
                _rotation = _facing * Quaternion.Euler(0, pose % PoseYaws * 20f, 0);
                _origin = _foot - _rotation * BlacksiteRappelGeometry.CableTopLocal + Vector3.up * PoseHeights[pose / PoseYaws];
                if (FlatDistance(_origin, _ship) < 24f) continue;
                _posesTried++; MaximumPoses = Math.Max(MaximumPoses, ++_footPoses);
                BlacksiteLandingGeometry.LastHullQueries = 0;
                bool clear = BlacksiteRappelGeometry.TryPose(_origin, _rotation, _foot, _mask, _footToDeck, out _access, out var reason);
                HullQueries += BlacksiteLandingGeometry.LastHullQueries;
                if (!clear) { Reject(reason, 2); return; }
                if (BlacksiteFlightCorridor.Clear(_origin, _rotation, _mask, null, out _access.Altitude, out reason)) { BeginHoverRoute(true); return; }
                if (!_hasPose && _lastResort == null) { _hasPose = true; _poseOrigin = _origin; _poseRotation = _rotation; _poseAccess = _access; }
                Reject(reason, 2); return;
            }
            if (_hasPose)
            {
                _origin = _poseOrigin; _rotation = _poseRotation; _access = _poseAccess; _access.Altitude = BlacksiteFlightCorridor.MaxAltitude;
                BeginHoverRoute(false); return;
            }
            if (_footPoses == 0) Reject("hover too close to crew ship", 1);
            NextFoot();
        }
        private void BeginHoverRoute(bool corridorClear)
        {
            _corridorClear = corridorClear;
            _detail = $"hover rappel={_access.DropHeight:0.00}m foot={_foot:F2} corridor={(corridorClear ? "clear" : "obstructed")}";
            _route = new BlacksiteLandingApproach(_origin, _rotation, _exit, _entrance, _mask, hover: true);
            _hoverStage = StepKind.RoutePath;
        }
        private void NextFoot() { _route = null; _hoverStage = StepKind.Frontier; }
        private static (int, int, int) Cell(Vector3 p) => (Mathf.FloorToInt(p.x / 2f), Mathf.FloorToInt(p.z / 2f), Mathf.FloorToInt(p.y / 2.5f));
        private void Visit(Vector3 p) { if (_visited.Add(Cell(p))) _frontier.Enqueue(p); }
        // The members stand along the proven route from the step-off, a body width apart, each slot
        // proven as the ground staging fan proves its own (TryStagingSlot); any the route cannot hold wait at the step-off.
        private Vector3[] RouteEgress(Vector3[] route)
        {
            var slots = new Vector3[_members]; int count = 0;
            for (int i = 0; i < route.Length && count < slots.Length; i++)
            {
                bool separated = true;
                for (int j = 0; j < count && separated; j++) separated = FlatDistance(slots[j], route[i]) >= 1.12f;
                if (separated && BlacksiteShipNavigation.TryStagingSlot(_exit, route[i], _mask, out var slot, out _)) slots[count++] = slot;
            }
            for (; count < slots.Length; count++) slots[count] = _exit;
            return slots;
        }
        // #1283 S7a: the first pose through the whole chain is the answer. #1305: sealed as the plan.
        private void Accept(bool corridorClear, float lipNavGap)
        {
            _approachesPassed++;
            Found = true;
            _access.GroundRoute = _route.Waypoints; Detail = _detail; RouteMetres = _route.Metres; LipNavGap = lipNavGap;
            Plan = new BlacksiteLandingPlan(_origin, _rotation, _access, _exit, _egress, corridorClear);
            Complete = true;
        }
        private void Add(Vector3 seed)
        {
            float distance = FlatDistance(seed, _main);
            if (distance < 12f || FlatDistance(seed, _ship) < 24f || distance > 150f) return;
            // A wide downward ray only returns the roof above an otherwise useful node. Keep
            // the nearby floor too; the hull clearance check decides whether it is landable.
            if (BlacksiteLandingGeometry.ProbeGround(seed, 4f, _mask, null, out var near, out _)) AddFloor(near.point);
            if (BlacksiteLandingGeometry.ProbeGround(seed, 32f, _mask, null, out var high, out _)) AddFloor(high.point);
        }
        private void AddFloor(Vector3 p)
        {
            int x = Mathf.FloorToInt(p.x / 3f), z = Mathf.FloorToInt(p.z / 3f);
            for (int dx = -1; dx <= 1; dx++) for (int dz = -1; dz <= 1; dz++)
                if (_cells.TryGetValue((x + dx, z + dz), out var neighbours))
                    for (int i = 0; i < neighbours.Count; i++)
                        if (FlatDistance(neighbours[i], p) <= 1.5f && Mathf.Abs(neighbours[i].y - p.y) < 1f) return;
            _candidates.Add(p);
            if (!_cells.TryGetValue((x, z), out var cell)) _cells[(x, z)] = cell = new List<Vector3>();
            cell.Add(p);
        }
        private void Reject(string reason, int depth)
        {
            Rejections++; reason = reason ?? "unknown";
            string category = reason.Split(new[] { " at ", " detail=" }, StringSplitOptions.None)[0];
            if (category.StartsWith("gear feet spread")) category = "gear feet spread exceeds limit";
            if (_reasons.ContainsKey(category)) _reasons[category]++; else _reasons[category] = 1;
            if (depth > _nearDepth) { _nearDepth = depth; _nearMiss = $"{_origin} yaw={_rotation.eulerAngles.y:0.0} reason={reason} {_detail}"; }
        }
        /// <summary>#873 S12 U5 (4): the one place the [UAT][Landing] line is built, so the runtime
        /// and the extracted-moon fixture print the same shape. #874 N1: lipNavGap= (last) is the
        /// accepted ground pose's lip-to-deck-navigation gap on the search's probe bake. #1305: the
        /// outcome is ground, hover or none; drop is the plan's foot and station its origin.</summary>
        internal static string FormatLandingLine(string outcome, string reason, float seconds, int slices,
            double maxSliceMilliseconds, int approaches, Vector3? drop, Vector3? station, float lipNavGap)
        {
            return "outcome=" + outcome
                + ", reason=" + (string.IsNullOrEmpty(reason) ? "none" : reason)
                + ", seconds=" + seconds.ToString("0.0")
                + ", slices=" + slices
                + ", maxSliceMs=" + maxSliceMilliseconds.ToString("0.00")
                + ", approaches=" + approaches
                + ", drop=" + (drop.HasValue ? drop.Value.ToString("F2") : "none")
                + ", station=" + (station.HasValue ? station.Value.ToString("F2") : "none")
                + ", lipNavGap=" + (float.IsNaN(lipNavGap) ? "none" : lipNavGap.ToString("0.000") + "m");
        }
        private static float FlatDistance(Vector3 a, Vector3 b) => Vector3.ProjectOnPlane(a - b, Vector3.up).magnitude;
    }
}
