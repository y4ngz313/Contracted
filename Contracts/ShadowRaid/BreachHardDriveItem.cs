using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
namespace Y4NGZCompany.Contracts.ShadowRaid
{
    internal sealed class BreachHardDriveItem : NetworkBehaviour
    {
        private readonly NetworkVariable<int> _originContract =
            new NetworkVariable<int>((int)MoonContractType.None);
        private readonly NetworkVariable<ulong> _missionNetworkId =
            new NetworkVariable<ulong>(0UL);
        private readonly NetworkVariable<bool> _eventCreated =
            new NetworkVariable<bool>(false);
        private readonly NetworkVariable<bool> _found =
            new NetworkVariable<bool>(false);
        private readonly NetworkVariable<ulong> _soldierCarrierId =
            new NetworkVariable<ulong>(0UL);
        private readonly NetworkVariable<bool> _extracted =
            new NetworkVariable<bool>(false);

        private PhysicsProp _prop;
        private ScanNodeProperties _scanNode;
        private bool _reportedLocal;
        private ulong _lastAppliedSoldierCarrierId;
        private bool _lastAppliedExtracted;
        private bool _everSoldierCarriedServer;

        internal MoonContractType OriginContract => (MoonContractType)_originContract.Value;
        internal ulong MissionNetworkId => _missionNetworkId.Value;
        internal bool EventCreated => _eventCreated.Value;
        internal bool Found => _found.Value;
        internal ulong SoldierCarrierNetworkId => _soldierCarrierId.Value;
        internal bool Extracted => _extracted.Value;
        internal PhysicsProp PhysicsProp
        {
            get
            {
                EnsureRuntimeParts();
                return _prop;
            }
        }
        internal bool CanBePickedUpBySoldier
        {
            get
            {
                EnsureRuntimeParts();
                return IsServer
                    && !_extracted.Value
                    && _soldierCarrierId.Value == 0UL
                    && _prop != null
                    && !_prop.isHeld
                    && !_prop.isHeldByEnemy
                    && !_prop.isPocketed
                    && !_prop.heldByPlayerOnServer
                    && _prop.playerHeldBy == null;
            }
        }
        internal bool HasPlayerTouched => _prop != null
            && (_prop.isHeld
                || _prop.isPocketed
                || _prop.hasBeenHeld
                || _prop.heldByPlayerOnServer
                || _prop.isInShipRoom
                || _prop.isInElevator);
        internal bool HasBeenSoldierCarriedServer => _everSoldierCarriedServer;

        private void Awake()
        {
            EnsureRuntimeParts();
            EnsureScrapValue();
        }

        private void Start()
        {
            EnsureRuntimeParts();
            EnsureScrapValue();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _found.OnValueChanged += OnFoundChanged;
            _soldierCarrierId.OnValueChanged += OnSoldierCarrierChanged;
            _extracted.OnValueChanged += OnExtractedChanged;
            BlacksiteHardDriveObjectiveService.RegisterDrive(this);
            if (_found.Value)
                ApplyFoundLocal("replicated state");
            ApplyReplicatedPhysicalState(force: true);
        }

        public override void OnNetworkDespawn()
        {
            _found.OnValueChanged -= OnFoundChanged;
            _soldierCarrierId.OnValueChanged -= OnSoldierCarrierChanged;
            _extracted.OnValueChanged -= OnExtractedChanged;
            BlacksiteHardDriveObjectiveService.UnregisterDrive(this);
            base.OnNetworkDespawn();
        }

        private void Update()
        {
            EnsureRuntimeParts();
            EnsureScrapValue();
            ApplyReplicatedPhysicalState(force: false);
            TrackHeldFrameDiagnostic();
            if (IsServer && !_found.Value && HasPlayerTouched)
                ReportFoundServer("server item state");
        }

        // #789 diagnostic: one hand-frame measurement half a second after each pickup, once vanilla
        // has parented the drive to the holder's item anchor. Bounded per item.
        private float _heldFrameLogAt;
        private int _heldFrameLogsEmitted;
        private bool _wasPlayerHeldLastFrame;
        private const int MaxHeldFrameLogs = 3;

