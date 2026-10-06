using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
namespace Y4NGZCompany.Contracts.Payload
{
    /// <summary>
    /// Static runtime driver for the Payload Haul contract (docs/PAYLOAD_CONTRACT_HANDOFF.md §6).
    /// Mirrors <see cref="DefuseContract"/>: a server-gated one-shot spawn of the caged containment
    /// cart deep in the interior, plus the objective HUD state (reach → haul → magnetize).
    /// </summary>
    internal static class PayloadContract
    {
        internal const float SpawnVerticalOffset = 0.05f;

        // #1305 C1 (D6): host latch, one spawn attempt per round; a failed attempt is not retried and
        // the round has no cart.
        private static bool _spawnAttempted;
        private static PayloadCartPlacement _placement;
        private static bool _delivered;
        private static bool _failed;
        private static string _failureReason;
        // #880: per-peer one-way latches mirrored from the cart's replicated state, so the objective
        // rows never step backwards - not on release, not on a soldier push, and not if the cart
        // despawns before the round ends.
        private static bool _located;
        private static bool _outside;
        // #1076: the Blacksite departure latch. It is what tells "they left with the cart" apart
        // from "the cart fell in a pit": both raise the cart's _failed, and only the first may
        // print the lost row.
        //
        // Per peer and one-way, and it is NOT mirrored from anything on an object. The cart's own
        // _failed.Value = true is written in BlacksiteObjectiveManifestController
        // .ConfirmDepartureServer and the cart is despawned a few lines later, in the same
        // synchronous chain that despawns the Blacksite ship; NGO flushes variable deltas at
        // PostLateUpdate over the spawned-object list, so on a client that delta is never sent and
        // only the destroy message arrives. The latch is raised instead by
        // MoonContractState.NotifyBlacksiteObjectiveDeparted - directly on the host, and from the
        // progression channel's named message (or its round-keyed replay, for a late joiner) on
        // every other peer.
        private static bool _blacksiteDeparted;

        /// <summary>#1197. The locate row's key, shared with the pity timer's target read.</summary>
        internal const string LocateKey = "payload.locate";

        internal static PayloadCartController ActiveCart { get; private set; }
        /// <summary>#1305 C1 (D6): host truth that the round's cart has network-spawned; the Blacksite gate waits on it.</summary>
        internal static bool CartSpawned { get; private set; }

        internal static bool TryGetBlacksiteRetrievalTarget(
            out BlacksiteRetrievalTargetPose target)
        {
            target = default;
            PayloadCartController cart = ActiveCart;
            if (cart == null)
                return false;

            NetworkObject expectedTarget = cart.GetComponent<NetworkObject>();
            return BlacksiteHardDriveObjectiveService.TryResolveContractTarget(
                cart.transform,
                expectedTarget,
                "payload",
                out target);
        }

        internal static void ResetRunState()
        {
            _spawnAttempted = false;
            CartSpawned = false;
            _delivered = false;
            _failed = false;
            _failureReason = null;
            _located = false;
            _outside = false;
            // #1076: the departure latch dies with the run, beside the two latches it sits next to.
            _blacksiteDeparted = false;
            // #1070: the cart is a spawned NetworkObject parented to StartOfRound.propsContainer and is
            // not a GrabbableObject, so vanilla's end-of-round sweep never collects it. Dropping the
            // ActiveCart reference alone left the previous moon's cart alive on the next one, still
            // polling its kill-zone watchdog. The reference is cleared BEFORE the despawn so both the
            // OnNetworkDespawn -> UnregisterCart re-entry and the second of the two passes this method
            // gets per round boundary (MoonContractState.ResetRunState, deferred then non-deferred)
            // find nothing left to do.
            PayloadCartController departingCart = ActiveCart;
            ActiveCart = null;
            DespawnCartServer(departingCart);
            PayloadCartPlacer.ReleaseReservation(_placement);
            _placement = null;
            PayloadCartPlacer.ResetScanState();
            // The controls tip is a per-round hint, not a per-install one (#716 E9).
            PayloadCartController.ResetControlsTipForNewRound();
            // The sampler's caches are session-lifetime statics; drop last round's cart references.
            PayloadCartGroundSampler.ResetSessionCaches();
        }

