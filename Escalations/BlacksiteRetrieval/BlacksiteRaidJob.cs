using System;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteJobKind { Objective, Egress, Infiltrate, Guard, Sweep, Escort, Rally, PlayerResponse, Backup }
    internal enum BlacksiteJobPhase { Travel, Waiting, Approach, Interact, Carry, Load, Recover, Holding }
    // #874 Q1: Unreachable closes an attempt whose goal this member cannot reach, so the planner
    // answers it with a DIFFERENT goal instead of retrying the same one. Appended last.
    internal enum BlacksiteJobResult { Continue, Reassign, LocalFailure, Completed, Invalidated, Unreachable }
    // #874 S4: only the causes something still produces. The stall causes went with the stall
    // clocks (BlacksiteMotionMonitor is the one progress judge).
    internal enum BlacksiteBlockedCause { None, NoReachableCandidate, PlanningBudget }
    // How an assignment attempt ended. Interrupted covers everything the squad did not cause -
    // player theft, crew storage, a casualty, a stun, an immediate cargo threat, a deliberate
    // handoff, cleanup and departure - and closes the attempt without blaming the objective.
    internal enum BlacksiteAttemptOutcome { Completed, Failed, Interrupted }
    // Runtime job progress: target identity survives a blocked assignee, and motion alone is not
    // progress. Objective identity, one assignment attempt and a movement replan are separate
    // things: an attempt is opened explicitly (BeginAttempt) and closed explicitly (Finish).
    // #874 S4: a job is a WORK record - attempt identity and the closest-approach watermark the
    // reachability rule reads. It no longer closes anything on time without closure: that was a
    // second stall detector, and BlacksiteMotionMonitor is the one progress judge.
    // #1293 R2: and it carries no budgets. An assignment ends when the planner replaces or releases
    // it, on arrival, or on the ledger's miss; a dwell keeps its declared duration on its owner.
    internal sealed class BlacksiteRaidJob
    {
        internal ulong Owner { get; private set; }
        internal BlacksiteJobKind Kind { get; private set; }
        internal BlacksiteJobPhase Phase { get; private set; }
        internal float AssignedAt { get; private set; }
        // When the watermark last closed by the progress epsilon. Printed, never judged.
        internal float LastProgressAt { get; private set; }
        internal int Generation { get; private set; }
        internal bool AttemptOpen => _assigned;
        // Terminal result of the last closed attempt; Continue while one is open.
        internal BlacksiteJobResult Result { get; private set; }
        // Defensive only: a bound attempt still open when the next one begins. Production closes
        // every attempt at its release point, so this must stay zero in the modelled flow.
        internal int StaleAttemptsClosed { get; private set; }
        private float _bestRemaining = float.PositiveInfinity;
        // #874 S4.2: read-only. The [UAT][Objective] event=closed line's best= field is this
        // watermark.
        internal float BestRemaining => _bestRemaining;
        // Only Tick's measured distance closure advances this; Retarget and phase changes establish
        // baselines and never count. Printed and pinned by the regressions; it judges nothing.
        internal int DistanceProgressCount { get; private set; }
        // #1305 G4 (R17): non-Waiting observes in a row with no new best, a cart lease's progress fact (reset by a new best, Waiting, BeginAttempt, Retarget, Finish). #1305 H4: and a staged cart's magnet pull.
        internal int ObservesWithoutProgress { get; private set; }
        private bool _observed, _assigned;
        // The goal the watermark measures against was replaced (world crossing, container lock to
        // drive, a re-probed approach point). The baseline restarts; the ATTEMPT does not, so
        // Generation, Result and Owner are untouched and the objective's failure history is
        // unaffected.
        internal void Retarget(float now, float remaining)
        {
            LastProgressAt = now; ObservesWithoutProgress = 0;
            _observed = !float.IsNaN(remaining);
            _bestRemaining = _observed ? remaining : float.PositiveInfinity;
        }
        // #989 C1: the same goal MOVED, because the target itself moved (a walking witness). Track
        // keeps the ALL-TIME-BEST watermark: it seeds the baseline only on the first observation of
        // an attempt, so the only thing that can credit progress is Tick's own closure epsilon
        // against the best distance the attempt ever reached. A receding goal is not progress
        // (#989 round 2: a recent-max ratchet re-credited every oscillation).
        internal void Track(float now, float remaining)
        {
            if (float.IsNaN(remaining) || _observed) return;
            _bestRemaining = remaining; _observed = true;
        }
        // A new eligible assignment: a different member, or the same member after its cooldown.
        internal void BeginAttempt(ulong owner, float now, BlacksiteJobKind kind = BlacksiteJobKind.Objective)
        {
            if (_assigned) { StaleAttemptsClosed++; Finish(false); }
            Generation++; Result = BlacksiteJobResult.Continue; LastProgressAt = now; _assigned = true;
            DistanceProgressCount = 0; ObservesWithoutProgress = 0;
            Bind(owner, now, kind);
        }
        // Rebinding a still-open attempt (replan, phase or goal churn) keeps its identity.
        internal void Assign(ulong owner, float now, BlacksiteJobKind kind = BlacksiteJobKind.Objective)
        {
            if (!_assigned) { BeginAttempt(owner, now, kind); return; }
            Bind(owner, now, kind);
        }
        private void Bind(ulong owner, float now, BlacksiteJobKind kind)
        {
            Owner = owner; Kind = kind; AssignedAt = now;
            _bestRemaining = float.PositiveInfinity; _observed = false;
        }
        internal BlacksiteJobResult Finish(bool completed)
        {
            _assigned = false; _observed = false; ObservesWithoutProgress = 0; // #1305 H4: a staged cart's pull starts from zero
            return Result = completed ? BlacksiteJobResult.Completed : BlacksiteJobResult.Invalidated;
        }
        // #874 Q1: the attempt's goal cannot be reached by its member. Closed ONCE: an open attempt
        // is closed Unreachable; one already closed Invalidated is the same episode, so its verdict
        // is named rather than a second closure made; any other closed verdict is kept.
        internal BlacksiteJobResult FinishUnreachable()
        {
            if (_assigned) Finish(false);
            else if (Result != BlacksiteJobResult.Invalidated) return Result;
            return Result = BlacksiteJobResult.Unreachable;
        }
        // Sweep and Escort are one discretionary local-security duty class. The Escort intent
        // exists whenever any record is Reserved or Carried, so ordinary reservation churn
        // flickers between the two; treating the pair as one class stops that flicker from
        // opening a fresh attempt. Any other kind change, a different objective or a world change
        // is genuinely different work and opens a fresh attempt.
        internal static bool OpensNewAttempt(BlacksiteJobKind was, ulong wasObject, bool wasOutside,
            BlacksiteJobKind now, ulong nowObject, bool nowOutside)
            => wasObject != nowObject || wasOutside != nowOutside || DutyClass(was) != DutyClass(now);
        private static BlacksiteJobKind DutyClass(BlacksiteJobKind kind)
            => kind == BlacksiteJobKind.Escort ? BlacksiteJobKind.Sweep : kind;
        // One observation: its phase and the watermark (progress: a new best by `gain`; #1305 H4: 5 cm for a magnet pull). A wait
        // or a NaN sample measures nothing; #874 S6.1: a phase change never re-baselines, a changed goal is declared by Retarget.
        internal void Tick(float now, BlacksiteJobPhase phase, float remaining, float gain = .5f)
        {
            Phase = phase;
            if (phase == BlacksiteJobPhase.Waiting) { ObservesWithoutProgress = 0; return; }
            if (!_observed && !float.IsNaN(remaining)) { _bestRemaining = remaining; _observed = true; }
            if (!float.IsNaN(remaining) && remaining < _bestRemaining - gain)
            { _bestRemaining = remaining; LastProgressAt = now; DistanceProgressCount++; ObservesWithoutProgress = 0; }
            else ObservesWithoutProgress++;
        }
    }
    // Bounded per-objective attempt history, independent of the local pickup probe. Every attempt
    // the squad itself failed escalates the objective's retry backoff (1, 3, 6, 12, 24 s, capped);
    // MaxFailures spends the history. What happens then is BlacksiteObjectivePolicy's decision:
    // #1219 A4 withdraws a spent mission objective (MissionAttemptsExhausted) instead of re-arming
    // it at the capped 24 s backoff without limit, and a loot record withdraws as before.
    // An interrupted attempt is not the objective's fault and neither counts nor escalates.
    // #1219 A4: acquisition alone no longer clears the history. On 2026-09-26 (Test 3 round 1) a
    // stuck cart ran 19 attempts and 8 acquisitions, and each acquisition closed Completed and
    // refilled the budget. Only haul progress clears it (CreditHaulProgress): NEW ground on the
    // objective's carry goals (ObserveCarry), or a record that completed its haul (staged, loaded,
    // delivered, witness completed). Per-attempt closure was not enough: the same cart moved 9.3 m
    // in attempt 2, and attempt 14 re-hauled it 34.7 m back to that best after a player had pushed
    // it back along its route, which cleared a 5/6 history and left the cart never withdrawn.
    internal sealed class BlacksiteObjectiveAttempts
    {
        internal const int MaxFailures = 6;
        private static readonly float[] Backoff = { 1f, 3f, 6f, 12f, 24f };
        internal int Failures { get; private set; }
        internal bool Exhausted => Failures >= MaxFailures;
        internal void Fail() { if (Failures < MaxFailures) Failures++; }
        // #1219 A4: the one way the history is cleared.
        internal void CreditHaulProgress() { Failures = 0; }
        // #1219 A4: the objective's haul ground, kept across attempts as one set per world (inside,
        // outside), each measured against that world's carry goal: the inside main entrance, then
        // the delivery point. In each world HaulBest is the lowest routed remaining any carry has
        // reached on its goal, and the baseline is the first routed sample, then the best at the
        // last credit. The uncredited partial (baseline minus best, always under progressMetres)
        // travels with the carry: at a world flip it leaves the world it was earned in, whose
        // baseline drops to its best, and is added to the entered world's baseline, or to that
        // world's first routed sample while it has none. So a flip credits nothing on its own; it
        // moves the measured partial and loses none of it, though the unsampled approach to the
        // door and the first metres past it are not counted. Every credit needs progressMetres of
        // new ground summed over both worlds.
        // Returning to a world reuses its set, so ground regained there - a cart a player or a
        // soldier took back through the door and re-hauled - is not below its best and never
        // counts. Only a goal point that moved more than goalTolerance within its world starts
        // that world over, and the partial it held goes with it. The metric is route distance, so
        // a navmesh change that shortens the route without the cart moving (a door opened, a carve
        // removed, the deck bake) can credit once per progressMetres of shortening; HaulBest only
        // falls within one goal, so that is bounded by the goal's route length.
        private sealed class HaulWorld
        {
            internal bool HasGoal;
            internal float GoalX, GoalY, GoalZ, Carried;
            internal float Best = float.PositiveInfinity, Baseline = float.PositiveInfinity;
            internal float Partial => float.IsPositiveInfinity(Baseline) ? Carried : Baseline - Best;
        }
        private readonly HaulWorld _haulInside = new HaulWorld(), _haulOutside = new HaulWorld();
        private HaulWorld _haulWorld;
        internal float HaulBest => _haulWorld == null ? float.PositiveInfinity : _haulWorld.Best;
        // One carry sample: the carrier's world, that world's carry goal and the routed remaining
        // to it. A Waiting sample (a crossing) and an unrouted one still declare the goal but
        // measure nothing. True when this sample made the haul progress: the baseline moves down
        // to HaulBest, gained is the new ground credited and the failure history is cleared.
        internal bool ObserveCarry(bool outside, float goalX, float goalY, float goalZ, float goalTolerance,
            bool waiting, float remaining, bool routed, float progressMetres, out float gained)
        {
            gained = 0f;
            var world = outside ? _haulOutside : _haulInside;
            float dx = goalX - world.GoalX, dy = goalY - world.GoalY, dz = goalZ - world.GoalZ;
            if (!world.HasGoal || dx * dx + dy * dy + dz * dz > goalTolerance * goalTolerance)
            {
                world.HasGoal = true; world.GoalX = goalX; world.GoalY = goalY; world.GoalZ = goalZ;
                world.Best = world.Baseline = float.PositiveInfinity; world.Carried = 0f;
            }
            if (_haulWorld != null && _haulWorld != world)
            {
                var left = _haulWorld;
                float partial = left.Partial;
                if (float.IsPositiveInfinity(left.Baseline)) left.Carried = 0f; else left.Baseline = left.Best;
                if (float.IsPositiveInfinity(world.Baseline)) world.Carried += partial; else world.Baseline += partial;
            }
            _haulWorld = world;
            if (waiting || !routed || !(remaining < world.Best)) return false;
            world.Best = remaining;
            if (float.IsPositiveInfinity(world.Baseline)) { world.Baseline = remaining + world.Carried; world.Carried = 0f; }
            if (world.Baseline - world.Best < progressMetres) return false;
            gained = world.Baseline - world.Best; world.Baseline = world.Best;
            CreditHaulProgress();
            return true;
        }
        // Completed and Interrupted closes leave the history as it is; only a squad failure counts.
        // #1233 S1 (rule 3): a drop the cart's stuck verdict answered is terminal, no squad failure.
        internal static BlacksiteAttemptOutcome DropOutcome(BlacksiteAttemptOutcome dropped, bool cartAnswered) => cartAnswered ? BlacksiteAttemptOutcome.Interrupted : dropped;
        internal void Close(BlacksiteAttemptOutcome outcome)
        {
            if (outcome == BlacksiteAttemptOutcome.Failed) Fail();
        }
        internal float RetryAt(float now) => now + Backoff[Math.Min(Math.Max(Failures - 1, 0), Backoff.Length - 1)];
    }
}

