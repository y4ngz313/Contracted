using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.Events;

namespace Y4NGZCompany.Escalations.Bundy.AI;

internal sealed class BundyDoorOpener : MonoBehaviour
{
    private const float DoorScanInterval = 0.1f;
    private const float InteractionScanInterval = 0.22f;
    private const float TriggerStayQueueInterval = 0.12f;
    private const float DoorOpenPauseSeconds = 0.08f;
    private const float DoorResumePauseSeconds = 0.18f;
    private const float InteractionUseRange = 2.65f;
    private const float InteractionScanForwardDistance = 5.25f;
    private const float InteractionScanHalfWidth = 3.6f;
    private const float InteractionUsePauseSeconds = 0.48f;
    private const float InteractionResumePauseSeconds = 1.05f;
    private const float InteractionApproachRefreshInterval = 0.4f;
    private const float InteractionApproachHoldSeconds = 0.55f;
    private const float InteractionQueueTimeoutSeconds = 14f;
    private const float InteractionUseCooldown = 1.4f;
    // 2026-07-24: an unreachable gate (ship hatch trigger with its approach point up on the deck)
    // re-queued instantly after every 14s approach timeout, freezing Bundy in place indefinitely
    // while the hunt target stood on the ship railing. A timed-out trigger sits out this long.
    private const float InteractionFailureCooldownSeconds = 45f;
    private const float InteractionStallForceRange = 4.5f;
    private const float InteractionStallVelocity = 0.2f;
    private const float InteractionStallSeconds = 1f;
    private static readonly Collider[] DoorHits = new Collider[48];
    private static readonly Collider[] InteractionHits = new Collider[48];

    private BundyAIBase _bundy;
    private DoorLock _pendingDoor;
    private InteractTrigger _pendingInteractionTrigger;
    private AnimatedObjectTrigger _pendingAnimatedTrigger;
    private Vector3 _pendingInteractionPosition;
    private float _pendingOpenTime;
    private float _pendingInteractionUseTime;
    private float _pendingInteractionExpireTime;
    private float _nextScanTime;
    private float _nextInteractionScanTime;
    private float _nextInteractionApproachTime;
    private float _nextTriggerQueueTime;
    private float _lastDoorOpenTime;
    private float _lastInteractionUseTime;
    private float _pendingInteractionLowVelocitySince;
    private bool _loggedPendingInteractionApproach;
    private readonly Dictionary<UnityEngine.Object, float> _interactionFailureCooldowns = new Dictionary<UnityEngine.Object, float>();
    // #271: one gated receipt per door per reason so a refused door explains itself without spamming.
    private const int DoorRejectReasonLocked = 1;
    private const int DoorRejectReasonAlreadyOpen = 2;
    private const int DoorRejectReasonCooldown = 4;
    private readonly Dictionary<int, int> _doorRejectionReceipts = new Dictionary<int, int>();
    private static readonly object[] OpenDoorAsEnemyLocalArgs = { true };
    private static MethodInfo _openDoorAsEnemy;
    private static MethodInfo _openDoorAsEnemyLocal;
    private static FieldInfo _doorOpenedField;
    private static FieldInfo _interactHasTriggeredField;
    private static PropertyInfo _doorOpenedProperty;
    private static bool _resolved;

    internal void Init(BundyAIBase bundy)
    {
        _bundy = bundy;
        int phase = _bundy != null ? (_bundy.GetInstanceID() & 7) : 0;
        _nextScanTime = Time.time + phase * 0.025f;
        ResolveOpenDoor();
    }

    private void Update()
    {
        TryOpenPendingDoor();
        TryUsePendingWorldInteraction();
        ScanForwardForDoor();
        ScanForwardForWorldInteraction();
    }

    private void OnTriggerEnter(Collider other)
    {
        QueueDoor(other);
        QueueWorldInteraction(other);
    }

    private void OnTriggerStay(Collider other)
    {
        if (Time.time < _nextTriggerQueueTime)
            return;

        _nextTriggerQueueTime = Time.time + TriggerStayQueueInterval;
        QueueDoor(other);
        QueueWorldInteraction(other);
    }

