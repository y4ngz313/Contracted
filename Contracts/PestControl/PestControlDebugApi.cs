using Unity.Netcode;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Reflection-facing diagnostics for the production Pest Control contract, called from
    /// Unity Explorer's C# console and the companion diagnostics mod. The production lifecycle
    /// resolves and spawns the trap automatically; these methods remain available for focused
    /// placement and network-path investigation after the dungeon and NavMesh are ready.
    ///
    /// These names and signatures are a reflection contract: do not rename or re-shape them.
    /// </summary>
    public static class PestControlDebugApi
    {
        /// <summary>Runs the synchronous probe, draws the visualization, and returns a summary.</summary>
        public static string RunPlacementProbe()
        {
            PestControlTrapPlacer.TryResolveImmediate(out PestControlTrapPlacement result);
            if (result == null)
                return "Pest Control probe: probe returned nothing (no dungeon?).";

            PestControlPlacementDebugVisual.Build(result);
            return result.BuildSummary() + "\n  " + PestControlPlacementDebugVisual.ColorKey;
        }

        public static string ClearVisualization()
        {
            PestControlPlacementDebugVisual.Clear();
            return "Pest Control placement visualization cleared.";
        }

        /// <summary>
        /// Reuses the last probe result when one exists (so the trap can be rebuilt without
        /// re-paying thousands of raycasts), reserves the footprint, and builds the trap: the
        /// real networked article when we are the host in a round, a collider-free local preview
        /// otherwise.
        /// </summary>
        public static string BuildTrapPreview()
        {
            PestControlPlacementDebugVisual.Clear();

            PestControlTrapPlacement placement = PestControlTrapPlacer.LastResult;
            bool reused = placement != null && placement.HasResult;
            if (!reused)
                PestControlTrapPlacer.TryResolveImmediate(out placement);

            if (placement == null || !placement.HasResult)
                return "Pest Control trap: no placement available (run RunPlacementProbe first, or the probe found nothing).";

            if (!placement.IsProductionValid)
            {
                return "Pest Control trap: production placement REJECTED; nothing spawned. " +
                       placement.BuildProductionValiditySummary();
            }

            bool reserved = PestControlTrapPlacer.TryReservePlacement(placement);
            if (!reserved)
                return "Pest Control trap: production reservation REJECTED; nothing spawned. " +
                       placement.BuildProductionValiditySummary(requireReservation: true);

            const string header = "production-valid";

            if (IsServer())
            {
                PestControlTrapController existing = PestControlTrapController.Instance;
                if (existing != null)
                    PestControlTrapRegistry.TryDespawn(existing);

                PestControlTrapController controller = PestControlTrapRegistry.TrySpawn(placement);
                if (controller == null)
                {
                    PestControlTrapPlacer.ReleaseReservation(placement);
                    return "Pest Control trap: networked spawn failed (prefab registration or NetworkManager problem).";
                }

                return "Pest Control trap spawned NETWORKED (" + header + ")\n" +
                       Describe(placement, reused, reserved) +
                       $"\n  lever      = in-world InteractTrigger (state {controller.State})";
            }

            PestControlTrapPreview preview = PestControlTrapAssembler.BuildLocalPreview(placement);
            if (preview == null)
                return "Pest Control trap: local assembly failed for the resolved placement.";

            return "Pest Control trap built LOCAL-ONLY (" + header + ", not host or not in a round — no colliders, no lever)\n" +
                   Describe(placement, reused, reserved) +
                   $"\n  panels     = {preview.PanelsPerSide} per side ({preview.PanelsPerSide * 4} total)" +
                   $"\n  lever      = ({preview.LeverPosition.x:0.00}, {preview.LeverPosition.y:0.00}, {preview.LeverPosition.z:0.00}) " +
                   $"{(preview.LeverValidated ? "floor-validated" : "UNVALIDATED (no floor hit, using placement height)")}";
        }

        /// <summary>
        /// Explicit diagnostic-only override. It never reserves space or network-spawns, and
        /// exists only to inspect a best-effort assembly that production rejected.
        /// </summary>
        public static string BuildUnsafeTrapPreview()
        {
            PestControlPlacementDebugVisual.Clear();
            if (PestControlTrapController.Instance != null)
                return "Pest Control unsafe preview refused: clear the networked trap first.";

            PestControlTrapPlacement placement = PestControlTrapPlacer.LastResult;
            bool reused = placement != null && placement.HasResult;
            if (!reused)
                PestControlTrapPlacer.TryResolveImmediate(out placement);

            if (placement == null || !placement.HasResult)
                return "Pest Control unsafe preview: no placement result available.";

            PestControlTrapPreview preview = PestControlTrapAssembler.BuildLocalPreview(placement);
            if (preview == null)
                return "Pest Control unsafe preview: local assembly failed.";

            return "Pest Control trap built UNSAFE LOCAL-ONLY (diagnostic override; no reservation, colliders, or network spawn)\n" +
                   Describe(placement, reused, reserved: false) +
                   $"\n  production = {placement.BuildProductionValiditySummary()}" +
                   $"\n  panels     = {preview.PanelsPerSide} per side ({preview.PanelsPerSide * 4} total)";
        }

        /// <summary>Routes through the same server path the lever uses, so solo testing needs no walk.</summary>
        public static string DropTrapPreview()
        {
            PestControlTrapController controller = PestControlTrapController.Instance;
            if (controller != null)
            {
                if (!IsServer())
                {
                    controller.PullLeverServerRpc();
                    return "Pest Control trap: pull request sent to the host.";
                }

                if (!controller.ServerRequestDrop())
                    return $"Pest Control trap: drop ignored — {DescribeBlockedState(controller)}.";

                return "Pest Control trap: dropping. " + CaptureLine(controller);
            }

            PestControlTrapPreview preview = PestControlTrapAssembler.Current;
            if (preview == null)
                return "Pest Control trap: nothing built (call BuildTrapPreview first).";

            if (!preview.TryDrop(out string reason))
                return $"Pest Control trap: local drop ignored — {reason} (state {preview.State}).";

            return "Pest Control trap: dropping (local preview, no capture).";
        }

        public static string RaiseTrapPreview()
        {
            PestControlTrapController controller = PestControlTrapController.Instance;
            if (controller != null)
            {
                if (!IsServer())
                {
                    controller.PullLeverServerRpc();
                    return "Pest Control trap: pull request sent to the host.";
                }

                if (!controller.ServerRequestRaise())
                    return $"Pest Control trap: raise ignored — {DescribeBlockedState(controller)}.";

                return $"Pest Control trap: winching back to rest ({PestControlTrapPreview.RaiseSeconds:0.0}s), " +
                       $"then {PestControlTrapController.RearmSeconds:0}s rearm. " + CaptureLine(controller);
            }

            PestControlTrapPreview preview = PestControlTrapAssembler.Current;
            if (preview == null)
                return "Pest Control trap: nothing built (call BuildTrapPreview first).";

            if (!preview.TryRaise(out string reason))
                return $"Pest Control trap: local raise ignored — {reason} (state {preview.State}).";

            return "Pest Control trap: winching back to rest (local preview).";
        }

        public static string ClearTrapPreview()
        {
            PestControlTrapController controller = PestControlTrapController.Instance;
            string captured = controller != null ? CaptureLine(controller) : string.Empty;

            bool despawned = controller != null && PestControlTrapRegistry.TryDespawn(controller);
            bool hadLocal = PestControlTrapAssembler.IsBuilt;
            PestControlTrapAssembler.Clear();
            bool released = PestControlTrapPlacer.ReleaseReservation(PestControlTrapPlacer.LastResult);

            if (!despawned && !hadLocal && !released)
                return "Pest Control trap: nothing to clear.";

            string trapState = despawned ? "despawned" : hadLocal ? "local preview destroyed" : "none";
            return $"Pest Control trap cleared (trap={trapState}, " +
                   $"reservation={(released ? "released" : "none held")}). {captured}";
        }

        private static string Describe(PestControlTrapPlacement placement, bool reused, bool reserved)
        {
            return $"  placement  = {(reused ? "reused last probe" : "fresh probe")}, tile {placement.TileName}\n" +
                   $"  center     = ({placement.Center.x:0.00}, {placement.Center.y:0.00}, {placement.Center.z:0.00}) yaw {placement.Yaw:0.0}deg\n" +
                   $"  footprint  = {placement.FootprintMeters:0.00}m square\n" +
                   $"  reservation= {(reserved ? "held" : "FAILED")} — {placement.ReservationStatus}";
        }

        private static string CaptureLine(PestControlTrapController controller)
        {
            if (controller == null || controller.CapturedTotal <= 0)
                return "CAPTURED: none";

            return $"CAPTURED: {controller.LastCaptureSummary} (total {controller.CapturedTotal})";
        }

        private static string DescribeBlockedState(PestControlTrapController controller)
        {
            switch (controller.State)
            {
                case PestControlTrapController.TrapState.Dropping:
                    return "already dropping";
                case PestControlTrapController.TrapState.Dropped:
                    return "already dropped (raise it first)";
                case PestControlTrapController.TrapState.Raising:
                    return "winching";
                case PestControlTrapController.TrapState.Rearming:
                    return $"rearming (up to {PestControlTrapController.RearmSeconds:0}s)";
                default:
                    return "armed and waiting";
            }
        }

        private static bool IsServer()
        {
            NetworkManager manager = NetworkManager.Singleton;
            return manager != null && manager.IsListening && manager.IsServer;
        }
    }
}
