using System.Collections.Generic;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Independent keys prevent alternating noise/contact events from bypassing a single last-event gate.
    internal sealed class BlacksiteEventCooldown
    {
        private readonly Dictionary<long, float> _next = new Dictionary<long, float>();
        internal void Clear() => _next.Clear();
        internal bool TryAccept(int source, int kind, float now, float interval)
        {
            long key = ((long)source << 32) | (uint)kind;
            if (_next.TryGetValue(key, out float next) && now < next) return false;
            if (_next.Count > 128) { var expired = new List<long>(); foreach (var e in _next) if (e.Value <= now) expired.Add(e.Key); foreach (var id in expired) _next.Remove(id); }
            _next[key] = now + interval; return true;
        }
    }
}