    private void ScanForwardForDoor()
    {
        if (!CanOpenDoors() || Time.time < _nextScanTime)
            return;
        if (_pendingAnimatedTrigger != null)
            return;

        _nextScanTime = Time.time + DoorScanInterval;
        Vector3 center = transform.position + transform.forward * 0.45f;
        Vector3 halfExtents = new Vector3(1.15f, 1.15f, 1.95f);
        int hitCount = Physics.OverlapBoxNonAlloc(center, halfExtents, DoorHits, transform.rotation, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < hitCount; i++)
        {
            DoorLock door = ResolveDoor(DoorHits[i]);
            if (!IsUsableDoor(door) || !DoorIsAhead(door))
                continue;

            QueueDoor(door);
            return;
        }
    }

    private void ScanForwardForWorldInteraction()
    {
        if (!CanUseWorldInteractions())
            return;
        if (_pendingAnimatedTrigger != null)
            return;
        if (Time.time < _nextInteractionScanTime || Time.time < _lastInteractionUseTime + InteractionUseCooldown)
            return;

        _nextInteractionScanTime = Time.time + InteractionScanInterval;
        Vector3 center = transform.position + transform.forward * (InteractionScanForwardDistance * 0.5f);
        Vector3 halfExtents = new Vector3(InteractionScanHalfWidth, 1.65f, InteractionScanForwardDistance * 0.5f);
        int hitCount = Physics.OverlapBoxNonAlloc(center, halfExtents, InteractionHits, transform.rotation, ~0, QueryTriggerInteraction.Collide);
        for (int i = 0; i < hitCount; i++)
        {
            if (!TryResolveWorldInteraction(InteractionHits[i], out InteractTrigger trigger, out AnimatedObjectTrigger animated))
                continue;
            if (!IsUsableWorldInteraction(trigger, animated) || !InteractionIsAheadOrNearby(trigger.transform))
                continue;

            QueueWorldInteraction(trigger, animated);
            return;
        }
    }

    private void QueueDoor(Collider other)
    {
        QueueDoor(ResolveDoor(other));
    }

    private void QueueWorldInteraction(Collider other)
    {
        if (!TryResolveWorldInteraction(other, out InteractTrigger trigger, out AnimatedObjectTrigger animated))
            return;

        QueueWorldInteraction(trigger, animated);
    }

    internal bool ForceOpenDoorForCollider(Collider other)
    {
        DoorLock door = ResolveDoor(other);
        if (!CanOpenDoors())
            return false;
        if (!IsUsableDoor(door))
        {
            LogDoorRejection(door);
            return false;
        }

        _pendingDoor = null;
        _pendingOpenTime = 0f;
        bool opened = TryOpenDoor(door);
        if (opened)
            _bundy.PauseForSmartInteraction(DoorResumePauseSeconds);
        return opened;
    }

    private void QueueDoor(DoorLock door)
    {
        if (!CanOpenDoors())
            return;
        if (!IsUsableDoor(door))
        {
            LogDoorRejection(door);
            return;
        }
        if (_pendingDoor == door)
            return;
        if (Time.time < _lastDoorOpenTime + 0.35f)
            return;

        _pendingDoor = door;
        _pendingOpenTime = Time.time + DoorOpenPauseSeconds;
        _bundy.PauseForSmartInteraction(DoorOpenPauseSeconds + DoorResumePauseSeconds);
    }

    private static UnityEngine.Object GetInteractionCooldownKey(InteractTrigger trigger, AnimatedObjectTrigger animated)
    {
        return animated != null ? (UnityEngine.Object)animated : trigger;
    }

    private bool IsInteractionOnFailureCooldown(InteractTrigger trigger, AnimatedObjectTrigger animated)
    {
        UnityEngine.Object key = GetInteractionCooldownKey(trigger, animated);
        return key != null &&
            _interactionFailureCooldowns.TryGetValue(key, out float readyAt) &&
            Time.time < readyAt;
    }

    private void SetInteractionFailureCooldown(InteractTrigger trigger, AnimatedObjectTrigger animated)
    {
        UnityEngine.Object key = GetInteractionCooldownKey(trigger, animated);
        if (key != null)
            _interactionFailureCooldowns[key] = Time.time + InteractionFailureCooldownSeconds;
    }

