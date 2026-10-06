using System.Collections.Generic;
using UnityEngine;

namespace Y4NGZCompany.Contracts._Shared
{
    public sealed class MoonContractObjectiveMarker : Y4NGZCore.Modules.Objectives.ISpatialObjectiveMarker
    {
        public string Key { get; set; }
        public Component Target { get; set; }
        object Y4NGZCore.Modules.Objectives.ISpatialObjectiveMarker.Target => Target;
        public bool IsArea { get; set; }
        public string Label { get; set; }
        public Vector3 Position { get; set; }
        public float Radius { get; set; }
        public int ColorKind { get; set; }
        public bool IsComplete { get; set; }
    }

    public static class MoonContractObjectiveMarkerApi
    {
        public static IReadOnlyList<MoonContractObjectiveMarker> GetActiveMarkers()
        {
            return MoonContractState.GetCctvObjectiveMarkers();
        }
    }
}
