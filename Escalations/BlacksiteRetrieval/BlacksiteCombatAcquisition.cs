using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Contact identity survives a brief occlusion. Dropping the live target reference must not
    // restart acquisition every time a player peeks or a teammate crosses the sightline.
    internal sealed class BlacksiteCombatAcquisition
    {
        internal object Target { get; private set; }
        private float _beganAt = float.NegativeInfinity;
        private float _lostSightAt = float.NegativeInfinity;
        private float _requiredSeconds;

        internal bool Select(object target)
        {
            if (ReferenceEquals(Target, target)) return false;
            Clear();
            Target = target;
            return true;
        }

        internal bool Observe(float now, bool visible, float requiredSeconds, float graceSeconds)
        {
            if (Target == null) return false;
            if (!visible)
            {
                if (float.IsNegativeInfinity(_lostSightAt)) _lostSightAt = now;
                return false;
            }

            bool sightBroke = !float.IsNegativeInfinity(_lostSightAt) && now - _lostSightAt > graceSeconds;
            _lostSightAt = float.NegativeInfinity;
            if (!float.IsNegativeInfinity(_beganAt) && !sightBroke) return false;
            _beganAt = now;
            _requiredSeconds = Math.Max(0f, requiredSeconds);
            return true;
        }

        internal bool IsComplete(float now) => Target != null
            && !float.IsNegativeInfinity(_beganAt)
            && float.IsNegativeInfinity(_lostSightAt)
            && now - _beganAt >= _requiredSeconds;

        internal void Clear()
        {
            Target = null;
            _beganAt = _lostSightAt = float.NegativeInfinity;
            _requiredSeconds = 0f;
        }
    }
}
