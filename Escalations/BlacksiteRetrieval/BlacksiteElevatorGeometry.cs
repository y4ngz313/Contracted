using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #874 S5.3: the elevator cabin measured, not guessed.
    ///
    /// The 2026-09-09 Offense / Shadow Raid run boarded four raiders into a hard-coded
    /// +/-0.52 m square around <c>elevatorInsidePoint</c> (findings-elevator.md RC2). That square
    /// was never derived from <c>elevatorBounds</c>, never consulted the door side, and was rotated
    /// blindly on failure, so which corner a member happened to draw decided whether he could reach
    /// the car at all: member 3 cycled slots 3, 0, 1, 2 while standing still and was dropped from
    /// the trip, and member 1 solved the identical problem 2.7 s later only because the rotation
    /// reached a slot that happened to be in line with the door.
    ///
    /// Everything this type produces is derived from three measurements the vanilla controller
    /// already publishes: the cabin bounds, the cabin's own horizontal axes, and the bottom door
    /// anchor (<c>elevatorBottomPointDoor</c>). The door side is expressed as one of the cabin's
    /// local axes, so the top floor - which ships no <c>elevatorTopPointDoor</c> at all (RC3) - uses
    /// the same face as the bottom.
    ///
    /// Deliberately free of colliders, transforms, NavMesh and the soldier, so
    /// tests/BlacksiteTierTwoThreeRegression can pin every number without an editor. Sampling the
    /// results onto the NavMesh and snapping them to the cabin floor is the caller's job.
    /// </summary>
    internal readonly struct BlacksiteElevatorGeometry
    {
        /// <summary>
        /// How far inside the cabin wall a body centre may stand: the inset of the usable door corridor.
        ///
        /// Derived: 0.32 m is the radius of the swept body capsule every physical step uses
        /// (<c>BlacksitePhysicalStep.ClearSegment</c> and <c>CanOccupy</c> both sweep a 0.32 m
        /// capsule from y+0.48 to y+1.8), so a slot exactly one radius from a wall grazes it, and
        /// <c>BlacksitePhysicalStep.BlocksAt</c> already calls a 3 mm penetration blocking. The
        /// extra 0.10 m is <see cref="BlacksiteArrivalPolicy.ProfileReachMargin"/>, the margin the
        /// S4.2 arrival contract already budgets between an action's anchor and where a member may
        /// actually come to rest. 0.32 + 0.10 = 0.42 m.
        /// </summary>
        internal const float CapsuleRadius = .32f;
        internal const float SlotInset = CapsuleRadius + BlacksiteArrivalPolicy.ProfileReachMargin;

        /// <summary>
        /// One body diameter, the spacing between two ride slots (<see cref="RideSlot"/>). Derived: two agent radii
        /// (<see cref="BlacksiteArrivalPolicy.AgentRadius"/> 0.45 m, the value
        /// <c>BlacksiteNavigator.ConfigureAgent</c> writes), i.e. one body diameter. Two points
        /// closer together than this are the same standing point, which is exactly what the old
        /// 1.04 m square produced when two members drew the same corner (RC2, log L4741 / L4752).
        /// Identical by derivation to <c>BlacksiteSquadDirector.ClaimSeparationBase</c>.
        /// </summary>
        internal const float SlotSeparation = BlacksiteArrivalPolicy.AgentRadius * 2f;

        /// <summary>
        /// How far back from the cabin's door face the squad gathers.
        ///
        /// Derived: exactly <c>BlacksiteElevatorInteraction.BoardCommitDistance</c>, the radius of
        /// the link's start around this point (<c>AtLinkStart</c>), so the point a member walks to and
        /// the start of the link are the same place. #1293 R4: the hand step itself starts only within
        /// <see cref="LandingStandoff"/> of the landing (<see cref="BoardingTarget"/>).
        ///
        /// #1157: it used to be that distance plus one body (2.4 + 0.9 = 3.3 m), so a member parked
        /// at the staging point sat outside the commit range by a whole body. The vanilla
        /// mineshaft's top landing has no platform that far out. Round 4 of the 2026-09-20 playtest
        /// staged the squad at (-15.46, -182.18, 73.34), 2.65 m past the landing, where the 2 m
        /// <c>NavMesh.SamplePosition</c> the adapter spends on the point can only answer with the
        /// stair tile behind the platform (UAT 74825): the squad gathered on the stairs, the first
        /// boarding hand-step was refused by <c>MineshaftStartTileMesh</c> 0.34 m under the foot
        /// (74970), the member was released to AutonomousSearch and re-walked the stairs, four
        /// times in one round. A body of clearance from the commit range is worth less than
        /// standing on the landing at all, and <see cref="ClampToLanding"/> keeps the sampled point
        /// there too.
        /// </summary>
        internal const float BoardCommitDistance = 2.4f;
        internal const float StagingDistance = BoardCommitDistance;

        /// <summary>
        /// How far outside the door face a floor's landing point sits. Derived: one agent radius
        /// (0.45 m) so the member's own body is clear of the door plane, plus
        /// <see cref="BlacksiteArrivalPolicy.SettleSlack"/> 0.2 m, the distance a braking
        /// NavMeshAgent may settle short of an issued destination. 0.45 + 0.20 = 0.65 m.
        /// </summary>
        internal const float LandingStandoff =
            BlacksiteArrivalPolicy.AgentRadius + BlacksiteArrivalPolicy.SettleSlack;

        /// <summary>
        /// #874 S9.1 (findings-elevator-traversal.md RC-E1): the tallest bounds this type will still
        /// believe is a CAR rather than a shaft.
        ///
        /// On the vanilla mineshaft <c>elevatorBounds</c> is the SHAFT. The 2026-09-09 run logged
        /// <c>cabinBounds=(-12.16, -220.20, 72.44)-(-10.13, -177.61, 74.24)</c>: 42.59 m tall
        /// against a 37.39 m travel, with the car parked at -182.17. Taking that box's own minimum y
        /// as the cabin floor put the doorway waypoint and all four standing slots 38.03 m
        /// underground, where <c>TickBoard</c>'s vertical gate could never pass and every retry
        /// printed <c>stepReason=none</c>. Zero of six raiders boarded, ever.
        ///
        /// Derived, not chosen: a car a member can stand in is one body column tall
        /// (<see cref="BlacksiteWorldLadderColumn.BodyColumnMetres"/> 1.96 m, the swept capsule
        /// every physical step uses), and two of them is generous headroom for a lift with a
        /// ceiling. 2 x 1.96 = 3.92 m. Adamance's 42.59 m fails it by 10.9x; the hand-authored
        /// 2.5 m cabin of tests/BlacksiteLandingCombatChecks passes it.
        /// </summary>
        internal const float MaxCabinHeightMetres = BlacksiteWorldLadderColumn.BodyColumnMetres * 2f;

        internal bool HasValue { get; }

        /// <summary>The cabin bounds centre.</summary>
        internal Vector3 Center { get; }

        /// <summary>
        /// Unit horizontal normal of the cabin face the door is on, pointing out of the cabin. One
        /// of the cabin's own four horizontal local axes, so it is the same face on both floors.
        /// </summary>
        internal Vector3 DoorNormal { get; }

        /// <summary>Unit horizontal axis across the door face.</summary>
        internal Vector3 DoorTangent { get; }

        /// <summary>Cabin half-extent along <see cref="DoorNormal"/>.</summary>
        internal float HalfDepth { get; }

        /// <summary>Cabin half-extent along <see cref="DoorTangent"/>.</summary>
        internal float HalfWidth { get; }

        /// <summary>
        /// The bounds' own minimum y. #874 S9.1: this is the cabin floor only when
        /// <see cref="BoundsAreCabinShaped"/>; the caller resolves the live floor from the car.
        /// </summary>
        internal float FloorY { get; }

        /// <summary>The measured bounds' vertical extent.</summary>
        internal float CabinHeight { get; }

        /// <summary>
        /// #874 S9.1: is the measured volume plausibly the CAR, or is it the shaft the car travels
        /// in? Only the vertical answer is in question: the horizontal derivation reproduces exactly
        /// from the run's own printed slots either way, which is why <c>HasValue</c> is unaffected.
        /// </summary>
        internal bool BoundsAreCabinShaped => CabinHeight <= MaxCabinHeightMetres;

        private readonly float _usableHalfWidth;

        private BlacksiteElevatorGeometry(
            Vector3 center,
            Vector3 doorNormal,
            Vector3 doorTangent,
            float halfDepth,
            float halfWidth,
            float floorY,
            float cabinHeight,
            float usableHalfWidth)
        {
            HasValue = true;
            Center = center;
            DoorNormal = doorNormal;
            DoorTangent = doorTangent;
            HalfDepth = halfDepth;
            HalfWidth = halfWidth;
            FloorY = floorY;
            CabinHeight = cabinHeight;
            _usableHalfWidth = usableHalfWidth;
        }

        /// <summary>
        /// Measure a cabin. <paramref name="cabinRight"/> and <paramref name="cabinForward"/> are
        /// the cabin transform's own axes; <paramref name="doorAnchor"/> is the vanilla
        /// <c>elevatorBottomPointDoor</c> (or its fallback), the only published evidence of which
        /// face the door is on.
        /// </summary>
        internal static BlacksiteElevatorGeometry Build(
            Bounds cabin,
            Vector3 cabinRight,
            Vector3 cabinForward,
            Vector3 doorAnchor)
        {
            Vector3 right = Flatten(cabinRight, new Vector3(1f, 0f, 0f));
            Vector3 forward = Flatten(cabinForward, new Vector3(0f, 0f, 1f));
            Vector3 center = cabin.center;

            // Which of the cabin's four horizontal faces does the door anchor lie beyond? The
            // anchor is a landing-side transform, so the face it is furthest past is the door.
            Vector3 toDoor = doorAnchor - center;
            toDoor.y = 0f;
            Vector3 normal = forward;
            if (toDoor.sqrMagnitude > .0001f)
            {
                float best = float.NegativeInfinity;
                for (int i = 0; i < 4; i++)
                {
                    Vector3 candidate = i == 0 ? forward : i == 1 ? -forward : i == 2 ? right : -right;
                    float score = Vector3.Dot(candidate, toDoor);
                    if (score > best)
                    {
                        best = score;
                        normal = candidate;
                    }
                }
            }

            Vector3 tangent = Vector3.Cross(Vector3.up, normal);
            tangent = Flatten(tangent, new Vector3(1f, 0f, 0f));

            // The vanilla elevatorBounds collider reports a world axis-aligned box, so a half-extent
            // along an arbitrary horizontal axis is the box's own support function, not one field.
            Vector3 extents = cabin.extents;
            float halfDepth = Mathf.Abs(normal.x) * extents.x + Mathf.Abs(normal.z) * extents.z;
            float halfWidth = Mathf.Abs(tangent.x) * extents.x + Mathf.Abs(tangent.z) * extents.z;

            float usableHalfWidth = Mathf.Max(0f, halfWidth - SlotInset);

            return new BlacksiteElevatorGeometry(
                center, normal, tangent, halfDepth, halfWidth, cabin.min.y, cabin.size.y, usableHalfWidth);
        }

        private static Vector3 Flatten(Vector3 axis, Vector3 fallback)
        {
            axis.y = 0f;
            return axis.sqrMagnitude > .0001f ? axis.normalized : fallback;
        }

        /// <summary>Signed distance of a point from the cabin centre along <see cref="DoorNormal"/>.</summary>
        internal float DepthOf(Vector3 point) => Vector3.Dot(point - Center, DoorNormal);

        /// <summary>Signed distance of a point from the cabin centre along <see cref="DoorTangent"/>.</summary>
        internal float LateralOf(Vector3 point) => Vector3.Dot(point - Center, DoorTangent);

        /// <summary>The centre of the door face, at the given floor height. Everything enters and leaves here.</summary>
        internal Vector3 DoorwayWaypoint(float floorY) => Anchor(floorY) + DoorNormal * HalfDepth;

        /// <summary>
        /// #1305 G3: how many members the cabin floor holds - one ride slot every
        /// <see cref="SlotSeparation"/> inside <see cref="SlotInset"/> of each wall, at least one per
        /// axis. The vanilla 2.03 x 1.80 m cabin holds four.
        /// </summary>
        internal int Capacity => SlotsAlong(HalfDepth) * SlotsAlong(HalfWidth);

        /// <summary>
        /// #1305 G3: ride slot <paramref name="index"/> on the cabin floor at <paramref name="floorY"/>:
        /// a grid centred on the cabin, back row (farthest from the door) first.
        /// </summary>
        internal Vector3 RideSlot(int index, float floorY)
        {
            int across = SlotsAlong(HalfWidth), rows = SlotsAlong(HalfDepth);
            index = Mathf.Clamp(index, 0, rows * across - 1);
            return Anchor(floorY)
                + DoorNormal * ((index / across - (rows - 1) * .5f) * SlotSeparation)
                + DoorTangent * ((index % across - (across - 1) * .5f) * SlotSeparation);
        }

        private static int SlotsAlong(float half)
            => Mathf.FloorToInt(2f * Mathf.Max(0f, half - SlotInset) / SlotSeparation) + 1;

        /// <summary>
        /// #1293 R4: the hand step's next point, decided by where the body stands and nothing else.
        /// Outside the door's clear width it is the landing point on the centreline
        /// (<see cref="LandingPosition"/>). Inside that width, in the door corridor - no further out
        /// than the landing point and on this floor - it is the ride slot (<see cref="RideSlot"/>); further out, or on
        /// another floor, it is the door face straight ahead (<see cref="DoorwayWaypoint"/>), so the
        /// stretch stays in the corridor and crosses the landing depth. A ride slot lies inside the
        /// clear width and a step toward it only narrows the depth, so the answer never flips
        /// back. There is no diagonal from beside the door: a body there walks to the corridor on its
        /// agent first, round whatever stands beside the door (#1056's pillar).
        /// </summary>
        internal Vector3 BoardingTarget(Vector3 position, Vector3 ride, float floorY)
        {
            if (Mathf.Abs(LateralOf(position)) > _usableHalfWidth) return LandingPosition(floorY);
            return DepthOf(position) <= HalfDepth + LandingStandoff && Mathf.Abs(position.y - floorY) < 1.2f
                ? ride
                : DoorwayWaypoint(floorY);
        }

        /// <summary>The landing point outside the door on a floor, before NavMesh sampling: the corridor's entry.</summary>
        internal Vector3 LandingPosition(float floorY)
            => Anchor(floorY) + DoorNormal * (HalfDepth + LandingStandoff);

        /// <summary>Where the link starts on a floor, before NavMesh sampling.</summary>
        internal Vector3 StagingPosition(float floorY)
            => Anchor(floorY) + DoorNormal * (HalfDepth + StagingDistance);

        /// <summary>
        /// #1157: keep a NavMesh-sampled landing-side point on the landing platform.
        ///
        /// The sample is a correction WITHIN the landing, never a licence to leave it. The landing
        /// this type can actually measure is the corridor in front of the door face: across the
        /// door, the same clear width <see cref="BoardingTarget"/> already treats as the landing
        /// corridor; in depth, between the landing point and the staging point; and at the floor's
        /// own height, the plane <see cref="LandingPosition"/> and <see cref="StagingPosition"/>
        /// are built on and the plane <c>CabinFloorDisagreesWithLanding</c> holds a boarding target
        /// to. A sample inside that corridor is believed as it stands; one outside it - round 4's
        /// stair tile behind the platform edge, or a sample dragged sideways out of the corridor -
        /// is clamped back into it. A clamped point the NavMesh cannot carry the member to is left
        /// to the approach and step refusals that already report it.
        /// </summary>
        internal Vector3 ClampToLanding(Vector3 sampled, float floorY)
            => Anchor(floorY)
                + DoorNormal * Mathf.Clamp(
                    DepthOf(sampled), HalfDepth + LandingStandoff, HalfDepth + StagingDistance)
                + DoorTangent * Mathf.Clamp(
                    LateralOf(sampled), -_usableHalfWidth, _usableHalfWidth);

        private Vector3 Anchor(float floorY) => new Vector3(Center.x, floorY, Center.z);
    }
}
