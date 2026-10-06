using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.InputSystem;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Escalations.BlacksiteRetrieval;
namespace Y4NGZCompany.Contracts.Payload
{
    /// <summary>Server-authoritative cart motion and contract state; clients own input and presentation.</summary>
    [DefaultExecutionOrder(220)]
    public partial class PayloadCartController : NetworkBehaviour
    {
        internal const ulong NoPlayer = ulong.MaxValue;

        // Marker child transforms authored on PayloadCart.fbx.
        private const string HandleGrabChildName = "HandleGrab";
        private const string MagnetPointChildName = "MagnetPoint";

        private const float MarkerHeight = 0.6f;

        private readonly NetworkVariable<ulong> _attachedClientId = new NetworkVariable<ulong>(
            NoPlayer,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        // The cart's own side, replicated (#1305 C2): written by the portal crossing
        // (CommitPortalCrossingServer) and the round reset (ConfigureServer), never from a pusher.
        private readonly NetworkVariable<bool> _outside = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<bool> _delivered = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        // Failure replicates like delivery (#440): FailPayloadServer only ever runs on the host, so
        // without this every client kept PayloadContract.IsFailed == false and rendered the objective
        // list unfailed with the "Secure the cart to the hull" label. Server-write / everyone-read so a
        // late joiner also picks the failure up from the initial value in OnNetworkSpawn.
        private readonly NetworkVariable<bool> _failed = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private readonly NetworkVariable<int> _cargoValue = new NetworkVariable<int>(
            0,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        // #880: "the crew found the cart" is a one-way fact, and it replicates so every peer's Locate
        // step reads the same. The objective used to read the live attached flag, so letting go of the
        // handle inside the facility un-ticked Locate and replayed its check-off on the next grab.
        private readonly NetworkVariable<bool> _located = new NetworkVariable<bool>(
            false,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        private Transform _handleGrab;
        private Transform _magnetPoint;

        // Push presentation: optional third-person camera and procedural body pose follow the motor.
        // The baked body push clip is intentionally not used at runtime; the third-person rig is
        // posed procedurally in LateUpdate so vanilla locomotion keeps driving legs/torso safely.
        private readonly PayloadThirdPersonView _thirdPersonView = new PayloadThirdPersonView();
        private readonly PayloadPushViewArms _pushViewArms = new PayloadPushViewArms();

        // Observer-side mirror of the push pose for a REMOTE pusher (#441). Third-person body pose only —
        // the camera swap, input clamps, steering and speed changes stay on the pushing client.
        private readonly RemotePushPresentation _remotePush = new RemotePushPresentation();
        private PayloadHandleInteractable _handleInteractable;
        private bool _localPushActive;
        private PlayerControllerB _localPushPlayer;
        private float _savedMovementSpeed;
        private bool _movementSpeedSaved;
        private Animator _pushBodyAnimator;
        private float _savedBodyAnimatorSpeed = 1f;
        private bool _bodyAnimatorSpeedSaved;
        // #1071: resolved once per attach; SetBool against a controller that does not declare the
        // parameter warns on every call.
        private bool _pushBodyAnimatorHasLocomotion;
        // #1071 follow-up: the local pusher's ownership of Sideways/crouching/Jumping/FallNoJump for
        // the length of one grab. Captured on the first pass and handed back by RestorePushAnimator.
        private readonly PayloadPushLocomotion.SecondaryFlags _pushBodySecondary = new PayloadPushLocomotion.SecondaryFlags();
        // #1071 follow-up: the latched side of the pelvis-fit gate's hysteresis band.
        private bool _pushPelvisFitWalking;
        private float _attachTime;
        private PayloadPushBodyRig _pushBodyRig;
        private Transform _leftGripAnchor;
        private Transform _rightGripAnchor;
        private Vector3 _lastPushPosition;
        private bool _lastPushPositionValid;
        private float _smoothedHorizontalSpeed;
        // #1071 follow-up: the same planar velocity projected onto the body's facing, so the walk
        // cycle can run backwards while S pulls the cart.
        private float _smoothedForwardSpeed;
        private bool _subscribed;
        private float _savedGrabDistance;
        private bool _grabDistanceSaved;
        private bool _releaseKeyDecisionLogged;
        private readonly PayloadCartGroundSampler.SampleState _groundSampleState = new PayloadCartGroundSampler.SampleState();
        private readonly Dictionary<ulong, float> _serverPilotSeconds = new Dictionary<ulong, float>();
        private bool _killZoneFailed;
        private static bool s_payloadControlsTipShown;

        // Kill-zone sweep scratch state (#716 B7). One shared buffer: the sweep only ever runs on the
        // host, one cart at a time, inside a single synchronous call.
        private const int KillZoneHitBufferSize = 128;
        private const float KillZoneCheckInterval = 0.25f;
        private const int MaxRejectedKillZoneColliders = 512;
        private static readonly Collider[] s_killZoneHits = new Collider[KillZoneHitBufferSize];
        private readonly HashSet<int> _rejectedKillZoneColliders = new HashSet<int>();
        private float _nextKillZoneCheckAt;
        // #1070: the strike counter, the arm window and the resolved fall threshold live together in a
        // Unity-free struct so the round boundary can drop all three on one edge, and so the reset can
        // be pinned headlessly (PayloadCartMotionChecks).
        private PayloadKillZoneWatchdogState _killZone;
        private string _killZoneLedgeLoggedId;

        // Vanilla "Interact" action, resolved once (#716 E7). The release poll used the raw eKey, which
        // ignores a rebound interact key and every non-keyboard binding.
        private static InputAction s_interactAction;
        private static bool s_interactActionResolved;

        private const float GripHalfWidth = 0.25f;
        private const float PushLeanDegrees = 8f;
        private const float PushAnimatorStillSpeed = 0.05f;
        private const float PushAnimatorVelocitySmooth = 12f;
        // #1071 follow-up: the pelvis-fit gate's OWN edge, separate from the Walking bool's. The
        // animator bool can chatter across PushAnimatorStillSpeed harmlessly - the walk cycle is
        // playing at a rate of ~0.01 down there and the pose barely moves - but the same edge now also
        // decides a hard 0.40 m pelvis write, and a cart creeping at 0.05 m/s against a wall or up a
        // slope would pop the body in and out of the crouch every few frames. Same band as the
        // carrier's (PayloadCartController.Blacksite.cs), and the exit is the Walking threshold itself
        // so the crouch can never be held on a body the locomotion rule calls stationary.
        private const float PushPelvisFitWalkEnterSpeed = 0.12f;
        private const float PushPelvisFitWalkExitSpeed = 0.05f;
        private const float RunawaySlopeAngle = 28f;
        private const float RunawayMaxSpeed = 7f;
        // #880: write-off thresholds. The centre probe replaces the padded overlap box, floor reach is
        // the navmesh radius that proves the cart can still be pushed, the arm window keeps a cart that
        // fell straight through a thin kill slab under scrutiny, and the poll count is how long a loss
        // must persist (KillZoneCheckInterval * KillZoneConfirmPolls seconds) before it is final.
        private const float KillZoneCentreProbeRadius = 0.1f;
        private const float KillZoneFloorReach = 1.5f;
        private const float KillZoneArmWindow = 2f;
        private const int KillZoneConfirmPolls = 3;
        private const float KillFallBelowLowestKnownY = 18f;

        // Handle geometry shared by the cart motor, player correction and NPC portal transfers.
        private const float FollowDistance = 1.4f;  // metres ahead of the pusher
        private const float FollowHeight = 0.1f;     // metres above the pusher transform (feet) origin
        private const float FollowYawOffset = 180f;  // handle faces the pusher

        // Visual upscale applied per-client at spawn (cart read a touch small/short in-game).
        // Uniform to keep the wheels round; ~0.85 m wide at 1.15x stays door-safe (base 0.74). TUNE.
        private const float VisualScale = 1.15f;

        // #1305 H1: the cart's half of the one magnet rule (MagnetRule). Server-only: the magnet the
        // cart is rising onto, and the root pose that puts its MagnetPoint on that magnet's dock.
        private IMagnetHost _magnetAttachHost;
        private Vector3 _magnetAttachRoot;
        private bool _deliveryStarted;
        private bool _caskUnsealed;

        public ulong AttachedClientId => _attachedClientId.Value;
        public bool IsAttached => _attachedClientId.Value != NoPlayer;
        public bool IsOutside => _outside.Value;
        public bool IsDelivered => _delivered.Value;
        public bool IsFailed => _failed.Value;
        /// <summary>One-way, replicated: a player has taken the handle at least once this round (#880).</summary>
        public bool IsLocated => _located.Value;
        internal static PayloadCartController LocalAttachedCart { get; private set; }
        private static PayloadBalanceConfig.RiskConfig RiskSettings =>
            ContractsBootstrap.PayloadBalance.ForRisk(MoonContractState.GetCurrentRiskTier());

        /// <summary>True on the client whose local player is currently pushing this cart.</summary>
        public bool IsAttachedToLocalPlayer
        {
            get
            {
                PlayerControllerB local = LocalPlayer();
                return local != null && IsAttached && _attachedClientId.Value == local.actualClientId;
            }
        }

        /// <summary>Where the CCTV objective marker anchors (top of the cart).</summary>
        public Vector3 MarkerPosition => transform.position + Vector3.up * MarkerHeight;

        /// <summary>#1305 H1: the cart's magnet plate, which a ship magnet's field and dock measure.</summary>
        public Transform MagnetPoint => _magnetPoint != null ? _magnetPoint : transform;

        /// <summary>
        /// Runs on every peer: the cart prefab is hashed and registered as a NetworkPrefab at plugin init
        /// (ContractAssetLoader.PreparePayloadCartPrefab), so clients now instantiate a real cart instead of
        /// dropping an unresolvable CreateObjectMessage (#434). Everything below must therefore be
        /// client-safe; the server-only haul/delivery work stays behind the IsServer gates in Update.
        /// </summary>
        public override void OnNetworkSpawn()
        {
            base.OnNetworkSpawn();
            PayloadPushPatches.EnsureApplied();
            ApplyVisualScale();
            // Keep the physical cage and the sweep volume identical; handle reach uses its own trigger.
            BoxCollider body = GetComponent<BoxCollider>();
            if (body != null) { body.center = MotionCenter; body.size = MotionHalfExtents * 2f; }
            if (GetComponent<PayloadCartAudio>() == null)
                gameObject.AddComponent<PayloadCartAudio>();
            PayloadCartAudio.EnsureAudioLoads(this);
            GetComponent<PayloadCartVisualJuice>()?.RefreshVisualRoot();
            ResolveMarkers();
            BuildHandleInteractable();
            PayloadCartHitbox.Ensure(this);
            _attachedClientId.OnValueChanged += OnAttachedClientChanged;
            _delivered.OnValueChanged += OnDeliveredChanged;
            _failed.OnValueChanged += OnFailedChanged;
            _stowedOnBlacksiteDeck.OnValueChanged += OnStowedOnBlacksiteDeckChanged;
            _subscribed = true;
            PayloadContract.RegisterCart(this);
            ApplyInitialReplicatedState();
        }

        /// <summary>
        /// Replay the current NetworkVariable values through the same handlers a change would use, so a
        /// client that spawns the cart late (mid-round join, or a scene/visibility re-spawn) still gets the
        /// failure/delivery objective state and the remote push presentation for whoever is already
        /// pushing. OnValueChanged only fires on subsequent writes, never for the state that arrived with
        /// the spawn payload. On the host this runs against the defaults and is a no-op.
        /// </summary>
        private void ApplyInitialReplicatedState()
        {
            if (_failed.Value)
                OnFailedChanged(false, true);
            if (_delivered.Value)
                OnDeliveredChanged(false, true);
            if (_attachedClientId.Value != NoPlayer)
                OnAttachedClientChanged(NoPlayer, _attachedClientId.Value);
            if (_stowedOnBlacksiteDeck.Value)
                OnStowedOnBlacksiteDeckChanged(false, true);
        }

        public override void OnNetworkDespawn()
        {
            if (_subscribed)
            {
                _attachedClientId.OnValueChanged -= OnAttachedClientChanged;
                _delivered.OnValueChanged -= OnDeliveredChanged;
                _failed.OnValueChanged -= OnFailedChanged;
                _stowedOnBlacksiteDeck.OnValueChanged -= OnStowedOnBlacksiteDeckChanged;
                _subscribed = false;
            }
            // #874: the cart is leaving the world with a soldier coupled to it - the round boundary
            // despawns it mid-haul - and the coupling has suspended his agent's own position
            // updates. Releasing here is what hands them back; without it the member is left with
            // a body no owner moves for the rest of the round.
            if (IsServer && IsNpcAttached) ReleaseNpcPusherServer("unsupported");
            if (_localPushActive)
                EndLocalPush();
            _remotePush.Stop("cart despawned");
            _rejectedKillZoneColliders.Clear();
            PayloadContract.UnregisterCart(this);
            base.OnNetworkDespawn();
        }

        private void OnDisable()
        {
            if (_localPushActive)
                EndLocalPush();
        }

        private void Update()
        {
            if (IsServer)
            {
                CheckKillZoneFailServer();
            }

            _handleInteractable?.RefreshState();
        }

        private void UpdateServerHaul()
        {
            if (!IsAttached)
                return;

            // A failed payload releases whoever is still holding it (#716 E9). FailPayloadServer already
            // detaches, but a failure raised anywhere else in the contract must not leave a pusher glued
            // to a dead cart.
            if (_failed.Value || PayloadContract.IsFailed)
            {
                DetachServer();
                return;
            }

            // Drop the attachment if the pusher became invalid (dead / left / gone).
            PlayerControllerB pusher = FindPlayer(_attachedClientId.Value);
            StartOfRound round = StartOfRound.Instance;
            if (pusher == null || pusher.isPlayerDead || !pusher.isPlayerControlled || (round != null && round.shipIsLeaving))
            {
                DetachServer();
                return;
            }

            float pilotDelta = Time.fixedDeltaTime;
            if (_serverPilotSeconds.TryGetValue(_attachedClientId.Value, out float existingPilotSeconds))
                _serverPilotSeconds[_attachedClientId.Value] = existingPilotSeconds + pilotDelta;
            else
                _serverPilotSeconds[_attachedClientId.Value] = pilotDelta;

            UpdatePlayerDriveServer(pusher);
        }

        /// <summary>
        /// #1066 U5: the cart's round is over. The kill-fall watchdog is the one end-of-round timer
        /// in the system that was still ticking after its contract had been resolved: on 2026-09-15
        /// the haul had already failed and been reported (quota applied, round-start snapshot
        /// cleared) when "Ship is leaving" destroyed the dungeon colliders, the cart then "fell",
        /// and a SECOND `Payload contract failed` was emitted ~7 s later for a contract that no
        /// longer existed. <c>PayloadContract.IsFailed</c> could not stop it because it reads a
        /// static that the round teardown resets, so the watchdog re-armed itself. This is the same
        /// departure signal every other contract in this folder gates on.
        /// </summary>
        private static bool RoundIsOver()
        {
            var start = StartOfRound.Instance;
            return start == null || start.inShipPhase || start.shipIsLeaving;
        }

        /// <summary>
        /// #1070: the round boundary is an EDGE for the watchdog, not just an early return. The #1066
        /// U5 guard below stopped the poll but froze the strike counter and the resolved fall threshold
        /// where they stood, so a cart that outlived its round completed that count on the next moon
        /// against the previous moon's threshold.
        /// </summary>
        private void NoteKillZoneRoundBoundaryServer()
        {
            if (!_killZone.NoteRoundOver(RoundIsOver()))
                return;

            ContractsBootstrap.Log?.LogInfo(
                "[MoonContracts.Payload.Fail] Write-off watchdog stood down for the round; strikes, arm window and fall threshold dropped.");
        }

        private void CheckKillZoneFailServer()
        {
            if (!IsServer)
                return;
            NoteKillZoneRoundBoundaryServer();

            // #1066 U5: _failed.Value is the cart's OWN replicated resolution and is added here
            // because the previous guard tested only the kill-zone path's private latch and the
            // resettable static - a contract failed by any other route left the watchdog live.
            if (_delivered.Value || _failed.Value || _killZoneFailed || PayloadContract.IsFailed || RoundIsOver()
                // #1070: and the watchdog may only speak for the cart the Payload contract is running.
                || !PayloadContract.IsActiveContractCart(this))
                return;

            // Perf (#716 B7): falling out of the world is not a frame-critical event, and the overlap
            // sweep is the most expensive thing this component does on the host. Poll it a few times a
            // second instead of every frame.
            if (Time.time < _nextKillZoneCheckAt)
                return;
            _nextKillZoneCheckAt = Time.time + KillZoneCheckInterval;

            // #880: a write-off is permanent and voids the contract, so it must never come from a
            // single poll. The cart is lost only when (a) its centre has entered a kill volume, or is
            // still inside the arming window after touching one, and no walkable floor is within
            // reach, or (b) it is below the fall threshold with no floor in reach; and either
            // condition has to hold for consecutive polls. A cart parked on a catwalk or pit ledge
            // whose kill volume was authored up to floor level used to be written off while it was
            // still fully reachable.
            string reason = null;
            Vector3 centre = transform.TransformPoint(MotionCenter);
            bool insideTrigger = TryFindContainingOutOfBoundsTrigger(centre, out string triggerName);
            if (insideTrigger)
                _killZone.ArmedUntil = Time.time + KillZoneArmWindow;

            if (insideTrigger || Time.time < _killZone.ArmedUntil)
            {
                if (HasReachableFloor(centre))
                {
                    if (insideTrigger && _killZoneLedgeLoggedId != triggerName)
                    {
                        _killZoneLedgeLoggedId = triggerName;
                        ContractsBootstrap.Log?.LogInfo(
                            $"[MoonContracts.Payload.Fail] Cart centre is inside kill-zone trigger '{triggerName}' at {centre} " +
                            $"but walkable floor is within {KillZoneFloorReach:0.##}m; treating it as a ledge, not a loss.");
                    }
                }
                else
                {
                    reason = insideTrigger
                        ? $"cart centre inside kill-zone trigger '{triggerName}' with no reachable floor"
                        : "cart left a kill-zone trigger with no reachable floor";
                }
            }

            if (reason == null)
            {
                float killY = ResolveKillFallY();
                if (transform.position.y < killY && !HasReachableFloor(centre))
                    reason = $"cart fell below kill fallback Y {killY:0.##} (cartY={transform.position.y:0.##}) with no reachable floor";
            }

            if (reason == null)
            {
                if (_killZone.Strikes > 0)
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Fail] Write-off cleared after {_killZone.Strikes} poll(s); the cart is back on reachable floor at {transform.position}.");
                _killZone.Strikes = 0;
                return;
            }

            _killZone.Strikes++;
            if (_killZone.Strikes == 1)
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload.Fail] Write-off pending: {reason} at {transform.position}; confirming over {KillZoneConfirmPolls} polls.");
            if (_killZone.Strikes < KillZoneConfirmPolls)
                return;

            FailPayloadServer($"{reason} for {_killZone.Strikes} consecutive polls");
        }

        /// <summary>
        /// True when the navmesh has walkable floor within <see cref="KillZoneFloorReach"/> of the
        /// cart centre. That is the same reachability a player has, so a cart with floor in reach can
        /// still be pushed and is never a loss (#880).
        /// </summary>
        private static bool HasReachableFloor(Vector3 centre)
        {
            return NavMesh.SamplePosition(centre, out _, KillZoneFloorReach, NavMesh.AllAreas);
        }

        private bool TryFindContainingOutOfBoundsTrigger(Vector3 centre, out string triggerName)
        {
            triggerName = null;
            // The reject list is a per-round memo, not a ledger: drop it once it grows past the cap so a
            // long haul across many rooms cannot let it accumulate without bound. Worst case the names of
            // the colliders around the cart are read once more after the flush.
            if (_rejectedKillZoneColliders.Count > MaxRejectedKillZoneColliders)
                _rejectedKillZoneColliders.Clear();

            // A small probe at the cart centre: the old padded, axis-aligned overlap box also counted a
            // kill volume the cart merely stood beside (#880).
            int hitCount = Physics.OverlapSphereNonAlloc(
                centre,
                KillZoneCentreProbeRadius,
                s_killZoneHits,
                Physics.AllLayers,
                QueryTriggerInteraction.Collide);
            for (int i = 0; i < hitCount; i++)
            {
                Collider collider = s_killZoneHits[i];
                if (collider == null || collider.transform == transform || collider.transform.IsChildOf(transform))
                    continue;

                // Whether a given collider is an out-of-bounds volume never changes, so a collider we
                // have already rejected is skipped without touching `.name` again (#716 B7): reading
                // UnityEngine.Object.name allocates a managed string on every access.
                int colliderId = collider.GetInstanceID();
                if (_rejectedKillZoneColliders.Contains(colliderId))
                    continue;

                OutOfBoundsTrigger trigger = collider.GetComponent<OutOfBoundsTrigger>() ?? collider.GetComponentInParent<OutOfBoundsTrigger>();
                bool looksLikeOutOfBounds = trigger != null
                    || collider.name.IndexOf("OutOfBounds", System.StringComparison.OrdinalIgnoreCase) >= 0
                    || collider.gameObject.name.IndexOf("OutOfBounds", System.StringComparison.OrdinalIgnoreCase) >= 0;
                if (!looksLikeOutOfBounds)
                {
                    _rejectedKillZoneColliders.Add(colliderId);
                    continue;
                }

                triggerName = trigger != null ? trigger.name : collider.name;
                return true;
            }

            return false;
        }

        private float ResolveKillFallY()
        {
            if (_killZone.FallThresholdResolved)
                return _killZone.FallThresholdY;

            float minY = transform.position.y;
            StartOfRound start = StartOfRound.Instance;
            if (start != null)
            {
                if (start.outsideShipSpawnPosition != null)
                    minY = Mathf.Min(minY, start.outsideShipSpawnPosition.position.y);
                if (start.playerSpawnPositions != null)
                {
                    for (int i = 0; i < start.playerSpawnPositions.Length; i++)
                    {
                        Transform spawn = start.playerSpawnPositions[i];
                        if (spawn != null)
                            minY = Mathf.Min(minY, spawn.position.y);
                    }
                }
            }

            EntranceTeleport[] entrances = FindObjectsOfType<EntranceTeleport>();
            for (int i = 0; i < entrances.Length; i++)
            {
                EntranceTeleport entrance = entrances[i];
                if (entrance == null)
                    continue;

                Transform point = entrance.entrancePoint != null ? entrance.entrancePoint : entrance.transform;
                if (point != null)
                    minY = Mathf.Min(minY, point.position.y);
            }

            if (NavMesh.SamplePosition(transform.position, out NavMeshHit navHit, 6f, NavMesh.AllAreas))
                minY = Mathf.Min(minY, navHit.position.y);

            float threshold = _killZone.ResolveFallThreshold(minY, KillFallBelowLowestKnownY);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload.Fail] Kill fall fallback resolved: lowestKnownY={minY:0.##}, " +
                $"thresholdY={threshold:0.##}, margin={KillFallBelowLowestKnownY:0.#}m.");
            return threshold;
        }