        private void TrackHeldFrameDiagnostic()
        {
            bool held = _prop != null && _prop.playerHeldBy != null && (_prop.isHeld || _prop.parentObject != null);
            if (held && !_wasPlayerHeldLastFrame)
                _heldFrameLogAt = Time.time + 0.5f;
            else if (!held)
                _heldFrameLogAt = 0f;
            _wasPlayerHeldLastFrame = held;

            if (!held || _heldFrameLogAt <= 0f || Time.time < _heldFrameLogAt || _heldFrameLogsEmitted >= MaxHeldFrameLogs)
                return;

            _heldFrameLogAt = 0f;
            _heldFrameLogsEmitted++;
            Transform visual = transform.Find("BreachHardDriveVisual");
            HeldItemFrameDiagnostics.Log("[MoonContracts.Blacksite] Hard drive", _prop, visual, "hard drive");
        }

        internal void ConfigureObjectiveServer(
            MoonContractType originContract,
            ulong missionNetworkId,
            bool eventCreated)
        {
            if (!IsServer)
                return;

            _originContract.Value = (int)originContract;
            _missionNetworkId.Value = missionNetworkId;
            _eventCreated.Value = eventCreated;
            _extracted.Value = false;
            if (_found.Value)
                ApplyFoundLocal("objective configured");
        }

        internal void DetachMissionServer()
        {
            if (!IsServer)
                return;
            _missionNetworkId.Value = 0UL;
            _eventCreated.Value = false;
        }

        internal bool SetSoldierCarrierServer(
            BlacksiteSoldierAI carrier,
            string reason)
        {
            EnsureRuntimeParts();
            if (!IsServer
                || carrier == null
                || carrier.NetworkObject == null
                || !carrier.NetworkObject.IsSpawned
                || carrier.isEnemyDead
                || _prop == null
                || _prop.playerHeldBy != null
                || _prop.heldByPlayerOnServer
                || _prop.isPocketed
                || _extracted.Value)
            {
                return false;
            }

            ulong carrierId = carrier.NetworkObject.NetworkObjectId;
            _soldierCarrierId.Value = carrierId;
            _everSoldierCarriedServer = true;
            _extracted.Value = false;
            ApplyReplicatedPhysicalState(force: true);
            // #1219 B3: while the Blacksite manifest holds this drive, its ShadowDrive record is the
            // custody truth and ReconcileDriveCarriers is the one writer of the mission's
            // (SoldierHeld, carrier) pair; only a drive outside the manifest reports it here.
            if (TryGetMission(out BlacksiteRetrievalMissionController mission) && !mission.Objectives.ContainsObject(NetworkObjectId))
                mission.SetDriveStateServer(BlacksiteHardDriveState.SoldierHeld, carrierId);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Hard drive attached to soldier {carrier.MemberIndex} " +
                $"without replacing NetworkObject identity. reason={reason}.");
            return true;
        }

        // Manifest phase is authoritative during Blacksite retrieval. Keep the legacy
        // NetworkVariable coherent without replaying a second item-placement writer.
        internal void ReconcileManifestCarrierServer(ulong carrierId)
        {
            if (!IsServer || _soldierCarrierId.Value == carrierId) return;
            _soldierCarrierId.Value = carrierId;
        }
        internal bool DropFromSoldierServer(Vector3 worldPosition, string reason)
        {
            if (!IsServer || _soldierCarrierId.Value == 0UL || _extracted.Value)
                return false;

            _soldierCarrierId.Value = 0UL;
            _extracted.Value = false;
            BlacksiteDriveCarrier.ApplyLooseVisual(this, worldPosition);
            ApplyDriveDropClientRpc(worldPosition);
            // #1219 B3: the manifest's record says Loose for a drive it holds (ReconcileDriveCarriers).
            if (TryGetMission(out BlacksiteRetrievalMissionController mission) && !mission.Objectives.ContainsObject(NetworkObjectId))
                mission.SetDriveStateServer(BlacksiteHardDriveState.Loose, 0UL);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Carrier dropped hard drive at reachable point {worldPosition}. reason={reason}.");
            return true;
        }

