using System;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksitePassageResult { Moving, Complete, Blocked }
    // An admitted climber follows the authored ladder column in either direction. Every body
    // step is swept again at runtime; the preflight proof cannot exempt a newly placed object.
    internal sealed class BlacksiteWorldLadderMotor
    {
        private readonly Vector3[] _points;
        private int _point = 1;
        private float _blocked, _elapsed;
        private bool _returning;
        private readonly float _verticalSpeed;
        private const float VerticalSegmentThreshold = .15f;
        private bool _hasClimbed;
        internal bool Returning => _returning;
        // #874 P07: the abort's own hand-back down the column, and its bound.
        //
        // A climb that cannot finish used to report Blocked from wherever the motor stood, which is
        // usually a rung several metres up. The navigator then released the queue slot, the column
        // slot and the body-owning lease and handed a body hanging in the air to a recovery walk
        // with no ground under it to step on. Settling is the same swept, speed-limited motion the
        // retreat already uses, on the same authored rungs, in the direction the member came from;
        // the climb is reported ended when the body has arrived at the foot it mounted, or when
        // the settle's own bound or the ordinary block window says it cannot get there - and then
        // the body keeps the rung it is on rather than being placed anywhere.
        private bool _settling;
        private float _settle;
        private readonly float _settleSeconds;
        /// <summary>#874 P07: the motor is walking a cancelled climb back down; it still owns the body.</summary>
        internal bool Settling => _settling;
        /// <summary>
        /// #874 P07: this climb ended because a budget or a permission ran out, not because the
        /// column asked it to step aside. <see cref="Returning"/> is true for both, so the log line
        /// cannot tell them apart on its own once a settle retreats.
        /// </summary>
        internal bool Aborted { get; private set; }
        // #874 F07: the mount band, in metres of rise: BlacksiteTraversalPresentation's Mounting
        // phase (LadderMount, for both loadouts) was unreachable on every world ladder, and the
        // member popped from a walk straight into the rise clip. The band is the mount dwell
        // expressed as the rise it covers at climb speed, so no frame is added to the climb.
        private const float MountRiseMetres =
            BlacksiteLandingGearPolicy.ClimbSpeed * BlacksiteLandingGearPolicy.MountTurnSeconds;
        internal BlacksiteLadderPhase PhaseAt(Vector3 position)
        {
            if (_point < 0 || _point >= _points.Length) return BlacksiteLadderPhase.None;
            if (Mathf.Abs(_points[_point].y - position.y) <= VerticalSegmentThreshold)
                return _hasClimbed ? BlacksiteLadderPhase.Dismounting : BlacksiteLadderPhase.Approaching;
            // On the rungs. A member backing down is climbing, never mounting: the mount is the
            // hand-over-hand at the foot of the run and it happens once, on the way in.
            if (_returning || _point <= 0) return BlacksiteLadderPhase.Climbing;
            return Mathf.Abs(position.y - _points[_point - 1].y) <= MountRiseMetres
                ? BlacksiteLadderPhase.Mounting
                : BlacksiteLadderPhase.Climbing;
        }
        internal string Reason { get; private set; }
        internal Vector3 Goal => _points[_points.Length - 1];
        private readonly Vector3 _rungFacing;
        internal Vector3 Facing => _returning || _point < _points.Length - 1
            ? _rungFacing
            : Vector3.ProjectOnPlane(_points[_points.Length - 1] - _points[_points.Length - 2], Vector3.up).normalized;
        // #874 S5.1: the two clocks the [UAT][WorldLadder] end line reports. Member 5's 9.9 s climb
        // on 2026-09-09 was 2.5 s longer than its peers and the log had no field that could say
        // whether a stun pause or a physical refusal spent them. Both are cumulative for the whole
        // climb; _blocked below stays the current consecutive run that drives the 6 s budget.
        internal float PausedSeconds { get; private set; }
        internal float BlockedSeconds { get; private set; }
        internal BlacksiteWorldLadderMotor(Vector3[] points, float verticalSpeed = BlacksiteLandingGearPolicy.ClimbSpeed)
            : this(points, Vector3.ProjectOnPlane(points[1] - points[0], Vector3.up), verticalSpeed) { }
        internal BlacksiteWorldLadderMotor(Vector3[] points, Vector3 rungFacing, float verticalSpeed = BlacksiteLandingGearPolicy.ClimbSpeed)
        {
            _points = (Vector3[])points.Clone(); _verticalSpeed = verticalSpeed;
            _rungFacing = Vector3.ProjectOnPlane(rungFacing, Vector3.up).normalized;
            // #874 P07: the settle's bound is the authored column's own length at this motor's own
            // climb speed - the time the hand-back physically needs, derived rather than chosen.
            // It widens nothing: the 60 s climb cap and the 6 s block window are untouched, and a
            // settle is only ever entered by an ending that has already spent one of them.
            float length = 0f;
            for (int i = 1; i < _points.Length; i++) length += Vector3.Distance(_points[i - 1], _points[i]);
            _settleSeconds = verticalSpeed > 0f ? length / verticalSpeed : 0f;
        }
        // canRetreat: #874 S5.1, whether this climber is the one the column allows to back down.
        // Null keeps the pre-column behaviour (a single climber may always retreat).
        internal BlacksitePassageResult Tick(Transform actor, float delta, int mask, bool paused, Func<Vector3,bool> canEnter, Func<bool> canRetreat = null)
        {
            if (_point < 0) return BlacksitePassageResult.Blocked;
            if (_point >= _points.Length) return BlacksitePassageResult.Complete;
            if (_settling)
            {
                // #874 P07: the settle's clock runs whether or not the body may move this frame, so
                // a member stunned through its whole hand-back is still bounded. A stun still stops
                // the motion itself: nobody lets go of a ladder because they were hit.
                _settle += delta;
                if (paused) PausedSeconds += delta;
                if (_settle >= _settleSeconds) return FinishBlocked();
                if (paused) return BlacksitePassageResult.Moving;
            }
            else
            {
                if (paused) { PausedSeconds += delta; return BlacksitePassageResult.Moving; }
                _elapsed += delta;
                if (_elapsed > 60f)
                {
                    // A higher occupant holds its budget running and backs down once it is the lowest,
                    // so the stack unwinds bottom-first instead of retreating through the body below.
                    if (!_returning && (canRetreat == null || canRetreat()))
                        Retreat("world ladder exceeded 60 seconds");
                    // #874 F07: and if it is STILL not the one allowed to unwind, the budget is spent
                    // anyway. This fell through and kept ticking: a climber held behind a lead that
                    // never moves was never retreated and never blocked, so its 60 s cap expired into
                    // nothing and it hung on the rungs holding a column slot for the rest of the round.
                    // Blocked is the bounded outcome - the navigator releases the queue and the column
                    // slot, frees everyone above it and re-plans the route - and it is the same answer
                    // an exhausted retreat already gives one line above.
                    //
                    // #874 P07: it is still that answer, and it is still the same one for both, but a
                    // body that is off the ground walks itself back down first. Reporting the ending
                    // from a rung is what handed the navigator a member it could neither rebind nor
                    // hand-walk, on a column it had just released to everyone below.
                    else return BeginSettle(actor, mask, "world ladder exceeded 60 seconds");
                }
            }
            float remainingTime = Mathf.Min(delta, .2f);
            for (int step = 0; step < 64 && remainingTime > .00001f; step++)
            {
            while (_point >= 0 && _point < _points.Length && Vector3.Distance(actor.position, _points[_point]) < .015f) _point += _returning ? -1 : 1;
            if (_point < 0) return FinishBlocked();
            if (_point == _points.Length) return BlacksitePassageResult.Complete;
            // Use the authored segment, not its shrinking remainder: the last .15 m of a
            // climb is still a climb and must not accelerate to the walking speed.
            int from = _point + (_returning ? 1 : -1);
            bool vertical = from >= 0 && from < _points.Length
                && Mathf.Abs(_points[_point].y - _points[from].y) > VerticalSegmentThreshold;
            float speed = vertical ? _verticalSpeed : BlacksitePhysicalStep.WalkSpeed;
            Vector3 next = Vector3.MoveTowards(actor.position, _points[_point], Mathf.Min(speed * remainingTime, .12f));
            if (BlacksitePhysicalStep.ClearSegment(actor, actor.position, next, mask, null, out var blocker)
                && (canEnter == null || canEnter(next)))
            { remainingTime -= Vector3.Distance(actor.position, next) / speed; _hasClimbed |= vertical; actor.position = next; _blocked = 0; continue; }
            _blocked += delta; BlockedSeconds += delta;
            if (_blocked < 6f) return BlacksitePassageResult.Moving;
            // #874 P07: a settle that cannot get past what is under it has done what it can. The
            // body keeps the rung it is on, and the navigator hands it to the recovery owner from
            // there; climbing through the obstruction, or being placed past it, is the thing this
            // whole file exists to refuse.
            if (_settling) return FinishBlocked();
            if (!_returning && (canRetreat == null || canRetreat()))
                Retreat("world ladder blocked by " + (blocker != null ? blocker.name : "another actor"));
            return BlacksitePassageResult.Moving;
            }
            return BlacksitePassageResult.Moving;
        }
        // #874 P07: turn an ending into the walk back down that ending owes the body. A member that
        // is already standing - level with the foot it mounted at, or on a floor within one manual
        // step - has nothing to walk, and reports the ending immediately as it always did.
        private BlacksitePassageResult BeginSettle(Transform actor, int mask, string reason)
        {
            Aborted = true; Reason = reason;
            if (Supported(actor, mask)) return FinishBlocked();
            if (!_returning) { _returning = true; _point = Mathf.Max(0, _point - 1); }
            _settling = true; _settle = 0f; _blocked = 0f;
            return BlacksitePassageResult.Moving;
        }
        private BlacksitePassageResult FinishBlocked()
        {
            _settling = false;
            _point = -1;
            return BlacksitePassageResult.Blocked;
        }
        private bool Supported(Transform actor, int mask)
            => Mathf.Abs(actor.position.y - _points[0].y) <= VerticalSegmentThreshold
                || (BlacksiteLandingGeometry.Ground(actor.position, .6f, mask, null, out var floor)
                    && Mathf.Abs(floor.point.y - actor.position.y) <= BlacksitePhysicalStep.StepHeight);
        private void Retreat(string reason)
        {
            Reason = reason; _returning = true; _point = Mathf.Max(0, _point - 1); _elapsed = _blocked = 0;
        }
    }
}