        private void FailPayloadServer(string reason)
        {
            // #1066 U5: and the failure PATH itself is a no-op once the contract has been resolved
            // or the round has ended, so a poll that was already in flight when the dungeon was
            // destroyed cannot re-fail a resolved contract and, with different frame ordering,
            // double-apply its penalty.
            if (!IsServer || _killZoneFailed || _delivered.Value || _failed.Value || RoundIsOver()
                // #1070: and only for the cart the Payload contract is currently running, so a cart
                // that outlived its moon cannot fail whatever contract the next moon has.
                || !PayloadContract.IsActiveContractCart(this))
                return;

            _killZoneFailed = true;
            _deliveryStarted = true;
            ReleaseNpcPusherServer();
            if (IsAttached)
                DetachServer();

            // Notify locally FIRST so the host log keeps the precise reason; the NetworkVariable write
            // below re-enters OnFailedChanged on this peer, and PayloadContract.NotifyFailed is idempotent
            // so the generic replicated reason cannot overwrite it (#440).
            PayloadContract.NotifyFailed(this, reason);
            _failed.Value = true;
            ShowPayloadTipClientRpc("Payload lost", "Contract failed: the cart fell out of bounds.", true);
            ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload.Fail] Payload contract failed: {reason}.");
        }

        private void LateUpdate()
        {
            ApplyNpcPushLate();
            ApplyLocalPushLate();
            // Runs only on peers where a REMOTE player is attached; a no-op on the pusher's own machine
            // and whenever nobody is attached (#441).
            _remotePush.LateApply(this);
        }

