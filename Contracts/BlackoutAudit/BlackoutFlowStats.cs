using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Contracts.BlackoutAudit
{
    internal sealed class BlackoutFlowStats
    {
        internal readonly HashSet<GameObject> SeenPrefabs = new HashSet<GameObject>();
        internal int PoweredLightPrefabs;
        internal int PoweredLightAnimators;
        internal int PoweredLights;
    }
}