        internal bool MarkExtractedServer(Transform extractionAnchor)
        {
            if (!IsServer || extractionAnchor == null || _soldierCarrierId.Value == 0UL)
                return false;

            _soldierCarrierId.Value = 0UL;
            _extracted.Value = true;
            BlacksiteDriveCarrier.ApplyExtractedVisual(this, extractionAnchor);
            ApplyExtractedClientRpc();
            if (TryGetMission(out BlacksiteRetrievalMissionController mission))
                mission.SetDriveStateServer(BlacksiteHardDriveState.Extracted, 0UL);
            return true;
        }

        internal bool ShouldCleanupUntouchedEventDrive()
        {
            EnsureRuntimeParts();
            return _prop != null
                   && BlacksiteRetrievalRules.ShouldCleanupUntouchedEventDrive(
                       _eventCreated.Value,
                       _prop.isHeld,
                       _prop.isPocketed,
                       _prop.hasBeenHeld,
                       _prop.heldByPlayerOnServer,
                       _prop.isInShipRoom,
                       _prop.isInElevator,
                       _soldierCarrierId.Value != 0UL || _extracted.Value);
        }

        internal BlacksiteHardDriveState GetPhysicalState(out ulong carrierNetworkId)
        {
            EnsureRuntimeParts();
            carrierNetworkId = 0UL;
            if (_prop == null)
                return BlacksiteHardDriveState.Missing;
            if (_extracted.Value)
                return BlacksiteHardDriveState.Extracted;
            if (_soldierCarrierId.Value != 0UL)
            {
                carrierNetworkId = _soldierCarrierId.Value;
                return BlacksiteHardDriveState.SoldierHeld;
            }
            if (_prop.isInShipRoom || _prop.isInElevator)
                return BlacksiteHardDriveState.InShip;
            if (_prop.playerHeldBy != null
                || _prop.isPocketed
                || _prop.heldByPlayerOnServer)
            {
                NetworkObject player = _prop.playerHeldBy != null
                    ? _prop.playerHeldBy.GetComponent<NetworkObject>()
                    : null;
                carrierNetworkId = player != null ? player.NetworkObjectId : 0UL;
                return BlacksiteHardDriveState.PlayerHeld;
            }

            if (_prop.isHeld)
                return BlacksiteHardDriveState.PlayerHeld;
            return BlacksiteHardDriveState.Loose;
        }

        private bool TryGetMission(out BlacksiteRetrievalMissionController mission)
        {
            mission = null;
            ulong missionId = _missionNetworkId.Value;
            NetworkManager manager = NetworkManager.Singleton;
            if (missionId == 0UL
                || manager?.SpawnManager?.SpawnedObjects == null
                || !manager.SpawnManager.SpawnedObjects.TryGetValue(
                    missionId,
                    out NetworkObject missionObject)
                || missionObject == null)
            {
                return false;
            }

            mission = missionObject.GetComponent<BlacksiteRetrievalMissionController>();
            return mission != null;
        }

        private void ApplyReplicatedPhysicalState(bool force)
        {
            if (!IsSpawned)
                return;
            if (TryGetMission(out var owner) && owner.Objectives.ContainsObject(NetworkObjectId)) return;
            if (!force
                && _lastAppliedSoldierCarrierId == _soldierCarrierId.Value
                && _lastAppliedExtracted == _extracted.Value)
            {
                return;
            }

            bool applied = true;
            if (_extracted.Value)
            {
                if (TryGetMission(out BlacksiteRetrievalMissionController mission))
                    BlacksiteDriveCarrier.ApplyExtractedVisual(this, GetOrCreateExtractionAnchor(mission.transform));
                else
                    applied = false;
            }
            else if (_soldierCarrierId.Value != 0UL)
            {
                applied = false;
                NetworkManager manager = NetworkManager.Singleton;
                if (manager?.SpawnManager?.SpawnedObjects != null
                    && manager.SpawnManager.SpawnedObjects.TryGetValue(
                        _soldierCarrierId.Value,
                        out NetworkObject carrierObject)
                    && carrierObject != null)
                {
                    BlacksiteSoldierAI carrier = carrierObject.GetComponent<BlacksiteSoldierAI>()
                        ?? carrierObject.GetComponentInChildren<BlacksiteSoldierAI>(true);
                    if (carrier != null)
                    {
                        BlacksiteDriveCarrier.ApplyHeldVisual(this, carrier);
                        applied = true;
                    }
                }
            }

            if (!applied)
                return;
            _lastAppliedSoldierCarrierId = _soldierCarrierId.Value;
            _lastAppliedExtracted = _extracted.Value;
        }

