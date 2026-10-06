using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #944 S11 U6: the two volume rules the Stage 11 diagnostic pass applies, extracted so they are
    /// pure and testable. Neither removes an event: both report how many occurrences they folded
    /// into the line they do emit, so a reader can still recover the rate from the log.
    ///
    /// Unity-free on purpose. These types are linked into BlacksiteReactiveRegression, which has no
    /// Unity reference, so the rule the runtime applies is pinned rather than restated in a fixture.
    /// </summary>
    internal sealed class BlacksiteChangeGate
    {
        private readonly Dictionary<(int, string), string> _last = new Dictionary<(int, string), string>();
        private readonly Dictionary<(int, string), int> _folded = new Dictionary<(int, string), int>();

        /// <summary>
        /// True when <paramref name="state"/> differs from the last state reported for this key, in
        /// which case <paramref name="foldedSinceLast"/> is how many identical reports were folded
        /// since that line and the gate re-arms. False otherwise, and the fold count grows.
        /// </summary>
        internal bool ShouldReport(int key, string state, out int foldedSinceLast)
            => ShouldReport(key, null, state, out foldedSinceLast);

        /// <summary>
        /// #951 S11H U3 (b): the same rule against a two-part key, (numeric key, sub key).
        ///
        /// A gate keyed on the member alone folds nothing when one member reports about more than
        /// one subject in the same tick: the (since deleted, #1305 H) BehaviourHold report offered
        /// Hazard first and then Combat on every think, so each report was a change of the member's
        /// last state and each one printed. Test 3 carried 11,622 such lines of which 11,558 said
        /// repeats=0 while the two refusals were in fact standing unchanged for minutes. Folding
        /// each subject against its own last line is what the gate was written to do; the subject
        /// simply was not part of the key. The single-key overload above passes a null sub key, so
        /// every other caller keeps exactly the behaviour it has.
        /// </summary>
        internal bool ShouldReport(int key, string subKey, string state, out int foldedSinceLast)
        {
            var composite = (key, subKey);
            _folded.TryGetValue(composite, out foldedSinceLast);
            if (_last.TryGetValue(composite, out string previous) && string.Equals(previous, state, StringComparison.Ordinal))
            {
                _folded[composite] = foldedSinceLast + 1;
                foldedSinceLast = 0;
                return false;
            }

            _last[composite] = state;
            _folded[composite] = 0;
            return true;
        }

        /// <summary>Keys are member indices, and member indices are reused by the next deployment.
        /// Both halves of the key go, so a reused index inherits no sub key either.</summary>
        internal void Reset()
        {
            _last.Clear();
            _folded.Clear();
        }
    }

    /// <summary>
    /// #944 S11 U6: at most one line per key per window, with the suppressed occurrences counted.
    /// The clock is passed in, never read, so the fixture drives it the way every other Blacksite
    /// fixture drives its own clock.
    /// </summary>
    internal sealed class BlacksiteWindowGate
    {
        private readonly float _intervalSeconds;
        private readonly Dictionary<int, float> _nextAt = new Dictionary<int, float>();
        private readonly Dictionary<int, int> _folded = new Dictionary<int, int>();

        internal BlacksiteWindowGate(float intervalSeconds)
        {
            _intervalSeconds = intervalSeconds;
        }

        internal bool ShouldReport(int key, float now, out int foldedSinceLast)
        {
            _folded.TryGetValue(key, out foldedSinceLast);
            if (_nextAt.TryGetValue(key, out float nextAt) && now < nextAt)
            {
                _folded[key] = foldedSinceLast + 1;
                foldedSinceLast = 0;
                return false;
            }

            _nextAt[key] = now + _intervalSeconds;
            _folded[key] = 0;
            return true;
        }

        internal void Reset()
        {
            _nextAt.Clear();
            _folded.Clear();
        }
    }

    /// <summary>
    /// #874 G5: per-member detail capture window for high-rate diagnostic lines.
    ///
    /// A failure transition (stall owner becoming non-empty, a non-Issued motion request)
    /// opens a short window during which the full gait, motion heartbeat and motion-request
    /// repeat lines are emitted for that member only. Re-opening while already open extends
    /// to the full configured seconds, capped at three times the configured value; after the
    /// cap the member must be quiet for one full window before the gate re-arms.
    ///
    /// Allocation-free after construction: every Dictionary key is an int and every value is
    /// a float. The class is Unity-free by design so the cap rule is pin-able by
    /// BlacksiteReactiveRegression.
    /// </summary>
    internal sealed class BlacksiteDetailCapture
    {
        private readonly Dictionary<int, float> _windowClosesAt = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _windowOpenedAt = new Dictionary<int, float>();
        private readonly Dictionary<int, float> _capHitAt = new Dictionary<int, float>();
        private float _configuredSeconds;

        internal float ConfiguredSeconds => _configuredSeconds;

        internal void Configure(float seconds)
        {
            _configuredSeconds = seconds < 0f ? 0f : seconds;
        }

        /// <summary>
        /// True while the configured window is open for this member. A PURE READ: it never
        /// retires an expired window, because <see cref="TryCloseExpired"/> is the one site
        /// that retires it and logs the close. If this removed the record, whichever caller
        /// happened to ask first in a frame would swallow the close line.
        /// </summary>
        internal bool IsOpen(int memberIndex, float now)
        {
            if (_configuredSeconds <= 0f) return false;
            return _windowClosesAt.TryGetValue(memberIndex, out float closesAt) && now < closesAt;
        }

        /// <summary>
        /// Open or extend a detail window for this member. Logs the open reason only on the
        /// first open of a cycle; extensions are silent except at the cap.
        /// Returns true when the window is currently open (after this call).
        /// </summary>
        internal bool Open(int memberIndex, string reason, float now,
            out bool firstOpen, out string capEvent)
        {
            firstOpen = false;
            capEvent = null;
            if (_configuredSeconds <= 0f) return false;

            // Still inside the quiet gap the cap imposes: no new window is granted. The answer
            // is whether the capped window is still running, never whether its record survives,
            // so a retired and an un-retired record read the same.
            if (_capHitAt.TryGetValue(memberIndex, out float hitAt)
                && now - hitAt < _configuredSeconds)
                return IsOpen(memberIndex, now);

            bool alreadyOpen = _windowClosesAt.TryGetValue(memberIndex, out float closesAt)
                && now < closesAt;
            if (!alreadyOpen)
            {
                // Fresh open.
                _windowClosesAt[memberIndex] = now + _configuredSeconds;
                _windowOpenedAt[memberIndex] = now;
                _capHitAt.Remove(memberIndex);
                firstOpen = true;
                return true;
            }

            // Extend.
            float openedAt = _windowOpenedAt.TryGetValue(memberIndex, out float oa)
                ? oa : now;
            float extended = now + _configuredSeconds;
            float capAt = openedAt + _configuredSeconds * 3f;
            if (extended > capAt)
            {
                extended = capAt;
                _capHitAt[memberIndex] = now;
                capEvent = "capped";
            }
            _windowClosesAt[memberIndex] = extended;
            return true;
        }

        /// <summary>
        /// Call once per frame per member (in TraceMotion which already runs that way).
        /// Returns true when a window that was open for this member has just expired,
        /// with the duration it was open so the caller can log a close line.
        /// Allocation-free in the false path (the common case).
        /// </summary>
        internal bool TryCloseExpired(int memberIndex, float now, out float duration)
        {
            duration = 0f;
            if (_configuredSeconds <= 0f) return false;
            if (!_windowClosesAt.TryGetValue(memberIndex, out float closesAt)) return false;
            if (now < closesAt) return false;

            float openedAt = _windowOpenedAt.TryGetValue(memberIndex, out float oa)
                ? oa : closesAt - _configuredSeconds;
            duration = now - openedAt;
            _windowClosesAt.Remove(memberIndex);
            _windowOpenedAt.Remove(memberIndex);
            return true;
        }

        internal void Reset()
        {
            _windowClosesAt.Clear();
            _windowOpenedAt.Clear();
            _capHitAt.Clear();
        }
    }
}
