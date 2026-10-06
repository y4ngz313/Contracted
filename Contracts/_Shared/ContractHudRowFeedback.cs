using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>#1196. The cue a card row plays when its snapshot changes between two polls.</summary>
    internal enum ContractHudRowCue
    {
        None = 0,
        Progress = 1,
        Complete = 2,
        Fail = 3
    }

    /// <summary>
    /// #1196. Pure row-feedback policy: which cue a row plays and how each cue moves over time.
    /// Free of Unity and Netcode types so ContractHudPresentationRegression links it directly;
    /// ContractHudRowCueRenderer turns these numbers into TMP vertex edits.
    ///
    /// <para>Every cue is driven by an event and ends in a still row: a row is identified by its
    /// #520 stable key, so a countdown or status-context change under the same key and the same
    /// counter never cues. Cue A is the accent sweep on progress and the card-edge glow on
    /// completion or failure, cue B rolls the n/m counter on every change (decreases included),
    /// cue C pulses the row's key word on the shared <see cref="ContractHudWordPulse"/> clock.</para>
    /// </summary>
    internal static class ContractHudRowFeedback
    {
        /// <summary>Cue A on progress: one pass of the accent sweep across the row.</summary>
        internal const float SweepSeconds = .45f;
        /// <summary>Half-width of the sweep's bright band, as a fraction of the row width.</summary>
        internal const float SweepBand = .22f;
        /// <summary>Cue A on completion or failure: one rise and fall of the card-edge glow.</summary>
        internal const float GlowSeconds = .6f;
        /// <summary>Cue B: the changed half of the n/m counter rolls into place.</summary>
        internal const float RollSeconds = .3f;
        /// <summary>Cue C on an event: the key word pulses across one and a half shared periods.</summary>
        internal const float WordEventSeconds = ContractHudWordPulse.PeriodSeconds * 1.5f;
        /// <summary>The longest any event cue draws; the tracker is idle this long after its last event.</summary>
        internal const float LongestCueSeconds = WordEventSeconds;

        /// <summary>Peak blend of the sweep band toward the theme's white.</summary>
        internal const float SweepStrength = .6f;
        /// <summary>Peak blend of the card edge toward the glow colour.</summary>
        internal const float GlowStrength = .75f;
        /// <summary>Blend of the key word toward the theme's white at the brightest point of the pulse.</summary>
        internal const float WordBrighten = .55f;
        /// <summary>Fraction of the key word's brightness removed at the darkest point of the pulse.</summary>
        internal const float WordDim = .45f;
        /// <summary>How far the rolling digits start from their resting line, as a fraction of the font size.</summary>
        internal const float RollTravel = .55f;

        /// <summary>
        /// The cue for one row, from the snapshot the card saw last and the one it sees now. Both
        /// snapshots belong to the same stable key; first sightings are ContractHudRowFeedbackTracker's call.
        /// </summary>
        internal static ContractHudRowCue Decide(in MoonContractObjective previous, in MoonContractObjective current)
        {
            if (current.IsFailed)
                return previous.IsFailed ? ContractHudRowCue.None : ContractHudRowCue.Fail;
            if (previous.IsFailed)
                return ContractHudRowCue.None;
            if (current.IsComplete && !previous.IsComplete)
                return ContractHudRowCue.Complete;
            if (current.Complete != previous.Complete || current.Total != previous.Total)
                return ContractHudRowCue.Progress;
            return ContractHudRowCue.None;
        }

        /// <summary>+1 when the counter went up, -1 when it went down, 0 when it did not move.
        /// The completed count leads; a changed total alone rolls in its own direction.</summary>
        internal static int CounterDirection(in MoonContractObjective previous, in MoonContractObjective current)
        {
            int complete = Math.Sign(current.Complete - previous.Complete);
            return complete != 0 ? complete : Math.Sign(current.Total - previous.Total);
        }

        /// <summary>Whether the card renders an n/m counter for this row (ContractHUDManager.FormatObjectiveLine).</summary>
        internal static bool ShowsCounter(in MoonContractObjective row)
            => row.Kind == MoonContractObjectiveKind.Action && row.Total > 1;

        /// <summary>
        /// Cue A. Brightness of the sweep band at <paramref name="position"/> (0 = row start, 1 = row
        /// end), <paramref name="age"/> seconds into the sweep. The band enters from before the row and
        /// leaves past its end, so the first and last frames are the resting row.
        /// </summary>
        internal static float SweepWeight(float age, float position)
        {
            if (!(age >= 0f && age < SweepSeconds))
                return 0f;
            float center = -SweepBand + age / SweepSeconds * (1f + 2f * SweepBand);
            float distance = Math.Abs(position - center);
            return distance >= SweepBand ? 0f : 1f - distance / SweepBand;
        }

        /// <summary>Cue A. Card-edge glow, 0 at rest, rising to 1 halfway and back to 0.</summary>
        internal static float GlowLevel(float age) => Window(age, GlowSeconds);

        /// <summary>Cue B. 0 when the digits start rolling, 1 once they are at rest (and before any roll).</summary>
        internal static float RollProgress(float age)
        {
            if (!(age >= 0f && age < RollSeconds))
                return 1f;
            float remaining = 1f - age / RollSeconds;
            return 1f - remaining * remaining * remaining;
        }

        /// <summary>Cue C on an event. Envelope of the word pulse: 0 at the event, 1 midway, 0 at the end.</summary>
        internal static float WordEnvelope(float age) => Window(age, WordEventSeconds);

        /// <summary>
        /// Cue C. The key word's signed modulation: +1 fully brightened, -1 fully dimmed, 0 at rest. The
        /// phase always comes from the shared clock; an external request holds the envelope open, an
        /// event opens it briefly.
        /// </summary>
        internal static float WordModulation(float now, float eventStart, bool requested)
        {
            float envelope = requested ? 1f : WordEnvelope(now - eventStart);
            return envelope <= 0f ? 0f : envelope * (2f * ContractHudWordPulse.Level(now) - 1f);
        }

        /// <summary>
        /// The start time a windowed cue uses when another event arrives while it still draws. A cue
        /// in its rising half keeps going; one in its falling half is mirrored onto the rising half, so
        /// its level is continuous and it peaks again. A finished (or never-started) cue restarts now.
        /// </summary>
        internal static float Restart(float previousStart, float now, float duration)
        {
            float age = now - previousStart;
            if (!(age >= 0f && age < duration))
                return now;
            return age <= duration * .5f ? previousStart : now - (duration - age);
        }

        /// <summary>The character ContractHudObjectivePresentation.Escape shows in place of <paramref name="c"/>.</summary>
        internal static char EscapedChar(char c) => c == '<' ? '\u2039' : c == '>' ? '\u203a' : c;

        private static float Window(float age, float duration)
        {
            if (!(age >= 0f && age < duration))
                return 0f;
            return (float)Math.Sin(Math.PI * age / duration);
        }
    }

    /// <summary>#1196. One row the tracker has seen, with the start time of each of its cues.</summary>
    internal struct ContractHudRowState
    {
        /// <summary>The last snapshot seen for this key.</summary>
        internal MoonContractObjective Last;
        /// <summary>The most recent cue; it does not reset when the cue ends.</summary>
        internal ContractHudRowCue Cue;
        internal float SweepAt;
        internal float GlowAt;
        internal float WordAt;
        internal float RollAt;
        /// <summary>+1 rolls new digits up into place, -1 rolls them down.</summary>
        internal int RollDirection;
        /// <summary>The completed count changed, so the numerator rolls.</summary>
        internal bool RollComplete;
        /// <summary>The required count changed, so the denominator rolls.</summary>
        internal bool RollTotal;
        internal bool Seen;

        internal string Key => Last.SignatureKey;
    }

    /// <summary>
    /// #1196. Row snapshots keyed by <see cref="MoonContractObjective.SignatureKey"/>. Each
    /// <see cref="Observe"/> compares the new rows with the previous ones and stamps the cues that
    /// start. Rows that leave the snapshot are forgotten. Steady-state polling allocates nothing.
    /// </summary>
    internal sealed class ContractHudRowFeedbackTracker
    {
        private const float Never = float.NegativeInfinity;
        private readonly List<ContractHudRowState> _rows = new List<ContractHudRowState>(8);

        internal int Count => _rows.Count;
        internal ContractHudRowState this[int index] => _rows[index];

        /// <summary>Changes whenever a row is added, removed or reworded, so a renderer's row map is stale.</summary>
        internal int Revision { get; private set; }

        /// <summary>Unscaled time until which some event cue may still draw.</summary>
        internal float ActiveUntil { get; private set; } = Never;
        /// <summary>True only on the poll that first observes a row becoming complete, never
        /// on a seed, repeated poll or glow frame.</summary>
        internal bool CompletedThisObserve { get; private set; }

        /// <summary>
        /// Records a snapshot. A seed pass (the card's reveal, a late join) records without cueing. A
        /// key seen for the first time does not cue either, its arrival belongs to the typewriter,
        /// unless it arrives failed: providers emit a written-off or lost row only once it happens,
        /// so that row is the failure itself.
        /// </summary>
        internal void Observe(IReadOnlyList<MoonContractObjective> rows, float now, bool seedOnly)
        {
            CompletedThisObserve = false;
            for (int i = 0; i < _rows.Count; i++)
            {
                ContractHudRowState state = _rows[i];
                state.Seen = false;
                _rows[i] = state;
            }

            int count = rows != null ? rows.Count : 0;
            for (int i = 0; i < count; i++)
            {
                MoonContractObjective current = rows[i];
                int index = IndexOf(current.SignatureKey);
                if (index < 0)
                {
                    var added = new ContractHudRowState
                    {
                        Last = current,
                        SweepAt = Never,
                        GlowAt = Never,
                        WordAt = Never,
                        RollAt = Never,
                        Seen = true
                    };
                    if (!seedOnly && current.IsFailed)
                        Start(ref added, ContractHudRowCue.Fail, now);
                    _rows.Add(added);
                    Revision++;
                    continue;
                }

                ContractHudRowState row = _rows[index];
                // A provider that repeats a key in one snapshot: the first row owns the identity.
                if (row.Seen)
                    continue;
                if (!seedOnly)
                    Stamp(ref row, current, now);
                if (!string.Equals(row.Last.Text, current.Text, StringComparison.Ordinal))
                    Revision++;
                row.Last = current;
                row.Seen = true;
                _rows[index] = row;
            }

            for (int i = _rows.Count - 1; i >= 0; i--)
            {
                if (_rows[i].Seen)
                    continue;
                _rows.RemoveAt(i);
                Revision++;
            }
        }

        /// <summary>Starts only the accent sweep for an already-seeded side row after its first reveal
        /// finishes typing. No synthetic count, counter roll, word pulse or completion glow.</summary>
        internal bool PlayRevealSweep(string key, float now)
        {
            int index = IndexOf(key);
            if (index < 0)
                return false;
            ContractHudRowState row = _rows[index];
            row.Cue = ContractHudRowCue.Progress;
            row.SweepAt = now;
            _rows[index] = row;
            float until = now + ContractHudRowFeedback.SweepSeconds;
            if (until > ActiveUntil)
                ActiveUntil = until;
            return true;
        }

        /// <summary>
        /// True while an event cue can still draw, or while a <see cref="ContractHudWordPulse"/>
        /// request targets one of the tracked rows. Also drops expired pulse requests. With neither
        /// a cue nor a request, this is a time compare, an empty-registry check and one early-out
        /// registry check per tracked row.
        /// </summary>
        internal bool IsAnimating(float now)
        {
            ContractHudWordPulse.Prune(now);
            if (now < ActiveUntil)
                return true;
            for (int i = 0; i < _rows.Count; i++)
            {
                if (ContractHudWordPulse.IsActive(_rows[i].Key, now))
                    return true;
            }
            return false;
        }

        internal void Clear()
        {
            _rows.Clear();
            CompletedThisObserve = false;
            ActiveUntil = Never;
            Revision++;
        }

        private void Stamp(ref ContractHudRowState row, in MoonContractObjective current, float now)
        {
            ContractHudRowCue cue = ContractHudRowFeedback.Decide(row.Last, current);
            if (cue == ContractHudRowCue.None)
                return;

            Start(ref row, cue, now);

            int direction = ContractHudRowFeedback.CounterDirection(row.Last, current);
            if (direction != 0 && ContractHudRowFeedback.ShowsCounter(current))
            {
                row.RollAt = now;
                row.RollDirection = direction;
                row.RollComplete = row.Last.Complete != current.Complete;
                row.RollTotal = row.Last.Total != current.Total;
            }
        }

        private void Start(ref ContractHudRowState row, ContractHudRowCue cue, float now)
        {
            if (cue == ContractHudRowCue.Complete)
                CompletedThisObserve = true;
            row.Cue = cue;
            if (cue == ContractHudRowCue.Progress)
                row.SweepAt = now;
            else
                row.GlowAt = ContractHudRowFeedback.Restart(row.GlowAt, now, ContractHudRowFeedback.GlowSeconds);
            row.WordAt = ContractHudRowFeedback.Restart(row.WordAt, now, ContractHudRowFeedback.WordEventSeconds);

            float until = now + ContractHudRowFeedback.LongestCueSeconds;
            if (until > ActiveUntil)
                ActiveUntil = until;
        }

        private int IndexOf(string key)
        {
            for (int i = 0; i < _rows.Count; i++)
            {
                if (string.Equals(_rows[i].Key, key, StringComparison.Ordinal))
                    return i;
            }
            return -1;
        }
    }

    /// <summary>
    /// #1196 cue C, the shared word-pulse primitive. One unscaled phase clock for every consumer,
    /// so the card's key word and anything blinking in step with it (the #1197 pity-timer outline)
    /// agree without talking to each other. External requests pulse the key word of the card row
    /// whose <see cref="MoonContractObjective.SignatureKey"/> matches for as long as they last.
    /// Presentation only: nothing here is networked, and the HUD clears it on every hide.
    /// </summary>
    internal static class ContractHudWordPulse
    {
        internal const float PeriodSeconds = 1f;

        private const string ContextSeparator = " \u00B7 ";
        private static readonly string[] FunctionWords =
        {
            "the", "a", "an", "to", "in", "into", "onto", "on", "at", "from", "with", "via",
            "for", "of", "and", "or", "by", "up", "out", "back", "inside", "outside"
        };
        private static readonly Dictionary<string, float> _requests = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly List<string> _expiredScratch = new List<string>(4);
        // The latest unscaled time any query reported. Begin has no clock of its own, so it uses this
        // to refuse a request that has already expired and to drop the ones that have since.
        private static float _clock = float.NegativeInfinity;

        /// <summary>0 = darkest, 1 = brightest. A pure function of the unscaled clock, so every consumer is in phase.</summary>
        internal static float Level(float unscaledTime)
        {
            double phase = unscaledTime / (double)PeriodSeconds;
            phase -= Math.Floor(phase);
            return (float)(.5 - .5 * Math.Cos(2.0 * Math.PI * phase));
        }

        /// <summary>Level(t) &gt;= 0.5f. The #1197 outline toggles on this so it blinks in step with the word.</summary>
        internal static bool IsLit(float unscaledTime) => Level(unscaledTime) >= .5f;

        /// <summary>
        /// Pulses the key word of the row with this key until the given unscaled time. A repeat replaces
        /// the deadline. Requests that have expired by the latest known time are dropped here, so
        /// distinct keys cannot pile up between queries.
        /// </summary>
        internal static void Begin(string signatureKey, float untilUnscaledTime)
        {
            if (signatureKey == null)
                return;
            Prune(_clock);
            if (untilUnscaledTime <= _clock)
            {
                _requests.Remove(signatureKey);
                return;
            }
            _requests[signatureKey] = untilUnscaledTime;
        }

        internal static void End(string signatureKey)
        {
            if (signatureKey != null)
                _requests.Remove(signatureKey);
        }

        /// <summary>True while a request for this key has not reached its deadline. An expired request is dropped here.</summary>
        internal static bool IsActive(string signatureKey, float unscaledTime)
        {
            if (unscaledTime > _clock)
                _clock = unscaledTime;
            if (_requests.Count == 0 || signatureKey == null || !_requests.TryGetValue(signatureKey, out float until))
                return false;
            if (unscaledTime < until)
                return true;
            _requests.Remove(signatureKey);
            return false;
        }

        /// <summary>Round reset and lobby change.</summary>
        internal static void Clear() => _requests.Clear();

        /// <summary>Drops every request whose deadline has passed. Allocation-free; the row tracker calls it each tick.</summary>
        internal static void Prune(float unscaledTime)
        {
            if (unscaledTime > _clock)
                _clock = unscaledTime;
            if (_requests.Count == 0)
                return;
            _expiredScratch.Clear();
            foreach (var request in _requests)
            {
                if (request.Value <= _clock)
                    _expiredScratch.Add(request.Key);
            }
            for (int i = 0; i < _expiredScratch.Count; i++)
                _requests.Remove(_expiredScratch[i]);
            _expiredScratch.Clear();
        }

        /// <summary>Live requests held; lets the regression suite prove expired ones do not accumulate.</summary>
        internal static int RequestCount => _requests.Count;

        /// <summary>
        /// Span of the row's key word inside its visible (tag-free) text; excludes the n/m counter and
        /// any " \u00B7 status" context. The key word is the head of the verb's object: the leading
        /// verb and any particles, prepositions or articles after it are skipped, then the last word
        /// before the next function word is taken ("Locate the breaker boxes" -&gt; "boxes", "Return
        /// the cart to the Company Ship" -&gt; "cart"). A row with no object uses its first word. The
        /// card's invisible checkbox marker ([ ], [X] or [!]) is skipped, and words need a letter.
        /// </summary>
        internal static bool TryGetKeyWord(string plainRowText, out int start, out int length)
        {
            start = 0;
            length = 0;
            if (string.IsNullOrEmpty(plainRowText))
                return false;

            int begin = 0;
            if (plainRowText.Length >= 3 && plainRowText[0] == '[' && plainRowText[2] == ']'
                && (plainRowText[1] == ' ' || plainRowText[1] == 'X' || plainRowText[1] == '!'))
                begin = 3;
            int end = TrimCounter(plainRowText, begin, plainRowText.Length);
            int context = plainRowText.IndexOf(ContextSeparator, begin, end - begin, StringComparison.Ordinal);
            if (context >= 0)
                end = context;

            int verbStart = -1, verbLength = 0, headStart = -1, headLength = 0;
            // 0: expecting the verb, 1: skipping function words after it, 2: inside the object.
            int stage = 0;
            int i = begin;
            while (i < end)
            {
                if (!IsWordChar(plainRowText, i, begin, end))
                {
                    i++;
                    continue;
                }

                int tokenStart = i;
                bool hasLetter = false;
                while (i < end && IsWordChar(plainRowText, i, begin, end))
                {
                    hasLetter |= char.IsLetter(plainRowText[i]);
                    i++;
                }
                if (!hasLetter)
                    continue;

                int tokenLength = i - tokenStart;
                bool function = IsFunctionWord(plainRowText, tokenStart, tokenLength);
                if (stage == 0)
                {
                    verbStart = tokenStart;
                    verbLength = tokenLength;
                    stage = 1;
                }
                else if (function)
                {
                    if (stage == 2)
                        break;
                }
                else
                {
                    headStart = tokenStart;
                    headLength = tokenLength;
                    stage = 2;
                }
            }

            if (headStart >= 0)
            {
                start = headStart;
                length = headLength;
                return true;
            }
            if (verbStart >= 0)
            {
                start = verbStart;
                length = verbLength;
                return true;
            }
            return false;
        }

        /// <summary>The end of the text without a trailing " n/m" counter.</summary>
        private static int TrimCounter(string text, int begin, int end)
        {
            int denominator = end;
            while (denominator > begin && char.IsDigit(text[denominator - 1]))
                denominator--;
            if (denominator == end || denominator <= begin || text[denominator - 1] != '/')
                return end;
            int slash = denominator - 1;
            int numerator = slash;
            while (numerator > begin && char.IsDigit(text[numerator - 1]))
                numerator--;
            if (numerator == slash || numerator <= begin || text[numerator - 1] != ' ')
                return end;
            return numerator - 1;
        }

        /// <summary>Letters and digits, plus an apostrophe, hyphen or dot joining two of them ("survey.db").</summary>
        private static bool IsWordChar(string text, int index, int begin, int end)
        {
            char c = text[index];
            if (char.IsLetterOrDigit(c))
                return true;
            if (c != '\'' && c != '\u2019' && c != '-' && c != '.')
                return false;
            return index > begin && index + 1 < end
                && char.IsLetterOrDigit(text[index - 1]) && char.IsLetterOrDigit(text[index + 1]);
        }

        private static bool IsFunctionWord(string text, int start, int length)
        {
            for (int i = 0; i < FunctionWords.Length; i++)
            {
                string word = FunctionWords[i];
                if (word.Length == length && string.Compare(text, start, word, 0, length, StringComparison.OrdinalIgnoreCase) == 0)
                    return true;
            }
            return false;
        }
    }
}
