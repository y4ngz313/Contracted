using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Contracts.Survey;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Contracts.WasteDisposal
{
    /// <summary>Server-authoritative incinerator gameplay; presentation is delegated to the rig.</summary>
    internal sealed class WasteDisposalIncineratorController : NetworkBehaviour
    {
        internal enum IncineratorState : byte
        {
            Loading = 0,
            Burning = 1,
            Paused = 2,
            Malfunctioned = 3,
            Complete = 4,
        }

        private const float ScanIntervalSeconds = 0.25f;
        private const float EnemyStrikeScanIntervalSeconds = 0.25f;
        private const float BurnNoiseRange = 24f;
        private const float BurnNoiseLoudness = 0.9f;
        private const float InteractDistanceMeters = 4f;
        private const int OverlapCapacity = 96;
        private static readonly Collider[] OverlapBuffer = new Collider[OverlapCapacity];

        private readonly NetworkVariable<byte> _state = new NetworkVariable<byte>((byte)IncineratorState.Loading);
        private readonly NetworkVariable<bool> _doorOpen = new NetworkVariable<bool>(true);
        private readonly NetworkVariable<int> _moonScrapValue = new NetworkVariable<int>(0);
        private readonly NetworkVariable<float> _targetPercent = new NetworkVariable<float>(0f);
        private readonly NetworkVariable<int> _requiredValue = new NetworkVariable<int>(0);
        private readonly NetworkVariable<int> _stagedValue = new NetworkVariable<int>(0);
        private readonly NetworkVariable<int> _stagedCount = new NetworkVariable<int>(0);
        private readonly NetworkVariable<int> _burnedValue = new NetworkVariable<int>(0);
        private readonly NetworkVariable<int> _burnedCount = new NetworkVariable<int>(0);
        private readonly NetworkVariable<float> _cycleDuration = new NetworkVariable<float>(0f);
        private readonly NetworkVariable<float> _pausedRemaining = new NetworkVariable<float>(0f);
        private readonly NetworkVariable<double> _burnEndServerTime = new NetworkVariable<double>(0d);
        private readonly NetworkVariable<float> _repairHoldSeconds = new NetworkVariable<float>(4f);

        private readonly List<CommittedItem> _committed = new List<CommittedItem>();
        private readonly List<GrabbableObject> _eligible = new List<GrabbableObject>();
        private readonly HashSet<int> _seenItems = new HashSet<int>();
        private WasteDisposalIncineratorRig _rig;
        private float _nextScanAt;
        private float _nextNoiseAt;
        private double _nextEmberAt;
        private double _nextMalfunctionAt = double.PositiveInfinity;
        private double _enemyStrikeCooldownUntil;
        private float _nextEnemyStrikeScanAt;
        private float _malfunctionIntervalMinSeconds = 24f;
        private float _malfunctionIntervalMaxSeconds = 48f;
        private float _enemyStrikeRangeMeters = 3.2f;
        private float _enemyStrikeCooldownSeconds = 8f;
        private BundyAIBase _activeBundyStrike;
        private float _bundyStrikeStopAt;
        private int _placementSequence;

        /// <summary>
        /// #406. Server-only: the client whose START press committed the burn now running; the
        /// completion is credited to them. The rule is the start press rather than a "dominant
        /// loader" because two people loading equal value has no deterministic winner, while the
        /// press is a single unambiguous server-observed act. <c>ulong.MaxValue</c> (a debug start,
        /// or a lost sender) falls back to the crew-wide fan-out.
        ///
        /// The id is latched at the start press and read back a whole burn cycle later, so its
        /// owner may have left in between. It is deliberately NOT re-validated here: the roster
        /// check lives at the single choke point in
        /// <c>ContractProgressionEvents.ResolveCredited</c>, which reroutes a departed starter's
        /// credit to the crew. A starter who merely died during the burn still keeps it.
        /// </summary>
        private ulong _burnStarterClientId = ContractProgressionEventIds.UnknownPlayerClientId;

        /// <summary>Server-only. Running repair number this round, so repeat repairs dedupe cleanly.</summary>
        private int _repairSequence;

        internal IncineratorState State => (IncineratorState)_state.Value;
        internal bool DoorOpen => _doorOpen.Value;
        internal int MoonScrapValue => _moonScrapValue.Value;
        internal float TargetPercent => _targetPercent.Value;
        internal int RequiredValue => _requiredValue.Value;
        internal int StagedValue => _stagedValue.Value;
        internal int StagedCount => _stagedCount.Value;
        internal int BurnedValue => _burnedValue.Value;
        internal int BurnedCount => _burnedCount.Value;
        internal float CycleDurationSeconds => _cycleDuration.Value > 0f ? _cycleDuration.Value : CalculateBurnDuration(_stagedValue.Value);
        internal float RepairHoldSeconds => Mathf.Max(0.5f, _repairHoldSeconds.Value);
        internal float RemainingSeconds
        {
            get
            {
                if (State == IncineratorState.Burning)
                    return Mathf.Max(0f, (float)(_burnEndServerTime.Value - ServerNow));
                if (State == IncineratorState.Paused || State == IncineratorState.Malfunctioned)
                    return Mathf.Max(0f, _pausedRemaining.Value);
                if (State == IncineratorState.Complete)
                    return 0f;
                return CalculateBurnDuration(_stagedValue.Value);
            }
        }
        internal bool IsComplete => State == IncineratorState.Complete;
        internal Vector3 MarkerPosition => _rig?.CctvMarker != null ? _rig.CctvMarker.position : transform.position + Vector3.up * 1.6f;

        internal void ServerInitialize(int moonScrapValue, float targetPercent)
        {
            if (!IsServer)
                return;

            WasteDisposalBalanceConfig balance = ContractsBootstrap.WasteDisposalBalance;
            _repairHoldSeconds.Value = Mathf.Clamp(balance?.RepairHoldSeconds.Value ?? 4f, 0.5f, 15f);
            _malfunctionIntervalMinSeconds = Mathf.Clamp(
                balance?.MalfunctionIntervalMinSeconds.Value ?? 24f,
                5f,
                180f);
            _malfunctionIntervalMaxSeconds = Mathf.Max(
                _malfunctionIntervalMinSeconds,
                Mathf.Clamp(balance?.MalfunctionIntervalMaxSeconds.Value ?? 48f, 5f, 240f));
            _enemyStrikeRangeMeters = Mathf.Clamp(balance?.EnemyStrikeRangeMeters.Value ?? 3.2f, 1f, 8f);
            _enemyStrikeCooldownSeconds = Mathf.Clamp(balance?.EnemyStrikeCooldownSeconds.Value ?? 8f, 1f, 60f);
            _moonScrapValue.Value = Mathf.Max(1, moonScrapValue);
            _targetPercent.Value = Mathf.Clamp(targetPercent, 1f, 100f);
            _requiredValue.Value = CalculateRequiredValue(_moonScrapValue.Value, _targetPercent.Value);
            _doorOpen.Value = true;
            _state.Value = (byte)IncineratorState.Loading;
            _nextMalfunctionAt = double.PositiveInfinity;
            ServerRefreshStagedItems();
        }

        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            _state.OnValueChanged += OnByteChanged;
            _doorOpen.OnValueChanged += OnBoolChanged;
            _targetPercent.OnValueChanged += OnFloatChanged;
            _cycleDuration.OnValueChanged += OnFloatChanged;
            _pausedRemaining.OnValueChanged += OnFloatChanged;
            _burnEndServerTime.OnValueChanged += OnDoubleChanged;
            _moonScrapValue.OnValueChanged += OnIntChanged;
            _requiredValue.OnValueChanged += OnIntChanged;
            _stagedValue.OnValueChanged += OnIntChanged;
            _repairHoldSeconds.OnValueChanged += OnFloatChanged;
            _stagedCount.OnValueChanged += OnIntChanged;
            _burnedValue.OnValueChanged += OnIntChanged;
            _burnedCount.OnValueChanged += OnIntChanged;
            WasteDisposalContract.RegisterIncinerator(this);
            EnsureRig();
            RefreshPresentation();
        }

        public override void OnNetworkDespawn()
        {
            _state.OnValueChanged -= OnByteChanged;
            _doorOpen.OnValueChanged -= OnBoolChanged;
            _targetPercent.OnValueChanged -= OnFloatChanged;
            _cycleDuration.OnValueChanged -= OnFloatChanged;
            _pausedRemaining.OnValueChanged -= OnFloatChanged;
            _burnEndServerTime.OnValueChanged -= OnDoubleChanged;
            _moonScrapValue.OnValueChanged -= OnIntChanged;
            _requiredValue.OnValueChanged -= OnIntChanged;
            _stagedValue.OnValueChanged -= OnIntChanged;
            _repairHoldSeconds.OnValueChanged -= OnFloatChanged;
            _stagedCount.OnValueChanged -= OnIntChanged;
            _burnedValue.OnValueChanged -= OnIntChanged;
            _burnedCount.OnValueChanged -= OnIntChanged;
            WasteDisposalContract.UnregisterIncinerator(this);
            base.OnNetworkDespawn();
        }

        public override void OnDestroy()
        {
            StopBundyStrikeAnimation();
            if (IsServer && State != IncineratorState.Complete)
                RestoreCommittedItems();
            _rig?.Dispose();
            _rig = null;
            WasteDisposalContract.UnregisterIncinerator(this);
            base.OnDestroy();
        }

        private void Update()
        {
            EnsureRig();
            _rig?.Tick(State, DoorOpen, StagedValue, RequiredValue, TargetPercent, CycleDurationSeconds, RemainingSeconds, RepairHoldSeconds);
            if (!IsServer)
                return;

            if (_activeBundyStrike != null && Time.realtimeSinceStartup >= _bundyStrikeStopAt)
                StopBundyStrikeAnimation();

            double now = ServerNow;

            if (State == IncineratorState.Loading && Time.realtimeSinceStartup >= _nextScanAt)
            {
                _nextScanAt = Time.realtimeSinceStartup + ScanIntervalSeconds;
                ServerRefreshStagedItems();
            }
            else if (State == IncineratorState.Burning)
            {
                if (RemainingSeconds <= 0.01f)
                {
                    CompleteBurnOnServer();
                    return;
                }
                if (now >= _nextMalfunctionAt)
                {
                    ServerMalfunction("periodic fault");
                    return;
                }

                if (now >= _enemyStrikeCooldownUntil && Time.realtimeSinceStartup >= _nextEnemyStrikeScanAt)
                {
                    _nextEnemyStrikeScanAt = Time.realtimeSinceStartup + EnemyStrikeScanIntervalSeconds;
                    if (TryTriggerNearbyEnemyStrike())
                        return;
                }

                if (Time.realtimeSinceStartup >= _nextNoiseAt)
                {
                    _nextNoiseAt = Time.realtimeSinceStartup + 2.5f;
                    RoundManager.Instance?.PlayAudibleNoise(transform.position, BurnNoiseRange, BurnNoiseLoudness);
                }
                if (now >= _nextEmberAt)
                {
                    int variant = UnityEngine.Random.Range(0, 2);
                    float intensity = UnityEngine.Random.Range(0.85f, 1.35f);
                    PlayEmberBurstClientRpc(variant, intensity);
                    _nextEmberAt = now + UnityEngine.Random.Range(4.5f, 9f);
                }
            }
        }

        private void EnsureRig()
        {
            if (_rig != null)
                return;
            WasteDisposalBalanceConfig balance = ContractsBootstrap.WasteDisposalBalance;
            float width = balance?.FixtureWidthMeters.Value ?? 2.4f;
            float height = balance?.FixtureHeightMeters.Value ?? 2.85f;
            float depth = balance?.FixtureDepthMeters.Value ?? 1.75f;
            Vector3 captureCenter = balance?.CaptureCenterLocal ?? new Vector3(0f, 1.05f, 0.25f);
            Vector3 captureSize = balance?.CaptureSizeMeters ?? new Vector3(1.55f, 1f, 1.1f);
            _rig = WasteDisposalIncineratorVisualAdapter.Build(transform, width, height, depth, captureCenter, captureSize);
            if (_rig == null)
                return;
            _rig.StartRequested += OnLocalStartRequested;
            _rig.RepairRequested += OnLocalRepairRequested;
            _rig.StopRequested += OnLocalStopRequested;
            _rig.DoorRequested += OnLocalDoorRequested;
            _rig.PlaceRequested += OnLocalPlaceRequested;
        }

        private void OnLocalStartRequested() => RequestStartServerRpc();
        private void OnLocalStopRequested() => RequestPauseServerRpc();
        private void OnLocalDoorRequested() => RequestToggleDoorServerRpc();
        private void OnLocalRepairRequested() => RequestRepairServerRpc();
        private void OnLocalPlaceRequested(PlayerControllerB player) => RequestPlaceHeldItemServerRpc();

        [ServerRpc(RequireOwnership = false)]
        internal void RequestStartServerRpc(ServerRpcParams rpcParams = default)
        {
            ulong senderClientId = rpcParams.Receive.SenderClientId;
            if (!IsSenderCloseEnough(senderClientId))
                return;
            bool wasPaused = State == IncineratorState.Paused;
            bool accepted = wasPaused ? ServerResumeBurn() : ServerTryStartBurn(senderClientId);
            // #406: a resume is a continuation of the same burn, so it does not steal the credit
            // from whoever committed it.
            if (accepted) PulseButtonClientRpc(startButton: true);
        }

        [ServerRpc(RequireOwnership = false)]
        internal void RequestPauseServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!IsSenderCloseEnough(rpcParams.Receive.SenderClientId) || !ServerPauseBurn())
                return;
            PulseButtonClientRpc(startButton: false);
        }

        [ServerRpc(RequireOwnership = false)]
        internal void RequestRepairServerRpc(ServerRpcParams rpcParams = default)
        {
            // #406: the repairer used to be discarded with the rest of the rpcParams.
            ulong senderClientId = rpcParams.Receive.SenderClientId;
            if (!IsSenderCloseEnough(senderClientId) || !ServerRepair(senderClientId))
                return;
            PulseButtonClientRpc(startButton: true);
        }

        [ServerRpc(RequireOwnership = false)]
        internal void RequestToggleDoorServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!IsSenderCloseEnough(rpcParams.Receive.SenderClientId) || State != IncineratorState.Loading)
                return;
            _doorOpen.Value = !_doorOpen.Value;
        }

        [ServerRpc(RequireOwnership = false)]
        internal void RequestPlaceHeldItemServerRpc(ServerRpcParams rpcParams = default)
        {
            PlayerControllerB player = FindPlayer(rpcParams.Receive.SenderClientId);
            if (player == null || State != IncineratorState.Loading || !DoorOpen || !IsSenderCloseEnough(rpcParams.Receive.SenderClientId))
                return;
            GrabbableObject item = player.currentlyHeldObjectServer;
            if (!IsEligibleHeldScrap(item))
                return;
            Vector3 localPosition = NextPlacementLocalPosition(item);
            int soundIndex = _placementSequence++ % 2;
            ConfirmPlaceHeldItemClientRpc((int)player.playerClientId, item.NetworkObjectId, localPosition, soundIndex);
        }

        [ClientRpc]
        private void ConfirmPlaceHeldItemClientRpc(int playerId, ulong itemNetworkObjectId, Vector3 localPosition, int soundIndex)
        {
            EnsureRig();
            _rig?.PlayItemPlaced(soundIndex);
            PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
            if (local == null || (int)local.playerClientId != playerId || !local.isHoldingObject || local.isGrabbingObjectAnimation)
                return;
            GrabbableObject held = local.currentlyHeldObjectServer;
            if (!IsEligibleHeldScrap(held) || held.NetworkObjectId != itemNetworkObjectId)
                return;
            local.DiscardHeldObject(placeObject: true, NetworkObject, localPosition, matchRotationOfParent: false);
        }

        [ClientRpc]
        private void PulseButtonClientRpc(bool startButton)
        {
            EnsureRig();
            if (startButton) _rig?.PulseStartButton();
            else _rig?.PulseStopButton();
        }

        [ClientRpc]
        private void PlayEmberBurstClientRpc(int variant, float intensity)
        {
            EnsureRig();
            _rig?.PlayEmberBurst(variant, intensity);
        }

        [ClientRpc]
        private void PlayMaskedStrikeClientRpc(ulong enemyNetworkObjectId, Vector3 strikePoint)
        {
            NetworkManager manager = NetworkManager.Singleton;
            if (manager?.SpawnManager == null
                || !manager.SpawnManager.SpawnedObjects.TryGetValue(enemyNetworkObjectId, out NetworkObject enemyObject))
                return;

            MaskedPlayerEnemy masked = enemyObject.GetComponent<MaskedPlayerEnemy>();
            if (masked == null || masked.isEnemyDead)
                return;

            try
            {
                masked.transform.LookAt(new Vector3(strikePoint.x, masked.transform.position.y, strikePoint.z));
                masked.transform.eulerAngles = new Vector3(0f, masked.transform.eulerAngles.y, 0f);
                Animator animator = masked.creatureAnimator != null
                    ? masked.creatureAnimator
                    : masked.GetComponentInChildren<Animator>();
                animator?.SetTrigger("HitEnemy");
            }
            catch { }
        }

        /// <summary>
        /// <paramref name="starterClientId"/> is the client whose START press commits this burn,
        /// or <see cref="ContractProgressionEventIds.UnknownPlayerClientId"/> when there is no
        /// identifiable presser - the completion credit then fans out crew-wide.
        /// </summary>
        internal bool ServerTryStartBurn(ulong starterClientId)
        {
            if (!IsServer || State != IncineratorState.Loading || DoorOpen)
                return false;
            int stagedValue = ServerCollectEligibleItems(_eligible);
            int stagedCount = _eligible.Count;
            SetStagedTotals(stagedValue, stagedCount);
            if (stagedValue < RequiredValue)
                return false;

            _committed.Clear();
            for (int i = 0; i < _eligible.Count; i++)
            {
                GrabbableObject item = _eligible[i];
                if (item == null) continue;
                CommittedItem committed = CommittedItem.Capture(item);
                committed.Lock();
                _committed.Add(committed);
            }
            if (_committed.Count == 0)
                return false;

            _burnStarterClientId = starterClientId;
            float duration = CalculateBurnDuration(stagedValue);
            _cycleDuration.Value = duration;
            _pausedRemaining.Value = duration;
            _burnEndServerTime.Value = ServerNow + duration;
            _state.Value = (byte)IncineratorState.Burning;
            _nextNoiseAt = 0f;
            _nextEmberAt = ServerNow + UnityEngine.Random.Range(4.5f, 8f);
            _enemyStrikeCooldownUntil = ServerNow + _enemyStrikeCooldownSeconds;
            ScheduleNextPeriodicMalfunction();
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.WasteDisposal] Burn committed: items={stagedCount}, value={stagedValue}, required={RequiredValue}, duration={duration:0.0}s.");
            return true;
        }

        private bool ServerPauseBurn()
        {
            if (!IsServer || State != IncineratorState.Burning)
                return false;
            float remaining = RemainingSeconds;
            if (remaining <= 0.01f)
            {
                CompleteBurnOnServer();
                return false;
            }
            _pausedRemaining.Value = remaining;
            _burnEndServerTime.Value = 0d;
            _nextMalfunctionAt = double.PositiveInfinity;
            _state.Value = (byte)IncineratorState.Paused;
            return true;
        }

        private bool ServerResumeBurn()
        {
            if (!IsServer || State != IncineratorState.Paused || _pausedRemaining.Value <= 0.01f)
                return false;
            _burnEndServerTime.Value = ServerNow + _pausedRemaining.Value;
            _state.Value = (byte)IncineratorState.Burning;
            _nextNoiseAt = 0f;
            _nextEmberAt = ServerNow + UnityEngine.Random.Range(4.5f, 8f);
            _enemyStrikeCooldownUntil = ServerNow + _enemyStrikeCooldownSeconds;
            ScheduleNextPeriodicMalfunction();
            return true;
        }

        private void CompleteBurnOnServer()
        {
            if (!IsServer || State != IncineratorState.Burning)
                return;

            int committedValue = StagedValue;
            int committedCount = StagedCount;
            for (int i = 0; i < _committed.Count; i++)
                _committed[i]?.DestroyServerObject(this);
            _committed.Clear();
            _burnedValue.Value = committedValue;
            _burnedCount.Value = committedCount;
            _pausedRemaining.Value = 0f;
            _burnEndServerTime.Value = 0d;
            _nextMalfunctionAt = double.PositiveInfinity;
            _state.Value = (byte)IncineratorState.Complete;
            WasteDisposalContract.RecordSuccessfulBurn(committedValue);
            ContractProgressionEvents.PublishWasteDisposalCompleted(
                committedCount, "incineration cycle", _burnStarterClientId);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.WasteDisposal] Burn complete: items={committedCount}, value={committedValue}, "
                + (ContractProgressionEventIds.IsAttributable(_burnStarterClientId)
                    ? $"credited to client {_burnStarterClientId}."
                    : "no identifiable starter; credited crew-wide."));
            _burnStarterClientId = ContractProgressionEventIds.UnknownPlayerClientId;
        }

        private bool ServerMalfunction(string reason)
        {
            if (!IsServer || State != IncineratorState.Burning)
                return false;

            _pausedRemaining.Value = RemainingSeconds;
            _burnEndServerTime.Value = 0d;
            _nextMalfunctionAt = double.PositiveInfinity;
            _state.Value = (byte)IncineratorState.Malfunctioned;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.WasteDisposal] Incinerator malfunctioned ({reason}); remaining={_pausedRemaining.Value:0.0}s.");
            return true;
        }

        /// <summary>
        /// Server-authoritative repair. <paramref name="repairerClientId"/> is the client who held
        /// the repair interact to completion, or
        /// <see cref="ContractProgressionEventIds.UnknownPlayerClientId"/> for crew-wide credit.
        /// </summary>
        private bool ServerRepair(ulong repairerClientId)
        {
            if (!IsServer
                || State != IncineratorState.Malfunctioned
                || _pausedRemaining.Value <= 0.01f)
                return false;

            _burnEndServerTime.Value = ServerNow + _pausedRemaining.Value;
            _state.Value = (byte)IncineratorState.Burning;
            _nextNoiseAt = 0f;
            _nextEmberAt = ServerNow + UnityEngine.Random.Range(4.5f, 8f);
            _enemyStrikeCooldownUntil = ServerNow + _enemyStrikeCooldownSeconds;
            ScheduleNextPeriodicMalfunction();
            _repairSequence++;
            ContractProgressionEvents.PublishWasteDisposalRepaired(
                _repairSequence, "incinerator repair", repairerClientId);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.WasteDisposal] Incinerator repaired (#{_repairSequence}); burn resumed with {_pausedRemaining.Value:0.0}s remaining.");
            return true;
        }

        private void ServerRefreshStagedItems()
        {
            if (!IsServer || State != IncineratorState.Loading)
                return;
            int value = ServerCollectEligibleItems(_eligible);
            SetStagedTotals(value, _eligible.Count);
        }

        private int ServerCollectEligibleItems(List<GrabbableObject> destination)
        {
            destination.Clear();
            _seenItems.Clear();
            WasteDisposalBalanceConfig balance = ContractsBootstrap.WasteDisposalBalance;
            Vector3 localCenter = balance?.CaptureCenterLocal ?? new Vector3(0f, 1.05f, 0.25f);
            Vector3 size = balance?.CaptureSizeMeters ?? new Vector3(1.55f, 1f, 1.1f);
            Vector3 worldCenter = transform.TransformPoint(localCenter);
            Vector3 halfExtents = Vector3.Scale(size * 0.5f, Abs(transform.lossyScale));
            int count = Physics.OverlapBoxNonAlloc(worldCenter, halfExtents, OverlapBuffer, transform.rotation, ~0, QueryTriggerInteraction.Collide);
            int total = 0;
            for (int i = 0; i < count; i++)
            {
                Collider overlap = OverlapBuffer[i];
                OverlapBuffer[i] = null;
                GrabbableObject item = overlap != null ? overlap.GetComponentInParent<GrabbableObject>() : null;
                if (!IsEligibleChamberScrap(item)) continue;
                int id = item.GetInstanceID();
                if (!_seenItems.Add(id)) continue;
                destination.Add(item);
                total += Mathf.Max(0, item.scrapValue);
            }
            if (count >= OverlapCapacity)
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.WasteDisposal] Capture overlap buffer filled; excess colliders were ignored.");
            return total;
        }

        internal static bool IsEligibleHeldScrap(GrabbableObject item)
        {
            return IsEligibleScrapBase(item);
        }

        private static bool IsEligibleChamberScrap(GrabbableObject item)
        {
            if (!IsEligibleScrapBase(item))
                return false;
            return !item.isHeld && item.playerHeldBy == null && !item.isPocketed && !item.heldByPlayerOnServer;
        }

        private static bool IsEligibleScrapBase(GrabbableObject item)
        {
            if (item == null || item.itemProperties == null || !item.itemProperties.isScrap || item.scrapValue <= 0)
                return false;
            if (item.GetComponentInChildren<WasteDisposalItemProtection>(true) != null)
                return false;
            if (item.GetComponentInChildren<BreachHardDriveItem>(true) != null
                || item.GetComponentInChildren<BreachDrillItem>(true) != null
                || item.GetComponentInChildren<SurveyDroneItem>(true) != null)
                return false;
            return true;
        }

        private Vector3 NextPlacementLocalPosition(GrabbableObject item)
        {
            WasteDisposalBalanceConfig balance = ContractsBootstrap.WasteDisposalBalance;
            Vector3 center = balance?.CaptureCenterLocal ?? new Vector3(0f, 1.05f, 0.25f);
            Vector3 size = balance?.CaptureSizeMeters ?? new Vector3(1.55f, 1f, 1.1f);
            int slot = _placementSequence % 12;
            int column = slot % 4;
            int row = (slot / 4) % 3;
            float xStep = Mathf.Min(0.29f, size.x / 5f);
            float zStep = Mathf.Min(0.2f, size.z / 4f);
            float verticalOffset = item?.itemProperties != null ? Mathf.Clamp(item.itemProperties.verticalOffset, 0f, 0.35f) : 0.08f;
            return center + new Vector3((column - 1.5f) * xStep, -size.y * 0.38f + verticalOffset, (row - 1f) * zStep);
        }

        private static float CalculateBurnDuration(int value)
        {
            return WasteDisposalIncineratorRules.CalculateBurnDurationSeconds(value);
        }

        internal static int CalculateRequiredValue(int moonScrapValue, float targetPercent)
        {
            return Mathf.Max(1, Mathf.CeilToInt(Mathf.Max(1, moonScrapValue) * Mathf.Clamp(targetPercent, 1f, 100f) / 100f));
        }

        private void SetStagedTotals(int value, int count)
        {
            value = Mathf.Max(0, value);
            count = Mathf.Max(0, count);
            if (_stagedValue.Value != value) _stagedValue.Value = value;
            if (_stagedCount.Value != count) _stagedCount.Value = count;
        }

        private void RestoreCommittedItems()
        {
            for (int i = 0; i < _committed.Count; i++)
                _committed[i]?.Restore();
            _committed.Clear();
        }

        private void RefreshPresentation()
        {
            EnsureRig();
            _rig?.ApplyState(State, DoorOpen, StagedValue, RequiredValue, CycleDurationSeconds, RemainingSeconds, RepairHoldSeconds);
        }

        private void OnByteChanged(byte previous, byte current) => RefreshPresentation();
        private void OnBoolChanged(bool previous, bool current) => RefreshPresentation();
        private void OnIntChanged(int previous, int current) => RefreshPresentation();
        private void OnFloatChanged(float previous, float current) => RefreshPresentation();
        private void OnDoubleChanged(double previous, double current) => RefreshPresentation();

        private bool IsSenderCloseEnough(ulong senderClientId)
        {
            PlayerControllerB player = FindPlayer(senderClientId);
            return player != null && Vector3.Distance(player.transform.position, transform.position) <= InteractDistanceMeters;
        }

        private static PlayerControllerB FindPlayer(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null) return null;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.actualClientId == clientId) return player;
            }
            return null;
        }

        private static Vector3 Abs(Vector3 value) => new Vector3(Mathf.Abs(value.x), Mathf.Abs(value.y), Mathf.Abs(value.z));

        private static double ServerNow => NetworkManager.Singleton != null
            ? NetworkManager.Singleton.ServerTime.Time
            : Time.timeAsDouble;

        private void ScheduleNextPeriodicMalfunction()
        {
            float delay = UnityEngine.Random.Range(
                _malfunctionIntervalMinSeconds,
                _malfunctionIntervalMaxSeconds);
            _nextMalfunctionAt = ServerNow + Mathf.Max(1f, delay);
        }

        private bool TryTriggerNearbyEnemyStrike()
        {
            List<EnemyAI> enemies = RoundManager.Instance?.SpawnedEnemies;
            if (enemies == null)
                return false;

            EnemyAI closest = null;
            float closestDistanceSquared = _enemyStrikeRangeMeters * _enemyStrikeRangeMeters;
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI candidate = enemies[i];
                if (!IsIncineratorSaboteur(candidate) || candidate.isOutside)
                    continue;

                float distanceSquared = (candidate.transform.position - transform.position).sqrMagnitude;
                if (distanceSquared > closestDistanceSquared)
                    continue;
                closest = candidate;
                closestDistanceSquared = distanceSquared;
            }

            if (closest == null)
                return false;

            Vector3 strikePoint = MarkerPosition;
            if (closest is BundyAIBase bundy)
            {
                try
                {
                    StopBundyStrikeAnimation();
                    bundy.OnBatterySiegeSwingPulse(strikePoint);
                    _activeBundyStrike = bundy;
                    _bundyStrikeStopAt = Time.realtimeSinceStartup + 1.5f;
                }
                catch (Exception exception)
                {
                    ContractsBootstrap.Log?.LogDebug(
                        $"[MoonContracts.WasteDisposal] Bundy strike animation skipped: {exception.Message}");
                }
            }
            else
            {
                NetworkObject enemyObject = closest.GetComponent<NetworkObject>();
                if (enemyObject != null && enemyObject.IsSpawned)
                    PlayMaskedStrikeClientRpc(enemyObject.NetworkObjectId, strikePoint);
            }

            return ServerMalfunction(GetEnemyName(closest) + " strike");
        }

        private void StopBundyStrikeAnimation()
        {
            if (_activeBundyStrike != null)
            {
                try
                {
                    _activeBundyStrike.OnBatterySiegeAnimationStopped();
                }
                catch
                {
                    // Ignore teardown races while the enemy despawns or changes state.
                }
            }
            _activeBundyStrike = null;
            _bundyStrikeStopAt = 0f;
        }

        private static bool IsIncineratorSaboteur(EnemyAI enemy)
        {
            return enemy != null
                   && !enemy.isEnemyDead
                   && (enemy is MaskedPlayerEnemy || enemy is BundyAIBase);
        }

        private static string GetEnemyName(EnemyAI enemy)
        {
            if (enemy == null)
                return "enemy";
            if (enemy.enemyType != null && !string.IsNullOrWhiteSpace(enemy.enemyType.enemyName))
                return enemy.enemyType.enemyName;
            return string.IsNullOrWhiteSpace(enemy.name) ? "enemy" : enemy.name;
        }

        private sealed class CommittedItem
        {
            private readonly GrabbableObject _item;
            private readonly bool _grabbable;
            private readonly bool _grabbableToEnemies;
            private readonly Rigidbody _body;
            private readonly bool _bodyKinematic;
            private readonly bool _bodyDetectCollisions;
            private readonly Collider[] _colliders;
            private readonly bool[] _colliderEnabled;

            private CommittedItem(GrabbableObject item)
            {
                _item = item;
                _grabbable = item.grabbable;
                _grabbableToEnemies = item.grabbableToEnemies;
                _body = item.GetComponent<Rigidbody>();
                _bodyKinematic = _body != null && _body.isKinematic;
                _bodyDetectCollisions = _body == null || _body.detectCollisions;
                _colliders = item.GetComponentsInChildren<Collider>(true);
                _colliderEnabled = new bool[_colliders.Length];
                for (int i = 0; i < _colliders.Length; i++)
                    _colliderEnabled[i] = _colliders[i] != null && _colliders[i].enabled;
            }

            internal static CommittedItem Capture(GrabbableObject item) => new CommittedItem(item);

            internal void Lock()
            {
                if (_item == null) return;
                _item.grabbable = false;
                _item.grabbableToEnemies = false;
                _item.EnablePhysics(false);
                if (_body != null)
                {
                    _body.velocity = Vector3.zero;
                    _body.angularVelocity = Vector3.zero;
                    _body.detectCollisions = false;
                    _body.isKinematic = true;
                }
                for (int i = 0; i < _colliders.Length; i++)
                    if (_colliders[i] != null) _colliders[i].enabled = false;
            }

            internal void Restore()
            {
                if (_item == null) return;
                _item.grabbable = _grabbable;
                _item.grabbableToEnemies = _grabbableToEnemies;
                _item.EnablePhysics(true);
                if (_body != null)
                {
                    _body.isKinematic = _bodyKinematic;
                    _body.detectCollisions = _bodyDetectCollisions;
                }
                for (int i = 0; i < _colliders.Length; i++)
                    if (_colliders[i] != null) _colliders[i].enabled = _colliderEnabled[i];
            }

            internal void DestroyServerObject(MonoBehaviour owner)
            {
                if (_item == null) return;
                NetworkObject networkObject = _item.GetComponent<NetworkObject>();
                if (networkObject != null && networkObject.IsSpawned) networkObject.Despawn(true);
                else if (owner != null) UnityEngine.Object.Destroy(_item.gameObject);
            }
        }
    }
}