    private void QueueWorldInteraction(InteractTrigger trigger, AnimatedObjectTrigger animated)
    {
        if (!CanUseWorldInteractions())
            return;
        if (!IsUsableWorldInteraction(trigger, animated))
            return;
        if (_pendingAnimatedTrigger == animated)
            return;
        if (Time.time < _lastInteractionUseTime + InteractionUseCooldown)
            return;
        if (IsInteractionOnFailureCooldown(trigger, animated))
            return;
        if (!InteractionIsAheadOrNearby(trigger.transform))
            return;

        _pendingInteractionTrigger = trigger;
        _pendingAnimatedTrigger = animated;
        _pendingInteractionPosition = GetInteractionPosition(trigger, animated);
        _pendingInteractionUseTime = Time.time + InteractionUsePauseSeconds;
        _pendingInteractionExpireTime = Time.time + InteractionQueueTimeoutSeconds;
        _nextInteractionApproachTime = 0f;
        _pendingInteractionLowVelocitySince = 0f;
        _loggedPendingInteractionApproach = false;
        Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Bundy queued gate interaction '{GetInteractionName(trigger, animated)}' at {_pendingInteractionPosition}.");
    }

    private void TryOpenPendingDoor()
    {
        if (_pendingDoor == null)
            return;

        if (!CanOpenDoors() || !IsUsableDoor(_pendingDoor))
        {
            _pendingDoor = null;
            return;
        }

        if (Time.time < _pendingOpenTime)
        {
            _bundy.PauseForSmartInteraction((_pendingOpenTime - Time.time) + DoorResumePauseSeconds);
            return;
        }

        TryOpenDoor(_pendingDoor);
        _pendingDoor = null;
    }

    private void TryUsePendingWorldInteraction()
    {
        if (_pendingAnimatedTrigger == null)
            return;

        if (!CanUseWorldInteractions() || !IsUsableWorldInteraction(_pendingInteractionTrigger, _pendingAnimatedTrigger))
        {
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Bundy gate interaction expired before use: '{GetInteractionName(_pendingInteractionTrigger, _pendingAnimatedTrigger)}' became unusable or authority was lost.");
            ClearPendingWorldInteraction();
            return;
        }

        _pendingInteractionPosition = GetInteractionPosition(_pendingInteractionTrigger, _pendingAnimatedTrigger);
        float distance = HorizontalDistance(_bundy.transform.position, _pendingInteractionPosition);
        if (ShouldForcePendingWorldInteraction(distance))
        {
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Bundy forcing gate interaction '{GetInteractionName(_pendingInteractionTrigger, _pendingAnimatedTrigger)}' after low-velocity stall. distance={distance:0.00}.");
            TryUseWorldInteraction(_pendingInteractionTrigger, _pendingAnimatedTrigger, forced: true, "low-velocity stall near interaction");
            ClearPendingWorldInteraction();
            return;
        }

        if (distance > InteractionUseRange)
        {
            if (Time.time >= _pendingInteractionExpireTime)
            {
                SetInteractionFailureCooldown(_pendingInteractionTrigger, _pendingAnimatedTrigger);
                Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo(
                    $"[Bundy] Bundy gate interaction timed out before reaching '{GetInteractionName(_pendingInteractionTrigger, _pendingAnimatedTrigger)}'. " +
                    $"distance={distance:0.00}, target={_pendingInteractionPosition}; cooling it down for {InteractionFailureCooldownSeconds:0}s.");
                ClearPendingWorldInteraction();
                return;
            }

            if (Time.time >= _nextInteractionApproachTime)
                IssueInteractionApproach(_pendingInteractionPosition);
            return;
        }

        if (Time.time < _pendingInteractionUseTime)
        {
            _bundy.PauseForSmartInteraction((_pendingInteractionUseTime - Time.time) + InteractionResumePauseSeconds);
            return;
        }

        TryUseWorldInteraction(_pendingInteractionTrigger, _pendingAnimatedTrigger);
        ClearPendingWorldInteraction();
    }

