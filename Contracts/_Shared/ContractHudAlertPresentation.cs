using System;
using System.Collections.Generic;
using System.Text;

namespace Y4NGZCompany.Contracts._Shared
{
    internal struct ContractHudAlertSegment
    {
        internal string Text;
        internal string Key;
        internal bool Lethal;
        internal string Identity => Key ?? Text ?? string.Empty;
    }

    /// <summary>Presentation copy only; the provider's objective and replicated state stay intact.</summary>
    internal static class ContractHudAlertText
    {
        internal static void Append(StringBuilder text, string title, in MoonContractObjective objective)
        {
            string key = objective.SignatureKey;
            string headline = null;
            if (title == "BLACKSITE RETRIEVAL")
            {
                if (key == "blacksite.inbound" || key == "blacksite.none")
                    headline = "BLACKSITE SHIP INBOUND";
                else if (key == "blacksite.deploying")
                    headline = "BLACKSITE TEAM DEPLOYING";
                else
                    title = "BLACKSITE";
            }

            if (headline == null && !key.StartsWith("cult.broadcast.", StringComparison.Ordinal))
                text.Append(title ?? string.Empty).Append(" · ");
            if (objective.IsFailed) text.Append("FAILED · ");
            text.Append(headline ?? objective.Text ?? string.Empty);
            if (objective.Kind == MoonContractObjectiveKind.Action && objective.Total > 1)
                text.Append(' ').Append(objective.Complete).Append('/').Append(objective.Total);
        }

        internal static string Identity(string title, in MoonContractObjective objective, bool lethal)
            => (title ?? string.Empty) + "|" + objective.SignatureKey
                + (objective.IsFailed ? "|failed" : objective.IsComplete ? "|complete" : "|active")
                + (lethal ? "|lethal" : string.Empty);
    }

    /// <summary>
    /// Distinguish an event from volatile copy. A newly changed source leads the pass, while
    /// every other live source remains in it. Poll order does not undo that reading position.
    /// </summary>
    internal sealed class ContractHudAlertContent
    {
        private List<ContractHudAlertSegment> _segments = new List<ContractHudAlertSegment>(6);
        private List<ContractHudAlertSegment> _next = new List<ContractHudAlertSegment>(6);
        private readonly StringBuilder _builder = new StringBuilder(160);
        private string _lead;
        internal string Pass { get; private set; } = string.Empty;
        internal bool MessageChanged { get; private set; }
        internal bool HasContent => _segments.Count != 0;

        internal bool Set(IReadOnlyList<ContractHudAlertSegment> segments, string accentHex)
        {
            _next.Clear();
            if (segments != null)
                for (int i = 0; i < segments.Count; i++)
                    if (!string.IsNullOrWhiteSpace(segments[i].Text)) _next.Add(segments[i]);

            MessageChanged = _next.Count != _segments.Count;
            int changed = -1;
            for (int i = 0; i < _next.Count; i++)
            {
                if (Find(_segments, _next[i].Identity, _next[i].Lethal) >= 0) continue;
                MessageChanged = true;
                // A new lethal warning gets the first readable position when several arrive together.
                if (changed < 0 || (!_next[changed].Lethal && _next[i].Lethal)) changed = i;
            }
            if (changed >= 0) _lead = _next[changed].Identity;
            (_segments, _next) = (_next, _segments);
            return Recolor(accentHex) || MessageChanged;
        }

        internal bool Recolor(string accentHex)
        {
            int lead = 0;
            for (int i = 0; i < _segments.Count; i++)
                if (string.Equals(_segments[i].Identity, _lead, StringComparison.Ordinal)) { lead = i; break; }
            if (_segments.Count != 0) _lead = _segments[lead].Identity;
            _builder.Clear();
            for (int i = 0; i < _segments.Count; i++)
            {
                if (i > 0) _builder.Append("   //   ");
                _builder.Append("<color=").Append(accentHex).Append('>');
                string text = _segments[(lead + i) % _segments.Count].Text.Trim();
                foreach (char c in text)
                    if (c != '<' && c != '>') _builder.Append(char.ToUpperInvariant(c));
                _builder.Append("</color>");
            }
            if (EqualsBuilder(_builder, Pass)) return false;
            Pass = _builder.ToString();
            return true;
        }

