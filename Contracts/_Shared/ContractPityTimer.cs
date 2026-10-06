using System;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>#1197. Where the objective pity timer is in its cycle.</summary>
    internal enum ContractPityTimerPhase
    {
        /// <summary>No objective is being tracked.</summary>
        Idle,
        /// <summary>Counting toward the first blink. Holds (does not reset) while the gates are closed.</summary>
        Counting,
        /// <summary>The outline and the card word pulse for <see cref="ContractPityTimerSettings.BlinkSeconds"/>.</summary>
        Blinking,
        /// <summary>Counting toward the next blink. Only reached when a repeat interval is set.</summary>
        Waiting,
        /// <summary>The one blink has played and no repeat is set. Stays until the objective is found or changes.</summary>
        Done,
    }

    /// <summary>#1197. What one tick learned about the current contract's locate row.</summary>
    internal enum ContractPityReading
    {
        /// <summary>The row was not read this tick (between samples, or the player is outside or
        /// dead and nothing is sampled). The last reading stands.</summary>
        NotSampled,
        /// <summary>Read: there is no unfound locate objective. The contract has none, or it was
        /// found, captured, finished or failed. Resets the timer.</summary>
        NoObjective,
        /// <summary>Read: an unfound locate objective, but none of its targets is inside the
        /// facility. Holds the count.</summary>
        TargetOutside,
        /// <summary>Read: an unfound locate objective with a target inside the facility. Counts.</summary>
        TargetInside,
    }

    /// <summary>#1197. The three config values the timer reads, cleaned once per tick.</summary>
    internal readonly struct ContractPityTimerSettings
    {
        internal const float MinBlinkSeconds = 5f;
        internal const float MaxBlinkSeconds = 8f;

        /// <summary>Seconds spent inside the facility before the first blink. Never negative.</summary>
        internal readonly float DelaySeconds;
        /// <summary>Length of one blink window, clamped to 5-8 s.</summary>
        internal readonly float BlinkSeconds;
        /// <summary>Seconds inside between the end of one blink and the next. 0 means the blink plays once.</summary>
        internal readonly float RepeatSeconds;

        internal ContractPityTimerSettings(float delaySeconds, float blinkSeconds, float repeatSeconds)
        {
            DelaySeconds = NonNegative(delaySeconds);
            BlinkSeconds = float.IsNaN(blinkSeconds)
                ? MinBlinkSeconds
                : Math.Min(MaxBlinkSeconds, Math.Max(MinBlinkSeconds, blinkSeconds));
            RepeatSeconds = NonNegative(repeatSeconds);
        }

        private static float NonNegative(float value) => float.IsNaN(value) || value < 0f ? 0f : value;
    }

    /// <summary>#1197. One tick's worth of what the local client can see.</summary>
    internal struct ContractPityTimerInput
    {
        internal float DeltaSeconds;
        internal bool Enabled;
        internal bool LocalPlayerAlive;
        internal bool LocalPlayerInside;
        internal ContractPityReading Reading;
        /// <summary>The unfound row's key with <see cref="ContractPityReading.TargetOutside"/> or
        /// <see cref="ContractPityReading.TargetInside"/>; ignored otherwise.</summary>
        internal string ObjectiveKey;
        /// <summary>Read this tick: the tracked row made progress (one of Blackout's breakers was found)
        /// while it stays unfound. Resets the count like a found objective.</summary>
        internal bool ObjectiveFound;
    }

    /// <summary>
    /// #1197. The objective pity timer as a pure state machine, free of Unity so
    /// ContractHudPresentationRegression drives it directly. Local to one client: it reads only
    /// what the caller measured and sends nothing.
    ///
    /// <para>It counts only while the local player is alive, inside the facility and the last
    /// reading was an unfound locate objective with a target inside. A tick that did not read the
    /// row keeps the last reading, so the count runs between samples and holds while the player is
    /// outside or dead. Leaving a gate holds the count; it never resets it. A reading of no unfound
    /// objective (found, captured, finished, failed, or no contract), progress on the tracked row, a
    /// different objective, or the timer being disabled resets it to
    /// <see cref="ContractPityTimerPhase.Idle"/>. Once the delay is reached it blinks for the blink
    /// window, then either waits the repeat interval and blinks again or, with no repeat, is done
    /// until the objective is found or changes. A blink that loses a gate (death, leaving the
    /// facility, the target leaving the facility) ends early and counts as played.</para>
    /// </summary>
    internal sealed class ContractPityTimer
    {
        private float _elapsed;
        private float _blinkRemaining;
        /// <summary>The last reading: the unfound row's key, and whether a target of it is inside.</summary>
        private string _readingKey;
        private bool _readingInside;

        internal ContractPityTimerPhase Phase { get; private set; }

        /// <summary>The tracked objective's row key; null while idle.</summary>
        internal string Key { get; private set; }

        /// <summary>Seconds counted toward the next blink, in Counting and Waiting.</summary>
        internal float Elapsed => _elapsed;

        /// <summary>Seconds left in the current blink window; 0 outside Blinking.</summary>
        internal float BlinkRemaining => _blinkRemaining;

        /// <summary>Incremented every time a blink window opens, so a caller can tell a new window from the same one.</summary>
        internal int BlinkSerial { get; private set; }

        internal ContractPityTimerPhase Tick(in ContractPityTimerInput input, in ContractPityTimerSettings settings)
        {
            if (!input.Enabled)
            {
                Reset();
                return Phase;
            }

            if (input.Reading != ContractPityReading.NotSampled && !ApplyReading(input))
                return Phase;

            float dt = float.IsNaN(input.DeltaSeconds) || input.DeltaSeconds < 0f ? 0f : input.DeltaSeconds;
            bool counts = input.LocalPlayerAlive && input.LocalPlayerInside && _readingInside;
            switch (Phase)
            {
                case ContractPityTimerPhase.Idle:
                    if (!counts)
                        return Phase;
                    Key = _readingKey;
                    Phase = ContractPityTimerPhase.Counting;
                    _elapsed = 0f;
                    goto case ContractPityTimerPhase.Counting;

                case ContractPityTimerPhase.Counting:
                    if (!counts)
                        return Phase;
                    _elapsed += dt;
                    if (_elapsed >= settings.DelaySeconds)
                        OpenBlink(settings);
                    return Phase;

                case ContractPityTimerPhase.Blinking:
                    if (!counts)
                    {
                        CloseBlink(settings);
                        return Phase;
                    }
                    _blinkRemaining -= dt;
                    if (_blinkRemaining <= 0f)
                        CloseBlink(settings);
                    return Phase;

                case ContractPityTimerPhase.Waiting:
                    if (!counts)
                        return Phase;
                    _elapsed += dt;
                    if (_elapsed >= settings.RepeatSeconds)
                        OpenBlink(settings);
                    return Phase;

                default:
                    return Phase;
            }
        }

        /// <summary>Back to idle, forgetting the last reading: the timer was disabled or the round reset.</summary>
        internal void Reset()
        {
            Phase = ContractPityTimerPhase.Idle;
            Key = null;
            _elapsed = 0f;
            _blinkRemaining = 0f;
            _readingKey = null;
            _readingInside = false;
        }

        /// <summary>Takes a fresh reading. False when it reset the timer and left nothing to count.</summary>
        private bool ApplyReading(in ContractPityTimerInput input)
        {
            string key = input.ObjectiveKey;
            if (input.Reading == ContractPityReading.NoObjective || key == null)
            {
                Reset();
                return false;
            }

            if (input.ObjectiveFound || (Key != null && !string.Equals(key, Key, StringComparison.Ordinal)))
                Reset();

            _readingKey = key;
            _readingInside = input.Reading == ContractPityReading.TargetInside;
            return true;
        }

        private void OpenBlink(in ContractPityTimerSettings settings)
        {
            Phase = ContractPityTimerPhase.Blinking;
            _elapsed = 0f;
            _blinkRemaining = settings.BlinkSeconds;
            BlinkSerial++;
        }

        private void CloseBlink(in ContractPityTimerSettings settings)
        {
            _elapsed = 0f;
            _blinkRemaining = 0f;
            Phase = settings.RepeatSeconds > 0f ? ContractPityTimerPhase.Waiting : ContractPityTimerPhase.Done;
        }
    }
}