    private bool TryOpenDoor(DoorLock door)
    {
        if (!CanOpenDoors())
            return false;
        if (Time.time < _lastDoorOpenTime + 0.55f)
        {
            LogDoorRejectionOnce(door, DoorRejectReasonCooldown, "open cooldown");
            return false;
        }
        if (!IsUsableDoor(door))
        {
            LogDoorRejection(door);
            return false;
        }

        ResolveOpenDoor();
        // #271: OpenDoorAsEnemyServerRpc only flips isDoorOpened and the NavMeshObstacle — the leaf
        // itself moves in AnimatedObjectTrigger.TriggerAnimationNonPlayer, which vanilla pairs with the
        // RPC in DoorLock.OnTriggerStay. A state-only open left the door visually shut and self-latched:
        // IsUsableDoor then rejected the "open" door forever.
        AnimatedObjectTrigger animated = door.GetComponent<AnimatedObjectTrigger>()
            ?? door.GetComponentInParent<AnimatedObjectTrigger>()
            ?? door.GetComponentInChildren<AnimatedObjectTrigger>(true);
        if (_openDoorAsEnemy == null && animated == null)
        {
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Door open skipped for '{door.gameObject.name}': no AnimatedObjectTrigger and DoorLock.OpenDoorAsEnemyServerRpc reflection member missing.");
            return false;
        }

        _lastDoorOpenTime = Time.time;
        _bundy.PauseForSmartInteraction(DoorResumePauseSeconds);

        // TriggerAnimationNonPlayer is an unconditional TOGGLE (boolValue = !boolValue when isBool;
        // overrideBool does not force open). boolValue true means the leaf is already open/opening —
        // e.g. vanilla's own enemy-door meter fired a frame ago while isDoorOpened still lags its RPC —
        // and toggling again would slam it shut. Skip the toggle then and only latch the door state.
        bool toggledAnimation = false;
        if (animated != null && !(animated.isBool && animated.boolValue))
        {
            try
            {
                animated.TriggerAnimationNonPlayer(playSecondaryAudios: false, overrideBool: true, setBoolFalse: false);
                toggledAnimation = true;
            }
            catch (Exception e)
            {
                Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Door open animation skipped: {e.Message}");
            }
        }

        // The RPC gets its own try so a throw cannot skip the state latch below or leave the toggled
        // leaf without a matching isDoorOpened flip (which would re-toggle it shut on the next retry).
        bool rpcInvoked = false;
        if (_openDoorAsEnemy != null)
        {
            try
            {
                _openDoorAsEnemy.Invoke(door, null);
                rpcInvoked = true;
            }
            catch (TargetInvocationException e)
            {
                Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Door open RPC threw: {e.InnerException?.Message ?? e.Message}");
            }
            catch (Exception e)
            {
                Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Door open RPC skipped: {e.Message}");
            }
        }

        bool latched = false;
        try
        {
            if (!DoorIsOpen(door) && _openDoorAsEnemyLocal != null)
                _openDoorAsEnemyLocal.Invoke(door, OpenDoorAsEnemyLocalArgs);
            latched = DoorIsOpen(door);
        }
        catch (Exception e)
        {
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Door open state latch skipped: {e.Message}");
        }

        bool attempted = toggledAnimation || rpcInvoked || latched;
        if (attempted)
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Bundy opened door '{door.gameObject.name}'. animated={toggledAnimation}, rpc={rpcInvoked}, latched={latched}.");
        return attempted;
    }

    private void LogDoorRejection(DoorLock door)
    {
        if (door == null)
            return;

        if (DoorIsOpen(door))
            LogDoorRejectionOnce(door, DoorRejectReasonAlreadyOpen, "already open");
        else if (door.isLocked)
            LogDoorRejectionOnce(door, DoorRejectReasonLocked, "locked");
    }

    private void LogDoorRejectionOnce(DoorLock door, int reasonFlag, string reason)
    {
        if (door == null || !BundyLogGate.Allows(BundyLogCategory.Movement, BundyLogGate.DefaultVerbosity))
            return;

        int id = door.GetInstanceID();
        _doorRejectionReceipts.TryGetValue(id, out int logged);
        if ((logged & reasonFlag) != 0)
            return;

        _doorRejectionReceipts[id] = logged | reasonFlag;
        Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Door '{door.gameObject.name}' refused: {reason}.");
    }

    private bool TryUseWorldInteraction(InteractTrigger trigger, AnimatedObjectTrigger animated, bool forced = false, string reason = null)
    {
        if (!CanUseWorldInteractions() || !IsUsableWorldInteraction(trigger, animated))
        {
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Gate interaction skipped before trigger: '{GetInteractionName(trigger, animated)}' unusable. forced={forced}, reason={reason ?? "normal use"}.");
            return false;
        }

        _lastInteractionUseTime = Time.time;
        _bundy.PauseForSmartInteraction(InteractionResumePauseSeconds);
        try
        {
            animated.TriggerAnimationNonPlayer(playSecondaryAudios: false, overrideBool: false, setBoolFalse: false);
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Bundy used gate interaction '{GetInteractionName(trigger, animated)}'. forced={forced}, reason={reason ?? "normal use"}.");
            return true;
        }
        catch (Exception e)
        {
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Gate interaction skipped: {e.Message}. forced={forced}, reason={reason ?? "normal use"}.");
            return false;
        }
    }

