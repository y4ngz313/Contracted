using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S4.0a / S4: who owns a stationary member. Seven words, first match wins. The type is
    // deliberately free of UnityEngine so the headless regression can replay the run's own rows.
    //
    // Nothing here decides anything. It observes; the caller only prints the answer.
    internal enum BlacksiteStallOwner
    {
        /// <summary>The body is moving.</summary>
        Moving,
        /// <summary>A seat writes the body: a ship passenger seat, death or a monster capture.</summary>
        Seated,
        /// <summary>A crossing motor writes the body: a link, a ladder, an elevator ride or a cart.</summary>
        Crossing,
        /// <summary>The member has combat intent.</summary>
        Combat,
        /// <summary>The agent stands still until the navigator's one dwell deadline.</summary>
        Dwell,
        /// <summary>No goal, or the goal is inside its arrival contract.</summary>
        Arrived,
        /// <summary>Stationary, short of its goal, and nothing above owns it.</summary>
        Stalled
    }

    internal enum BlacksitePathState { None, Pending, Complete, Partial, Invalid }

    // #956 S12A U3: who throttles a member that is trying to travel.
    //
    // stallOwner above answers "why is this member not moving". It cannot answer "why is this
    // member moving at 0.14 of the speed it was commanded", which is what the 2026-09-10 March run
    // did for 271 seconds. The measurements are in
    // artifacts/s12a-u3-evidence/investigation.md: agentVelocity is below 0.2 m/s on 74.7 % of
    // samples, so the throttle is agent-side, and the three owners that share the mission are
    // ArrivalBand 37.7 %, Acceleration 24.2 % and PathPending 23.2 % of member-seconds.
    //
    // The first four values are terms of the commanded speed itself and are decided by
    // CommandedSpeed at the moment the speed is applied, the way _holdReason is written on the
    // rising edge of a hold. The rest are agent-side facts read at the sample.
    internal enum BlacksiteSpeedOwner
    {
        /// <summary>Nothing is throttling: the body is at or above 0.7 of a commanded speed that is the role speed.</summary>
        None,
        /// <summary>The approach lerp bound the commanded speed below the role speed.</summary>
        ApproachSlowdown,
        /// <summary>The crouch multiplier bound it.</summary>
        Crouch,
        /// <summary>The spider-web multiplier bound it.</summary>
        Web,
        /// <summary>
        /// A cargo cap bound the role speed itself: the cart push cap, or (#965 S14 U3) the
        /// witness escort's clamp, which binds at the same point for the same reason.
        /// </summary>
        Payload,
        /// <summary>
        /// #990: a pursuit FLOOR raised the role speed. The Witness-reserved operator running down
        /// a target it can see, at <see cref="BlacksiteMotionPolicy.WitnessPursuitSpeed"/>. It is
        /// the only term that binds upward, and it binds at the same point the caps bind: on the
        /// role speed, before the approach lerp and the multipliers see it.
        /// </summary>
        Pursuit,
        /// <summary>
        /// #874 P-S2: a Follow request paced the member to its anchor. Inside the follow band of a
        /// moving anchor the member walks at the anchor's own speed plus the gap it still has to
        /// close (<see cref="BlacksiteMotionPolicy.FollowSpeed"/>), so it neither overruns its slot
        /// and stops nor falls behind it.
        /// </summary>
        Follow,
        /// <summary>A crossing or a seat writes the body, or a dwell holds the agent.</summary>
        Hold,
        /// <summary>The member is inside the stopping distance of the goal it was given.</summary>
        ArrivalBand,
        /// <summary>The agent is stopped.</summary>
        AgentStopped,
        /// <summary>A complete path, the role speed commanded, and the body still under cruise.</summary>
        Acceleration,
        /// <summary>Inside the braking band the agent needs to land on its destination.</summary>
        AutoBraking,
        /// <summary>No usable path: pending, invalid, or consumed and not yet replaced.</summary>
        PathPending,
        /// <summary>The agent is off the mesh or on an off-mesh link.</summary>
        OffMesh,
        /// <summary>The agent is at speed and the body is not. Nothing above explains it.</summary>
        Unknown,
        /// <summary>
        /// #1219 F2: a rifle burst is firing, so the combat step is walked at
        /// BlacksiteCombatPolicy.FiringWalkSpeed. Appended so no ordinal moves.
        /// </summary>
        CombatFire
    }

    /// <summary>
    /// One frame of a member's movement state, in plain numbers and strings. Every field is read
    /// from live navigation state; none of them is derived by this file.
    /// </summary>
    internal struct BlacksiteMotionSample
    {
        /// <summary>The frame's Time.time, the clock <see cref="DwellUntil"/> is read against.</summary>
        internal float Now;
        /// <summary>Measured transform displacement per second over a one-second trailing window.</summary>
        internal float Speed;
        /// <summary>#874 S4: who writes the transform this frame.</summary>
        internal BlacksiteBodyWriter BodyWriter;
        /// <summary>#874 S4: the navigator's dwell deadline, 0 when none.</summary>
        internal float DwellUntil;
        /// <summary>#874 S4: the member has combat intent.</summary>
        internal bool InCombat;
        internal bool HasGoal;
        internal BlacksitePathState Path;
        /// <summary>Agent remaining distance, or a negative value when there is no path to measure.</summary>
        internal float Remaining;
        /// <summary>
        /// #874 P-S1: the substitution the issued point is, when it is not the goal itself -
        /// "groundRoute", "detour", "interim" or "claim" - or null when the issued point IS the
        /// goal. <see cref="Remaining"/> is then the distance to the leg, not to the goal.
        /// </summary>
        internal string Leg;
        /// <summary>
        /// #874 P-S1: metres to the GOAL the issued leg serves: <see cref="Remaining"/> plus the
        /// straight line from the leg to the goal, or <see cref="Remaining"/> itself when no leg is
        /// in force. Negative when there is no path to measure.
        /// </summary>
        internal float GoalRemaining;
        /// <summary>
        /// #1219 C1: 3D metres from where the path ends - the body when no path stands - to the
        /// point it serves. Negative when not measured (a Complete or Pending path, or no issue).
        /// </summary>
        internal float PathEndGap;
        internal float StoppingDistance;
        internal string StoppingDistanceOwner;
        // #874 S4.2: the rest of the ordered arrival contract. Under the 2026-09-09 build these
        // three had no relation to each other and to the stop distance; the classifier could
        // therefore report arrived:default for a member that was nowhere near the gate its own
        // action had to satisfy, which is why nothing in the build responded to episodes 1, 6 or 9.
        internal float ArrivalFloor;
        internal float ArrivalTolerance;

        // #956 S12A U3: the speed half of the same frame. Every field is read from live navigation
        // state or recorded by the navigator when it wrote agent.speed; none is derived here.

        /// <summary>What agent.speed was last set to, which is what the line prints as desiredSpeed.</summary>
        internal float CommandedSpeed;
        /// <summary>Which term of <see cref="BlacksiteMotionPolicy.CommandedSpeed"/> bound the result.</summary>
        internal BlacksiteSpeedOwner SpeedOwner;
        /// <summary>agent.velocity.magnitude at this frame.</summary>
        internal float AgentVelocity;
        /// <summary>agent.acceleration, so the braking band can be computed from the agent's own number.</summary>
        internal float Acceleration;
        internal bool AutoBraking;
        internal bool AgentStopped;
        /// <summary>The agent is enabled and on the mesh.</summary>
        internal bool AgentBound;
        /// <summary>agent.pathPending, or a goal with no path at all.</summary>
        internal bool PathPending;
        internal bool OffMeshLink;
        // #874 P-S2: the follow half of the frame. Written from the motion owner's follow state,
        // which is the one place a Follow request's anchor is sampled. #1271 S2: it paces and
        // reports a Follow; it never decides its arrival, which is the goal's like every member's.

        /// <summary>A Follow request is this member's goal: its slot on the route behind the anchor.</summary>
        internal bool Follow;
        /// <summary>Who the member follows, as the [UAT][Motion] goalSource= token: "carrier:0", "member:2".</summary>
        internal string FollowSource;
        /// <summary>The anchor's smoothed speed is at or above <see cref="BlacksiteMotionPolicy.MovingSpeed"/>.</summary>
        internal bool AnchorMoving;
        /// <summary>Horizontal metres from the member to its follow goal; negative when there is none.</summary>
        internal float FollowGap;
        /// <summary>The band inside which the member is paced to a moving anchor (<see cref="BlacksiteMotionPolicy.FollowSpeed"/>).</summary>
        internal float FollowBand;
        /// <summary>#874 Q2: the member stands off its anchor's next <see cref="BlacksiteMotionPolicy.FollowPathClearMetres"/>.</summary>
        internal bool FollowOffPath;
    }

    /// <summary>#874 Q2: an escort's crowd point against the holders' keep-outs (BlacksiteMotionPolicy.KeepOutClaim).</summary>
    internal enum BlacksiteKeepOutClaim : byte
    {
        /// <summary>In no keep-out; the point stands.</summary>
        Clear,
        /// <summary>Moved out to the keep-out's edge.</summary>
        Pushed,
        /// <summary>No admissible point outside; the claim is withdrawn and the request refused.</summary>
        Refused,
    }

    internal static class BlacksiteMotionPolicy
    {
        /// <summary>
        /// The measured speed at or above which a member counts as moving. Taken from the
        /// 2026-09-09 movement forensics, which bucketed alive time at 0.2 m/s and found 11.6 % of
        /// it below that line; acceptance A1 is written against the same number.
        /// </summary>
        internal const float MovingSpeed = .2f;

        /// <summary>
        /// How far outside the stopping distance a goal must be before a stationary member counts
        /// as not-yet-arrived. Acceptance A1's own margin: remaining &gt; stop + 0.3 m.
        /// </summary>
        internal const float ArrivalMargin = .3f;

        /// <summary>
        /// The closed vocabulary, in precedence order. A score script and the regression both read
        /// this so a new owner cannot be added without both of them seeing it.
        /// </summary>
        internal static readonly BlacksiteStallOwner[] Precedence =
        {
            BlacksiteStallOwner.Moving,
            BlacksiteStallOwner.Seated,
            BlacksiteStallOwner.Crossing,
            BlacksiteStallOwner.Combat,
            BlacksiteStallOwner.Dwell,
            BlacksiteStallOwner.Arrived,
            BlacksiteStallOwner.Stalled
        };

        /// <summary>
        /// #874 S4: the seven owners in <see cref="Precedence"/> order, first match wins. A moving
        /// body is moving whoever writes it; a seat outranks a crossing because it is the only
        /// writer that may take the body from one; combat outranks the dwell it rides on; arrival
        /// is judged against the member's own GOAL (never a reached leg), a Follow's included
        /// (#1271 S2: 2026-09-29 round 2 held escorts "arrived" by an anchor band beside an operator
        /// that had no route, Player.log 38553-38618). Only <see cref="BlacksiteStallOwner.Dwell"/>
        /// carries a detail: the seconds left.
        /// </summary>
        internal static BlacksiteStallOwner Classify(BlacksiteMotionSample sample, out string detail)
        {
            detail = null;
            if (sample.Speed >= MovingSpeed) return BlacksiteStallOwner.Moving;
            if (sample.BodyWriter == BlacksiteBodyWriter.Seated) return BlacksiteStallOwner.Seated;
            if (sample.BodyWriter == BlacksiteBodyWriter.Crossing) return BlacksiteStallOwner.Crossing;
            if (sample.InCombat) return BlacksiteStallOwner.Combat;
            if (sample.DwellUntil > sample.Now)
            {
                detail = (sample.DwellUntil - sample.Now).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
                return BlacksiteStallOwner.Dwell;
            }
            bool arrived = !sample.HasGoal
                || (GoalWithinContract(sample.GoalRemaining, sample.StoppingDistance, sample.ArrivalTolerance)
                    && !PathStopsShort(sample.Path, sample.PathEndGap, sample.ArrivalTolerance));
            return arrived ? BlacksiteStallOwner.Arrived : BlacksiteStallOwner.Stalled;
        }

        /// <summary>#874 P-S1: the issued point is a substitution for the goal, not the goal.</summary>
        internal static bool LegInForce(string leg) => !string.IsNullOrEmpty(leg);

        // #874 P-S2 -----------------------------------------------------------------------------

        /// <summary>The leg name a Follow request's re-derived point carries.</summary>
        internal const string FollowLeg = "follow";

        /// <summary>
        /// #874 P-S2: the band inside which a follower is paced to its moving anchor
        /// (<see cref="FollowSpeed"/>), printed as the [UAT][Motion] band=. Two goal quanta: one
        /// <see cref="BlacksiteArrivalPolicy.MinDestinationChange"/> is the smallest distance the
        /// navigator treats as a different place, and the goal is re-derived on every refresh, so a
        /// band of one quantum would flicker on the re-derivation alone; the second is that slack.
        /// </summary>
        internal const float FollowBandMetres = 2f * BlacksiteArrivalPolicy.MinDestinationChange;

        /// <summary>
        /// #874 P-S2: a re-derived point closer than this to the point the agent already has is the
        /// same leg (<c>sameGoal</c>). One agent radius: the smallest move that is not the same body
        /// standing in the same place. It replaces the 1 m ordinary re-issue threshold and the
        /// <see cref="BlacksiteArrivalPolicy.MinDestinationChange"/> hysteresis for a Follow, which
        /// would each hold a walking member on a point its anchor has already left.
        /// </summary>
        internal const float FollowReissueMetres = BlacksiteArrivalPolicy.AgentRadius;

        /// <summary>
        /// #874 P-S2's follow issue gate, named for #874 Q2: a re-derived follow
        /// leg within <see cref="FollowReissueMetres"/> of the leg last issued is the leg the agent
        /// has and is not re-issued. A side step forgets the issued leg (<paramref name="hasIssuedLeg"/>
        /// false), because the agent's destination is the side slot from then on: kept, the resume
        /// reads the old leg as "in hand" and the follower stands on the slot. #1293 R2: no age
        /// re-validates a leg; a lost route is the route-end event (the caller's <c>routeEnded</c>)
        /// and a leg that stops progressing is the progress ledger's miss.
        /// </summary>
        internal static bool FollowLegInHand(bool hasIssuedLeg, float legMovedMetres)
            => hasIssuedLeg && legMovedMetres < FollowReissueMetres;

        /// <summary>
        /// #874 P-S2: how far ahead of the moving goal the leg is placed, in seconds of the anchor's
        /// own travel, so the member never reaches its leg and brakes while the anchor still walks.
        /// </summary>
        internal const float FollowLookAheadSeconds = 1f;

        /// <summary>
        /// #874 P-S2: inside the band the member walks at the anchor's speed plus the gap over this
        /// many seconds, so a member one band behind closes it in about a second without overrunning.
        /// </summary>
        internal const float FollowCatchUpSeconds = 1f;

        /// <summary>
        /// #874 P-S2: the anchor speed is smoothed over this window, the same half second the
        /// planner observes at, so one frame of avoidance jitter or a turn in place does not read
        /// as the anchor starting or stopping.
        /// </summary>
        internal const float AnchorSpeedSmoothingSeconds = .5f;

        /// <summary>
        /// #874 P-S2: how often the motion owner samples a follow anchor - its speed, heading and
        /// (#874 Q2) whether it has left the follow - so a follow the anchor left is ended within a
        /// tenth of a second of its leaving.
        /// </summary>
        internal const float FollowSampleSeconds = .1f;

        /// <summary>#874 P-S2: the anchor is walking, by the same line a member is.</summary>
        internal static bool AnchorMoving(float anchorSpeed) => anchorSpeed >= MovingSpeed;

        /// <summary>
        /// #874 P-S2: metres the leg leads the goal. Zero while the anchor holds, so the member walks
        /// onto its slot and stops there. While it moves, the anchor's look-ahead travel, and never
        /// less than the stop band plus one re-issue step, so the member cannot come inside its own
        /// stopping distance of a leg that is still being carried forward.
        /// #874 Q2 / #1271 S2: the slot is on the anchor's own line, <paramref name="trailMetres"/>
        /// behind it along the route (<see cref="TrailPoint"/>), so the leg is led at most to where
        /// the member, stopping one stop distance short of it, still stands a body
        /// (<see cref="BlacksiteArrivalPolicy.ClaimSeparationBase"/>) behind the anchor. The raycast
        /// cut this replaces put the leg ON the holder in a corridor (round 1, log 11641, 11096).
        /// </summary>
        internal static float FollowLead(bool anchorMoving, float anchorSpeed, float stoppingDistance, float trailMetres)
        {
            if (!anchorMoving) return 0f;
            float lead = Math.Max(anchorSpeed * FollowLookAheadSeconds, stoppingDistance + ArrivalMargin + FollowReissueMetres);
            return Math.Min(lead, Math.Max(0f, stoppingDistance - BlacksiteArrivalPolicy.ClaimSeparationBase + trailMetres));
        }

        // #874 Q2 -------------------------------------------------------------------------------
        // An escort's claim keeps off the holder's reach point and approach line. #1271 S2: a follow
        // slot is a point on the route behind its anchor (TrailPoint), never the anchor's body plus
        // an offset. Unity-free, in plain numbers, so the headless suites drive the runtime's rules.

        /// <summary>#874 Q2: the anchor's route the follower keeps off, from the plan: its next 3 m.</summary>
        internal const float FollowPathClearMetres = 3f;

        /// <summary>
        /// #874 Q2: the spacing of a column of followers behind the anchor: the widest crowd claim the
        /// arrival table allows, one body contact plus a radius.
        /// </summary>
        internal const float FollowSlotSpacingMetres = BlacksiteArrivalPolicy.MaxClaimOffset;

        /// <summary>
        /// #1271 S2: how far behind its anchor, along the route, the follower of rank
        /// <paramref name="rank"/> (its place among the anchor's followers) stands: one column
        /// spacing per place, the first one spacing back, so no two ranks share a slot.
        /// </summary>
        internal static float FollowTrailMetres(int rank) => (Math.Max(rank, 0) + 1) * FollowSlotSpacingMetres;

        /// <summary>
        /// #1271 S2: the follow slot on a route - the one follow goal the motion owner and the planner
        /// both derive. <paramref name="corners"/> is the route from the anchor to its follower as
        /// x, y, z triples, corner 0 at the anchor. The slot is <paramref name="metres"/> along it -
        /// behind the anchor, on the way the follower comes - clamped at the anchor and at the
        /// follower's own end of the route. Answers the metres actually trailed, or -1 for an empty
        /// route. Round a bend the slot stays on the route, where the anchor's body plus an offset
        /// would stand in the wall.
        /// </summary>
        internal static float TrailPoint(ReadOnlySpan<float> corners, float metres, out float x, out float y, out float z)
        {
            x = y = z = 0f;
            int count = corners.Length / 3;
            if (count == 0) return -1f;
            x = corners[0]; y = corners[1]; z = corners[2];
            float wanted = Math.Max(metres, 0f), left = wanted;
            for (int i = 1; i < count; i++)
            {
                float dx = corners[3 * i] - x, dy = corners[3 * i + 1] - y, dz = corners[3 * i + 2] - z;
                float span = (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
                if (span >= left)
                {
                    float t = span > 0f ? left / span : 0f;
                    x += dx * t; y += dy * t; z += dz * t;
                    return wanted;
                }
                left -= span;
                x = corners[3 * i]; y = corners[3 * i + 1]; z = corners[3 * i + 2];
            }
            return wanted - left;
        }

        /// <summary>#874 Q2: horizontal distance from a point to a segment.</summary>
        internal static float SegmentDistance(float ax, float az, float bx, float bz, float px, float pz)
        {
            float ux = bx - ax, uz = bz - az;
            float lengthSquared = ux * ux + uz * uz;
            float t = lengthSquared < 1e-6f ? 0f : ((px - ax) * ux + (pz - az) * uz) / lengthSquared;
            t = t < 0f ? 0f : t > 1f ? 1f : t;
            float dx = px - (ax + ux * t), dz = pz - (az + uz * t);
            return (float)Math.Sqrt(dx * dx + dz * dz);
        }

        /// <summary>
        /// #874 Q2: a point whose clearance from the anchor's next <see cref="FollowPathClearMetres"/>
        /// is at least one body contact (<see cref="BlacksiteArrivalPolicy.ClaimSeparationBase"/>) stands off that path.
        /// </summary>
        internal static bool OffAnchorPath(float clearance) => clearance >= BlacksiteArrivalPolicy.ClaimSeparationBase;

        /// <summary>
        /// #874 Q2: how far an escort stages off the holder's approach line - the straight final leg
        /// from the holder's last route corner to its reach point - and off the reach point itself.
        /// </summary>
        internal const float EscortApproachClearMetres = 2f;

        /// <summary>
        /// #874 Q2: the point lies inside a holder's keep-out: within
        /// <see cref="EscortApproachClearMetres"/> of the approach line still AHEAD of the holder, or
        /// of the reach point (its reach radius where that is wider). Behind the holder is where a
        /// column of escorts walks, so only the reach point's own disc reaches back there. Round 1
        /// sent the escorts to the apparatus itself, their claims displaced the holder's reach point
        /// 0.9 m (log 11720, 11754, 11892) and he walked to the far corner of the room.
        /// </summary>
        internal static bool InHolderKeepOut(float px, float pz, float reachX, float reachZ,
            float fromX, float fromZ, float reachRadius)
        {
            KeepOutNearest(px, pz, reachX, reachZ, fromX, fromZ, out float cx, out float cz);
            float dx = px - cx, dz = pz - cz;
            float clear = Math.Max(EscortApproachClearMetres, reachRadius);
            return dx * dx + dz * dz < clear * clear;
        }

        /// <summary>
        /// #874 Q2: the nearest point just outside a holder's keep-out, pushed straight away from the
        /// keep-out's nearest point (across the line, to its right, for a point dead on it).
        /// </summary>
        internal static void KeepOutPush(float px, float pz, float reachX, float reachZ,
            float fromX, float fromZ, float reachRadius, out float x, out float z)
        {
            KeepOutNearest(px, pz, reachX, reachZ, fromX, fromZ, out float cx, out float cz);
            float dx = px - cx, dz = pz - cz;
            if (!Unit(ref dx, ref dz))
            {
                dx = reachZ - fromZ; dz = fromX - reachX;
                if (!Unit(ref dx, ref dz)) { dx = 1f; dz = 0f; }
            }
            float clear = Math.Max(EscortApproachClearMetres, reachRadius) + BlacksiteArrivalPolicy.SubstituteEpsilon;
            x = cx + dx * clear;
            z = cz + dz * clear;
        }

        /// <summary>
        /// #874 Q2: what becomes of an escort's crowd point against the
        /// holders' keep-outs. Outside every keep-out it stands. Inside one it is moved to the
        /// <see cref="KeepOutPush"/> point when that point stands on the floor, passes the goal's own
        /// test (#874 Q1) and is inside no keep-out itself; otherwise it is REFUSED - never handed
        /// back where it stood, which is how a forbidden claim used to survive a failed push.
        /// </summary>
        internal static BlacksiteKeepOutClaim KeepOutClaim(bool inKeepOut, bool pushStands, bool pushWithinGoalTest,
            bool pushInKeepOut)
            => !inKeepOut ? BlacksiteKeepOutClaim.Clear
                : pushStands && pushWithinGoalTest && !pushInKeepOut ? BlacksiteKeepOutClaim.Pushed
                : BlacksiteKeepOutClaim.Refused;

        // The keep-out's nearest point: on the line ahead of the holder, or the reach point itself
        // for a point level with or behind the holder.
        private static void KeepOutNearest(float px, float pz, float reachX, float reachZ,
            float fromX, float fromZ, out float cx, out float cz)
        {
            float ux = reachX - fromX, uz = reachZ - fromZ;
            float lengthSquared = ux * ux + uz * uz;
            float t = lengthSquared < 1e-6f ? 0f : ((px - fromX) * ux + (pz - fromZ) * uz) / lengthSquared;
            if (t <= 0f) { cx = reachX; cz = reachZ; return; }
            if (t > 1f) t = 1f;
            cx = fromX + ux * t;
            cz = fromZ + uz * t;
        }

        /// <summary>
        /// #874 Q2: why a follow ends, or null while it stands. The anchor gone, dead, seated aboard,
        /// boarding (on the rope or the ladder), in another world, or no longer holding what an
        /// escort was escorting. Round 2's escorts kept following a carrier that had crossed
        /// (waiting:follow, 165 s). The motion owner ends the follow on the frame it samples any of
        /// these; the planner asks the same rule (with the world left to its crossing target) and
        /// re-targets in the same think.
        /// </summary>
        internal static string FollowEndReason(bool anchorExists, bool anchorDead, bool anchorInMemberWorld,
            bool anchorBoarding, bool anchorSeated, bool followCarries, bool anchorCarries)
        {
            if (!anchorExists) return "gone";
            if (anchorDead) return "dead";
            if (anchorSeated) return "seated";
            if (anchorBoarding) return "boarding";
            if (!anchorInMemberWorld) return "world";
            if (followCarries && !anchorCarries) return "dropped";
            return null;
        }

        /// <summary>
        /// #1271 S2 (rule 2): a member is under way on a route of its own - a committed motion carries
        /// it (a crossing, the car), or it has an issued goal and does not stand where the path to it
        /// stops short or has run out (<see cref="StandsAtRouteEnd"/>). A path consumed at the goal
        /// itself is a route that got there. It is what a follow of that member follows; an anchor
        /// without one leads nobody anywhere.
        /// </summary>
        internal static bool RouteInForce(bool committedMotion, bool hasIssue, bool routeEnded)
            => committedMotion || (hasIssue && !routeEnded);

        private static bool Unit(ref float x, ref float z)
        {
            float length = (float)Math.Sqrt(x * x + z * z);
            if (length < 1e-4f) return false;
            x /= length; z /= length;
            return true;
        }

        /// <summary>
        /// #874 P-S2: speed matches the anchor's. Inside the band of a moving anchor the commanded
        /// speed is capped at the anchor's speed plus the gap to close; outside the band, or with the
        /// anchor holding, the member walks at what it was commanded so it catches up or settles.
        /// </summary>
        internal static float FollowSpeed(float commanded, bool anchorMoving, float anchorSpeed, float gap, float band)
        {
            if (!anchorMoving || gap < 0f || float.IsInfinity(gap) || gap > band) return commanded;
            float paced = anchorSpeed + gap / FollowCatchUpSeconds;
            return paced < commanded ? paced : commanded;
        }

        /// <summary>
        /// #874 P-S2: one step of the anchor-speed smoothing. <paramref name="dt"/> of zero or less
        /// keeps the previous value.
        /// </summary>
        internal static float SmoothAnchorSpeed(float previous, float measured, float dt)
        {
            if (dt <= 0f) return previous;
            float weight = dt / (dt + AnchorSpeedSmoothingSeconds);
            return previous + (measured - previous) * weight;
        }

        /// <summary>
        /// #874 P-S1: the GOAL is inside the arrival contract - the stop distance plus A1's margin,
        /// or the ordered tolerance where that is wider. The one arrival test a leg is judged by.
        /// </summary>
        internal static bool GoalWithinContract(float goalRemaining, float stoppingDistance, float arrivalTolerance)
            => goalRemaining >= 0f && goalRemaining <= Math.Max(stoppingDistance + ArrivalMargin, arrivalTolerance);

        /// <summary>
        /// #1219 C1: the path the agent walks ends short of the point it serves - it is Partial,
        /// None or Invalid and its end lies beyond the arrival tolerance of that point. The agent's
        /// remaining distance is to the END of a Partial path, so such a member is never arrived.
        /// A negative gap is not measured and never stops short.
        /// </summary>
        internal static bool PathStopsShort(BlacksitePathState path, float pathEndGap, float arrivalTolerance)
            => (path == BlacksitePathState.Partial || path == BlacksitePathState.None || path == BlacksitePathState.Invalid)
               && pathEndGap > arrivalTolerance;

        /// <summary>
        /// #1233 S5 (rules 4 and 6): the body has come to rest where its route stops short of its
        /// goal - Partial, None or Invalid beyond the arrival tolerance, with no more than its stop
        /// contract left to walk. It is where a carved doorway leaves a member, and it is the one
        /// place a request for the same goal is never a re-issue: the route has to be asked again
        /// from live geometry, because the thing that ended it (a locked door the hand just unlocked,
        /// an obstacle that moved) is not in the agent's stale corridor. #1271 S1: a body with no
        /// path (the navigator's -1) has nothing left to walk, so a goal whose path is gone is asked
        /// again too; on 2026-09-29 the fold kept a warped member pathless until its second stall.
        /// </summary>
        internal static bool StandsAtRouteEnd(BlacksitePathState path, float pathEndGap, float remaining,
            float stoppingDistance, float arrivalTolerance)
            => PathStopsShort(path, pathEndGap, arrivalTolerance)
               && remaining <= Math.Max(stoppingDistance + ArrivalMargin, arrivalTolerance);

        /// <summary>
        /// #874 P-S1: the identical-issue test, against the GOAL. A request is a byte-identical
        /// re-issue only when it asks for the goal the last issue was made for, the leg it resolves
        /// to is the leg already on the agent, and that is not a leg the member has already reached
        /// short of its goal. The last clause is the 2026-09-23 defect: a leg that was reached made
        /// every later request for the unreached goal "identical", 364 drops in round 3 alone.
        /// #1233 S5: and a member standing where its route ends short of the goal
        /// (<paramref name="routeEnded"/>, <see cref="StandsAtRouteEnd"/>) is never folded: on
        /// 2026-09-27 that member was the one at a locked door, and the only thing that re-asked
        /// the route for it was the 22 s revalidation. #1293 R2: that revalidation is gone. A lost
        /// or ended route is the route-end event above, and a leg that stops progressing is the
        /// progress ledger's miss; neither waits on the leg's age.
        /// </summary>
        internal static bool IsIdenticalIssue(float goalMove, float legMove, bool legInForce,
            bool legReached, bool goalReached, bool routeEnded)
        {
            if (goalMove > BlacksiteArrivalPolicy.MinDestinationChange) return false;
            if (legMove > BlacksiteArrivalPolicy.MinDestinationChange) return false;
            if (legInForce && legReached && !goalReached) return false;
            return !routeEnded;
        }

        /// <summary>The exact stallOwner token the [UAT][Motion] line prints.</summary>
        internal static string Describe(BlacksiteMotionSample sample)
        {
            BlacksiteStallOwner owner = Classify(sample, out string detail);
            return Describe(owner, detail);
        }

        internal static string Describe(BlacksiteStallOwner owner, string detail)
        {
            switch (owner)
            {
                case BlacksiteStallOwner.Moving: return "moving";
                case BlacksiteStallOwner.Seated: return "seated";
                case BlacksiteStallOwner.Crossing: return "crossing";
                case BlacksiteStallOwner.Combat: return "combat";
                case BlacksiteStallOwner.Dwell: return "dwell:" + (detail ?? "0.0");
                case BlacksiteStallOwner.Arrived: return "arrived";
                default: return "stalled";
            }
        }

        // #956 S12A U3 -------------------------------------------------------------------------

        /// <summary>
        /// How far from the goal the approach lerp begins. Unchanged from the value it had as
        /// BlacksiteSoldierAI.ApproachSlowdownDistance; it is here so the arbitration that reads it
        /// and the regression that replays it quote one number.
        /// </summary>
        internal const float ApproachSlowdownDistance = 1.4f;

        /// <summary>Where the lerp bottoms out at <see cref="WalkApproachSpeed"/>.</summary>
        internal const float ApproachWalkDistance = .45f;

        /// <summary>The speed the last half metre of an action approach is walked at.</summary>
        internal const float WalkApproachSpeed = 2.2f;

        /// <summary>
        /// #990 (Stage D2): the whistleblower's escape speed, quoted here because
        /// <c>WhistleblowerEnemyAI.EscapeSpeed</c> (<c>WhistleblowerEnemyAI.cs:36</c>) is a
        /// <c>private const</c> and <c>ConfigureAgent</c> writes it straight onto his agent. It is
        /// not a tunable and nothing here may change it: it is read only to derive the pursuit floor
        /// below and the knowledge model's extrapolation speed. If a later unit makes the witness's
        /// constant internal, these two lines read it instead and the arithmetic is untouched.
        /// (This unit is forbidden from editing the Whistleblower sources; see the report.)
        /// </summary>
        internal const float WitnessEscapeSpeed = 6.6f;

        /// <summary>
        /// #990 (Stage D2): the speed the Witness-RESERVED operator is commanded while it can see
        /// its target. The 2026-09-12 run chased a 6.6 m/s man with 4.8 m/s soldiers for 267 s and
        /// its closest approach got worse, not better, between the two attempts; a chase that
        /// cannot converge is not a navigation problem at any quality of navigation.
        ///
        /// Derivation: the escape speed plus the ladder's own definition of moved
        /// (<c>BlacksiteRecoveryLadder.MinimumGoalDistance</c>, half a metre) per second, so a
        /// pursuer gains exactly one "real move" every second it holds line of sight and a five
        /// metre lead closes in ten. It is deliberately the smallest margin that converges rather
        /// than a round number above it: the cordon, not the footrace, is meant to be the tactic
        /// that wins, and an operator that outran its own squad would arrive alone.
        ///
        /// It is a FLOOR on the role speed, applied only while the target is visible, so it binds
        /// upward exactly where the cart cap binds downward, and it is named by
        /// its own <see cref="BlacksiteSpeedOwner.Pursuit"/> so the [UAT][Motion] line says why a
        /// member is running.
        /// </summary>
        internal const float WitnessPursuitSpeed = WitnessEscapeSpeed + BlacksiteRecoveryLadder.MinimumGoalDistance;

        /// <summary>
        /// The share of the commanded speed a body must achieve before nothing owns its pacing.
        /// Acceptance A3's own ratio (score.ps1 $A3_PacingRatio), so a member the score calls
        /// paced and a member this arbitration calls unowned are the same member.
        /// </summary>
        internal const float AchievedSpeedRatio = .7f;

        /// <summary>
        /// The closed vocabulary the speedOwner= key prints, in the precedence
        /// <see cref="ClassifySpeedOwner"/> applies. Read by the regression so a new owner cannot
        /// be added without the fixture seeing it.
        /// </summary>
        internal static readonly BlacksiteSpeedOwner[] SpeedPrecedence =
        {
            BlacksiteSpeedOwner.None,
            BlacksiteSpeedOwner.Hold,
            BlacksiteSpeedOwner.OffMesh,
            BlacksiteSpeedOwner.AgentStopped,
            BlacksiteSpeedOwner.PathPending,
            BlacksiteSpeedOwner.ArrivalBand,
            BlacksiteSpeedOwner.ApproachSlowdown,
            BlacksiteSpeedOwner.Crouch,
            BlacksiteSpeedOwner.Web,
            BlacksiteSpeedOwner.Payload,
            BlacksiteSpeedOwner.Pursuit,
            BlacksiteSpeedOwner.Follow,
            BlacksiteSpeedOwner.CombatFire,
            BlacksiteSpeedOwner.AutoBraking,
            BlacksiteSpeedOwner.Acceleration,
            BlacksiteSpeedOwner.Unknown
        };

        /// <summary>
        /// The arbitration BlacksiteSoldierAI.GetAgentMoveSpeed performs, extracted so the term
        /// that bound the result is NAMED rather than inferred from the number afterwards. The
        /// caller keeps every Unity read; this is the same arithmetic in the same order.
        ///
        /// <paramref name="roleSpeed"/> is the role speed AFTER the caller's cargo cap, and
        /// <paramref name="pushing"/> says whether that cap actually bound it, because the cap is
        /// applied to the role speed itself before the lerp sees it. #990: <paramref name="pursuing"/>
        /// is the mirror of it - the Witness pursuit FLOOR, raised on the role speed at the same
        /// point and named by its own owner - and it is optional so that the fixture's existing call
        /// sites, which have nothing to pursue, are unchanged. #965 S14 U3: there are two such
        /// caps now, the cart push speed and the witness escort's clamp, and both bind at the same
        /// point, for the same reason, under the same owner. The parameter keeps the cart's name
        /// because BlacksiteMotionStallRegression passes it by name; it reads "a cargo cap bound the
        /// role speed", and what the member is carrying is the caller's business.
        ///
        /// <paramref name="approachEligible"/> is the caller's ShouldUseApproachSlowdown. Since
        /// #956 that gate also requires a reach contract: the March run walked 86 % of its samples
        /// on a plain transit goal (stopOwner=default) and the lerp had no action to protect on any
        /// of them, so a member passing through a waypoint is no longer walked at
        /// <see cref="WalkApproachSpeed"/>.
        ///
        /// Naming order when several terms bind: the multiplicative terms are applied last in the
        /// production expression and always bind, so crouch outranks web, and both outrank the
        /// lerp, which outranks the cargo cap that was already folded into the role speed.
        /// </summary>
        internal static float CommandedSpeed(
            float roleSpeed,
            float remaining,
            bool approachEligible,
            bool crouched,
            bool webbed,
            bool pushing,
            out BlacksiteSpeedOwner owner,
            bool pursuing = false)
        {
            owner = pushing ? BlacksiteSpeedOwner.Payload
                : pursuing ? BlacksiteSpeedOwner.Pursuit : BlacksiteSpeedOwner.None;
            float speed = roleSpeed;
            if (approachEligible
                && remaining >= 0f
                && remaining < ApproachSlowdownDistance)
            {
                float approach = InverseLerp(ApproachWalkDistance, ApproachSlowdownDistance, remaining);
                float walked = WalkApproachSpeed + (roleSpeed - WalkApproachSpeed) * approach;
                if (walked < speed) { speed = walked; owner = BlacksiteSpeedOwner.ApproachSlowdown; }
            }
            if (webbed) { speed *= .4f; owner = BlacksiteSpeedOwner.Web; }
            if (crouched) { speed *= CrouchedMoveSpeedMultiplier; owner = BlacksiteSpeedOwner.Crouch; }
            return speed;
        }

        /// <summary>
        /// The crouch multiplier, quoted here so the arbitration and BlacksiteSoldierAI share one
        /// number. Unchanged from the value it had as BlacksiteSoldierAI.CrouchedMoveSpeedMultiplier.
        /// </summary>
        internal const float CrouchedMoveSpeedMultiplier = 0.55f;

        /// <summary>
        /// Who owns the gap between the speed a member was commanded and the speed its body made.
        ///
        /// Precedence, highest first, and why each step sits where it does:
        ///
        ///  1. none              - the body is at or above <see cref="AchievedSpeedRatio"/> of its
        ///                         commanded speed AND nothing bound that speed below the role
        ///                         speed. A member keeping up with a throttled command is still
        ///                         throttled, so the second half of that test is load-bearing.
        ///  2. hold              - somebody else owns the transform. Pacing is not a question.
        ///  3. offMesh           - the agent is unbound or on a link; agent.speed means nothing.
        ///  4. agentStopped      - isStopped. The agent was told not to move at all.
        ///  5. pathPending       - no usable path. The March run spent 42.9 % of its interior
        ///                         member-seconds here, which is why it outranks everything below.
        ///  6. arrivalBand       - inside stop + 0.3 m: the member is where it was sent.
        ///  7. approachSlowdown  - the commanded speed itself was bound below the role speed.
        ///     crouch/web/payload/pursuit/follow
        ///                         Reported from the term the navigator recorded when it wrote
        ///                         agent.speed, never re-derived from the number.
        ///  8. autoBraking       - inside the braking band the agent needs to land on its goal.
        ///  9. acceleration      - a clear path, the role speed commanded, the agent under cruise.
        /// 10. unknown           - the agent is at speed and the body is not. The 11.7 % case.
        /// </summary>
        internal static BlacksiteSpeedOwner ClassifySpeedOwner(BlacksiteMotionSample sample)
        {
            float commanded = sample.CommandedSpeed;
            bool bound = sample.SpeedOwner != BlacksiteSpeedOwner.None;
            if (!bound && (commanded <= 0f || sample.Speed >= AchievedSpeedRatio * commanded))
                return BlacksiteSpeedOwner.None;
            // #874 S4: a crossing or a seat writes the body, or a dwell holds the agent. Above
            // offMesh and agentStopped, both of which are true of it and neither of which names it.
            if (sample.BodyWriter != BlacksiteBodyWriter.Agent || sample.DwellUntil > sample.Now)
                return BlacksiteSpeedOwner.Hold;
            if (!sample.AgentBound || sample.OffMeshLink) return BlacksiteSpeedOwner.OffMesh;
            if (sample.AgentStopped) return BlacksiteSpeedOwner.AgentStopped;
            if (sample.PathPending
                || sample.Path == BlacksitePathState.Pending
                || sample.Path == BlacksitePathState.None
                || sample.Path == BlacksitePathState.Invalid)
                return BlacksiteSpeedOwner.PathPending;
            if (sample.Remaining >= 0f
                && !float.IsInfinity(sample.Remaining)
                && sample.Remaining <= sample.StoppingDistance + ArrivalMargin)
                return BlacksiteSpeedOwner.ArrivalBand;
            if (bound) return sample.SpeedOwner;
            // The braking band is the agent's own: the distance it needs to shed its commanded
            // speed at its own acceleration, plus the stop distance it must land on.
            if (sample.AutoBraking
                && sample.Acceleration > 0f
                && sample.Remaining >= 0f
                && !float.IsInfinity(sample.Remaining)
                && sample.Remaining <= sample.StoppingDistance + commanded * commanded / (2f * sample.Acceleration))
                return BlacksiteSpeedOwner.AutoBraking;
            if (sample.AgentVelocity < AchievedSpeedRatio * commanded)
                return BlacksiteSpeedOwner.Acceleration;
            return BlacksiteSpeedOwner.Unknown;
        }

        /// <summary>The exact speedOwner token the [UAT][Motion] line prints, appended last.</summary>
        internal static string SpeedOwnerToken(BlacksiteSpeedOwner owner)
        {
            switch (owner)
            {
                case BlacksiteSpeedOwner.ApproachSlowdown: return "approachSlowdown";
                case BlacksiteSpeedOwner.Crouch: return "crouch";
                case BlacksiteSpeedOwner.Web: return "web";
                case BlacksiteSpeedOwner.Payload: return "payload";
                case BlacksiteSpeedOwner.Pursuit: return "pursuit";
                case BlacksiteSpeedOwner.Follow: return "follow";
                case BlacksiteSpeedOwner.CombatFire: return "combatFire";
                case BlacksiteSpeedOwner.Hold: return "hold";
                case BlacksiteSpeedOwner.ArrivalBand: return "arrivalBand";
                case BlacksiteSpeedOwner.AgentStopped: return "agentStopped";
                case BlacksiteSpeedOwner.Acceleration: return "acceleration";
                case BlacksiteSpeedOwner.AutoBraking: return "autoBraking";
                case BlacksiteSpeedOwner.PathPending: return "pathPending";
                case BlacksiteSpeedOwner.OffMesh: return "offMesh";
                case BlacksiteSpeedOwner.Unknown: return "unknown";
                default: return "none";
            }
        }

        /// <summary>
        /// #957 S12A U4. How long an idle soldier holds one facing target before it looks at the
        /// next one. Seeded per member per target, and it lives here rather than in
        /// BlacksiteRetrievalTuning because that file needs BlacksiteSoldierTacticalRole and cannot
        /// be compiled into the headless regression, so
        /// BlacksiteRetrievalTuning.GetFacingSweepHoldSeconds is a one-line delegation to this and
        /// tests/BlacksiteMotionStallRegression pins the production arithmetic itself.
        ///
        /// Derivation of the 3 s floor (#874 2026-09-25): a facing turn takes the short way round, so
        /// the largest step between two targets is 180 deg, and the slowest idle turn is the lowered
        /// rifle's BlacksiteStancePolicy.RestTurnDegreesPerSecond 68 deg/s: 2.65 s. With the 0.24 s
        /// BlacksiteStancePolicy.IdleTurnSettleSeconds before it that is 2.89 s, so the 3 s floor
        /// still outlasts the slowest turn; every faster stance (240 deg/s, 0.75 s) keeps over two
        /// seconds of standing still and looking.
        ///
        /// Derivation of the 6 s ceiling: the plan's own upper bound, and the number that makes
        /// the live read work. Lawson's verdict was taken on a thirty-second watch, and a 3 to 6 s
        /// hold gives that watch five to ten distinct looks: fewer would read as a statue, more as
        /// the fidgeting this unit exists to remove.
        ///
        /// The seed spread is the Stage 12A synchronisation guard: six members standing in one
        /// room hold their targets for six different times, so a sweep never becomes a squad-wide
        /// metronome. The target index is folded into the salt so one member's own targets are not
        /// all held for the same time either. Salt 151 is prime and clear of the other seeded salts.
        /// </summary>
        internal const float SweepHoldMinSeconds = 3f;
        internal const float SweepHoldMaxSeconds = 6f;

        internal static float FacingSweepHold(int animationSeed, int targetIndex)
            // The lerp is written out because this type is deliberately free of UnityEngine, and
            // SeededUnit is already a [0, 1) draw, so no clamp is needed to stay inside the band.
            => SweepHoldMinSeconds + (SweepHoldMaxSeconds - SweepHoldMinSeconds)
                * SeededUnit(animationSeed, 151 + (targetIndex < 0 ? 0 : targetIndex));

        /// <summary>
        /// #957 S12A U4. The step a member takes through its own sweep list, seeded per member.
        ///
        /// The seeded STARTING target alone is not enough: six members walking one cyclic list from
        /// six different offsets keep those offsets, so the member behind repeatedly catches the one
        /// in front at every advance and two of them shadow each other round the room. Measured in
        /// the native fixture before this existed: members 1 and 2, one place apart on a seven-target
        /// list, held the same target together on four separate holds inside twenty seconds.
        ///
        /// The stride is forced coprime with the target count, which is what makes it a permutation:
        /// stepping by it visits every target exactly once per cycle, so no doorway or corner is
        /// ever skipped and the skip loop that steps over a target the member is standing on cannot
        /// run out. Two members with different strides walk different sequences, so a coincidence
        /// does not repeat. A count of one or two has only the trivial stride.
        ///
        /// Salt 163 is the next prime clear of the hold's own 151 plus its target index, so a stride
        /// cannot alias a hold or any other seeded tuning value.
        /// </summary>
        internal static int FacingSweepStride(int animationSeed, int targetCount)
        {
            if (targetCount <= 2) return 1;
            int stride = 1 + (int)(SeededUnit(animationSeed, 163) * (targetCount - 1));
            if (stride >= targetCount) stride = targetCount - 1;
            for (int step = 0; step < targetCount; step++)
            {
                int candidate = 1 + (stride - 1 + step) % (targetCount - 1);
                if (GreatestCommonDivisor(candidate, targetCount) == 1) return candidate;
            }
            return 1;
        }

        private static int GreatestCommonDivisor(int a, int b)
        {
            while (b != 0) { int next = a % b; a = b; b = next; }
            return a < 0 ? -a : a;
        }

        /// <summary>A stable [0, 1) draw from a seed and a salt. The runtime's only hash.</summary>
        internal static float SeededUnit(int seed, int salt)
        {
            unchecked
            {
                uint value = Mix((uint)seed ^ ((uint)salt * 0x85EBCA6Bu));
                return (value & 0x00FFFFFFu) / 16777215f;
            }
        }

        internal static uint Mix(uint value)
        {
            unchecked
            {
                value ^= value >> 16;
                value *= 0x7FEB352Du;
                value ^= value >> 15;
                value *= 0x846CA68Bu;
                value ^= value >> 16;
                return value;
            }
        }

        private static float InverseLerp(float from, float to, float value)
        {
            if (to - from <= 0f) return 0f;
            float t = (value - from) / (to - from);
            return t < 0f ? 0f : t > 1f ? 1f : t;
        }

        internal static string PathToken(BlacksitePathState path)
        {
            switch (path)
            {
                case BlacksitePathState.Pending: return "Pending";
                case BlacksitePathState.Complete: return "Complete";
                case BlacksitePathState.Partial: return "Partial";
                case BlacksitePathState.Invalid: return "Invalid";
                default: return "none";
            }
        }
    }
}
