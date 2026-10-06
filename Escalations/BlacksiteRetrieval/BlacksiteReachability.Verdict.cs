using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #1305 G1 (R15): the route service's verdict. #874 S2 (rule 4): reachability is a navmesh query, never a motor
    // verdict, so one Walk's verdict is the navmesh's own answer: Reachable for a complete path judged whole,
    // PartialFrontier for a partial one, Unsupported when no route can start. Join chains walks across the known
    // transitions (BlacksiteReachability.Route). Unity-free so the headless suites compile it.

    /// <summary>R15 verdict. PartialFrontier is a temporary gap, never a write-off on its own (R19).</summary>
    internal enum BlacksiteRouteReach : byte { Reachable, PartialFrontier, Unsupported }

    /// <summary>Typed legs. Walk is NavMesh on one island; the others are the known transitions joining islands.</summary>
    internal enum BlacksiteRouteLegKind : byte { Walk, Entrance, ElevatorCar, OperableDoor }

    internal enum BlacksiteTravellerKind : byte { Soldier, Cart }

    /// <summary>Why a route is Unsupported (log token only; None otherwise).</summary>
    internal enum BlacksiteRouteGap : byte { None, NoPath, RefusedLink, Flooded, NoTransition }

    /// <summary>One corner-to-corner leg of a computed path.</summary>
    internal struct BlacksiteRouteLeg
    {
        internal float AX, AY, AZ, BX, BY, BZ;

        internal BlacksiteRouteLeg(float ax, float ay, float az, float bx, float by, float bz)
        { AX = ax; AY = ay; AZ = az; BX = bx; BY = by; BZ = bz; }

        internal float Length
        {
            get
            {
                float dx = BX - AX, dy = BY - AY, dz = BZ - AZ;
                return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }
    }

    /// <summary>One transition on a route. Kind == Walk means "no transition".</summary>
    internal readonly struct BlacksiteRouteTransition
    {
        internal readonly BlacksiteRouteLegKind Kind;
        /// <summary>Where the walk before it ends (walk here, then cross).</summary>
        internal readonly float EntryX, EntryY, EntryZ;
        /// <summary>Where the walk after it starts.</summary>
        internal readonly float ExitX, ExitY, ExitZ;
        /// <summary>Span of the transition itself: the shaft height for the car, 0 otherwise.</summary>
        internal readonly float Metres;
        /// <summary>Entrance: the EntranceTeleport to use; ElevatorCar: the MineshaftElevatorController; OperableDoor: the DoorLock.</summary>
        internal readonly object Handle;

        internal BlacksiteRouteTransition(BlacksiteRouteLegKind kind, float entryX, float entryY, float entryZ,
            float exitX, float exitY, float exitZ, float metres, object handle)
        {
            Kind = kind; EntryX = entryX; EntryY = entryY; EntryZ = entryZ;
            ExitX = exitX; ExitY = exitY; ExitZ = exitZ; Metres = metres; Handle = handle;
        }

        internal static readonly BlacksiteRouteTransition None = default;

        internal bool Exists => Kind != BlacksiteRouteLegKind.Walk;
    }

    /// <summary>
    /// The one route answer. <see cref="Distance"/> is the walked and crossed metres to the route's end; an
    /// Unsupported verdict is a query no route could even start. Judging allocates nothing;
    /// <see cref="Token"/> and <see cref="PathReason"/> are formatted only when they are read.
    /// </summary>
    internal readonly struct BlacksiteRouteVerdict
    {
        internal readonly BlacksiteRouteReach Reach;
        /// <summary>NavMesh produced the walks (Reachable or PartialFrontier).</summary>
        internal readonly bool Planned;
        internal readonly float Distance;
        /// <summary>The goal when Reachable; the frontier point when PartialFrontier.</summary>
        internal readonly float EndX, EndY, EndZ;
        /// <summary>Straight metres End to goal: 0 Reachable, +inf Unsupported.</summary>
        internal readonly float EndToGoal;
        /// <summary>Start of the final Walk leg: where the route arrives on the goal's island.</summary>
        internal readonly float EntryX, EntryY, EntryZ;
        /// <summary>The first transition; none means the whole route is one Walk.</summary>
        internal readonly BlacksiteRouteTransition Next;
        internal readonly byte Transitions;
        /// <summary>#1262 P1: the route boards the mineshaft car; <see cref="Distance"/> counts the shaft.</summary>
        internal readonly bool Rides;
        internal readonly BlacksiteRouteGap Gap;

        private BlacksiteRouteVerdict(BlacksiteRouteReach reach, bool planned, float distance,
            float endX, float endY, float endZ, float endToGoal, float entryX, float entryY, float entryZ,
            in BlacksiteRouteTransition next, byte transitions, bool rides, BlacksiteRouteGap gap)
        {
            Reach = reach; Planned = planned; Distance = distance;
            EndX = endX; EndY = endY; EndZ = endZ; EndToGoal = endToGoal;
            EntryX = entryX; EntryY = entryY; EntryZ = entryZ;
            Next = next; Transitions = transitions; Rides = rides; Gap = gap;
        }

        internal static BlacksiteRouteVerdict Unsupported(BlacksiteRouteGap gap)
            => new BlacksiteRouteVerdict(BlacksiteRouteReach.Unsupported, false, 0f, 0f, 0f, 0f, float.PositiveInfinity,
                0f, 0f, 0f, BlacksiteRouteTransition.None, 0, false, gap);

        internal bool Reachable => Reach == BlacksiteRouteReach.Reachable;

        /// <summary>#1262 P1: the service has no route. Every reader treats it as no sample.</summary>
        internal const float Unrouted = float.NaN;

        /// <summary>The `remaining=` form: walked (and crossed) metres plus the straight line a PartialFrontier
        /// route lacks (a lower bound: a route that becomes complete reads farther by no more than its detour, so
        /// the watermark keeps crediting), or <see cref="Unrouted"/>.</summary>
        internal float ReportedRemaining => Planned ? Distance + EndToGoal : Unrouted;

        /// <summary>The `reach=` form: walk:76.1m, ride:112.4m, entrance:40.0m, door:12.0m, partial:12.0m, unsupported:noPath.</summary>
        internal string Token
            => Reach == BlacksiteRouteReach.Reachable
                ? (Rides ? "ride:" : Next.Kind == BlacksiteRouteLegKind.Entrance ? "entrance:"
                    : Next.Kind == BlacksiteRouteLegKind.OperableDoor ? "door:" : "walk:") + Distance.ToString("0.0") + "m"
                : Reach == BlacksiteRouteReach.PartialFrontier ? "partial:" + Distance.ToString("0.0") + "m"
                : "unsupported:" + GapName;

        /// <summary>The motion request's reason form; the PathComplete/PathPartial tokens are NavMesh's own status names.</summary>
        internal string PathReason
            => Reach == BlacksiteRouteReach.Reachable ? "path:PathComplete"
                : Reach == BlacksiteRouteReach.PartialFrontier ? "path:PathPartial"
                : Gap == BlacksiteRouteGap.NoPath ? "path:none" : "path:" + GapName;

        private string GapName
            => Gap == BlacksiteRouteGap.RefusedLink ? "refusedLink"
                : Gap == BlacksiteRouteGap.Flooded ? "flooded"
                : Gap == BlacksiteRouteGap.NoTransition ? "noTransition"
                : "noPath";

        public override string ToString() => Token;

        /// <summary>
        /// One walk from its corner legs. <paramref name="complete"/> is NavMesh's PathComplete judged whole
        /// (<see cref="BlacksiteReachability.JudgedWhole"/>) and reads Reachable; anything else is PartialFrontier,
        /// ending at the last corner. Entry is the first leg's start.
        /// </summary>
        internal static BlacksiteRouteVerdict Judge(BlacksiteRouteLeg[] legs, int count, bool complete,
            float goalX, float goalY, float goalZ)
        {
            if (legs == null || count <= 0)
                return Unsupported(BlacksiteRouteGap.NoPath);
            float walked = 0f;
            for (int i = 0; i < count; i++)
                walked += legs[i].Length;
            BlacksiteRouteLeg first = legs[0], last = legs[count - 1];
            float endToGoal = complete ? 0f : Metres(last.BX, last.BY, last.BZ, goalX, goalY, goalZ);
            return new BlacksiteRouteVerdict(complete ? BlacksiteRouteReach.Reachable : BlacksiteRouteReach.PartialFrontier,
                true, walked, last.BX, last.BY, last.BZ, endToGoal, first.AX, first.AY, first.AZ,
                BlacksiteRouteTransition.None, 0, false, BlacksiteRouteGap.None);
        }

        /// <summary>
        /// <paramref name="head"/> (Reachable to the transition's entry), the transition, then <paramref name="tail"/>:
        /// reach, end, shortfall, entry and gap are the tail's; the metres add up; the first transition is the head's
        /// own when it has one.
        /// </summary>
        internal static BlacksiteRouteVerdict Join(in BlacksiteRouteVerdict head, in BlacksiteRouteTransition transition,
            in BlacksiteRouteVerdict tail)
            => new BlacksiteRouteVerdict(tail.Reach, head.Planned && tail.Planned,
                head.Distance + transition.Metres + tail.Distance, tail.EndX, tail.EndY, tail.EndZ, tail.EndToGoal,
                tail.EntryX, tail.EntryY, tail.EntryZ, head.Next.Exists ? head.Next : transition,
                (byte)(head.Transitions + 1 + tail.Transitions),
                head.Rides || tail.Rides || transition.Kind == BlacksiteRouteLegKind.ElevatorCar, tail.Gap);

        /// <summary>The composition's ordering: Reachable over PartialFrontier (the nearer frontier wins) over Unsupported.</summary>
        internal static bool Better(in BlacksiteRouteVerdict candidate, in BlacksiteRouteVerdict best)
            => candidate.Reach != best.Reach
                ? candidate.Reach < best.Reach
                : candidate.Reach == BlacksiteRouteReach.PartialFrontier && candidate.EndToGoal < best.EndToGoal;

        private static float Metres(float ax, float ay, float az, float bx, float by, float bz)
        {
            float dx = bx - ax, dy = by - ay, dz = bz - az;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }
    }

    internal static partial class BlacksiteReachability
    {
        /// <summary>
        /// PathfindingLib's SmartPathfinding.NonSmartAgentOffMeshLinkAreaIndex. The soldier's agent
        /// excludes it (every round logs areaMask=0xFDFFFFFF), so a route over one of those links is
        /// not a route this member's agent can walk; a member-less query uses the same mask.
        /// </summary>
        internal const int NonSmartAgentLinkArea = 25;
        internal const int SoldierAreaMask = ~(1 << NonSmartAgentLinkArea);

        /// <summary>
        /// The most corners one walk judges; the walk's buffers are this size and are never grown.
        /// </summary>
        internal const int MaxJudgedCorners = 256;

        /// <summary>
        /// Whether the judged corners are the whole route. A path that fills the corner buffer may
        /// go on past it, so it is reported PartialFrontier - its end the last judged corner - and the
        /// caller walks it as an interim goal and asks again from closer, instead of reading a
        /// route it has not seen as Reachable.
        /// </summary>
        internal static bool JudgedWhole(bool pathComplete, int corners)
            => pathComplete && corners < MaxJudgedCorners;
    }
}