    private bool CanOpenDoors()
    {
        return _bundy != null && _bundy.IsOwner && !_bundy.isEnemyDead;
    }

    private bool CanUseWorldInteractions()
    {
        return CanOpenDoors();
    }

    private static DoorLock ResolveDoor(Collider other)
    {
        return other != null ? other.GetComponentInParent<DoorLock>() : null;
    }

    private static bool TryResolveWorldInteraction(Collider other, out InteractTrigger trigger, out AnimatedObjectTrigger animated)
    {
        trigger = null;
        animated = null;
        if (other == null)
            return false;

        trigger = other.GetComponentInParent<InteractTrigger>();
        if (trigger == null)
            trigger = other.GetComponentInChildren<InteractTrigger>();
        if (trigger == null)
            return false;

        animated = ResolveAnimatedTrigger(trigger);
        return animated != null;
    }

    private static bool IsUsableDoor(DoorLock door)
    {
        return door != null && !DoorIsOpen(door) && !door.isLocked && HasInteractableOpenSide(door);
    }

    private static bool IsUsableWorldInteraction(InteractTrigger trigger, AnimatedObjectTrigger animated)
    {
        if (trigger == null || animated == null)
            return false;
        if (!trigger.interactable || trigger.isLadder || trigger.holdInteraction || trigger.isBeingHeldByPlayer)
            return false;
        if (trigger.triggerOnce && TriggerHasAlreadyFired(trigger))
            return false;
        if (trigger.interactCooldown && trigger.currentCooldownValue > 0.05f)
            return false;
        if (animated.triggerByChance || animated.triggerAnimator == null)
            return false;
        if (animated.isBool && animated.boolValue)
            return false;

        return LooksLikeGateInteraction(trigger, animated);
    }

    private static bool HasInteractableOpenSide(DoorLock door)
    {
        // #271: the enemy open path (TriggerAnimationNonPlayer + OpenDoorAsEnemyServerRpc) never goes
        // through the player-side InteractTriggers, so their interactable flag says nothing about
        // whether Bundy can open the door. Locked doors are still rejected in IsUsableDoor.
        return door != null;
    }

    internal static bool DoorIsOpen(DoorLock door)
    {
        if (door == null)
            return false;

        ResolveOpenDoor();
        try
        {
            if (_doorOpenedField != null && _doorOpenedField.GetValue(door) is bool fieldValue)
                return fieldValue;
            if (_doorOpenedProperty != null && _doorOpenedProperty.GetValue(door, null) is bool propertyValue)
                return propertyValue;
        }
        catch (Exception e)
        {
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Door open-state check skipped: {e.Message}");
        }

        return false;
    }

    private bool DoorIsAhead(DoorLock door)
    {
        if (_bundy == null || door == null)
            return false;

        Vector3 toDoor = door.transform.position - _bundy.transform.position;
        toDoor.y = 0f;
        if (toDoor.sqrMagnitude < 0.01f)
            return true;
        if (toDoor.sqrMagnitude <= 9f)
            return true;

        return Vector3.Dot(_bundy.transform.forward, toDoor.normalized) > -0.25f;
    }

    private bool InteractionIsAheadOrNearby(Transform target)
    {
        if (_bundy == null || target == null)
            return false;

        Vector3 toTarget = target.position - _bundy.transform.position;
        toTarget.y = 0f;
        float distance = toTarget.magnitude;
        if (distance <= InteractionUseRange + 0.65f)
            return true;
        if (distance > InteractionScanForwardDistance + 1.5f || toTarget.sqrMagnitude < 0.01f)
            return false;

        return Vector3.Dot(_bundy.transform.forward, toTarget.normalized) > -0.25f;
    }