        internal void Clear()
        {
            _segments.Clear(); _next.Clear(); _builder.Clear();
            _lead = null; Pass = string.Empty; MessageChanged = false;
        }

        private static int Find(List<ContractHudAlertSegment> segments, string key, bool lethal)
        {
            for (int i = 0; i < segments.Count; i++)
                if (segments[i].Lethal == lethal && string.Equals(segments[i].Identity, key, StringComparison.Ordinal)) return i;
            return -1;
        }

        private static bool EqualsBuilder(StringBuilder builder, string text)
        {
            if (builder.Length != text.Length) return false;
            for (int i = 0; i < builder.Length; i++) if (builder[i] != text[i]) return false;
            return true;
        }
    }

    /// <summary>Unscaled local presentation clock, independent of Unity and gameplay/network state.</summary>
    internal sealed class ContractHudAlertMotion
    {
        internal const float RevealSeconds = .35f;
        internal const float ReadSeconds = 2.5f;
        internal const float FadeOutSeconds = .12f;
        internal const float FadeInSeconds = .20f;
        internal const float ScrollUnitsPerSecond = 35f;
        private enum Transition { None, Out, In }
        private Transition _transition;
        private double _age, _transitionAge, _hold;
        private float _fadeFrom;
        private bool _active;
        internal double ScrollDistance { get; private set; }
        internal bool WaitingToSwap => _transition == Transition.Out;
        internal float Alpha => _active ? Clamp(_age / RevealSeconds) : 0f;
        internal float TextAlpha => _transition == Transition.Out
            ? _fadeFrom * (1f - Clamp(_transitionAge / FadeOutSeconds))
            : _transition == Transition.In ? Clamp(_transitionAge / FadeInSeconds) : 1f;
        internal float PulseGain => _active && _age >= .25 && _age < .65
            ? 1f + .16f * (float)Math.Sin(Math.PI * (_age - .25) / .4) : 1f;

        internal void Show()
        {
            Clear(); _active = true;
            _hold = (double)RevealSeconds + ReadSeconds;
        }

        internal void ChangeMessage()
        {
            // A newer pending status replaces the pending copy, without indefinitely delaying its swap.
            if (!_active || WaitingToSwap) return;
            _fadeFrom = TextAlpha;
            _transition = Transition.Out;
            _transitionAge = 0;
        }

        /// <returns>True once when the old text is invisible and the adapter must install the latest copy.</returns>
        internal bool Tick(float deltaTime)
        {
            if (!_active || deltaTime <= 0f || float.IsNaN(deltaTime) || float.IsInfinity(deltaTime)) return false;
            double remaining = deltaTime;
            _age += remaining;
            bool swap = false;
            if (_transition == Transition.Out)
            {
                double used = Math.Min(remaining, Math.Max(0, FadeOutSeconds - _transitionAge));
                _transitionAge += used; remaining -= used;
                if (_transitionAge < FadeOutSeconds) return false;
                _transition = Transition.In; _transitionAge = 0;
                ScrollDistance = 0; _hold = ReadSeconds; swap = true;
            }
            if (_transition == Transition.In)
            {
                double used = Math.Min(remaining, Math.Max(0, FadeInSeconds - _transitionAge));
                _transitionAge += used; remaining -= used;
                if (_transitionAge < FadeInSeconds) return swap;
                _transition = Transition.None;
            }
            double hold = Math.Min(remaining, _hold);
            _hold -= hold; remaining -= hold;
            ScrollDistance += remaining * ScrollUnitsPerSecond;
            return swap;
        }

        internal void Clear()
        {
            _active = false; _age = _transitionAge = _hold = 0;
            _transition = Transition.None; _fadeFrom = 1; ScrollDistance = 0;
        }

        private static float Clamp(double value) => (float)Math.Max(0, Math.Min(1, value));
    }
}
