using System;
using System.Collections;
using System.Diagnostics;
using Y4NGZCompany.Facility.Interior.Placement;
using UnityEngine;

using Y4NGZCompany.Facility.Interior;
namespace Y4NGZCompany.Core
{
    /// <summary>Budgeted placement entry point for large companion-mod facility fixtures.</summary>
    public static class FacilityFeaturePlacementApi
    {
        public const int ProtocolVersion = 1;

        // The trailing optional parameter keeps every existing caller source-compatible; 0
        // disables the spread rule for one request without touching the shared default.
        public static bool RequestLargeBackWallPlacement(
            string requesterId,
            float width,
            float height,
            float depth,
            float frontClearance,
            float minEntranceDistance,
            Action<bool, Vector3, Quaternion, Vector3, string> onComplete,
            float minSeparationFromOtherFixtures = InteriorPlacementRequest.DefaultMinSeparationFromOtherFixturesMeters)
        {
            // #393: the plugin's own host component, not the plugin component — the latter
            // rides BepInEx's shared manager object, which another mod's Find-based cleanup
            // can destroy, silently killing this placement coroutine.
            MonoBehaviour host = Plugin.CoroutineHost;
            RoundManager round = RoundManager.Instance;
            if (host == null || onComplete == null || round == null || !round.IsServer)
                return false;

            // #393: the mainframe reservation this used to call directly now belongs to
            // LethalCCTV, which subscribes to this hook at bootstrap. Inert without it.
            InteriorFixtureHooks.RaiseBeforeExternalPlacementRequest();

            var request = new InteriorPlacementRequest
            {
                Role = InteriorPlacementRole.LargeFloorFixtureBackToWall,
                Footprint = BuildLargeBackWallFootprint(width, height, depth, frontClearance),
                MinEntranceDistance = Mathf.Max(8f, minEntranceDistance),
                MinSeparationFromOtherFixturesMeters = Mathf.Max(0f, minSeparationFromOtherFixtures),
                AllowEntranceRoom = false,
                RequireWallContact = true,
                RequireFloorContact = true,
                PreferImportantRoom = true,
                RequireImportantRoomWhenReviewed = false,
                DisallowHallway = true,
                RequireBodyClearance = true,
                RequireReachableInteractionPoint = true,
                DebugLabel = BuildExternalLargeFixtureLabel(requesterId),
            };

            host.StartCoroutine(BuildPlacement(request, onComplete));
            return true;
        }

        /// <summary>Shared clamp ranges for the large back-wall fixture, applied identically to
        /// the procedural request and any authored reservation so both paths validate the same
        /// fixture.</summary>
        internal static void ClampLargeBackWallDimensions(
            float width,
            float height,
            float depth,
            float frontClearance,
            out float safeWidth,
            out float safeHeight,
            out float safeDepth,
            out float safeFrontClearance)
        {
            safeWidth = Mathf.Clamp(width, 0.5f, 4f);
            safeHeight = Mathf.Clamp(height, 0.5f, 4f);
            safeDepth = Mathf.Clamp(depth, 0.25f, 2.5f);
            safeFrontClearance = Mathf.Clamp(frontClearance, 0.75f, 3f);
        }

        /// <summary>Canonical large back-wall footprint: one home for the clamp ranges and the
        /// back-offset/interaction-height parameter math shared by the procedural request and the
        /// authored reservation path.</summary>
        internal static FixtureFootprint BuildLargeBackWallFootprint(
            float width,
            float height,
            float depth,
            float frontClearance)
        {
            ClampLargeBackWallDimensions(
                width,
                height,
                depth,
                frontClearance,
                out float safeWidth,
                out float safeHeight,
                out float safeDepth,
                out float safeFrontClearance);
            return FixtureFootprint.LargeBackWall(
                safeWidth,
                safeHeight,
                safeDepth,
                Mathf.Clamp(safeDepth * 0.5f + 0.035f, 0.12f, 1.25f),
                safeFrontClearance,
                Mathf.Clamp(safeHeight * 0.48f, 0.7f, 1.35f));
        }

        /// <summary>Canonical reservation/debug label for an external large fixture request.</summary>
        internal static string BuildExternalLargeFixtureLabel(string requesterId)
        {
            return "external-large-fixture:" + Sanitize(requesterId);
        }

        private static IEnumerator BuildPlacement(
            InteriorPlacementRequest request,
            Action<bool, Vector3, Quaternion, Vector3, string> onComplete)
        {
            var frame = Stopwatch.StartNew();
            Func<bool> shouldYield = () =>
            {
                if (frame.Elapsed.TotalMilliseconds < 2.5d)
                    return false;
                frame.Restart();
                return true;
            };

            IEnumerator build = InteriorPlacementService.TryBuildPlanBudgeted(request, (ok, plan) =>
            {
                if (!ok || plan == null)
                {
                    onComplete(false, Vector3.zero, Quaternion.identity, Vector3.zero, "no-valid-placement");
                    return;
                }

                FixtureReservationHandle reservation = default;
                bool reserved = false;
                bool handedOff = false;
                try
                {
                    if (!FixtureReservationRegistry.TryReserve(
                            request.DebugLabel,
                            plan.Pose.Source,
                            request.Footprint,
                            plan.Pose.Position,
                            plan.Pose.Rotation,
                            out reservation,
                            out FixtureReservationConflict conflict))
                    {
                        onComplete(
                            false,
                            Vector3.zero,
                            Quaternion.identity,
                            Vector3.zero,
                            conflict.ToDiagnosticString());
                        return;
                    }

                    reserved = true;
                    handedOff = true;
                    onComplete(
                        true,
                        plan.Pose.Position,
                        plan.Pose.Rotation,
                        plan.Pose.InteractionPoint,
                        plan.FallbackTier);
                }
                catch (Exception ex)
                {
                    if (reserved && !handedOff)
                        FixtureReservationRegistry.Release(reservation);
                    Plugin.Log?.LogWarning(
                        $"[Y4NGZ.FacilityPlacement] Completion callback failed for '{request.DebugLabel}': {ex.Message}");
                }
            }, shouldYield);

            while (build.MoveNext())
                yield return build.Current;
        }

        private static string Sanitize(string value)
        {
            string text = string.IsNullOrWhiteSpace(value) ? "unknown" : value.Trim();
            return text.Length <= 48 ? text : text.Substring(0, 48);
        }
    }
}
