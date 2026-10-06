using System;

namespace Y4NGZCompany.Escalations.Congregation
{
    // Server-owned, one-shot contact window for one knife slash. Cancellation removes the target as
    // well as the timer. Timings are clip seconds at AttackSpeed 1, measured on the retargeted v11
    // clips (30 fps, t = (frame - 1) / 30); an attack played at another AttackSpeed divides both.
    internal sealed class CongregantAttackWindow
    {
        internal const int KnifeInwardIndex = 0;
        internal const int KnifeOutwardIndex = 1;
        internal const float KnifeInwardContactSeconds = 1.2000f;   // frame 37 of 67
        internal const float KnifeInwardDurationSeconds = 2.2000f;
        internal const float KnifeOutwardContactSeconds = 0.8333f;  // frame 26 of 62
        internal const float KnifeOutwardDurationSeconds = 2.0333f;
        // Horizontal cosine between the attacker's facing and the victim: the whole front half.
        internal const float FrontArcMinDot = 0f;

        private bool _pending;
        private ulong _target;
        private float _contactAt;
        private float _endsAt = float.NegativeInfinity;

        internal static float ContactSeconds(int attackIndex) =>
            attackIndex == KnifeOutwardIndex ? KnifeOutwardContactSeconds : KnifeInwardContactSeconds;

        internal static float DurationSeconds(int attackIndex) =>
            attackIndex == KnifeOutwardIndex ? KnifeOutwardDurationSeconds : KnifeInwardDurationSeconds;

        /// <summary>Contact as the attack state's normalized time, independent of AttackSpeed.</summary>
        internal static float ContactNormalizedTime(int attackIndex) => ContactSeconds(attackIndex) / DurationSeconds(attackIndex);

        /// <summary>Each cultist alternates its two slashes.</summary>
        internal static int NextAttackIndex(int previous) =>
            previous == KnifeInwardIndex ? KnifeOutwardIndex : KnifeInwardIndex;

        /// <summary>The one reach test for a slash: within <paramref name="range"/> (3D) and in the
        /// attacker's front half. The server applies damage and every peer plays the impact on it.</summary>
        internal static bool IsInReach(float offsetX, float offsetY, float offsetZ, float forwardX, float forwardZ, float range)
        {
            if (!(range > 0f) || offsetX * offsetX + offsetY * offsetY + offsetZ * offsetZ > range * range)
                return false;
            float horizontal = (float)Math.Sqrt(offsetX * offsetX + offsetZ * offsetZ);
            float facing = (float)Math.Sqrt(forwardX * forwardX + forwardZ * forwardZ);
            if (horizontal < 0.001f || facing < 0.001f)
                return true;
            return (offsetX * forwardX + offsetZ * forwardZ) / (horizontal * facing) >= FrontArcMinDot;
        }

        internal void Begin(ulong targetClientId, float startTime, float contactSeconds, float durationSeconds)
        {
            float duration = Math.Max(0f, durationSeconds);
            _target = targetClientId;
            _contactAt = startTime + Math.Max(0f, Math.Min(contactSeconds, duration));
            _endsAt = startTime + duration;
            _pending = true;
        }

        /// <summary>True from Begin until the swing's duration elapses, contact or not; false once cancelled.</summary>
        internal bool IsSwinging(float now) => now < _endsAt;

        internal void Cancel()
        {
            _pending = false;
            _target = 0;
            _endsAt = float.NegativeInfinity;
        }

        internal bool TryConsume(float now, out ulong target)
        {
            target = 0;
            if (!_pending || now < _contactAt) return false;
            target = _target;
            _pending = false;
            _target = 0;
            return true;
        }
    }
}
