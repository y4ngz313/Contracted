using System;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>#1195 Contract S: what the contract card's OPTIONAL section shows.</summary>
    internal enum SideObjectivePhase
    {
        Hidden = 0,
        Progress = 1,
        ClaimReward = 2,
        Recovered = 3,
    }

    /// <summary>
    /// Who authored a <see cref="SideObjectiveSnapshot"/>. A wire value: append only, never
    /// renumber. A peer that does not know a value reads it as <see cref="None"/>.
    /// </summary>
    internal enum SideObjectiveSource : byte
    {
        None = 0,
        WesleyInterior = 1,

        /// <summary>#1198. A Wesley's Moons trial; scope and entry are the trial key.</summary>
        WesleyTrial = 2,
    }

    /// <summary>
    /// #1195. The source-neutral state the host publishes and every peer applies. The host reads the
    /// source's live objective into one of these and the network layer carries it field for field,
    /// so a new source only has to fill it in.
    /// </summary>
    internal readonly struct SideObjectiveSnapshot : IEquatable<SideObjectiveSnapshot>
    {
        internal static readonly SideObjectiveSnapshot None = default;

        internal SideObjectiveSnapshot(
            SideObjectiveSource source,
            string scope,
            string entry,
            int count,
            int required,
            bool completed,
            bool rewardClaimed,
            bool revealed)
        {
            Source = source;
            Scope = scope ?? string.Empty;
            Entry = entry ?? string.Empty;
            Count = count;
            Required = required;
            Completed = completed;
            RewardClaimed = rewardClaimed;
            Revealed = revealed;
        }

        internal SideObjectiveSource Source { get; }

        /// <summary>Where the objective lives: the interior id for <see cref="SideObjectiveSource.WesleyInterior"/>,
        /// the trial key for <see cref="SideObjectiveSource.WesleyTrial"/>. Empty when the source could not
        /// place it, which reads as the generic copy.</summary>
        internal string Scope { get; }

        /// <summary>The source's own key for the chosen objective.</summary>
        internal string Entry { get; }

        /// <summary>Steps done. It can fall (Toy Store mask statues).</summary>
        internal int Count { get; }

        internal int Required { get; }

        internal bool Completed { get; }

        internal bool RewardClaimed { get; }

        /// <summary>A crewmate has sighted the objective or interacted with it (<c>SideObjectiveSight</c>).</summary>
        internal bool Revealed { get; }

        internal bool HasObjective => Source != SideObjectiveSource.None && !string.IsNullOrEmpty(Entry);

        internal bool SameObjective(in SideObjectiveSnapshot other)
        {
            return Source == other.Source
                && string.Equals(Scope ?? string.Empty, other.Scope ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(Entry ?? string.Empty, other.Entry ?? string.Empty, StringComparison.Ordinal);
        }

        internal SideObjectiveSnapshot WithRevealed(bool revealed)
        {
            return new SideObjectiveSnapshot(Source, Scope, Entry, Count, Required, Completed, RewardClaimed, revealed);
        }

        public bool Equals(SideObjectiveSnapshot other)
        {
            return SameObjective(other)
                && Count == other.Count
                && Required == other.Required
                && Completed == other.Completed
                && RewardClaimed == other.RewardClaimed
                && Revealed == other.Revealed;
        }

        public override bool Equals(object obj)
        {
            return obj is SideObjectiveSnapshot other && Equals(other);
        }

        public override int GetHashCode()
        {
            return StringComparer.Ordinal.GetHashCode(Entry ?? string.Empty) ^ (Count * 397) ^ (int)Source;
        }
    }

    /// <summary>
    /// #1195. Phase selection, the one card row per phase, and the reveal rules. Unity-free and
    /// Netcode-free: tests/ContractHudPresentationRegression links this file.
    /// </summary>
    internal static class SideObjectivePolicy
    {
        /// <summary>Stable row identities (#520): a count change never changes the key.</summary>
        internal const string ProgressKey = "wesley.side.progress";
        internal const string ClaimKey = "wesley.side.claim";
        internal const string RecoveredKey = "wesley.side.recovered";

        internal const string RecoveredText = "Reward recovered";

        /// <summary>The host reads the live source at most this often (4 a second).</summary>
        internal const float HostPollIntervalSeconds = 0.25f;

        /// <summary>A peer's local sight check runs at most this often (5 a second).</summary>
        internal const float SightCheckIntervalSeconds = 0.2f;

        /// <summary>A target farther than this from the camera is not sighted.</summary>
        internal const float SightRangeMeters = 25f;

        /// <summary>Line-of-sight probes per check, across all targets.</summary>
        internal const int MaxLinecastsPerCheck = 2;

        /// <summary>A client re-sends an unanswered sighting no sooner than this.</summary>
        internal const float SightingResendSeconds = 2f;

        internal static SideObjectivePhase SelectPhase(in SideObjectiveSnapshot snapshot)
        {
            if (!snapshot.HasObjective || !snapshot.Revealed)
                return SideObjectivePhase.Hidden;
            if (snapshot.RewardClaimed)
                return SideObjectivePhase.Recovered;
            if (snapshot.Completed)
                return SideObjectivePhase.ClaimReward;
            return SideObjectivePhase.Progress;
        }

        /// <summary>The one row for <paramref name="phase"/>; default for <see cref="SideObjectivePhase.Hidden"/>.</summary>
        internal static MoonContractObjective BuildRow(SideObjectivePhase phase, in SideObjectiveSnapshot snapshot, SideObjectiveCopy copy)
        {
            SideObjectiveTrialDefinition trial = phase == SideObjectivePhase.Progress && snapshot.Source == SideObjectiveSource.WesleyTrial
                ? SideObjectiveTrialTable.Find(snapshot.Entry)
                : null;
            return BuildRow(phase, snapshot, copy, trial);
        }

        internal static MoonContractObjective BuildRow(SideObjectivePhase phase, in SideObjectiveSnapshot snapshot, SideObjectiveCopy copy,
            SideObjectiveTrialDefinition trial)
        {
            copy = copy ?? SideObjectiveCopyTable.Fallback;
            switch (phase)
            {
                case SideObjectivePhase.Progress:
                    int required = Math.Max(snapshot.Required, 1);
                    string action = snapshot.Source == SideObjectiveSource.WesleyTrial
                        ? SideObjectiveTrialTable.ProgressLabel(trial, snapshot.Count, snapshot.Required)
                        : copy.Title;
                    return new MoonContractObjective(action, Math.Max(snapshot.Count, 0), required, key: ProgressKey);
                case SideObjectivePhase.ClaimReward:
                    return new MoonContractObjective(copy.ClaimVerb, 0, 1, key: ClaimKey);
                case SideObjectivePhase.Recovered:
                    return new MoonContractObjective(copy.RecoveredText, 1, 1, key: RecoveredKey);
                default:
                    return default;
            }
        }

        /// <summary>
        /// Host-side: folds a fresh source reading into what was last published. Only the reveal
        /// carries over, and only for the same objective: a reveal comes from a sighting or an
        /// interaction, never from the reading. Progress alone reveals nothing (#874 F8): a counter
        /// can start above zero (Toy Store masks spawn already turned), and it keeps counting while
        /// hidden so a later reveal shows the current count.
        /// </summary>
        internal static SideObjectiveSnapshot MergeHostReading(in SideObjectiveSnapshot published, in SideObjectiveSnapshot reading)
        {
            if (!reading.HasObjective)
                return SideObjectiveSnapshot.None;

            return reading.WithRevealed(published.Revealed && published.SameObjective(reading));
        }

        /// <summary>Host-side: a sighting counts only for the objective the host is publishing.</summary>
        internal static bool AcceptSighting(in SideObjectiveSnapshot published, SideObjectiveSource source, string scope, string entry)
        {
            return published.HasObjective
                && published.Source == source
                && string.Equals(published.Scope, scope ?? string.Empty, StringComparison.Ordinal)
                && string.Equals(published.Entry, entry ?? string.Empty, StringComparison.Ordinal);
        }

        /// <summary>A peer looks for the objective only while there is one and nobody has found it.</summary>
        internal static bool NeedsLocalSighting(in SideObjectiveSnapshot published)
        {
            return published.HasObjective && !published.Revealed;
        }
    }

    /// <summary>
    /// #1195. How a Wesley's Interiors controller reading becomes the neutral snapshot's
    /// <c>Completed</c> and <c>RewardClaimed</c>. Unity-free so the rules are pinned.
    /// </summary>
    internal static class SideObjectiveWesleyInteriorRules
    {
        /// <summary>
        /// Completed on the controller's own flag, on a full counter, or once the reward has
        /// spawned. The host spawns the reward the moment the counter fills, and some interiors
        /// (Art Gallery's codepad, Dilapidated Manor's key) spawn it without ever moving the
        /// counter, so a spawned reward is the one signal every interior shares.
        /// </summary>
        internal static bool Completed(bool objectiveCompleted, int count, int required, bool rewardSpawned)
        {
            return objectiveCompleted || (required > 0 && count >= required) || rewardSpawned;
        }

        /// <summary>
        /// Claimed once the case has opened or the reward item has been picked up. Opening the case
        /// disables the controller's trigger collider; that only reads as an opening when the
        /// collider was seen enabled first, so an interior whose collider starts disabled cannot
        /// skip the claim step. <paramref name="triggerColliderEnabled"/> is -1 when there is none.
        /// </summary>
        internal static bool RewardClaimed(bool triggerSeenEnabled, int triggerColliderEnabled, bool rewardTaken)
        {
            return (triggerSeenEnabled && triggerColliderEnabled == 0) || rewardTaken;
        }
    }

    /// <summary>
    /// #1195. Host-side bookkeeping for the attributed progression events: which steps, and whether
    /// the completion and the claim, have been credited for the current objective. A step is credited
    /// the first time the count reaches it and never again, so a count that falls and climbs back
    /// (Toy Store's mask statues) cannot pay the same step twice, and steps past the required total
    /// are not steps. The claim is never credited before the completion.
    /// </summary>
    internal sealed class SideObjectiveCreditLedger
    {
        private SideObjectiveSource _source;
        private string _scope = string.Empty;
        private string _entry = string.Empty;
        private int _stepsCredited;

        internal bool CompletionCredited { get; private set; }
        internal bool ClaimCredited { get; private set; }

        /// <summary>Points the ledger at an objective; a different one starts from nothing.</summary>
        internal void Begin(SideObjectiveSource source, string scope, string entry)
        {
            scope = scope ?? string.Empty;
            entry = entry ?? string.Empty;
            if (source == _source
                && string.Equals(scope, _scope, StringComparison.Ordinal)
                && string.Equals(entry, _entry, StringComparison.Ordinal))
            {
                return;
            }

            Reset();
            _source = source;
            _scope = scope;
            _entry = entry;
        }

        internal void Reset()
        {
            _source = SideObjectiveSource.None;
            _scope = string.Empty;
            _entry = string.Empty;
            _stepsCredited = 0;
            CompletionCredited = false;
            ClaimCredited = false;
        }

        /// <summary>The next step not yet credited, when <paramref name="count"/> has reached it; call
        /// until false to take every step a jump covered.</summary>
        internal bool TryTakeStep(int count, int required, out int step)
        {
            int reachable = Math.Min(count, required);
            if (_stepsCredited >= reachable)
            {
                step = 0;
                return false;
            }

            step = ++_stepsCredited;
            return true;
        }

        internal bool TryTakeCompletion()
        {
            if (CompletionCredited)
                return false;
            CompletionCredited = true;
            return true;
        }

        internal bool TryTakeClaim()
        {
            if (!CompletionCredited || ClaimCredited)
                return false;
            ClaimCredited = true;
            return true;
        }
    }

    /// <summary>
    /// #1195. Continuous-sight dwell: a target has to pass every check for
    /// <see cref="DwellSeconds"/> before it counts, so a glance through a doorway does not reveal.
    /// </summary>
    internal sealed class SideObjectiveSightDwell
    {
        internal const float DwellSeconds = 0.35f;

        private float _seenSince = -1f;

        /// <summary>Feeds one check's result; true once sight has been unbroken for the dwell.</summary>
        internal bool Observe(bool seen, float now)
        {
            if (!seen)
            {
                _seenSince = -1f;
                return false;
            }

            if (_seenSince < 0f)
                _seenSince = now;
            return now - _seenSince >= DwellSeconds;
        }

        internal void Reset()
        {
            _seenSince = -1f;
        }
    }

    /// <summary>
    /// #1195. Which two sight targets get the check's two linecasts: the two best aligned with the
    /// view centre, from different targets, one ray each. Ranking by alignment rather than distance
    /// stops one large occluded bounds beside the player from taking the whole budget while a prop
    /// in plain view sits at the centre of the screen, and splitting the rays across two targets
    /// stops any single one from starving the rest. A bounds that contains the eye is never picked:
    /// a ray from inside it proves nothing about the mesh.
    /// </summary>
    internal struct SideObjectiveSightPick
    {
        internal int Count { get; private set; }
        internal int FirstGroup { get; private set; }
        internal int SecondGroup { get; private set; }

        private float _firstAlignment;
        private float _firstSqrDistance;
        private float _secondAlignment;
        private float _secondSqrDistance;

        internal void Reset()
        {
            Count = 0;
            FirstGroup = 0;
            SecondGroup = 0;
        }

        /// <summary>
        /// Offers one frustum-passing target. <paramref name="alignment"/> is the cosine between the
        /// view direction and the direction to the target; <paramref name="group"/> identifies the
        /// target a bounds belongs to. Returns the slot it took (0 or 1; taking 0 moves the old first
        /// to 1 when it is another target), or -1.
        /// </summary>
        internal int Offer(int group, float alignment, float sqrDistance, bool containsEye, float maxSqrDistance)
        {
            if (containsEye || sqrDistance > maxSqrDistance || alignment <= 0f)
                return -1;

            if (Count == 0 || Beats(alignment, sqrDistance, _firstAlignment, _firstSqrDistance))
            {
                if (Count > 0 && group != FirstGroup)
                {
                    SecondGroup = FirstGroup;
                    _secondAlignment = _firstAlignment;
                    _secondSqrDistance = _firstSqrDistance;
                    Count = 2;
                }
                else if (Count == 2 && group == SecondGroup)
                {
                    Count = 1;
                }

                FirstGroup = group;
                _firstAlignment = alignment;
                _firstSqrDistance = sqrDistance;
                if (Count == 0)
                    Count = 1;
                return 0;
            }

            if (group == FirstGroup)
                return -1;

            if (Count == 1 || Beats(alignment, sqrDistance, _secondAlignment, _secondSqrDistance))
            {
                SecondGroup = group;
                _secondAlignment = alignment;
                _secondSqrDistance = sqrDistance;
                Count = 2;
                return 1;
            }

            return -1;
        }

        private static bool Beats(float alignment, float sqrDistance, float otherAlignment, float otherSqrDistance)
        {
            return alignment > otherAlignment || (alignment == otherAlignment && sqrDistance < otherSqrDistance);
        }
    }

    /// <summary>
    /// #1195. The values Contract S publishes, derived from the applied snapshot. The revision moves
    /// when a visible value or objective identity changes (even before reveal, so first sight can be
    /// distinguished from a late-join seed), and never goes backwards.
    /// </summary>
    internal sealed class SideObjectivePublication
    {
        private SideObjectiveSnapshot _publishedIdentity;
        internal SideObjectivePhase Phase { get; private set; }
        internal string Title { get; private set; } = string.Empty;
        internal MoonContractObjective Row { get; private set; }
        internal int Revision { get; private set; }

        /// <summary>Applies a snapshot; true when a visible value or objective identity changes.</summary>
        internal bool Apply(in SideObjectiveSnapshot snapshot)
        {
            SideObjectivePhase phase = SideObjectivePolicy.SelectPhase(snapshot);
            string title = string.Empty;
            MoonContractObjective row = default;
            if (phase != SideObjectivePhase.Hidden)
            {
                SideObjectiveTrialDefinition trial = snapshot.Source == SideObjectiveSource.WesleyTrial
                    ? SideObjectiveTrialTable.Find(snapshot.Entry)
                    : null;
                SideObjectiveCopy copy = snapshot.Source == SideObjectiveSource.WesleyTrial
                    ? (trial != null ? trial.Copy : SideObjectiveTrialTable.Fallback)
                    : SideObjectiveCopyTable.Lookup(snapshot.Scope, snapshot.Entry);
                title = snapshot.Source == SideObjectiveSource.WesleyTrial
                    ? (trial != null ? "TRIAL" : string.Empty)
                    : SideObjectiveCopyTable.DisplayName(snapshot.Scope);
                row = SideObjectivePolicy.BuildRow(phase, snapshot, copy, trial);
            }

            bool sameObjective = snapshot.HasObjective
                ? _publishedIdentity.SameObjective(snapshot)
                : !_publishedIdentity.HasObjective;
            if (phase == Phase && sameObjective
                && string.Equals(title, Title, StringComparison.Ordinal) && SameRow(row, Row))
                return false;

            _publishedIdentity = snapshot.HasObjective ? snapshot : SideObjectiveSnapshot.None;
            Phase = phase;
            Title = title;
            Row = row;
            Revision++;
            return true;
        }

        internal bool Reset()
        {
            return Apply(SideObjectiveSnapshot.None);
        }

        private static bool SameRow(in MoonContractObjective a, in MoonContractObjective b)
        {
            return string.Equals(a.Text, b.Text, StringComparison.Ordinal)
                && a.Complete == b.Complete
                && a.Total == b.Total
                && a.Failed == b.Failed
                && string.Equals(a.Key, b.Key, StringComparison.Ordinal);
        }
    }
}