    private void IssueInteractionApproach(Vector3 position)
    {
        _nextInteractionApproachTime = Time.time + InteractionApproachRefreshInterval;
        Vector3 destination = position;
        if (NavMesh.SamplePosition(position, out NavMeshHit hit, 4f, NavMesh.AllAreas))
            destination = hit.position;

        _bundy.PauseForSmartInteraction(InteractionApproachHoldSeconds);
        _bundy.IssueRawAgentDestination(destination, "door-approach", "closing to gate interaction");
        if (!_loggedPendingInteractionApproach)
        {
            _loggedPendingInteractionApproach = true;
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Bundy approaching gate interaction '{GetInteractionName(_pendingInteractionTrigger, _pendingAnimatedTrigger)}'. destination={destination}, target={position}.");
        }
    }

    private bool ShouldForcePendingWorldInteraction(float distance)
    {
        if (distance > InteractionStallForceRange)
        {
            _pendingInteractionLowVelocitySince = 0f;
            return false;
        }

        float velocity = GetActorVelocityMagnitude();
        if (velocity > InteractionStallVelocity)
        {
            _pendingInteractionLowVelocitySince = 0f;
            return false;
        }

        if (_pendingInteractionLowVelocitySince <= 0f)
        {
            _pendingInteractionLowVelocitySince = Time.time;
            return false;
        }

        return Time.time - _pendingInteractionLowVelocitySince >= InteractionStallSeconds;
    }

    private float GetActorVelocityMagnitude()
    {
        if (_bundy == null)
            return 0f;

        NavMeshAgent agent = _bundy.GetComponent<NavMeshAgent>();
        return agent != null && agent.enabled ? agent.velocity.magnitude : 0f;
    }

    internal void ClearPendingWorldInteractionForTeleport(string reason)
    {
        if (_pendingAnimatedTrigger == null)
            return;

        Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Clearing pending gate interaction '{GetInteractionName(_pendingInteractionTrigger, _pendingAnimatedTrigger)}' before {reason}.");
        ClearPendingWorldInteraction();
    }

    private void ClearPendingWorldInteraction()
    {
        _pendingInteractionTrigger = null;
        _pendingAnimatedTrigger = null;
        _pendingInteractionPosition = Vector3.zero;
        _pendingInteractionUseTime = 0f;
        _pendingInteractionExpireTime = 0f;
        _pendingInteractionLowVelocitySince = 0f;
        _loggedPendingInteractionApproach = false;
    }

    private static AnimatedObjectTrigger ResolveAnimatedTrigger(InteractTrigger trigger)
    {
        if (trigger == null)
            return null;

        AnimatedObjectTrigger animated = trigger.GetComponent<AnimatedObjectTrigger>()
            ?? trigger.GetComponentInParent<AnimatedObjectTrigger>()
            ?? trigger.GetComponentInChildren<AnimatedObjectTrigger>();
        if (animated != null)
            return animated;

        animated = ResolveAnimatedTriggerFromEvent(trigger.onInteract);
        if (animated != null)
            return animated;

        animated = ResolveAnimatedTriggerFromEvent(trigger.onInteractEarly);
        if (animated != null)
            return animated;

        return ResolveAnimatedTriggerFromEvent(trigger.onInteractEarlyOtherClients);
    }

    private static AnimatedObjectTrigger ResolveAnimatedTriggerFromEvent(UnityEventBase unityEvent)
    {
        if (unityEvent == null)
            return null;

        try
        {
            int count = unityEvent.GetPersistentEventCount();
            for (int i = 0; i < count; i++)
            {
                UnityEngine.Object target = unityEvent.GetPersistentTarget(i);
                if (target is AnimatedObjectTrigger animated)
                    return animated;
                if (target is Component component)
                {
                    animated = component.GetComponent<AnimatedObjectTrigger>()
                        ?? component.GetComponentInParent<AnimatedObjectTrigger>()
                        ?? component.GetComponentInChildren<AnimatedObjectTrigger>();
                    if (animated != null)
                        return animated;
                }
            }
        }
        catch (Exception e)
        {
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo($"[Bundy] Gate interaction event probe skipped: {e.Message}");
        }

        return null;
    }

    private static bool LooksLikeGateInteraction(InteractTrigger trigger, AnimatedObjectTrigger animated)
    {
        string descriptor = GetInteractionDescriptor(trigger, animated).ToLowerInvariant();
        if (ContainsAny(descriptor, "lever", "gate", "bulkhead", "blast", "bunker", "security door", "securitydoor", "entrance door", "entrancedoor"))
            return true;

        return descriptor.Contains("open") && (descriptor.Contains("door") || descriptor.Contains("gate"));
    }

