using System;
using System.Collections.Generic;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteShotgunTrace
    {
        internal static bool DedicatedFallback(bool server, bool client) => server && !client;
        // Native gun spread/range/force. This fallback is never run on a host or observing client.
        internal static void Visit(Vector3 origin, Vector3 forward, int obstructionMask, Func<Collider, int> identity, Action<Collider, int> hit)
        {
            var hits = Physics.SphereCastAll(origin - forward * 10f, 5f, forward, 15f, 524288, QueryTriggerInteraction.Collide);
            var seen = new HashSet<int>();
            foreach (var impact in hits)
            {
                int id = identity(impact.collider);
                if (id == 0 || seen.Contains(id) || impact.distance == 0f
                    || Physics.Linecast(origin, impact.point, obstructionMask, QueryTriggerInteraction.Ignore)) continue;
                float distance = Vector3.Distance(origin, impact.point);
                int force = distance < 3.7f ? 5 : distance < 6f ? 3 : 2;
                seen.Add(id); hit(impact.collider, force);
            }
        }
    }
}
