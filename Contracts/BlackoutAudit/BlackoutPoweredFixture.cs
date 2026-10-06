using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Contracts.BlackoutAudit
{
    internal sealed class BlackoutPoweredFixture
    {
        internal GameObject Root;
        internal Animator Animator;
        internal readonly List<Light> Lights = new List<Light>();
        internal Vector3 Position;
        internal int ZoneIndex = -1;
    }
}
