using System;
using System.Collections;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Production lifecycle for the Pest Control directive. The host waits for the shared
    /// facility-placement barrier, resolves one authored/procedural open-area site, reserves
    /// the complete fixture compound, and spawns the artless network prefab. Clients derive
    /// objective progress from the trap's replicated state.
    /// </summary>
    internal static class PestControlContract
    {
        private static bool _spawnAttempted;
        private static Coroutine _spawnRoutine;
        private static MonoBehaviour _spawnHost;
        private static PestControlTrapPlacement _placement;
        private static PestControlTrapController _activeTrap;

        /// <summary>
        /// Sticky "a specimen was caged this round", mirroring <c>PayloadContract._delivered</c>.
        /// The trap despawns at round end; without the latch every objective would collapse back
        /// to zero at exactly the moment the escape step is supposed to tick.
        /// </summary>
        private static bool _trapped;

        /// <summary>
        /// #672. Sticky "this client has actually walked up to the rig". Objectives are derived
        /// locally on every peer, so this latch is deliberately per-client: the locate step is a
        /// reading aid for the player looking at the card, not replicated contract state.
        ///
        /// It can never stall completion. <c>_trapped</c> implies located below, and both the trap
        /// and depart steps already require a capture, so a crew that cages a specimen without this
        /// client ever approaching the rig still finishes the contract.
        /// </summary>
        private static bool _located;

        /// <summary>#1197. The locate row's key, shared with the pity timer's target read.</summary>
        internal const string LocateKey = "pest.locate";

        /// <summary>#1197. Read-only view of <see cref="_located"/> for the pity timer. It moves on the
        /// same objective poll that ticks the card row, so the two never disagree.</summary>
        internal static bool Located => _located;

        /// <summary>
        /// Slack past the rig's own footprint before it counts as found. The compound is a large
        /// outdoor structure, so discovery is edge-relative rather than a fixed radius from centre.
        /// </summary>
        private const float LocateMarginMeters = 12f;

        internal static PestControlTrapController ActiveTrap =>
            _activeTrap != null ? _activeTrap : PestControlTrapController.Instance;

        internal static void ResetRunState()
        {
            if (_spawnRoutine != null && _spawnHost != null)
                _spawnHost.StopCoroutine(_spawnRoutine);

            _spawnRoutine = null;
            _spawnHost = null;
            _spawnAttempted = false;
            _trapped = false;
            _located = false;

            PestControlTrapController trap = ActiveTrap;
            if (trap != null && IsServerRuntime())
                PestControlTrapRegistry.TryDespawn(trap);

            _activeTrap = null;
            if (_placement != null)
                PestControlTrapPlacer.ReleaseReservation(_placement);
            _placement = null;
        }

        internal static void RegisterTrap(PestControlTrapController trap)
        {
            if (trap != null)
                _activeTrap = trap;
        }

        internal static void UnregisterTrap(PestControlTrapController trap)
        {
            if (_activeTrap == trap)
                _activeTrap = null;
        }

        internal static void UpdateRuntime()
        {
            if (!IsRuntimeActive() || !IsServerRuntime())
                return;
            if (_spawnAttempted || _spawnRoutine != null || ActiveTrap != null)
                return;

            MonoBehaviour host = RoundManager.Instance != null
                ? (MonoBehaviour)RoundManager.Instance
                : StartOfRound.Instance;
            if (host == null)
                return;

            _spawnAttempted = true;
            _spawnHost = host;
            _spawnRoutine = host.StartCoroutine(SpawnProductionTrap());
        }

        /// <summary>
        /// Three ordered milestones, in the sentence case Defuse, Payload and WasteDisposal
        /// already use — this contract was the only one shouting.
        ///
        /// The middle step is gated on an actual capture, never on presence. It used to read
        /// <c>SpecimenLured</c>, which the server latches the instant any living enemy's centre
        /// point crosses the platform, cage up or down; a creature wandering through once marked
        /// the contract for the rest of the round. <c>SpecimenLured</c> is untouched and still
        /// drives the CCTV trap marker — it simply no longer claims a milestone.
        ///
        /// The locate step used to complete the moment the trap had assembled, on the "it exists"
        /// semantics behind "Locate the defuse case" and "Locate the waste incinerator". #672: the
        /// host assembles this rig during the placement barrier, and <c>_footprint</c> replicates to
        /// every peer the same frame, so the step arrived on the card already ticked and the crew's
        /// visible first objective was the capture. It now waits for someone to actually reach the
        /// compound, which is what "locate" reads as.
        /// </summary>
        internal static List<MoonContractObjective> GetObjectives()
        {
            PestControlTrapController trap = ActiveTrap;
            bool rigged = trap != null && trap.FootprintMeters > 0f;
            _trapped |= rigged && trap.CapturedTotal > 0;
            _located |= _trapped || (rigged && LocalPlayerHasReachedTrap(trap));

            // The departure line is the shared closing objective MoonContractState appends
            // to every contract; Pest Control no longer carries its own departure row.

            // The combined action completes only on an actual capture.
            return new List<MoonContractObjective>
            {
                new MoonContractObjective("Locate the containment cage", _located ? 1 : 0, 1, key: LocateKey),
                new MoonContractObjective("Lure a monster onto the platform and drop the cage", _trapped ? 1 : 0, 1, key: "pest.trap"),
            };
        }

        /// <summary>
        /// #672. True once the local player has stood inside the rig compound's discovery ring.
        /// Measured from the replicated footprint so a wide authored site is found at its edge
        /// rather than only at its centre point, which keeps the step honest on every layout.
        ///
        /// Deliberately proximity-only: a sightline test would leave the step unticked for a crew
        /// standing under the cage in fog, which reads as a bug rather than as a rule.
        /// </summary>
        private static bool LocalPlayerHasReachedTrap(PestControlTrapController trap)
        {
            if (trap == null)
                return false;

            PlayerControllerB player = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (player == null || player.isPlayerDead || !player.isPlayerControlled)
                return false;

            float reach = trap.FootprintMeters * 0.5f + LocateMarginMeters;
            Vector3 offset = player.transform.position - trap.MarkerPosition;
            return offset.sqrMagnitude <= reach * reach;
        }

        private static IEnumerator SpawnProductionTrap()
        {
            PestControlTrapPlacement resolved = null;
            IEnumerator resolver = PestControlTrapPlacer.ResolveProductionBudgeted(
                placement => resolved = placement,
                reserveOnSuccess: true);
            Exception resolverFailure = null;
            while (true)
            {
                object current;
                try
                {
                    if (!resolver.MoveNext())
                        break;
                    current = resolver.Current;
                }
                catch (Exception exception)
                {
                    resolverFailure = exception;
                    break;
                }

                yield return current;
            }

            _spawnRoutine = null;
            _spawnHost = null;
            if (resolverFailure != null)
            {
                if (resolved != null)
                    PestControlTrapPlacer.ReleaseReservation(resolved);
                ContractsBootstrap.Log?.LogError(
                    $"[MoonContracts.PestControl] Placement resolver failed: {resolverFailure}");
                DisableUnsupportedRound("the production placement resolver failed");
                yield break;
            }

            if (!IsRuntimeActive())
            {
                if (resolved != null)
                    PestControlTrapPlacer.ReleaseReservation(resolved);
                yield break;
            }

            if (resolved == null)
            {
                DisableUnsupportedRound("no fully valid authored or procedural trap site was available");
                yield break;
            }

            _placement = resolved;
            PestControlTrapController trap = null;
            try
            {
                trap = PestControlTrapRegistry.TrySpawn(resolved);
            }
            catch (Exception exception)
            {
                ContractsBootstrap.Log?.LogError(
                    $"[MoonContracts.PestControl] Network trap spawn threw: {exception}");
            }

            if (trap == null)
            {
                PestControlTrapPlacer.ReleaseReservation(resolved);
                _placement = null;
                DisableUnsupportedRound("the network trap prefab could not be spawned");
                yield break;
            }

            _activeTrap = trap;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.PestControl] Contract trap ready source='{resolved.PlacementSource}' " +
                $"center={resolved.Center} readiness='{resolved.ReadinessStatus}'.");
        }

        /// <summary>
        /// #716 (WP-F F4). This is a supported outcome, not a fault. The placement resolver can
        /// only run once the ship has landed and the interior exists, and the contract card is
        /// revealed on that same landing, so "this moon has no site the rig fits on" is knowable
        /// only after the reveal - there is no earlier point in the flow to decide it.
        ///
        /// So it logs as a Warning that says what happened and what replaced it, instead of the
        /// Error that read as a crash in every log. The card does not need any help here:
        /// <c>DisableCurrentContractForRound</c> re-runs the reveal, and
        /// <c>ContractHUDManager.RenderLivePanel</c>'s semantic signature leads with the contract
        /// type, so the swap to Salvage is a semantic change and the card retypes cleanly.
        /// </summary>
        private static void DisableUnsupportedRound(string reason)
        {
            _spawnAttempted = true;
            bool fallbackApplied = MoonContractState.DisableCurrentContractForRound(
                MoonContractType.PestControl,
                reason);
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.PestControl] PEST_TRAP_FAILURE this moon cannot host the containment rig " +
                $"(reason='{reason}'); the directive is withdrawn and fallback=Salvage applied={fallbackApplied}.");
        }

        private static bool IsRuntimeActive()
        {
            StartOfRound start = StartOfRound.Instance;
            return start != null
                   && MoonContractState.GetCurrentContract() == MoonContractType.PestControl
                   && start.shipHasLanded
                   && !start.inShipPhase
                   && !start.shipIsLeaving;
        }

        private static bool IsServerRuntime()
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager != null && manager.IsServer)
                return true;

            RoundManager round = RoundManager.Instance;
            if (round != null && round.IsServer)
                return true;

            return NetRole.IsServerOrHost();
        }
    }
}
