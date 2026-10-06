using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>#874 S3 (rule 2): the four crossings of the ship deck's two one-way links.</summary>
    internal enum BlacksiteShipCrossingKind : byte { RopeDescend, RopeAscend, LipDrop, LadderUp }

    // #991, #874 S2/S3 (rule 2): the crossing of an off-mesh link, the way vanilla crosses one - the
    // off-mesh animation autoTraverseOffMeshLink runs (DetourCrowd, dtCrowd::update). Unity's own
    // traversal consumed a link in a single frame the moment anything reset the path: the 2026-09-12
    // run recorded sixteen agentVelocity spikes of 200-460 m/s. The crossing is walked over a duration
    // fixed at its begin to a fixed point on the navmesh: no sweep, no wait, no refusal. An interior
    // jump is Detour's own shape (onto the link start in the first 15 % of tmax, across to the link end
    // in the rest), its flight ballistic under the world's gravity (#1262 P6: the clip's root is flat);
    // a ship crossing is walked by arc length. The rest is presentation - the facing and the clip's rate.
    internal sealed class BlacksiteLinkCrossing
    {
        private readonly Vector3[] _points;
        // The elapsed time at which the body stands on each point; _times[^1] is the duration.
        private readonly float[] _times;
        // #1283 S4: the link's own last point - a ladder climb's top, where its deck leg begins; the end otherwise.
        private readonly int _ladderTop;
        private readonly Vector3? _fixedFacing;
        private int _segment;
        internal Vector3 End => _points[_points.Length - 1];
        internal float Elapsed { get; private set; }
        /// <summary>The duration fixed at the begin (Detour's tmax for an interior jump).</summary>
        internal float PlannedAirtime => _times[_times.Length - 1];
        /// <summary>Null for an interior jump; the ship crossing this is otherwise.</summary>
        internal BlacksiteShipCrossingKind? Kind { get; }
        /// <summary>DetourCrowd's ta: the share of tmax spent walking from the body's position onto the link start.</summary>
        private const float WalkOnFraction = .15f;
        private const float StartSlack = .05f; // #1219 C2: how far the body may stand off the link start and still begin on it
        /// <summary>#1262 P6: the ballistic apex of a body crossing at flightSpeed, g*T*T/8 over its flight time T and never taller even when a slowed agent stretches Detour's tmax; the body stands 4*Apex*u*(1-u) above the chord. 0 for a ship crossing.</summary>
        internal float Apex { get; }

        /// <summary>Detour's jump: tmax = planar(link start, link end) / speed * 0.5, fixed at the begin.</summary>
        internal BlacksiteLinkCrossing(Vector3 initPos, Vector3 linkStart, Vector3 linkEnd, float speed, float flightSpeed)
        {
            _points = new[] { initPos, linkStart, linkEnd };
            _ladderTop = _points.Length - 1;
            Vector3 planar = Vector3.ProjectOnPlane(linkEnd - linkStart, Vector3.up);
            float tmax = speed > 0f ? planar.magnitude / speed * .5f : 0f;
            _times = new[] { 0f, tmax * WalkOnFraction, tmax };
            float flight = (1f - WalkOnFraction) * (flightSpeed > 0f ? planar.magnitude / flightSpeed * .5f : 0f);
            Apex = Mathf.Max(0f, -Physics.gravity.y) * flight * flight / 8f;
            _fixedFacing = planar.sqrMagnitude > .000001f ? planar.normalized : (Vector3?)null;
        }

        /// <summary>#874 S3, #1219 C2: <paramref name="ship"/>'s link from <paramref name="start"/> to <paramref name="end"/>, walked by arc
        /// length over <paramref name="duration"/> along its kind's polyline raised by <paramref name="lift"/>, from <paramref name="body"/> when
        /// it stands over StartSlack off the start. #1271 S6: the ladder steps onto the players' column, climbs it, steps over the top.
        /// #1283 S4: a ladder climb then walks <paramref name="deck"/> (the floor points after the top, ending on the member's seat) at
        /// the approach walk; null or empty ends it on the top. Every other kind ignores it.</summary>
        internal BlacksiteLinkCrossing(Transform ship, BlacksiteLandingAccess access, Vector3 start, Vector3 end, Vector3 body, Vector3 lift, float duration, Vector3[] deck)
        {
            bool board = end.y > start.y, fromBody = (body - (start + lift)).sqrMagnitude > StartSlack * StartSlack;
            int lead = fromBody ? 1 : 0, walk = 0;
            Vector3 lip = ship.TransformPoint(BlacksiteBoardingGeometry.LipStep), cable = ship.TransformPoint(BlacksiteRappelGeometry.CableTopLocal);
            Vector3 facing = ship.forward;
            if (access.Hover && board) { Kind = BlacksiteShipCrossingKind.RopeAscend; _points = new Vector3[lead + 4]; _points[lead + 1] = cable; _points[lead + 2] = lip; }
            else if (access.Hover) { Kind = BlacksiteShipCrossingKind.RopeDescend; _points = new Vector3[lead + 4]; _points[lead + 1] = lip; _points[lead + 2] = cable; }
            else if (board)
            {
                var ladder = BlacksiteLandingGearPolicy.Ladders[access.LadderIndex];
                Vector3 column = ship.TransformPoint(new Vector3(ladder.X + ladder.Outward * BlacksiteLandingGearPolicy.LadderNodeOut, 0f, ladder.Z)), up = ship.up;
                walk = deck != null ? deck.Length : 0;
                Kind = BlacksiteShipCrossingKind.LadderUp; _points = new Vector3[lead + 4 + walk]; facing = ship.right * -ladder.Outward;
                _points[lead + 1] = column + up * ((start.y - column.y) / up.y);
                _points[lead + 2] = column + up * ((end.y - column.y) / up.y);
                for (int i = 0; i < walk; i++) _points[lead + 4 + i] = deck[i];
            }
            else { Kind = BlacksiteShipCrossingKind.LipDrop; _points = new Vector3[lead + 3]; _points[lead + 1] = lip; }
            _ladderTop = _points.Length - 1 - walk;
            _points[lead] = start;
            _points[_ladderTop] = end;
            for (int i = lead; i < _points.Length; i++) _points[i] += lift;
            if (fromBody) _points[0] = body;
            _fixedFacing = Vector3.ProjectOnPlane(facing, Vector3.up).normalized;
            _times = new float[_points.Length];
            for (int i = 1; i <= _ladderTop; i++) _times[i] = _times[i - 1] + Vector3.Distance(_points[i - 1], _points[i]);
            float length = _times[_ladderTop];
            for (int i = 1; i <= _ladderTop; i++) _times[i] = length > 0f ? _times[i] / length * duration : 0f;
            _times[_ladderTop] = Mathf.Max(0f, duration);
            for (int i = _ladderTop + 1; i < _points.Length; i++)
                _times[i] = _times[i - 1] + Vector3.Distance(_points[i - 1], _points[i]) / BlacksiteMotionPolicy.WalkApproachSpeed;
        }

        /// <summary>Which way the body faces: the link's own direction for a jump. A ship crossing faces along a
        /// walked segment and, on a climbed one (steeper than 45 degrees), its own: the rungs for the ladder, else the bow.</summary>
        internal Vector3? FacingHint => Kind == null ? _fixedFacing : Walked(_points[_segment + 1] - _points[_segment]) ?? _fixedFacing;
        private static Vector3? Walked(Vector3 d) { Vector3 p = Vector3.ProjectOnPlane(d, Vector3.up); return p.sqrMagnitude > .0001f && p.sqrMagnitude >= d.y * d.y ? p.normalized : (Vector3?)null; }
        /// <summary>#1271 S6: a ladder climb's phase is its leg, counted from the top: over it (Dismounting), the column
        /// (Climbing), onto the column (Mounting, where vanilla plays EnterLadder), the walk onto the link start (Approaching).
        /// #1283 S4: the deck leg past the top is ordinary locomotion (None).</summary>
        internal BlacksiteLadderPhase LadderPhase => Kind != BlacksiteShipCrossingKind.LadderUp || !OnLadder ? BlacksiteLadderPhase.None
            : (BlacksiteLadderPhase)Mathf.Max((int)BlacksiteLadderPhase.Approaching, (int)BlacksiteLadderPhase.Dismounting + _segment + 1 - _ladderTop);
        /// <summary>#1283 S4: the body is on the link itself, the part the ship's Ladder FIFO covers: the whole crossing,
        /// except a ladder climb's deck leg from the top to the seat.</summary>
        internal bool OnLadder => _segment < _ladderTop;
        /// <summary>#1283 S4: whether a ship crossing takes both hands this frame - the host's Climbing bit and
        /// BlacksiteNavigator.RequiresBothHands both ask here, so they agree. A lip drop never does; the ladder
        /// only on the rungs (Mounting to Dismounting), not on the walk onto the mount or the deck leg after the top.</summary>
        internal static bool ShipCrossingTakesHands(BlacksiteShipCrossingKind kind, BlacksiteLadderPhase phase)
            => kind == BlacksiteShipCrossingKind.LadderUp ? phase >= BlacksiteLadderPhase.Mounting : kind != BlacksiteShipCrossingKind.LipDrop;

        /// <summary>One frame of the crossing; true once the body stands on the end (#1283 S4: on its last leg, so a
        /// frame that overruns the deck leg's start still reads off the ladder).</summary>
        internal bool Tick(Transform actor, float delta)
        {
            Elapsed += delta;
            if (Elapsed >= PlannedAirtime) { _segment = _points.Length - 2; actor.position = End; return true; }
            while (_segment < _points.Length - 2 && Elapsed >= _times[_segment + 1]) _segment++;
            float span = _times[_segment + 1] - _times[_segment];
            float u = span > 0f ? (Elapsed - _times[_segment]) / span : 1f;
            actor.position = Vector3.Lerp(_points[_segment], _points[_segment + 1], u)
                + (Kind == null && _segment == 1 ? Vector3.up * (4f * Apex * u * (1f - u)) : Vector3.zero);
            return false;
        }
    }
}
