using System.Collections;
using System.Diagnostics;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Facility.Interior.Placement;

namespace Y4NGZCompany.Contracts.Defuse
{
    internal static class DefuseFloorBombPlacer
    {
        private static IEnumerator _scan;
        private static readonly Stopwatch Frame = new Stopwatch();
        private static InteriorPlacementPlan _plan;
        private static bool _planAllowsCrowding;
        internal static void Reset() { (_scan as System.IDisposable)?.Dispose(); _scan = null; _plan = null; _planAllowsCrowding = false; }

        internal static bool TryResolve(out Vector3 position, out Quaternion rotation, out string source, out bool complete)
        {
            position = default; rotation = Quaternion.identity; source = "room-floor-search"; complete = false;
            Frame.Restart();
            if (_scan == null) _scan = Search();
            while (Frame.Elapsed.TotalMilliseconds < 8)
            {
                if (_scan.MoveNext()) continue;
                var plan = _plan;
                bool allowsCrowding = _planAllowsCrowding;
                Reset();
                complete = true;
                if (plan == null) return false;
                position = plan.Pose.Position + Vector3.up * DefuseBalanceConfig.BombVerticalOffsetMeters;
                rotation = plan.Pose.Rotation;
                float nearest = FixtureReservationRegistry.GetNearestReservationCenterDistance(position);
                if (!allowsCrowding && !DefusePlacementPolicy.IsSeparated(nearest))
                {
                    ContractsBootstrap.Log?.LogWarning("[MoonContracts.Defuse] Floor spacing changed during the scan; retrying.");
                    return false;
                }
                if (!FixtureReservationRegistry.TryReserve(DefuseElevatedBombPlacer.ReservationLabel, "defuse-floor",
                    DefuseElevatedBombPlacer.CreateBombFootprint(), position, rotation, out _, out var conflict))
                {
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Floor reservation lost: {conflict.ToDiagnosticString()}; retrying.");
                    return false;
                }
                source = $"room-floor:{plan.Pose.TileName}:{plan.FallbackTier}";
                if (!DefusePlacementPolicy.IsSeparated(nearest))
                    ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Defuse] Spacing relaxed after no validated 25m placement: nearestFixture={nearest:0.00}m tile='{plan.Pose.TileName}'.");
                return true;
            }
            return false;
        }
        private static IEnumerator Search()
        {
            // Relax geometry first while retaining separation; only then permit a crowded floor pose.
            for (int pass = 0; pass < 2 && _plan == null; pass++)
            {
                var request = new InteriorPlacementRequest
                {
                    Role = InteriorPlacementRole.BreachRoomProp,
                    Footprint = DefuseElevatedBombPlacer.CreateBombFootprint(),
                    RequireWallContact = false,
                    RequireFloorContact = true,
                    RequireBodyClearance = true,
                    RequireReachableInteractionPoint = true,
                    DisallowHallway = true,
                    MinEntranceDistance = 24f,
                    MinSeparationFromOtherFixturesMeters = DefusePlacementPolicy.PreferredSeparation,
                    RequireFixtureSeparation = pass == 0,
                    DebugLabel = "defuse-bomb-floor"
                };
                IEnumerator search = InteriorPlacementService.TryBuildPlanBudgeted(request,
                    (ok, plan) => { if (ok) { _plan = plan; _planAllowsCrowding = pass > 0; } }, () => Frame.Elapsed.TotalMilliseconds >= 8);
                try { while (search.MoveNext()) yield return search.Current; }
                finally { (search as System.IDisposable)?.Dispose(); }
            }
        }
    }
}
