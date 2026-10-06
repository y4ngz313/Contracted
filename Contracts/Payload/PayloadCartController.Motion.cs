using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.Payload
{
    public partial class PayloadCartController
    {
        // One motor owns all ordinary movement. Explicit portal/elevator transfers and docking are
        // the only discontinuities. These dimensions cover the cage without the handle overhang.
        // Internal for PayloadCartRoute (#1219 D1): the route proof sweeps this same box with the
        // same skin, so the route and the motor can never disagree about the cart's volume.
        internal static readonly Vector3 MotionHalfExtents = new Vector3(0.43f, 0.60f, 0.70f);
        internal static readonly Vector3 MotionCenter = new Vector3(0f, 0.72f, 0f);
        internal const float MotionSkin = 0.025f;
        /// <summary>The root's height over the wheels' support, as the motor grounds it.</summary>
        internal const float GroundedRootHeight = FollowHeight;
        private const float InputTimeout = 0.35f;
        private readonly RaycastHit[] _motionHits = new RaycastHit[64];
        private readonly Collider[] _motionOverlaps = new Collider[64];
        private Vector3 _motionVelocity;
        private bool _motionGrounded;
        private bool _motionInitialized;
        private float _driveForward;
        private float _driveSteer;
        private float _driveReceivedAt = -10f;
        private float _nextDriveSendAt;
        private float _driveTargetSpeed;
        private float _driveTargetYaw;
        private float _driveTurnRate;
        private bool _driveRequested;
        // #1305 H1: the ship magnet's pull on an unheld cart this step (MagnetRule), reset with the drive.
        private Vector3 _magnetPull;
        private float _nextRollingNoiseAt;
        private bool _motionLandingContact;
        // 2026-09-16 (WP2 step 4): the collider that refused the last overlap or stopped the last
        // sweep, named for the release and brake lines. Written on the same terms _motionLandingContact
        // is - a field set inside the shared geometry rather than a return value - because
        // MotionPoseClear and SweepMotion are called from five sites and two external fixtures
        // reflect on their signatures.
        // v2: a Collider REFERENCE, not its name. SweepMotion blocks on the ordinary path every time
        // the cart slides along a wall, and marshalling Collider.name there would allocate a string
        // per fixed step for a line nobody asked for. The name is resolved in
        // NpcPoseRefusalDetail, which only a diagnostic line calls.
        private Collider _motionBlocker;
        private float _driverLostHandleAt = -1f;
        private float _nextLedgeHoldLogAt = -10f;

        internal Vector3 DriverHandlePosition => transform.position
            + Quaternion.Euler(0f, transform.eulerAngles.y, 0f) * Vector3.forward * FollowDistance
            - Vector3.up * FollowHeight;

        private void ResetDriveInput()
        {
            _driveForward = _driveSteer = 0f;
            _driveReceivedAt = -10f;
            _nextDriveSendAt = 0f;
            _driverLostHandleAt = -1f;
        }

        private void ResetCartMotion()
        {
            _motionVelocity = Vector3.zero;
            _motionGrounded = false;
            _motionInitialized = false;
            ResetDriveInput();
        }

        private bool CanGrabMotionServer()
        {
            return _motionVelocity.magnitude < 2.5f
                && PayloadCartGroundSampler.TrySampleCartGround(transform, _groundSampleState, out var ground)
                && PayloadCartMotionMath.CanGround(ground.FreshMask, transform.position.y, ground.SupportY + FollowHeight, true);
        }

        private void UpdateLocalCartInput(PlayerControllerB player)
        {
            if (Time.unscaledTime < _nextDriveSendAt)
                return;
            _nextDriveSendAt = Time.unscaledTime + 0.05f;
            bool blocked = player.isPlayerDead || player.inSpecialMenu || player.disableMoveInput
                || player.inSpecialInteractAnimation || player.isClimbingLadder
                || (player.quickMenuManager != null && player.quickMenuManager.isMenuOpen);
            Vector2 input = blocked ? Vector2.zero : player.playerActions.Movement.Move.ReadValue<Vector2>();
            SubmitCartDriveServerRpc(input.y, input.x);
        }

        [ServerRpc(RequireOwnership = false)]
        private void SubmitCartDriveServerRpc(float forward, float steer, ServerRpcParams rpcParams = default)
        {
            if (!IsServer || !IsAttached || rpcParams.Receive.SenderClientId != AttachedClientId
                || !PayloadCartMotionMath.IsFinite(forward) || !PayloadCartMotionMath.IsFinite(steer))
                return;
            _driveForward = Mathf.Clamp(forward, -1f, 1f);
            _driveSteer = Mathf.Clamp(steer, -1f, 1f);
            _driveReceivedAt = Time.unscaledTime;
        }

        /// <summary>
        /// The flat offset from the pusher to the driver handle, and whether a follow step can close
        /// it. False while they are further apart than that: the server releases a separated
        /// driver, and an entrance crossing moves the cart only after the server re-places it.
        /// </summary>
        internal bool TryGetDriverHandleOffset(PlayerControllerB player, out Vector3 offset)
        {
            offset = DriverHandlePosition - player.transform.position;
            offset.y = 0f;
            return offset.sqrMagnitude <= 9f;
        }

        private void FollowCartHandleLocal(PlayerControllerB player)
        {
            // Correct only horizontal position through CharacterController so a wall never teleports the
            // pusher through it. The server releases a separated driver; an entrance crossing re-places the
            // cart at his handle first (CrossPortalWithPlayerServer).
            if (player.thisController == null || !player.thisController.enabled || player.isPlayerDead
                || !TryGetDriverHandleOffset(player, out Vector3 error))
                return;
            player.thisController.Move(Vector3.ClampMagnitude(error, 6f * Time.deltaTime));
        }

        private void UpdatePlayerDriveServer(PlayerControllerB pusher)
        {
            Vector3 handleError = pusher.transform.position - DriverHandlePosition;
            float separation = Vector3.ProjectOnPlane(handleError, Vector3.up).magnitude;
            if (separation > 2.5f || Mathf.Abs(handleError.y) > 3f || pusher.isClimbingLadder)
            { DetachServer(); return; }
            // Do not drag the cart away from a player blocked by a jamb during a turn.
            if (separation > 0.65f)
            {
                if (_driverLostHandleAt < 0f) _driverLostHandleAt = Time.unscaledTime;
                if (Time.unscaledTime - _driverLostHandleAt > LostHandleGraceSeconds) DetachServer();
                return;
            }
            _driverLostHandleAt = -1f;
            if (Time.unscaledTime - _driveReceivedAt > InputTimeout)
                return;
            _driveRequested = true;
            float forward = _driveForward;
            _driveTargetSpeed = 4.6f * RiskSettings.PushSpeedMultiplier.Value * forward
                * (forward < 0f ? RiskSettings.ReverseInputMultiplier.Value : 1f);
            float speed = new Vector2(_motionVelocity.x, _motionVelocity.z).magnitude;
            _driveTurnRate = Mathf.Lerp(30f, 55f, Mathf.Clamp01(speed / 1.5f));
            _driveTargetYaw = transform.eulerAngles.y + _driveSteer * _driveTurnRate * Time.fixedDeltaTime;
        }

        private void FixedUpdate()
        {
            if (!IsServer || !IsSpawned) return;
            if (TickBlacksiteMagnet()) return;
            // #1305 H1: a started rise finishes even when the contract fails mid-rise (a failed Blacksite cart is let go above).
            if (_magnetAttachHost != null) { RiseOntoMagnetServer(); return; }
            if (_deliveryStarted || IsDelivered || IsFailed || PayloadContract.IsFailed)
            {
                if (IsNpcAttached) ReleaseNpcPusherServer("unsupported");
                _motionVelocity = Vector3.zero;
                return;
            }
            if (IsNpcAttached && Time.frameCount - _npcElevatorFrame <= 1) return;
            Physics.SyncTransforms();
            _driveRequested = false;
            _driveTargetSpeed = 0f;
            _driveTargetYaw = transform.eulerAngles.y;
            _driveTurnRate = 55f;
            _magnetPull = Vector3.zero;
            UpdateServerHaul();
            UpdateNpcHaul();
            if (TickShipMagnetServer()) return;
            StepCartMotion(Time.fixedDeltaTime);
            // #874: the coupling's second half runs on the step the motor just integrated, not on
            // the one it was commanded. The operator is carried to the handle the cart's own
            // collision sweep, wheel-support and ledge tests left behind, so legal cart motion is
            // what determines his progress and a refused cart moves neither body.
            StepNpcOperatorServer();
        }

        private void StepCartMotion(float dt)
        {
            Vector3 position = transform.position;
            bool sampled = PayloadCartGroundSampler.TrySampleCartGround(transform, _groundSampleState, out var ground);
            bool supported = sampled && PayloadCartMotionMath.CanGround(ground.FreshMask, position.y,
                ground.SupportY + FollowHeight, _motionGrounded || !_motionInitialized);
            _motionInitialized = true;
            bool wasGrounded = _motionGrounded;
            _motionGrounded = supported;
            if (!supported && (IsAttached || IsNpcAttached))
            {
                DetachServer(); ReleaseNpcPusherServer(); _driveRequested = false;
            }
            if (wasGrounded && !supported)
                ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Motion] Airborne: contacts={(sampled ? ground.FreshHitCount : 0)}, velocity={_motionVelocity}.");

            Vector3 flatVelocity = Vector3.ProjectOnPlane(_motionVelocity, Vector3.up);
            Quaternion yaw = Quaternion.Euler(0f, transform.eulerAngles.y, 0f);
            Quaternion rotation = transform.rotation;
            if (supported)
            {
                float grade = ground.VerticalGradeAlong(-(yaw * Vector3.forward));
                bool held = IsAttached || IsNpcAttached;
                if (held && _driveRequested && ContractsBootstrap.PayloadBalance.RunawayEnabled.Value
                    && ground.SlopeAngleDegrees >= RunawaySlopeAngle && grade * Mathf.Sign(_driveTargetSpeed) < -0.12f
                    && flatVelocity.magnitude > 0.5f)
                {
                    DetachServer(); ReleaseNpcPusherServer(); _driveRequested = false; held = false;
                    PlayPayloadRunawayClientRpc(position);
                }
                if (held)
                {
                    float targetYaw = _driveRequested ? _driveTargetYaw : yaw.eulerAngles.y;
                    Quaternion wantedYaw = Quaternion.RotateTowards(yaw, Quaternion.Euler(0f, targetYaw, 0f), _driveTurnRate * dt);
                    Quaternion wantedRotation = GroundRotation(wantedYaw, ground);
                    // 2026-09-16 (E1): an NPC-pushed cart turns about the HANDLE, so the handle
                    // stays in the pusher's hands and the body swings round him. A centre pivot
                    // swept the handle 2 * FollowDistance * sin(turn / 2) away from him - past the
                    // 2.5 m leash at 126.5 degrees - and released him as separated on every one of
                    // the 2026-09-16 run's nine attempts. The player path keeps the centre pivot:
                    // FollowCartHandleLocal drags the driver onto the handle every frame, so there
                    // is no arc for him to lose. `position` moves with the swing, which is why the
                    // velocity below is measured from the pivoted pose and not the original one.
                    // 2026-09-16 (WP2 step 1): the sub-step stays the authored 2 degrees. The finer
                    // quarter-step retry lives inside ResolveNpcPivotRotation, reached only from a
                    // swing that was refused outright, so a clear turn still costs one pose sweep
                    // per fixed step instead of four.
                    if (IsNpcAttached)
                        rotation = ResolveNpcPivotRotation(ref position, rotation, wantedRotation, NpcPivotStepDegrees);
                    else
                        rotation = ResolveMotionRotation(position, rotation, wantedRotation);
                    yaw = Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
                    Vector3 forward = -(yaw * Vector3.forward);
                    float target = _driveRequested ? _driveTargetSpeed : 0f;
                    float uphill = ground.VerticalGradeAlong(forward * Mathf.Sign(target));
                    if (uphill > 0f)
                        target *= Mathf.Lerp(1f, RiskSettings.UphillMinimumSpeedMultiplier.Value, Mathf.Clamp01(uphill / 0.7f));
                    float speed = PayloadCartMotionMath.DriveSpeed(Vector3.Dot(flatVelocity, forward), target, dt);
                    flatVelocity = forward * speed;
                }
                else if (_magnetPull.sqrMagnitude > 0f)
                {
                    // #1305 H1: a magnet-pulled cart rolls at the pull in its own yaw, with no slope gravity.
                    flatVelocity = _magnetPull;
                    rotation = ResolveMotionRotation(position, rotation, GroundRotation(yaw, ground));
                }
                else
                {
                    // Continuous gravity along the slope and rolling resistance: no timeout freezes.
                    Vector3 acceleration = ground.DownhillDirection * (PayloadCartMotionMath.Gravity
                        * Mathf.Sin(ground.SlopeAngleDegrees * Mathf.Deg2Rad));
                    if (ContractsBootstrap.PayloadBalance.RunawayEnabled.Value)
                        flatVelocity += acceleration * dt;
                    flatVelocity = Vector3.MoveTowards(flatVelocity, Vector3.zero, 1.2f * dt);
                    flatVelocity = Vector3.ClampMagnitude(flatVelocity, RunawayMaxSpeed);
                    rotation = ResolveMotionRotation(position, rotation, GroundRotation(yaw, ground));
                }
                _motionVelocity = flatVelocity;
            }
            else
            {
                if (sampled && !ground.HasStableSupport)
                {
                    // With an upright assisted chassis, its underside can catch a cliff lip even
                    // after the centre leaves the wheel support polygon. Slide toward the unsupported
                    // side, preserving existing momentum, so the lip cannot become an invisible shelf.
                    int mask = ground.FreshMask;
                    float front = ((mask & 1) != 0 ? 1f : 0f) + ((mask & 2) != 0 ? 1f : 0f);
                    float rear = ((mask & 4) != 0 ? 1f : 0f) + ((mask & 8) != 0 ? 1f : 0f);
                    float left = ((mask & 1) != 0 ? 1f : 0f) + ((mask & 4) != 0 ? 1f : 0f);
                    float right = ((mask & 2) != 0 ? 1f : 0f) + ((mask & 8) != 0 ? 1f : 0f);
                    Vector3 unsupported = ground.Forward * (rear - front) + ground.Right * (left - right);
                    flatVelocity += unsupported.normalized * (3f * dt);
                    flatVelocity = Vector3.ClampMagnitude(flatVelocity, RunawayMaxSpeed);
                }
                _motionVelocity = new Vector3(flatVelocity.x, PayloadCartMotionMath.FallSpeed(_motionVelocity.y, dt), flatVelocity.z);
            }

            Vector3 next = position + _motionVelocity * dt;
            bool nextSampled = PayloadCartGroundSampler.TrySampleCartGround(transform, next, rotation, _groundSampleState, out var nextGround);
            bool nextSupported = nextSampled && PayloadCartMotionMath.CanGround(nextGround.FreshMask, next.y,
                nextGround.SupportY + FollowHeight, supported);
            // #1003: lock the wheels rather than roll off a drop. Support loss is only measured
            // after the step that caused it, so a held cart walked at a pit lip used to be released
            // in mid-air with reason=unsupported and fall wherever the pit took it. Refusing the
            // step that costs a wheel keeps the handle, and the cart, on the floor it is standing
            // on, while leaving every step that keeps or regains floor available. #1305 H1: a
            // magnet-pulled cart stops at a drop the same way, silently.
            bool pulled = _magnetPull.sqrMagnitude > 0f;
            bool heldAtLedge = supported && (IsAttached || IsNpcAttached || pulled)
                && Vector3.ProjectOnPlane(_motionVelocity, Vector3.up).sqrMagnitude > 0f
                && PayloadCartMotionMath.IsLedgeAhead(ground.FreshMask, nextSampled, nextGround.FreshMask);
            if (heldAtLedge)
            {
                int ledgeMask = nextSampled ? nextGround.FreshMask : 0;
                _motionVelocity.x = 0f; _motionVelocity.z = 0f;
                flatVelocity = Vector3.zero;
                next = position;
                nextSampled = PayloadCartGroundSampler.TrySampleCartGround(transform, next, rotation, _groundSampleState, out nextGround);
                nextSupported = nextSampled && PayloadCartMotionMath.CanGround(nextGround.FreshMask, next.y,
                    nextGround.SupportY + FollowHeight, supported);
                if (!pulled && Time.unscaledTime >= _nextLedgeHoldLogAt)
                {
                    _nextLedgeHoldLogAt = Time.unscaledTime + 2f;
                    ContractsBootstrap.Log?.LogInfo($"[MoonContracts.Payload.Motion] Ledge hold: wheels locked at a drop (nextMask={ledgeMask}, held={(IsAttached ? "player" : "npc")}).");
                }
            }
            if (nextSupported && _motionVelocity.y <= 0f)
            {
                next.y = nextGround.SupportY + FollowHeight;
                rotation = ResolveMotionRotation(position, rotation, GroundRotation(yaw, nextGround));
            }
            _motionLandingContact = false;
            Vector3 start = position;
            // Lift before a small step, but sweep the lift too so low ceilings remain solid.
            if (supported && nextSupported && next.y > position.y + MotionSkin)
                start = SweepMotion(position, Vector3.up * (next.y - position.y), rotation);
            Vector3 resolved = SweepMotion(start, next - start, rotation);
            // A chassis sweep can touch down slightly before/after the wheel plane. Establish
            // support from fresh wheel contacts at the resolved pose, then seat the wheels safely.
            if (_motionLandingContact && PayloadCartGroundSampler.TrySampleCartGround(transform, resolved, rotation,
                _groundSampleState, out var landing) && PayloadCartMotionMath.CanGround(landing.FreshMask,
                    resolved.y, landing.SupportY + FollowHeight, true))
            {
                Vector3 seated = new Vector3(resolved.x, landing.SupportY + FollowHeight, resolved.z);
                if (MotionPoseClear(seated, rotation))
                { resolved = seated; next = seated; nextSupported = true; }
            }
            transform.SetPositionAndRotation(resolved, rotation);
            Vector3 actual = (resolved - position) / Mathf.Max(0.001f, dt);
            _motionVelocity.x = actual.x; _motionVelocity.z = actual.z;
            if (!supported && actual.y > _motionVelocity.y + 0.05f) _motionVelocity.y = 0f;
            if (nextSupported && Mathf.Abs(resolved.y - next.y) < 0.04f)
            {
                if (!wasGrounded && !supported)
                    PayloadCartAudio.PlayJoltAt(resolved, Mathf.Clamp01(Mathf.Abs(_motionVelocity.y) / 12f));
                _motionVelocity.y = 0f;
                _motionGrounded = true;
            }
            else _motionGrounded = false;
            if (_motionGrounded && _motionVelocity.magnitude > 1.5f && !IsAttached && !IsNpcAttached
                && Time.time >= _nextRollingNoiseAt && RoundManager.Instance != null)
            {
                _nextRollingNoiseAt = Time.time + 0.35f;
                RoundManager.Instance.PlayAudibleNoise(resolved, 18f, 0.65f);
            }
        }

        internal static Quaternion GroundRotation(Quaternion yaw, PayloadCartGroundSampler.GroundSample ground)
        {
            return yaw * Quaternion.Euler(-Mathf.Clamp(ground.SignedPitchDegrees, -40f, 40f), 0f,
                Mathf.Clamp(ground.SignedRollDegrees, -30f, 30f));
        }

        private bool IgnoreMotionCollider(Collider hit)
        {
            return hit == null || hit.transform == transform || hit.transform.IsChildOf(transform)
                || hit.GetComponentInParent<PlayerControllerB>() != null || hit.GetComponentInParent<EnemyAI>() != null;
        }

        private int MotionMask => StartOfRound.Instance != null
            ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : Physics.DefaultRaycastLayers;

        /// <summary>Extra clearance the spawn gate demands around the motion box, so "clear" means not touching.</summary>
        private const float SpawnClearancePadding = 0.05f;
        private static readonly Collider[] SpawnOverlaps = new Collider[32];

        /// <summary>Full width, height and length of the volume the spawn gate sweeps (motion box plus padding).</summary>
        internal static Vector3 SpawnClearanceSize => (MotionHalfExtents + Vector3.one * SpawnClearancePadding) * 2f;

        /// <summary>
        /// Spawn-time gate (#877): true when nothing but players and enemies intersects the cart's
        /// padded motion box at the given pose. This is the exact volume the motor sweeps from, so
        /// a pose that passes here is one the cart can move out of; a pose that fails would leave
        /// the sweep and rotation tests blocked from the first tick, because the motor has no
        /// depenetration step. <paramref name="ignore"/> excludes an already-spawned cart's own
        /// colliders when re-checking its live pose. <paramref name="margin"/> (#1271 D1) widens the
        /// box by that much on its four sides and its top, never its bottom, so a caller can demand
        /// clear space around the cart without the floor under the wheels counting against it.
        /// </summary>
        internal static bool IsSpawnPoseClear(Vector3 position, Quaternion rotation, out string blocker, Transform ignore = null, float margin = 0f)
        {
            blocker = null;
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : Physics.DefaultRaycastLayers;
            Vector3 halfExtents = MotionHalfExtents + Vector3.one * SpawnClearancePadding + new Vector3(margin, margin * 0.5f, margin);
            Vector3 center = MotionCenter + Vector3.up * (margin * 0.5f);
            int count = Physics.OverlapBoxNonAlloc(position + rotation * center,
                halfExtents, SpawnOverlaps, rotation, mask, QueryTriggerInteraction.Ignore);
            if (count == SpawnOverlaps.Length) { blocker = "<too many colliders>"; return false; }
            for (int i = 0; i < count; i++)
            {
                Collider hit = SpawnOverlaps[i];
                SpawnOverlaps[i] = null;
                if (hit == null) continue;
                if (ignore != null && (hit.transform == ignore || hit.transform.IsChildOf(ignore))) continue;
                if (hit.GetComponentInParent<PlayerControllerB>() != null || hit.GetComponentInParent<EnemyAI>() != null) continue;
                if (blocker == null) blocker = hit.name;
            }
            return blocker == null;
        }

        private bool MotionPoseClear(Vector3 position, Quaternion rotation)
        {
            int count = Physics.OverlapBoxNonAlloc(position + rotation * MotionCenter,
                MotionHalfExtents, _motionOverlaps, rotation, MotionMask, QueryTriggerInteraction.Ignore);
            if (count == _motionOverlaps.Length) { _motionBlocker = null; return false; }
            for (int i = 0; i < count; i++)
                if (!IgnoreMotionCollider(_motionOverlaps[i]))
                { _motionBlocker = _motionOverlaps[i]; return false; }
            return true;
        }

        private Quaternion ResolveMotionRotation(Vector3 position, Quaternion from, Quaternion to)
        {
            int steps = Mathf.Max(1, Mathf.CeilToInt(Quaternion.Angle(from, to) / 2f));
            Quaternion accepted = from;
            for (int i = 1; i <= steps; i++)
            {
                Quaternion candidate = Quaternion.Slerp(from, to, i / (float)steps);
                if (!MotionPoseClear(position, candidate)) break;
                accepted = candidate;
            }
            return accepted;
        }

        private Vector3 SweepMotion(Vector3 position, Vector3 movement, Quaternion rotation)
        {
            for (int pass = 0; pass < 3 && movement.sqrMagnitude > 0.000001f; pass++)
            {
                float distance = movement.magnitude;
                Vector3 direction = movement / distance;
                int count = Physics.BoxCastNonAlloc(position + rotation * MotionCenter, MotionHalfExtents,
                    direction, _motionHits, rotation, distance + MotionSkin, MotionMask, QueryTriggerInteraction.Ignore);
                if (count == _motionHits.Length) break;
                float closest = distance + MotionSkin;
                Vector3 normal = Vector3.zero;
                Collider blocker = null;
                bool blocked = false;
                for (int i = 0; i < count; i++)
                {
                    if (IgnoreMotionCollider(_motionHits[i].collider)) continue;
                    if (_motionHits[i].distance < closest)
                    { closest = _motionHits[i].distance; normal = _motionHits[i].normal; blocker = _motionHits[i].collider; blocked = true; }
                }
                if (!blocked) return position + movement;
                // 2026-09-16 (WP2 step 4): the reference only. Ordinary wall-sliding reaches this
                // line every fixed step, so nothing here may allocate.
                _motionBlocker = blocker;
                if (direction.y < 0f && normal.y > 0.5f) _motionLandingContact = true;
                float travel = Mathf.Clamp(closest - MotionSkin, 0f, distance);
                position += direction * travel;
                movement = Vector3.ProjectOnPlane(direction * (distance - travel), normal);
                if (normal.sqrMagnitude < 0.1f) break;
            }
            return position;
        }
    }
}