        private void OnSoldierCarrierChanged(ulong _, ulong __)
        {
            ApplyReplicatedPhysicalState(force: true);
        }

        private void OnExtractedChanged(bool _, bool __)
        {
            ApplyReplicatedPhysicalState(force: true);
        }

        [ClientRpc]
        private void ApplyDriveDropClientRpc(Vector3 worldPosition)
        {
            BlacksiteDriveCarrier.ApplyLooseVisual(this, worldPosition);
            _lastAppliedSoldierCarrierId = 0UL;
            _lastAppliedExtracted = false;
        }

        [ClientRpc]
        private void ApplyExtractedClientRpc()
        {
            ApplyReplicatedPhysicalState(force: true);
        }

        private static Transform GetOrCreateExtractionAnchor(Transform missionRoot)
        {
            Transform existing = missionRoot != null
                ? missionRoot.Find("BlacksiteExtractedDriveAnchor")
                : null;
            if (existing != null)
                return existing;
            if (missionRoot == null)
                return null;
            var anchor = new GameObject("BlacksiteExtractedDriveAnchor").transform;
            anchor.SetParent(missionRoot, worldPositionStays: false);
            anchor.localPosition = new Vector3(0.65f, 0.8f, -0.4f);
            anchor.localRotation = Quaternion.Euler(0f, 90f, 0f);
            return anchor;
        }

        private void EnsureRuntimeParts()
        {
            if (_prop == null)
                _prop = GetComponent<PhysicsProp>();
            if (_scanNode == null)
                _scanNode = GetComponentInChildren<ScanNodeProperties>(true);
        }

        private void EnsureScrapValue()
        {
            if (_prop != null && _prop.scrapValue != BreachHardDriveItemRegistry.ScrapValue)
                _prop.SetScrapValue(BreachHardDriveItemRegistry.ScrapValue);
            if (_scanNode != null)
                _scanNode.scrapValue = BreachHardDriveItemRegistry.ScrapValue;
        }

        private void ReportFoundServer(string source)
        {
            if (!IsServer || _found.Value)
                return;
            _found.Value = true;
            // #406: "Recover the hard drive" is a named objective and paid nothing. The finder is
            // whoever is holding it at the moment the server calls it recovered; a drive reported
            // found with no carrier (already in the ship, or soldier-carried) pays crew-wide.
            // Only the ShadowRaid drive publishes: a Blacksite Retrieval drive is a different
            // objective and would be mislabelled by this kind.
            if (OriginContract == MoonContractType.ShadowRaid)
            {
                ContractProgressionEvents.PublishShadowRaidHardDriveRecovered(
                    ContractProgressionEvents.ResolveClientId(ResolveCarrier()), NetworkObjectId, source);
            }
            ApplyFoundLocal(source);
        }

        /// <summary>Server-side best identification of who is physically holding the drive.</summary>
        private PlayerControllerB ResolveCarrier()
        {
            EnsureRuntimeParts();
            return _prop != null ? _prop.playerHeldBy : null;
        }

        private void OnFoundChanged(bool _, bool found)
        {
            if (found)
                ApplyFoundLocal("replicated state change");
        }

        private void ApplyFoundLocal(string source)
        {
            if (_reportedLocal)
                return;
            _reportedLocal = true;
            BlacksiteHardDriveObjectiveService.NotifyHardDriveFoundServer(this, source);
        }
    }
}
