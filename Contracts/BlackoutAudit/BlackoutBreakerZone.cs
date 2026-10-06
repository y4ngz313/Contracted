using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Contracts.BlackoutAudit
{
    internal sealed class BlackoutBreakerZone
    {
        internal int Index;
        internal BreakerBox Breaker;
        internal Vector3 Position;
        internal bool Restored;
        internal bool Found;
        internal bool SpawnedByBlackout;
        internal BlackoutBreakerDistressEffect DistressEffect;
        internal readonly List<BlackoutPoweredFixture> Fixtures = new List<BlackoutPoweredFixture>();
    }
}