        /// <summary>
        /// #1070: server-only teardown of the round's cart. Despawn is a server call - a client that
        /// made it would be rejected by netcode - and the despawn replicates, so every peer's
        /// OnNetworkDespawn runs and a player still attached at that moment goes through EndLocalPush.
        /// On the Disconnect postfix frame NetworkManager.IsListening is still true (see
        /// MoonContractRunGate), so the host does despawn there too; the try/catch below is for the
        /// case where netcode has already gone away, when the session tears the object down itself.
        /// </summary>
        private static void DespawnCartServer(PayloadCartController cart)
        {
            if (cart == null || !IsServerRuntime())
                return;

            NetworkManager manager = NetworkManager.Singleton;
            if (manager == null || !manager.IsListening)
                return;

            NetworkObject networkObject = cart.GetComponent<NetworkObject>();
            if (networkObject == null || !networkObject.IsSpawned)
                return;

            try
            {
                networkObject.Despawn();
                ContractsBootstrap.Log?.LogInfo(
                    "[MoonContracts.Payload] Payload cart despawned at the round boundary; the cart does not outlive its moon.");
            }
            catch (System.Exception error)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Payload] Payload cart despawn failed: {error.GetType().Name}: {error.Message}");
            }
        }

        /// <summary>
        /// #1070: true only while the Payload contract owns <paramref name="cart"/> on this peer. The
        /// write-off watchdog used to run off the cart's own fields alone, so a cart that outlived its
        /// round kept polling on the next moon and failed a contract that was never Payload.
        /// </summary>
        internal static bool IsActiveContractCart(PayloadCartController cart)
        {
            return cart != null
                && ActiveCart == cart
                && MoonContractState.GetCurrentContract() == MoonContractType.Payload;
        }

        internal static void RegisterCart(PayloadCartController cart)
        {
            if (cart != null)
                ActiveCart = cart;
        }

        internal static void UnregisterCart(PayloadCartController cart)
        {
            if (ActiveCart == cart)
                ActiveCart = null;
        }

        internal static void NotifyDelivered(PayloadCartController cart)
        {
            if (cart != null && ActiveCart == null)
                ActiveCart = cart;

            if (_delivered || _failed)
                return;

            _delivered = true;
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.Payload] Payload delivered.");
        }

        internal static bool IsFailed => _failed;

        /// <summary>#1076: raise the departure latch. True on the transition only.</summary>
        internal static bool NoteBlacksiteDeparted()
        {
            if (_blacksiteDeparted)
                return false;
            _blacksiteDeparted = true;
            return true;
        }

        internal static void NotifyFailed(PayloadCartController cart, string reason)
        {
            if (cart != null && ActiveCart == null)
                ActiveCart = cart;

            if (_failed || _delivered)
                return;

            _failed = true;
            _failureReason = string.IsNullOrWhiteSpace(reason) ? "payload lost" : reason;
            ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload] Payload failed ({_failureReason}).");
        }

        internal static void UpdateRuntime()
        {
            if (!IsRuntimeActive())
                return;

            if (_failed)
                return;

            if (_spawnAttempted || ActiveCart != null)
                return;

            var round = RoundManager.Instance;
            var start = StartOfRound.Instance;
            if (round == null || start == null || !IsServerRuntime() || !start.shipHasLanded || start.inShipPhase || start.shipIsLeaving)
                return;

            // A missing prefab used to retry (and warn) every frame; with the scan below being
            // physics-heavy that would also rescan every frame, so it spends the round's one attempt.
            if (ContractAssetLoader.PayloadCartPrefab == null)
            {
                ContractsBootstrap.Log?.LogError("[MoonContracts.Payload] PayloadCart prefab unavailable (not yet built into lgucontracts.bundle); the cart cannot spawn this round.");
                _spawnAttempted = true;
                return;
            }

            // #877: the spawn site comes from a budgeted clearance scan that may span several
            // frames; false means "call again next frame". #1305 C1 (D6): the scan always ends in a
            // placement.
            long spawnStart = System.Diagnostics.Stopwatch.GetTimestamp();
            // #874 Q11: the Payload cart's landed frame (80 ms on 23 September), named for the
            // profiler and the Blacksite hitch tracer.
            using var activation = BlacksiteHitchTracer.Scope(BlacksiteHitchScope.PayloadCartActivation);
            if (!PayloadCartPlacer.TryResolveBudgeted(out PayloadCartPlacement placement))
                return;

            _spawnAttempted = true;
            _placement = placement;
            Vector3 position = placement.Position + Vector3.up * SpawnVerticalOffset;
            Quaternion rotation = placement.Rotation;
            string source = $"{placement.Source}:{placement.TileName}:{placement.TierName}";
            bool spawned = false;
            try
            {
                spawned = TrySpawnCart(placement, position, rotation, source);
            }
            finally
            {
                // #1305 C1 (D6): an exit before the network spawn (false or a throw, which still
                // propagates) frees the claim; a live cart keeps its claim.
                if (!CartSpawned)
                {
                    PayloadCartPlacer.ReleaseReservation(_placement);
                    _placement = null;
                }
            }
            if (!spawned)
                return;

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Timing] Payload cart activation frame took " +
                $"{(System.Diagnostics.Stopwatch.GetTimestamp() - spawnStart) * 1000.0 / System.Diagnostics.Stopwatch.Frequency:0.00}ms.");
        }

        internal static List<MoonContractObjective> GetObjectives()
        {
            bool spawned = ActiveCart != null;
            // #880: every step is a latch. Locate used to read the live attached flag, so releasing
            // the handle inside the facility un-ticked it; now the cart's replicated one-way located
            // flag and these per-peer mirrors keep each row where it was.
            _located |= spawned && (ActiveCart.IsLocated || ActiveCart.IsOutside || ActiveCart.IsDelivered);
            _outside |= spawned && (ActiveCart.IsOutside || ActiveCart.IsDelivered);
            bool located = _located;
            bool outside = _outside;
            bool delivered = _delivered || (spawned && ActiveCart.IsDelivered);
            // Mirror the delivered fallback (#440): the cart's replicated failure flag is authoritative on
            // every peer, so the HUD stays correct even if the OnValueChanged notification was missed
            // (cart spawned into an already-failed state, static reset ordering, etc.).
            // #1076: the Blacksite squad's own reading. The departure latch is checked first and is
            // one-way; possession comes live from the replicated manifest snapshots, so host and
            // clients say the same thing with no new RPC and the rows revert the moment the crew
            // takes the cart back off them.
            bool departed = _blacksiteDeparted;
            BlacksitePossessionReading possession = departed
                ? default
                : BlacksitePossessionReader.Read(BlacksiteObjectiveKind.Payload);
            // A departed cart is a failed contract on every peer, including one whose copy of the
            // cart's _failed never arrived - which, on a departure, is every client.
            bool failed = _failed || departed || (spawned && ActiveCart.IsFailed);
            bool squadHolds = !departed && !delivered && possession.IsHeld;

            // #725: the trip out of the facility is its own effort, so it is its own milestone
            // in plain words. A written-off cart is a status condition, not an action the crew
            // can still take, so it moves to a status row instead of overwriting the secure step.
            var objectives = new List<MoonContractObjective>();
            // #1076: the lost row replaces the write-off row when the failure was a departure, and
            // renders dim rather than danger - there is no cart left to do anything about.
            if (departed)
                objectives.Add(new MoonContractObjective("BLACKSITE TEAM LEFT WITH THE CART", 0, 1, true, MoonContractObjectiveKind.Status, key: "payload.blacksite", lost: true));
            else if (failed)
                objectives.Add(new MoonContractObjective("PAYLOAD · WRITTEN OFF", 0, 1, failed, MoonContractObjectiveKind.Status, key: "payload.status"));

            // #1076, amended by the 2026-09-16 playtest: possession leads the list so the card
            // selects this row over Return/Magnetize, the same precedence the failed row has. It is
            // emitted only while the squad actually has it, so recovering the cart reverts the card
            // by itself. The status row that used to compose beside it is gone: the belt already
            // says PAYLOAD TAKEN / PUSHING PAYLOAD / PAYLOAD ABOARD, so the card is one action row
            // and its lethal flag is the only card-side danger tint left.
            if (squadHolds)
            {
                objectives.Add(new MoonContractObjective(
                    "Recover the payload cart", 0, 1, false, key: "payload.blacksite.recover", lethal: possession.IsAboard));
            }

            objectives.Add(new MoonContractObjective("Locate the payload cart", located || outside || delivered ? 1 : 0, 1, failed, key: LocateKey));
            objectives.Add(new MoonContractObjective("Return the payload cart to the Company Ship", outside || delivered ? 1 : 0, 1, failed, key: "payload.move"));
            objectives.Add(new MoonContractObjective("Magnetize the payload cart", delivered ? 1 : 0, 1, failed, key: "payload.secure"));
            return objectives;
        }

        private static bool IsRuntimeActive()
        {
            return MoonContractState.GetCurrentContract() == MoonContractType.Payload;
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

        private static bool TrySpawnCart(PayloadCartPlacement placement, Vector3 position, Quaternion rotation, string source)
        {
            GameObject instance = Object.Instantiate(ContractAssetLoader.PayloadCartPrefab, position, rotation);
            if (instance == null)
            {
                ContractsBootstrap.Log?.LogError("[MoonContracts.Payload] PayloadCart failed to instantiate. Aborting spawn.");
                return false;
            }

            instance.name = "PayloadCart";
            if (!instance.activeSelf)
                instance.SetActive(true);

            // Nothing may be attached to the instance here: the controller, visual juice, audio, collider,
            // NetworkObject and NetworkTransform all live on the prefab and are hashed + registered at plugin
            // init by ContractAssetLoader.PreparePayloadCartPrefab, so every peer resolves the same
            // GlobalObjectIdHash. Adding them at spawn time left the hash at 0 and clients saw nothing (#434).
            NetworkObject networkObject = instance.GetComponent<NetworkObject>();
            if (networkObject == null)
            {
                ContractsBootstrap.Log?.LogError("[MoonContracts.Payload] PayloadCart prefab has no NetworkObject; the cart would exist on the host only. Aborting spawn.");
                Object.Destroy(instance);
                return false;
            }

            if (!networkObject.IsSpawned)
            {
                // #1305 C1 (D6): a throw here is a cart-specific error that spends the round's one
                // attempt, not a designed outcome.
                try
                {
                    networkObject.Spawn();
                }
                catch (System.Exception exception)
                {
                    ContractsBootstrap.Log?.LogError(
                        $"[MoonContracts.Payload] PAYLOAD_SPAWN failed: network spawn threw source={source} " +
                        $"pos=({position.x:0.0},{position.y:0.0},{position.z:0.0}) yaw={rotation.eulerAngles.y:0.0} {exception}");
                    if (!networkObject.IsSpawned)
                    {
                        Object.Destroy(instance);
                        return false;
                    }
                }
            }
            // #1305 C1 (D6): the cart exists on every peer from here; a throw below (parenting,
            // configuration, diagnostics) must not release its claim.
            CartSpawned = true;
            if (StartOfRound.Instance?.propsContainer != null)
                networkObject.TrySetParent(StartOfRound.Instance.propsContainer);

            PayloadCartController controller = instance.GetComponent<PayloadCartController>();
            ActiveCart = controller;
            controller?.ConfigureServer(ContractsBootstrap.PayloadBalance.ForRisk(MoonContractState.GetCurrentRiskTier()).CargoValue.Value);

            // Post-spawn sanity line: the pusher's standing test against the live instance, and the
            // placer's route verdict (#1305 R22); only the D22 last resort is unproved, a warning.
            bool pusherClear = PayloadCartController.IsPusherStandClear(
                instance.transform.position, instance.transform.rotation, out string pusherBlocker, instance.transform);
            string line = $"[MoonContracts.Payload] PAYLOAD_SPAWN source={source} pos=({position.x:0.0},{position.y:0.0},{position.z:0.0}) yaw={rotation.eulerAngles.y:0.0} " +
                $"pusherClear={pusherClear}{(pusherClear ? string.Empty : $" pusherBlocker='{pusherBlocker}'")} score={placement.Score:0.0} runway={placement.RunwayCells} " +
                $"entrancePath={placement.EntrancePathDistance:0.0}m band={PayloadCartPlacer.SpawnBandName(placement.EntrancePathDistance)} proved={(placement.RouteProved ? 1 : 0)}/{placement.RouteProofs} " +
                $"route={(placement.RouteProved ? "proved" : $"unproved:lastResort refusal={placement.RouteRefusal}")}";
            if (placement.RouteProved)
                ContractsBootstrap.Log?.LogInfo(line);
            else
                ContractsBootstrap.Log?.LogWarning(line);
            if (!pusherClear)
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Payload] Payload cart spawned with no standing room behind the handle ('{pusherBlocker}').");

            // Wheel support is the ledge half of #877: the fit math proves grid cells, this proves
            // the four wheels actually rest on something. Diagnostic only, so a false positive
            // never moves a cart the crew can see.
            if (!PayloadCartGroundSampler.TrySampleCartGround(instance.transform, new PayloadCartGroundSampler.SampleState(), out PayloadCartGroundSampler.GroundSample sample)
                || !sample.HasStableSupport)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Payload] PAYLOAD_SPAWN unsupported: no stable wheel support under the spawn pose (freshMask={sample.FreshMask}).");
            }

            return true;
        }
    }
}