    private static bool ContainsAny(string text, params string[] tokens)
    {
        if (string.IsNullOrEmpty(text) || tokens == null)
            return false;
        for (int i = 0; i < tokens.Length; i++)
        {
            if (!string.IsNullOrEmpty(tokens[i]) && text.Contains(tokens[i]))
                return true;
        }
        return false;
    }

    private static bool TriggerHasAlreadyFired(InteractTrigger trigger)
    {
        if (trigger == null)
            return false;

        ResolveOpenDoor();
        try
        {
            return _interactHasTriggeredField != null
                && _interactHasTriggeredField.GetValue(trigger) is bool value
                && value;
        }
        catch
        {
            return false;
        }
    }

    private static Vector3 GetInteractionPosition(InteractTrigger trigger, AnimatedObjectTrigger animated)
    {
        if (trigger != null)
            return trigger.transform.position;
        return animated != null ? animated.transform.position : Vector3.zero;
    }

    private static string GetInteractionName(InteractTrigger trigger, AnimatedObjectTrigger animated)
    {
        if (trigger != null && trigger.gameObject != null)
            return trigger.gameObject.name;
        if (animated != null && animated.gameObject != null)
            return animated.gameObject.name;
        return "<unknown>";
    }

    private static string GetInteractionDescriptor(InteractTrigger trigger, AnimatedObjectTrigger animated)
    {
        string descriptor = string.Empty;
        if (trigger != null)
        {
            descriptor += " " + trigger.name;
            descriptor += " " + trigger.hoverTip;
            descriptor += " " + trigger.disabledHoverTip;
            descriptor += " " + GetTransformNameChain(trigger.transform);
        }
        if (animated != null)
        {
            descriptor += " " + animated.name;
            descriptor += " " + animated.animationString;
            descriptor += " " + GetTransformNameChain(animated.transform);
            if (animated.triggerAnimator != null)
                descriptor += " " + animated.triggerAnimator.name;
            if (animated.triggerAnimatorB != null)
                descriptor += " " + animated.triggerAnimatorB.name;
        }
        return descriptor;
    }

    private static string GetTransformNameChain(Transform transform)
    {
        string names = string.Empty;
        Transform current = transform;
        for (int i = 0; i < 4 && current != null; i++)
        {
            names += " " + current.name;
            current = current.parent;
        }
        return names;
    }

    private static float HorizontalDistance(Vector3 a, Vector3 b)
    {
        float dx = a.x - b.x;
        float dz = a.z - b.z;
        return Mathf.Sqrt(dx * dx + dz * dz);
    }

    private static void ResolveOpenDoor()
    {
        if (_resolved)
            return;

        _resolved = true;
        try
        {
            _openDoorAsEnemy = typeof(DoorLock).GetMethod(
                "OpenDoorAsEnemyServerRpc",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            _openDoorAsEnemyLocal = typeof(DoorLock).GetMethod(
                "OpenDoorAsEnemy",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            const BindingFlags doorStateFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            _doorOpenedField = typeof(DoorLock).GetField("isDoorOpened", doorStateFlags)
                ?? typeof(DoorLock).GetField("isDoorOpen", doorStateFlags);
            _doorOpenedProperty = typeof(DoorLock).GetProperty("isDoorOpened", doorStateFlags)
                ?? typeof(DoorLock).GetProperty("isDoorOpen", doorStateFlags);
            _interactHasTriggeredField = typeof(InteractTrigger).GetField("hasTriggered", doorStateFlags);
            if (_openDoorAsEnemy == null)
                Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo("[Bundy] Door opener reflection member missing: DoorLock.OpenDoorAsEnemyServerRpc.");
            if (_doorOpenedField == null && _doorOpenedProperty == null)
                Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo("[Bundy] Door opener reflection member missing: DoorLock open-state field/property.");
            if (_interactHasTriggeredField == null)
                Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogInfo("[Bundy] Door opener reflection member missing: InteractTrigger.hasTriggered.");
        }
        catch (Exception e)
        {
            Y4NGZCompany.Bootstrap.BundyPlugin.Log?.LogWarning($"[Bundy] Door opener reflection failed: {e.Message}");
        }
    }
}