        private void ApplyLocalPushLate()
        {
            if (!_localPushActive)
                return;

            PlayerControllerB local = LocalPlayer();
            if (!CanContinueLocalPush(local))
            {
                EndLocalPush();
                return;
            }

            // Exit on interact: poll the bound action directly. In third-person you aren't aiming at the
            // handle trigger, so the raycast interact never fires. Guard the first moment after grabbing.
            if (Time.time - _attachTime > 0.4f
                && WasInteractPressedThisFrame()
                && ShouldDetachFromReleaseKey(local))
            {
                RequestDetachServerRpc();
                return;
            }

            UpdateLocalCartInput(local);
            // #1305 H6: vanilla LateUpdate glued the visor to the camera before the handle move and yaw clamp
            // below; carry that pose relative to the camera through them (no second lerp). A visor another
            // writer parked away from the camera (Bundy's death cam) is left alone.
            Transform visor = local.localVisor, visorTarget = local.localVisorTargetPoint;
            bool visorGlued = visor != null && visorTarget != null
                && (visor.position - visorTarget.position).sqrMagnitude < 1e-4f;
            Quaternion visorLag = visorGlued ? Quaternion.Inverse(visorTarget.rotation) * visor.rotation : default;
            FollowCartHandleLocal(local);
            PayloadPushPatches.ApplyPayloadSteering(local);
            if (visorGlued)
                visor.SetPositionAndRotation(visorTarget.position, visorTarget.rotation * visorLag);
            _thirdPersonView.LateApply(local);
            RefreshViewControlTip();
            ApplyPushPoseLate(local);
            _pushViewArms.LateApply(local, _thirdPersonView.IsApplied, _leftGripAnchor, _rightGripAnchor);
            // The cart motor owns travel; vanilla retains character gravity and collision.
            local.movementSpeed = 0f;
        }

        /// <summary>Server-only spawn configuration (mirrors DefuseBombController.ConfigureServer).</summary>
        internal void ConfigureServer(int cargoValue)
        {
            if (!IsServer)
                return;

            _attachedClientId.Value = NoPlayer;
            _outside.Value = false;
            _delivered.Value = false;
            _failed.Value = false;
            _located.Value = false;
            _cargoValue.Value = Mathf.Max(0, cargoValue);
            _killZoneFailed = false;
            // #1070: including the round-over edge latch, so the next round boundary is an edge again.
            _killZone.ResetForNewRound();
            _killZoneLedgeLoggedId = null;
            _rejectedKillZoneColliders.Clear();
            ResetCartMotion();
            _deliveryStarted = false;
            _magnetAttachHost = null;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload] Cart configured (cargoValue={_cargoValue.Value}).");
        }

        /// <summary>
        /// Server: mark the cart delivered and publish its pilots' times. RiseOntoMagnetServer calls it once
        /// the cart docks on the player ship's magnet.
        /// </summary>
        internal void BeginDeliveryServer()
        {
            if (!IsServer || _delivered.Value || PayloadContract.IsFailed)
                return;

            _delivered.Value = true;
            PublishSuccessfulPilotTimesServer();
            PayloadContract.NotifyDelivered(this);
        }

        private void PublishSuccessfulPilotTimesServer()
        {
            foreach (KeyValuePair<ulong, float> contribution in _serverPilotSeconds)
            {
                float seconds = Mathf.Max(0f, contribution.Value);
                if (seconds <= 0f)
                    continue;

                PlayerControllerB player = FindPlayer(contribution.Key);
                MoonContractRoundStats.RecordPayloadPilotSeconds(player, seconds);
                PublishPayloadPilotTimeClientRpc(contribution.Key, NetworkObjectId, seconds);
            }
        }

        [ClientRpc]
        private void PublishPayloadPilotTimeClientRpc(ulong playerClientId, ulong cartNetworkId, float seconds)
        {
            ContractProgressionEvents.PublishPayloadPilotSeconds(playerClientId, cartNetworkId, seconds);
        }

        // --- Push interaction + presentation (W4). ---

        /// <summary>Handle InteractTrigger callback (fires on the interacting client's local player).</summary>
        internal void OnHandleInteract(PlayerControllerB player)
        {
            PlayerControllerB local = LocalPlayer();
            // A failed cart is as inert as a delivered one (#716 E9): _failed replicates to every peer,
            // so this refusal is consistent on the host and on clients.
            if (player == null || local == null || player != local || IsDelivered || IsFailed)
                return;

            if (!IsAttached)
                RequestAttachServerRpc();
            else if (_attachedClientId.Value == local.actualClientId)
                RequestDetachServerRpc();
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestAttachServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!IsServer || IsAttached || IsNpcAttached || _delivered.Value || _failed.Value || PayloadContract.IsFailed)
                return;

            ulong sender = rpcParams.Receive.SenderClientId;
            PlayerControllerB player = FindPlayer(sender);
            if (player == null)
                return;

            Vector3 anchor = _handleGrab != null ? _handleGrab.position : transform.position;
            if (Vector3.Distance(player.transform.position, anchor) > HandleReachDistance)
                return;

            // #874 Q4: a cart on the Blacksite cable is mid-air; nobody takes its handle (no ride).
            // #1305 H1: a cart rising onto either ship's magnet is the ship's; a docked one may be taken back.
            if (IsOnBlacksiteCable || _magnetAttachHost != null) return;
            if (_blacksiteMagnetOwner.Value != ulong.MaxValue) ReleaseBlacksiteMagnetServer();
            if (_deliveryStarted || !CanGrabMotionServer())
                return;
            ResetDriveInput();
            _attachedClientId.Value = sender;
            if (!_located.Value)
                _located.Value = true;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload] Cart grabbed by client {sender}.");
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestDetachServerRpc(ServerRpcParams rpcParams = default)
        {
            if (!IsServer || _attachedClientId.Value != rpcParams.Receive.SenderClientId)
                return;

            DetachServer();
        }

        /// <summary>Asks the server to detach the local pusher; the server's sender check decides.</summary>
        internal void RequestDetach() => RequestDetachServerRpc();

        internal void RequestPayloadClang(Vector3 position, int force)
        {
            float volume = Mathf.Clamp01(0.55f + Mathf.Max(1, force) * 0.08f);
            float pitch = Random.Range(0.86f, 1.08f);
            if (IsServer)
            {
                PlayPayloadClangClientRpc(position, volume, pitch);
                return;
            }

            RequestPayloadClangServerRpc(position, volume, pitch);
        }

        [ServerRpc(RequireOwnership = false)]
        private void RequestPayloadClangServerRpc(Vector3 position, float volume, float pitch, ServerRpcParams rpcParams = default)
        {
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload.Hit] Server broadcasting payload clang from client={rpcParams.Receive.SenderClientId}, " +
                $"position={position}, volume={volume:0.##}, pitch={pitch:0.##}.");
            PlayPayloadClangClientRpc(position, volume, pitch);
        }

        [ClientRpc]
        private void PlayPayloadClangClientRpc(Vector3 position, float volume, float pitch)
        {
            PayloadCartAudio.PlayShovelClangAt(position, volume, pitch);
        }

        [ClientRpc]
        private void PlayPayloadMagnetEngageClientRpc(Vector3 position)
        {
            PayloadCartAudio.PlayMagnetEngageAt(position, 1f, 1f);
        }

        [ClientRpc]
        private void PlayPayloadMagnetClangClientRpc(Vector3 position, float volume, float pitch)
        {
            PayloadCartAudio.PlayMagnetClangAt(position, volume, pitch);
        }

        [ClientRpc]
        private void PlayPayloadRunawayClientRpc(Vector3 position)
        {
            PayloadCartAudio.PlayRunawayAt(position, 0.95f, 1f);
        }

        /// <summary>Server: release the cart back to the props container at its current world pose.</summary>
        private void DetachServer()
        {
            if (!IsServer)
                return;

            _attachedClientId.Value = NoPlayer;
            ResetDriveInput();

            NetworkObject netObj = NetworkObject;
            Transform props = StartOfRound.Instance != null ? StartOfRound.Instance.propsContainer : null;
            if (netObj != null && props != null)
                netObj.TrySetParent(props, worldPositionStays: true);
        }

        // --- Ship magnet (#1305 H1). ---

        /// <summary>
        /// #1305 H1 (R20): the cart's half of the one magnet rule, run from FixedUpdate after the haul
        /// and before the motor. True when the attach starts; FixedUpdate then runs the rise.
        /// For the nearer powered magnet whose field holds the cart's MagnetPoint: within
        /// <see cref="MagnetRule.DockRadius"/> of the dock, horizontally, the attach starts, held or
        /// not; otherwise an unheld cart is pulled toward the dock (StepCartMotion rolls it) and a held
        /// one is left to its holder. Outside every field nothing happens and nothing is logged.
        /// </summary>
        private bool TickShipMagnetServer()
        {
            if (!IsOutside || _blacksiteMagnetOwner.Value != ulong.MaxValue) return false;
            Vector3 plate = MagnetPoint.position;
            IMagnetHost host = NearerPoweredMagnet(PlayerShipMagnetHost.Instance, plate, null);
            BlacksiteRetrievalMissionController mission = BlacksiteHardDriveObjectiveService.ActiveMission;
            BlacksiteShipController ship = mission != null ? mission.GetComponent<BlacksiteShipController>() : null;
            if (ship != null) host = NearerPoweredMagnet(ship, plate, host);
            if (host == null) return false;
            Pose dock = host.DockPose;
            Vector3 toDock = Vector3.ProjectOnPlane(dock.position - plate, Vector3.up);
            if (toDock.sqrMagnitude <= MagnetRule.DockRadius * MagnetRule.DockRadius)
            {
                BeginMagnetAttachServer(host, dock);
                return true;
            }
            if (!IsAttached && !IsNpcAttached) _magnetPull = toDock.normalized * MagnetRule.PullSpeed;
            return false;
        }

        private static IMagnetHost NearerPoweredMagnet(IMagnetHost candidate, Vector3 plate, IMagnetHost best)
        {
            if (!candidate.LeverOn || !MagnetRule.InField(candidate, plate)) return best;
            return best == null || (candidate.MagnetPoint.position - plate).sqrMagnitude < (best.MagnetPoint.position - plate).sqrMagnitude
                ? candidate : best;
        }

        /// <summary>
        /// #1305 H1: the attach starts on the ground. The holder lets go and the cart squares to the
        /// fixed dock pose: xz and rotation are set without a sweep, which can move the root by more
        /// than DockRadius when the plate faces away; from then on it only rises. A Blacksite hull
        /// claims the cart now, so its NPC, player and manifest guards hold for the rise.
        /// </summary>
        private void BeginMagnetAttachServer(IMagnetHost host, Pose dock)
        {
            if (IsAttached) DetachServer();
            if (IsNpcAttached) ReleaseNpcPusherServer();
            Quaternion plateFromRoot = Quaternion.Inverse(transform.rotation) * MagnetPoint.rotation;
            Vector3 plateOffset = transform.InverseTransformPoint(MagnetPoint.position);
            Quaternion rotation = dock.rotation * Quaternion.Inverse(plateFromRoot);
            _magnetAttachRoot = dock.position - rotation * plateOffset;
            transform.SetPositionAndRotation(new Vector3(_magnetAttachRoot.x, transform.position.y, _magnetAttachRoot.z), rotation);
            ResetCartMotion();
            _magnetAttachHost = host;
            if (host is BlacksiteShipController ship)
            {
                _blacksiteMagnetOwner.Value = ship.NetworkObjectId; _blacksiteMagnetSeated = false;
                BlacksiteDiagnostics.Log("Cargo", $"magnet attaching payload cart object={NetworkObjectId} at {transform.position}.");
            }
            else ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Magnet] Cart attaching to the ship magnet at {transform.position}.");
            PlayPayloadMagnetEngageClientRpc(transform.position);
        }

        /// <summary>
        /// #1305 H1: straight up at <see cref="MagnetRule.AttachSpeed"/> with no sweep, the only
        /// off-ground motion the magnet makes. A lever turned off mid-rise drops the cart where it is.
        /// </summary>
        private void RiseOntoMagnetServer()
        {
            IMagnetHost host = _magnetAttachHost;
            if (!host.LeverOn)
            {
                if (host is BlacksiteShipController) ReleaseBlacksiteMagnetServer();
                else { _magnetAttachHost = null; ResetCartMotion(); }
                return;
            }
            Vector3 position = transform.position;
            position.y = Mathf.MoveTowards(position.y, _magnetAttachRoot.y, MagnetRule.AttachSpeed * Time.fixedDeltaTime);
            transform.position = position;
            if (position.y != _magnetAttachRoot.y) return;
            _magnetAttachHost = null;
            PlayPayloadMagnetClangClientRpc(MagnetPoint.position, 1f, .78f);
            if (host is BlacksiteShipController ship)
            {
                // The manifest loads the record when it sees IsOnBlacksiteMagnet.
                SeatOnBlacksiteShipServer(ship);
                BlacksiteDiagnostics.Log("Cargo", $"magnet docked payload cart object={NetworkObjectId} at {transform.position}.");
                return;
            }
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Magnet] Cart docked on the ship magnet at {transform.position}.");
            // #1305 H1: a cart that failed mid-rise stays docked; only a live one delivers.
            if (IsFailed || PayloadContract.IsFailed) return;
            _deliveryStarted = true;
            BeginDeliveryServer();
            SpawnCargoRewardServer(position + Vector3.up * 0.3f);
        }

        /// <summary>Server: reveal the sealed cargo as sellable scrap worth the cargo value.</summary>
        private void SpawnCargoRewardServer(Vector3 position)
        {
            Item rewardItem = ResolveRewardItem();
            if (rewardItem == null || rewardItem.spawnPrefab == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Payload] No vanilla scrap item available for the cargo reward.");
                return;
            }

            GameObject spawned = Instantiate(rewardItem.spawnPrefab, position, Quaternion.identity, StartOfRound.Instance?.propsContainer);
            if (spawned == null)
                return;
            if (!spawned.activeSelf)
                spawned.SetActive(true);

            GrabbableObject grabbable = spawned.GetComponent<GrabbableObject>();
            if (grabbable != null)
            {
                grabbable.itemProperties = rewardItem;
                grabbable.isInFactory = false;
                grabbable.fallTime = 0f;
                grabbable.hasHitGround = true;
                grabbable.reachedFloorTarget = true;
                grabbable.isHeld = false;
                int value = Mathf.Max(1, _cargoValue.Value);
                grabbable.SetScrapValue(value);
                if (RoundManager.Instance != null)
                    RoundManager.Instance.totalScrapValueInLevel += value;
            }

            NetworkObject netObj = spawned.GetComponent<NetworkObject>();
            if (netObj != null && !netObj.IsSpawned)
            {
                netObj.Spawn();
                if (StartOfRound.Instance?.propsContainer != null)
                    netObj.TrySetParent(StartOfRound.Instance.propsContainer);
            }

            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload] Cargo delivered; spawned reward value={_cargoValue.Value}.");
        }

        private static Item ResolveRewardItem()
        {
            var items = StartOfRound.Instance?.allItemsList?.itemsList;
            if (items == null)
                return null;

            Item fallback = null;
            for (int i = 0; i < items.Count; i++)
            {
                Item item = items[i];
                if (item == null || !item.isScrap || item.spawnPrefab == null)
                    continue;
                if (item.itemName != null && item.itemName.IndexOf("Gold", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    return item;
                fallback = fallback ?? item;
            }

            return fallback;
        }

        private void OnAttachedClientChanged(ulong previous, ulong current)
        {
            // The motor sweeps its own volume. Disable the physical root collider while the
            // local character follows the handle, so it cannot fight the cart correction.
            SetCartColliderEnabled(current == NoPlayer);

            PlayerControllerB local = LocalPlayer();
            ulong localId = local != null ? local.actualClientId : NoPlayer;

            if (current == localId && current != NoPlayer)
                BeginLocalPush(local);
            else if (previous == localId && previous != NoPlayer)
                EndLocalPush();

            // Observers mirror the same third-person push pose onto the remote pusher's body (#441).
            // A direct A -> B handoff stops A and starts B; NoPlayer stops whatever was running.
            UpdateRemotePushPresentation(current, localId);

            _handleInteractable?.RefreshState();
        }

        /// <summary>
        /// Arm/disarm the observer-side push presentation for a non-local pusher. Input, steering and the
        /// cart pose all stay owner/server-side; this only drives the body pose against the replicated cart.
        /// </summary>
        private void UpdateRemotePushPresentation(ulong current, ulong localId)
        {
            bool wantsRemote = current != NoPlayer && !(current == localId && localId != NoPlayer);
            if (!wantsRemote)
            {
                _remotePush.Stop(current == NoPlayer ? "cart released" : "local player is the pusher");
                return;
            }

            if (_remotePush.ClientId == current)
                return;

            _remotePush.Begin(current);
        }

        private void OnFailedChanged(bool previous, bool current)
        {
            if (!current)
                return;

            if (_localPushActive)
                EndLocalPush();
            _remotePush.Stop("payload failed");
            // Fires on every peer: FailPayloadServer is host-only, so before #440 clients never learned the
            // contract had failed and their objective HUD kept rendering the un-failed delivery label.
            // Idempotent on the host, which already recorded the precise reason.
            PayloadContract.NotifyFailed(this, "payload lost");
            _handleInteractable?.RefreshState();
        }

        private void OnDeliveredChanged(bool previous, bool current)
        {
            if (current)
            {
                if (_localPushActive)
                    EndLocalPush();
                _remotePush.Stop("cart delivered");
                // Fires on every peer, so clients pick the delivery up for their objective HUD instead of
                // waiting on a server-only NotifyDelivered call they never see. Idempotent on the host.
                PayloadContract.NotifyDelivered(this);
                UnsealCask();
            }
            _handleInteractable?.RefreshState();
        }

        /// <summary>Cask unseal cue (W3): flip the status light from red to green on every client.</summary>
        private void UnsealCask()
        {
            if (_caskUnsealed)
                return;
            _caskUnsealed = true;

            Color green = new Color(0.05f, 4.0f, 0.1f);
            Color greenBase = new Color(0.1f, 0.7f, 0.15f);
            var block = new MaterialPropertyBlock();
            Renderer[] renderers = GetComponentsInChildren<Renderer>(true);
            for (int r = 0; r < renderers.Length; r++)
            {
                Renderer renderer = renderers[r];
                if (renderer == null)
                    continue;

                Material[] mats = renderer.sharedMaterials;
                for (int i = 0; i < mats.Length; i++)
                {
                    if (mats[i] == null || mats[i].name.IndexOf("Cart_StatusLight", System.StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    renderer.GetPropertyBlock(block, i);
                    block.SetColor("_EmissiveColor", green);
                    block.SetColor("_EmissionColor", green);
                    block.SetColor("_BaseColor", greenBase);
                    block.SetColor("_Color", greenBase);
                    renderer.SetPropertyBlock(block, i);
                }
            }
        }

        private void BeginLocalPush(PlayerControllerB player)
        {
            if (_localPushActive || player == null)
                return;

            _localPushActive = true;
            _localPushPlayer = player;
            _attachTime = Time.time;
            LocalAttachedCart = this;
            InputSystem.onActionChange += OnViewActionChange;
            PayloadPushPatches.EnsureApplied();
            PayloadPushPatches.ResetRuntimeState();
            if (!_movementSpeedSaved)
            {
                _savedMovementSpeed = player.movementSpeed;
                _movementSpeedSaved = true;
            }
            player.movementSpeed = 0f;
            TryApplyPushAnimator(player);
            LogSlopeAttachDiagnostics(player);
            RefreshViewControlTip();
            ShowPayloadControlsTipOnce();
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.Payload] Local push started (first-person).");
            _pushViewArms.Take(player);
        }

        private void LogSlopeAttachDiagnostics(PlayerControllerB player)
        {
            Transform visual = transform.Find("PayloadCart_Visual");
            bool handleUnderVisual = visual != null && _handleGrab != null && (_handleGrab == visual || _handleGrab.IsChildOf(visual));
            bool leftGripUnderVisual = visual != null && _leftGripAnchor != null && _leftGripAnchor.IsChildOf(visual);
            bool rightGripUnderVisual = visual != null && _rightGripAnchor != null && _rightGripAnchor.IsChildOf(visual);

            if (PayloadCartGroundSampler.TrySampleCartGround(transform, _groundSampleState, out PayloadCartGroundSampler.GroundSample sample))
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Payload.Slope] Attach ground diagnostics: player={player?.actualClientId}, " +
                    $"cartPitch={sample.SignedPitchDegrees:0.#}deg, cartRoll={sample.SignedRollDegrees:0.#}deg, " +
                    $"avgHeightOffset={sample.AverageHeightOffset:0.###}m, wheelbase={sample.WheelBase:0.###}m, " +
                    $"track={sample.TrackWidth:0.###}m, freshHits={sample.FreshHitCount}, " +
                    $"handleUnderVisual={handleUnderVisual}, leftGripUnderVisual={leftGripUnderVisual}, rightGripUnderVisual={rightGripUnderVisual}.");
                return;
            }

            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload.Slope] Attach ground diagnostics: player={player?.actualClientId}, no initial ground sample, " +
                $"handleUnderVisual={handleUnderVisual}, leftGripUnderVisual={leftGripUnderVisual}, rightGripUnderVisual={rightGripUnderVisual}.");
        }

        private void EndLocalPush()
        {
            bool wasActive = _localPushActive;
            _localPushActive = false;
            if (LocalAttachedCart == this)
                LocalAttachedCart = null;
            InputSystem.onActionChange -= OnViewActionChange;
            PayloadPushPatches.ResetRuntimeState();
            _thirdPersonView.Disable();
            _pushViewArms.Release();
            RestoreViewControlTips();
            RestorePushAnimator();

            PlayerControllerB local = _localPushPlayer;
            if (local != null && _movementSpeedSaved)
                local.movementSpeed = _savedMovementSpeed;
            RestoreGrabDistance(local);
            _movementSpeedSaved = false;
            _localPushPlayer = null;
            _releaseKeyDecisionLogged = false;

            if (wasActive)
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.Payload] Local push ended.");
        }

        private void SaveAndExtendGrabDistance(PlayerControllerB player)
        {
            if (player == null || _grabDistanceSaved)
                return;

            _savedGrabDistance = player.grabDistance;
            _grabDistanceSaved = true;
            player.grabDistance = Mathf.Max(player.grabDistance, _savedGrabDistance + PayloadThirdPersonView.CameraDistanceMeters);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Payload.Controls] Extended interact range while attached: savedGrabDistance={_savedGrabDistance:0.##}, " +
                $"activeGrabDistance={player.grabDistance:0.##}, cameraOffset={PayloadThirdPersonView.CameraDistanceMeters:0.##}.");
        }

        private void RestoreGrabDistance(PlayerControllerB player)
        {
            if (player == null || !_grabDistanceSaved)
                return;

            player.grabDistance = _savedGrabDistance;
            ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Controls] Restored interact range after release: grabDistance={player.grabDistance:0.##}.");
            _savedGrabDistance = 0f;
            _grabDistanceSaved = false;
        }

        /// <summary>
        /// True on the frame the player pressed their bound interact control. Uses the vanilla "Interact"
        /// action so a rebound key or a controller works (#716 E7); falls back to the raw E key only when
        /// the action asset is unavailable.
        /// </summary>
        private static bool WasInteractPressedThisFrame()
        {
            if (!s_interactActionResolved)
            {
                InputActionAsset actions = null;
                try
                {
                    IngamePlayerSettings settings = IngamePlayerSettings.Instance;
                    PlayerInput input = settings != null ? settings.playerInput : null;
                    actions = input != null ? input.actions : null;
                    s_interactAction = actions != null ? actions.FindAction("Interact", false) : null;
                }
                catch
                {
                    s_interactAction = null;
                }

                // Only latch once the settings object exists, so an early poll cannot pin the fallback
                // for the rest of the session.
                s_interactActionResolved = actions != null;
                if (s_interactActionResolved && s_interactAction == null)
                {
                    ContractsBootstrap.Log?.LogWarning(
                        "[MoonContracts.Payload.Controls] Vanilla 'Interact' action not resolvable; falling back to Keyboard.current.eKey for payload release.");
                }
            }

            if (s_interactAction != null)
                return s_interactAction.WasPressedThisFrame();

            return Keyboard.current != null && Keyboard.current.eKey.wasPressedThisFrame;
        }

        private bool ShouldDetachFromReleaseKey(PlayerControllerB local)
        {
            InteractTrigger hover = local != null ? local.hoveringOverTrigger : null;
            bool hoveringInteractable = hover != null && hover.gameObject != null && hover.gameObject.activeInHierarchy && hover.interactable;
            string hoverName = hover != null ? hover.name : "none";
            string hoverTip = hover != null ? hover.hoverTip : string.Empty;
            string action = hoveringInteractable ? "let vanilla interact" : "release payload";
            // Debug, not Info (#716 F5): this fires on every interact press while pushing.
            ContractsBootstrap.Log?.LogDebug(
                $"[MoonContracts.Payload.Controls] Interact release poll: hover={hoverName}, interactable={hoveringInteractable}, " +
                $"tip='{hoverTip}', action={action}.");

            if (hoveringInteractable)
                return false;

            if (!_releaseKeyDecisionLogged)
            {
                _releaseKeyDecisionLogged = true;
                ContractsBootstrap.Log?.LogInfo("[MoonContracts.Payload.Controls] No hovered interactable; treating E as payload release.");
            }
            return true;
        }

        // --- Push body presentation (W4): vanilla animator + procedural arm IK.

        private void TryApplyPushAnimator(PlayerControllerB player)
        {
            if (player == null || player.playerBodyAnimator == null)
            {
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Payload] Procedural push skipped: local player has no playerBodyAnimator.");
                return;
            }

            EnsureGripAnchors();
            _pushBodyAnimator = player.playerBodyAnimator;
            _pushBodyAnimatorHasLocomotion = PayloadPushLocomotion.HasLocomotionParameters(_pushBodyAnimator);
            if (!_bodyAnimatorSpeedSaved)
            {
                _savedBodyAnimatorSpeed = _pushBodyAnimator.speed;
                _bodyAnimatorSpeedSaved = true;
            }

            _lastPushPosition = player.transform.position;
            _lastPushPositionValid = true;
            _smoothedHorizontalSpeed = 0f;
            _smoothedForwardSpeed = 0f;
            _pushPelvisFitWalking = false;
            _pushBodyRig = PayloadPushBodyRig.Resolve(player, PushLeanDegrees);
            ContractsBootstrap.Log?.LogInfo(
                "[MoonContracts.Payload] Procedural push active: vanilla playerBodyAnimator controller left untouched; " +
                $"baked push clip/controller ignored, savedAnimatorSpeed={_savedBodyAnimatorSpeed:0.###}, " +
                $"locomotionParams={_pushBodyAnimatorHasLocomotion}, " +
                $"grips=({_leftGripAnchor?.name},{_rightGripAnchor?.name}).");
            if (!_pushBodyAnimatorHasLocomotion)
            {
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.Payload] Push body animator declares no Walking/Sprinting bools; the pusher's legs stay in whatever pose vanilla left (#1071).");
            }
        }

        private void RestorePushAnimator()
        {
            if (_pushBodyAnimator != null)
            {
                // #1071: hand the body back exactly as it was found. Restoring a hardcoded 1 was a
                // silent overwrite of whatever rate another system had set, and both locomotion flags
                // this pass drives have to come off with it or the pusher keeps walking on the spot.
                float pushSpeed = _pushBodyAnimator.speed;
                PayloadPushLocomotion.Clear(_pushBodyAnimator, _pushBodyAnimatorHasLocomotion, _pushBodySecondary);
                _pushBodyAnimator.speed = _savedBodyAnimatorSpeed;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Payload] Procedural push animator restored: speed set to {_savedBodyAnimatorSpeed:0.###} " +
                    $"(was saved as {_savedBodyAnimatorSpeed:0.###}, pushing at {pushSpeed:0.###}), walking=false, sprinting=false.");
            }

            // #1305: put the dropped pelvis back unless the animator already rewrote it.
            _pushBodyRig?.ReleaseReachShift();
            // TryApplyPushAnimator re-initialises the rest of the per-grab state before anything reads it.
            _pushBodyAnimator = null;
            _bodyAnimatorSpeedSaved = false;
        }

        private void ApplyPushPoseLate(PlayerControllerB player)
        {
            if (player == null || _pushBodyAnimator == null)
                return;

            EnsureGripAnchors();
            bool walking = UpdatePushAnimatorSpeed(player);
            if (_pushBodyRig == null || !_pushBodyRig.IsFor(player))
            {
                _pushBodyRig = PayloadPushBodyRig.Resolve(player, PushLeanDegrees);
                // #1071 follow-up: a rebuilt rig starts from the standing solve, like the observer path.
                _pushPelvisFitWalking = false;
            }

            _pushBodyRig?.ApplyLate(player, _leftGripAnchor, _rightGripAnchor, walking);
        }

        /// <summary>
        /// Returns whether a walk cycle is actually moving the legs under the IK, which is what the
        /// crouch-to-reach is gated on (#1071 follow-up). That is the hysteresed speed band AND the
        /// fact that this controller declares the parameters to act on: a body whose controller has no
        /// Walking bool is standing however fast the cart carries it, and must keep the correction.
        /// The Walking bool the animator receives is NOT this answer - it keeps its own single
        /// threshold, because the two decisions have different costs at the same edge.
        /// </summary>
        private bool UpdatePushAnimatorSpeed(PlayerControllerB player)
        {
            if (_pushBodyAnimator == null || player == null)
                return false;

            float dt = Mathf.Max(Time.deltaTime, 0.0001f);
            Vector3 position = player.transform.position;
            float instantHorizontalSpeed = 0f;
            float instantForwardSpeed = 0f;
            if (_lastPushPositionValid)
            {
                Vector3 delta = position - _lastPushPosition;
                delta.y = 0f;
                instantHorizontalSpeed = delta.magnitude / dt;
                instantForwardSpeed = Vector3.Dot(delta / dt, PushBodyForward(player));
            }

            _lastPushPosition = position;
            _lastPushPositionValid = true;

            float blend = 1f - Mathf.Exp(-PushAnimatorVelocitySmooth * dt);
            _smoothedHorizontalSpeed = Mathf.Lerp(_smoothedHorizontalSpeed, instantHorizontalSpeed, blend);
            _smoothedForwardSpeed = Mathf.Lerp(_smoothedForwardSpeed, instantForwardSpeed, blend);
            float walkReference = _savedMovementSpeed * RiskSettings.PushSpeedMultiplier.Value;
            bool walking = ResolvePushWalkRate(
                _smoothedHorizontalSpeed, _smoothedForwardSpeed, walkReference, out float speed, out float direction);
            // #1071: the flags first, then the rate. Writing only the rate left the body in Idle, so
            // the "speed" the walk cycle was being scaled by was being applied to a pose that never
            // moved. Vanilla's own Walking/Sprinting writes stop once its isWalking has gone false
            // under the move clamp, so this LateUpdate pass owns both bools from here on.
            PayloadPushLocomotion.Apply(_pushBodyAnimator, _pushBodyAnimatorHasLocomotion, walking, direction, _pushBodySecondary);
            _pushBodyAnimator.speed = speed;
            _pushPelvisFitWalking = ResolvePushPelvisFitWalking(
                _smoothedHorizontalSpeed, _pushBodyAnimatorHasLocomotion, _pushPelvisFitWalking);
            return _pushPelvisFitWalking;
        }

        /// <summary>
        /// The pelvis-fit gate, shared by the pusher's own machine and by every observer's mirror so
        /// the crouch cannot be on for one peer and off for another (#1071 follow-up). See
        /// <see cref="PushPelvisFitWalkEnterSpeed"/> for why it is hysteresed where the Walking bool
        /// is not.
        /// </summary>
        private static bool ResolvePushPelvisFitWalking(float smoothedHorizontalSpeed, bool hasLocomotion, bool wasWalking)
        {
            if (!hasLocomotion)
                return false;
            return wasWalking
                ? smoothedHorizontalSpeed >= PushPelvisFitWalkExitSpeed
                : smoothedHorizontalSpeed > PushPelvisFitWalkEnterSpeed;
        }

        /// <summary>
        /// The facing the walk cycle is judged against: the transform the payload steering actually
        /// yaws (<c>thisPlayerBody</c>, the third-person model's root), flattened. Vanilla keeps a
        /// remote body's rotation on the same transform, so an observer reads the same facing.
        /// </summary>
        private static Vector3 PushBodyForward(PlayerControllerB player)
        {
            Transform body = player != null && player.thisPlayerBody != null ? player.thisPlayerBody : player?.transform;
            Vector3 forward = body != null ? body.forward : Vector3.forward;
            forward.y = 0f;
            return forward.sqrMagnitude > 0.0001f ? forward.normalized : Vector3.forward;
        }

        /// <summary>
        /// The one push locomotion rule (#1071), shared by the pusher's own body and by every
        /// observer's mirror of it so the two cannot disagree. Below
        /// <see cref="PushAnimatorStillSpeed"/> the body is standing: the Walking flag drops and the
        /// animator goes back to its natural rate, because a rate of 0 would freeze the walk->idle
        /// transition itself and leave the legs mid-stride. Above it the walk cycle plays at the
        /// fraction of a normal walk the cart is actually travelling at, in the direction the body is
        /// actually travelling - S pulls the cart, and a body reversing to a forward walk cycle
        /// moonwalks. Both inputs are smoothed the same way by the caller.
        /// </summary>
        private static bool ResolvePushWalkRate(
            float smoothedHorizontalSpeed,
            float smoothedForwardSpeed,
            float walkReference,
            out float animatorSpeed,
            out float animationDirection)
        {
            animationDirection = PayloadCartMotionMath.PushWalkDirectionSign(smoothedForwardSpeed, smoothedHorizontalSpeed);
            if (smoothedHorizontalSpeed < PushAnimatorStillSpeed)
            {
                animatorSpeed = 1f;
                animationDirection = 1f;
                return false;
            }

            animatorSpeed = Mathf.Clamp01(smoothedHorizontalSpeed / Mathf.Max(0.1f, walkReference));
            return true;
        }

        private static void ShowPayloadControlsTipOnce()
        {
            if (s_payloadControlsTipShown)
                return;

            s_payloadControlsTipShown = true;
            HUDManager.Instance?.DisplayTip("Payload controls", $"W and S push and pull, A and D steer, the mouse looks around. [{ViewToggleBindingDisplay()}] switches first/third person.", false);
        }

        /// <summary>
        /// Clear the once-per-round controls tip (#716 E9). It used to latch for the whole game launch,
        /// so a second Payload contract in the same session never explained its controls again.
        /// </summary>
        internal static void ResetControlsTipForNewRound()
        {
            s_payloadControlsTipShown = false;
            LocalAttachedCart?.EndLocalPush();
        }

        [ClientRpc]
        private void ShowPayloadTipClientRpc(string title, string body, bool warning)
        {
            HUDManager.Instance?.DisplayTip(title, body, warning);
        }

        private void EnsureGripAnchors()
        {
            Transform parent = _handleGrab != null ? _handleGrab : transform;
            _leftGripAnchor = EnsureGripAnchor(_leftGripAnchor, parent, "PayloadPush_LeftGripAnchor", -GripHalfWidth);
            _rightGripAnchor = EnsureGripAnchor(_rightGripAnchor, parent, "PayloadPush_RightGripAnchor", GripHalfWidth);
        }

        private static Transform EnsureGripAnchor(Transform current, Transform parent, string name, float localX)
        {
            if (parent == null)
                return current;

            if (current == null || current.parent != parent)
            {
                Transform existing = parent.Find(name);
                current = existing != null ? existing : new GameObject(name).transform;
                current.SetParent(parent, worldPositionStays: false);
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload] Created/resolved grip anchor '{name}' under '{parent.name}'.");
            }

            current.localPosition = new Vector3(localX, 0f, 0f);
            current.localRotation = Quaternion.identity;
            current.localScale = Vector3.one;
            return current;
        }

        internal static bool IsLocalPlayerPushingPayload(PlayerControllerB player)
        {
            PayloadCartController cart = LocalAttachedCart;
            if (cart == null || !cart._localPushActive || player == null)
                return false;

            PlayerControllerB local = LocalPlayer();
            return player == local && cart.CanContinueLocalPush(local);
        }

        /// <summary>
        /// Observer-side push presentation for a REMOTE pusher (#441). Same presentation code as the local
        /// push — the grip anchors under HandleGrab, <see cref="PayloadPushBodyRig"/>'s two-arm IK and spine lean —
        /// but driven purely off what this machine observes: the replicated cart transform (NetworkTransform,
        /// #434) and the remote player's replicated body pose. It never touches input, steering, movement
        /// speed, the camera or any NetworkVariable, so it cannot desync anything; the worst case is a body
        /// pose that lags the cart by the interpolation window. No prediction is applied.
        /// </summary>
        private sealed class RemotePushPresentation
        {
            private const float PendingResolveWarnSeconds = 3f;

            private ulong _clientId = NoPlayer;
            private PlayerControllerB _player;
            private PayloadPushBodyRig _rig;
            // #1071: the observer's mirror of the pusher's locomotion state. Resolved with the rig and
            // restored when the presentation stops, so an observer never leaves a remote body walking.
            private Animator _bodyAnimator;
            private bool _bodyAnimatorHasLocomotion;
            // #1071 follow-up: the observer's own copy of the four owned secondary bools. A remote body
            // runs a different controller asset, so what it declares is resolved against THIS animator.
            private readonly PayloadPushLocomotion.SecondaryFlags _bodySecondary = new PayloadPushLocomotion.SecondaryFlags();
            private float _savedBodyAnimatorSpeed = 1f;
            private float _smoothedSpeed;
            private float _smoothedForwardSpeed;
            // #1071 follow-up: the latched side of the pelvis-fit gate, mirrored from the pusher's.
            private bool _pelvisFitWalking;
            private Vector3 _lastPosition;
            private bool _lastPositionValid;
            private float _beginTime;
            private bool _resolveWarned;
            private bool _poseStartLogged;

            /// <summary>Client id this presentation is armed for, or <see cref="NoPlayer"/> when idle.</summary>
            internal ulong ClientId => _clientId;

            internal void Begin(ulong clientId)
            {
                if (_clientId != NoPlayer && _clientId != clientId)
                    Stop("pusher handed the cart to another player");

                _clientId = clientId;
                _beginTime = Time.time;
                _resolveWarned = false;
                _poseStartLogged = false;
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Payload] Remote push presentation armed for playerClientId={clientId}.");
            }

            internal void Stop(string reason)
            {
                if (_clientId == NoPlayer)
                {
                    Reset();
                    return;
                }

                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Payload] Remote push presentation stopped for playerClientId={_clientId} ({reason}).");
                Reset();
            }

            private void Reset()
            {
                // #1071: the locomotion flags ARE cached state and must come off here. The IK rotations
                // need no restore, but the rig also drops the pelvis from a cached original local position
                // (#1305), so release that before the rig reference goes.
                _rig?.ReleaseReachShift();
                if (_bodyAnimator != null)
                {
                    PayloadPushLocomotion.Clear(_bodyAnimator, _bodyAnimatorHasLocomotion, _bodySecondary);
                    _bodyAnimator.speed = _savedBodyAnimatorSpeed;
                }

                // Begin and LateApply's rig rebuild re-initialise the rest before anything reads it.
                _clientId = NoPlayer;
                _player = null;
                _rig = null;
                _bodyAnimator = null;
            }

            internal void LateApply(PayloadCartController cart)
            {
                if (_clientId == NoPlayer || cart == null)
                    return;

                if (!TryResolvePlayer())
                    return;

                cart.EnsureGripAnchors();
                if (_rig == null || !_rig.IsFor(_player))
                {
                    _rig = PayloadPushBodyRig.Resolve(_player, PushLeanDegrees, verboseLog: false);
                    _lastPositionValid = false;
                    _smoothedSpeed = 0f;
                    _smoothedForwardSpeed = 0f;
                    _pelvisFitWalking = false;
                    ResolveBodyAnimator();
                }

                UpdateObservedSpeed();
                bool walking = ApplyObservedLocomotion();
                _rig.ApplyLate(_player, cart._leftGripAnchor, cart._rightGripAnchor, walking);

                if (!_poseStartLogged)
                {
                    _poseStartLogged = true;
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.Payload] Remote push presentation started for playerClientId={_clientId}: " +
                        $"usableArms={_rig.HasUsableArms}, cart={cart.NetworkObjectId}, " +
                        $"locomotionParams={_bodyAnimatorHasLocomotion}, " +
                        $"grips=({cart._leftGripAnchor?.name},{cart._rightGripAnchor?.name}).");
                }
            }

            /// <summary>
            /// Resolve (and keep validating) the target body. A missing PlayerControllerB is treated as
            /// "not ready yet" — a client that spawns the cart mid-push may not have the player list
            /// populated on the same frame — while a resolved but dead/uncontrolled/disconnected player
            /// tears the presentation down. The server drops the attachment for those cases too, but this
            /// peer must not pose a corpse in the frames before that write lands.
            /// </summary>
            private bool TryResolvePlayer()
            {
                if (_player == null || _player.actualClientId != _clientId)
                {
                    _player = FindPlayer(_clientId);
                    _rig = null;
                    if (_player == null)
                    {
                        if (!_resolveWarned && Time.time - _beginTime > PendingResolveWarnSeconds)
                        {
                            _resolveWarned = true;
                            ContractsBootstrap.Log?.LogWarning(
                                $"[MoonContracts.Payload] Remote push presentation still cannot resolve playerClientId={_clientId} " +
                                $"after {PendingResolveWarnSeconds:0.#}s; body pose stays vanilla for this observer.");
                        }
                        return false;
                    }
                }

                PlayerControllerB local = LocalPlayer();
                if ((local != null && _player == local) || _player.IsOwner)
                {
                    // The local pusher runs BeginLocalPush (first-person handling, camera, input) instead.
                    // IsOwner is the backstop for the frames where LocalPlayer() is not resolvable yet.
                    Stop("target resolved to the local player");
                    return false;
                }

                if (_player.isPlayerDead || !_player.isPlayerControlled || _player.disconnectedMidGame)
                {
                    Stop($"target invalid (dead={_player.isPlayerDead}, controlled={_player.isPlayerControlled}, " +
                        $"disconnected={_player.disconnectedMidGame})");
                    return false;
                }

                return true;
            }

            /// <summary>
            /// The smoothed horizontal and forward speeds the locomotion mirror below runs off, derived
            /// from the remote body's observed position.
            /// </summary>
            private void UpdateObservedSpeed()
            {
                float dt = Mathf.Max(Time.deltaTime, 0.0001f);
                Vector3 position = _player.transform.position;
                float instantSpeed = 0f;
                float instantForwardSpeed = 0f;
                if (_lastPositionValid)
                {
                    Vector3 delta = position - _lastPosition;
                    delta.y = 0f;
                    instantSpeed = delta.magnitude / dt;
                    instantForwardSpeed = Vector3.Dot(delta / dt, PushBodyForward(_player));
                }

                _lastPosition = position;
                _lastPositionValid = true;
                float blend = 1f - Mathf.Exp(-PushAnimatorVelocitySmooth * dt);
                _smoothedSpeed = Mathf.Lerp(_smoothedSpeed, instantSpeed, blend);
                _smoothedForwardSpeed = Mathf.Lerp(_smoothedForwardSpeed, instantForwardSpeed, blend);
            }

            private void ResolveBodyAnimator()
            {
                if (_bodyAnimator != null)
                {
                    PayloadPushLocomotion.Clear(_bodyAnimator, _bodyAnimatorHasLocomotion, _bodySecondary);
                    _bodyAnimator.speed = _savedBodyAnimatorSpeed;
                }

                _bodyAnimator = _player != null ? _player.playerBodyAnimator : null;
                _bodyAnimatorHasLocomotion = PayloadPushLocomotion.HasLocomotionParameters(_bodyAnimator);
                _savedBodyAnimatorSpeed = _bodyAnimator != null ? _bodyAnimator.speed : 1f;
            }

            /// <summary>
            /// #1071: the observer's mirror of the pusher's locomotion state, run through the SAME rule
            /// the pusher uses so host and clients agree on both the flags and the rate.
            ///
            /// It has to be computed here rather than received: vanilla replicates a remote body's
            /// animation as a state hash plus the "animationSpeed" float
            /// (PlayerControllerB.UpdatePlayerAnimation*Rpc), and neither the two bools nor
            /// Animator.speed - which is what carries the push rate - travels with it. The walk
            /// reference is the remote body's own movementSpeed, the same field the pusher saved on
            /// their machine before the cart zeroed it.
            ///
            /// Returns whether a walk cycle is playing on the observed body, on the same rule the
            /// pusher's own machine uses, so the IK crouch-to-reach is gated identically on every peer
            /// (#1071 follow-up). An observer that kept driving the standing solve under the mirrored
            /// walk cycle would see the knees invert while the pusher's own screen did not.
            /// </summary>
            private bool ApplyObservedLocomotion()
            {
                if (_bodyAnimator == null)
                    return false;

                float walkReference = _player != null ? _player.movementSpeed * RiskSettings.PushSpeedMultiplier.Value : 0f;
                bool walking = ResolvePushWalkRate(
                    _smoothedSpeed, _smoothedForwardSpeed, walkReference, out float speed, out float direction);
                PayloadPushLocomotion.Apply(_bodyAnimator, _bodyAnimatorHasLocomotion, walking, direction, _bodySecondary);
                _bodyAnimator.speed = speed;
                _pelvisFitWalking = ResolvePushPelvisFitWalking(
                    _smoothedSpeed, _bodyAnimatorHasLocomotion, _pelvisFitWalking);
                return _pelvisFitWalking;
            }
        }

        private void BuildHandleInteractable()
        {
            Transform host = _handleGrab != null ? _handleGrab : transform;
            _handleInteractable = host.GetComponent<PayloadHandleInteractable>();
            if (_handleInteractable == null)
                _handleInteractable = host.gameObject.AddComponent<PayloadHandleInteractable>();
            _handleInteractable.Build(this);
        }

        /// <summary>
        /// Per-client visual upscale (uniform, applied at spawn). See <see cref="VisualScale"/>.
        /// The assignment is absolute and targets the visual CHILD, so it cannot compound when host and
        /// client both run it, and the root scale — the only one NetworkTransform replicates — stays 1.
        /// </summary>
        private void ApplyVisualScale()
        {
            Transform visual = transform.Find("PayloadCart_Visual");
            if (visual == null && transform.childCount > 0)
                visual = transform.GetChild(0);
            if (visual != null)
                visual.localScale = Vector3.one * VisualScale;
        }

        private void SetCartColliderEnabled(bool enabled)
        {
            BoxCollider box = GetComponent<BoxCollider>();
            if (box != null)
                box.enabled = enabled;
        }

        private const float HandleReachDistance = 3.0f;

        private static PlayerControllerB LocalPlayer()
        {
            if (GameNetworkManager.Instance != null && GameNetworkManager.Instance.localPlayerController != null)
                return GameNetworkManager.Instance.localPlayerController;
            return StartOfRound.Instance != null ? StartOfRound.Instance.localPlayerController : null;
        }

        private static PlayerControllerB FindPlayer(ulong clientId)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null)
                return null;
            for (int i = 0; i < players.Length; i++)
            {
                PlayerControllerB player = players[i];
                if (player != null && player.actualClientId == clientId)
                    return player;
            }
            return null;
        }

        private void ResolveMarkers()
        {
            _handleGrab = PayloadThirdPersonView.FindChildDeep(transform, HandleGrabChildName);
            _magnetPoint = PayloadThirdPersonView.FindChildDeep(transform, MagnetPointChildName);
            if (_handleGrab == null)
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload] Cart '{name}' is missing a '{HandleGrabChildName}' child transform.");
            if (_magnetPoint == null)
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Payload] Cart '{name}' is missing a '{MagnetPointChildName}' child transform.");
        }
    }

    /// <summary>
    /// #1305 C2: vanilla moves a player through an EntranceTeleport, and the cart he pushes crosses with
    /// him (<see cref="PayloadCartController.CrossPortalWithPlayerServer"/>). <c>TeleportPlayer</c> is the
    /// host's own player, whose inside-factory flag it flips as its last statement; <c>TeleportPlayerClientRpc</c>
    /// moves the host's copy of a remote player.
    /// </summary>
    [HarmonyPatch(typeof(EntranceTeleport))]
    internal static class PayloadCartEntranceTeleportPatch
    {
        [HarmonyPatch("TeleportPlayer")]
        [HarmonyPostfix]
        private static void TeleportPlayerPostfix(EntranceTeleport __instance)
        {
            PayloadCartController cart = PayloadContract.ActiveCart;
            if (cart != null && GameNetworkManager.Instance != null)
                cart.CrossPortalWithPlayerServer(GameNetworkManager.Instance.localPlayerController, __instance);
        }

        [HarmonyPatch("TeleportPlayerClientRpc")]
        [HarmonyPostfix]
        private static void TeleportPlayerClientRpcPostfix(EntranceTeleport __instance, int playerObj)
        {
            PayloadCartController cart = PayloadContract.ActiveCart;
            PlayerControllerB[] players = StartOfRound.Instance != null ? StartOfRound.Instance.allPlayerScripts : null;
            if (cart != null && players != null && playerObj >= 0 && playerObj < players.Length)
                cart.CrossPortalWithPlayerServer(players[playerObj], __instance);
        }
    }
}
