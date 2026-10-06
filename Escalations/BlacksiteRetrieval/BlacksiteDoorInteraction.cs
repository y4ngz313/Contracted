using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1233 S5 (rule 6): the hand. The navigator routes; this operates whatever gate stands on
    /// that route within reach, and never holds a destination, a lease or a clock of its own.
    /// When the BODY is held on its route (<see cref="BlacksiteSoldierAI.BlacksiteNavigator.BodyHeldOnRoute"/>)
    /// the navigator asks once per frame for an operable gate along the agent's own path or, on a
    /// partial route, beside its end in any direction (#1305 E3: a lock is not a wall); one found is
    /// operated through vanilla's own replicated API and the task ends on that tick. Whether the body
    /// then passes is the route's fact, re-read by the navigator. Soldiers may crowd one door: every
    /// held body operates the same gate, and vanilla's own state (unlocked, open, boolValue) makes the
    /// second and later operations no-ops.
    /// </summary>
    internal sealed class BlacksiteDoorInteraction
    {
        private const float PathProbeRadius = 0.34f;
        private const float PathProbeHeight = 1.05f;
        // #1305 E3: the probe reach past a partial route's end that the hand's reach budget adds:
        // one probe sphere plus one body. A locked door CARVES the navmesh (DoorLock.LockDoor arms
        // its NavMeshObstacle), so the route ends at the carve boundary beside the leaf.
        private const float PartialPathProbeExtension =
            PathProbeRadius + BlacksiteArrivalPolicy.AgentRadius;
        private const int MaxPathCorners = 32;
        private static readonly RaycastHit[] PathHits = new RaycastHit[24];
        private static readonly Collider[] TailHits = new Collider[64];
        private static readonly Vector3[] PathCorners = new Vector3[MaxPathCorners];

        private static bool _compatibilityResolved;
        private static bool _loggedMissingDoorState;
        private static FieldInfo _doorOpenedField;
        private static PropertyInfo _doorOpenedProperty;
        // #1305 G1 (R14): the round's doors, found once a round for the route service's door legs. Keyed
        // like the entrance hand's round cache (the RoundManager and BlacksiteEntranceInteraction.GetRoundKey),
        // so a new round refreshes it on its first question.
        private static readonly List<DoorLock> RoundDoors = new List<DoorLock>();
        private static RoundManager _doorsRound;
        private static int _doorsRoundKey = int.MinValue;
        // A door leg's two sides: this far either side of the leaf, sampled within this radius at the frontier's height.
        private const float DoorSideMetres = 1.25f;
        private const float DoorSideSample = .75f;

        private readonly BlacksiteSoldierAI _soldier;
        private readonly NavMeshAgent _agent;
        // The gate this hand operated on the held episode it is still in and that has not read open
        // since. Not a blacklist and not a clock: it is forgotten the frame the gate reads open (so a
        // gate a player closes again while the body is still held by the crowd is a gate again) and
        // the frame the body is no longer held on its route (BlacksiteInteractionCoordinator
        // .TickGateInReach), while a gate whose leaf never reads open - a non-bool trigger, a DoorLock
        // whose open flag this build cannot read - costs one vanilla operation per approach instead
        // of one per frame.
        private Component _gateInHand;

        private sealed class DoorContext
        {
            internal DoorLock Door;
            internal AnimatedObjectTrigger AnimatedTrigger;
            // #1179 P03: this member's task performed the vanilla no-key unlock on this door.
            internal bool Unlocked;
            // #1305 E3: why IssueOpen returned false - noTrigger, stillLocked or error:<Type>.
            internal string Failure;
        }

        internal BlacksiteDoorInteraction(BlacksiteSoldierAI soldier, NavMeshAgent agent)
        {
            _soldier = soldier;
            _agent = agent;
            EnsureCompatibilityResolved();
        }

        /// <summary>The body is no longer held on its route; whatever it operated is behind it.</summary>
        internal void ReleaseHand() => _gateInHand = null;

        /// <summary>
        /// #1305 G1 (R14): the route service's door leg. The nearest door this hand can operate - locked with a
        /// trigger to unlock it, or unlocked and shut - within <paramref name="radius"/> of a route's frontier, and a
        /// standable point either side of it: <paramref name="near"/> on the frontier's side, <paramref name="far"/>
        /// straight through the leaf. A locked door carves the navmesh, so its frontier stands beside it.
        /// </summary>
        internal static bool TryGetOperableDoor(Vector3 frontier, float radius, out DoorLock door, out Vector3 near, out Vector3 far)
        {
            door = null;
            near = far = frontier;
            RoundManager round = RoundManager.Instance;
            int roundKey = BlacksiteEntranceInteraction.GetRoundKey();
            if (!ReferenceEquals(round, _doorsRound) || roundKey != _doorsRoundKey)
            {
                _doorsRound = round;
                _doorsRoundKey = roundKey;
                RoundDoors.Clear();
                RoundDoors.AddRange(UnityEngine.Object.FindObjectsOfType<DoorLock>());
            }
            float best = radius * radius;
            for (int i = 0; i < RoundDoors.Count; i++)
            {
                DoorLock candidate = RoundDoors[i];
                if (candidate == null) continue;
                float metres = (candidate.transform.position - frontier).sqrMagnitude;
                if (metres > best || !(candidate.isLocked ? CanUnlockDoor(candidate) : !DoorIsOpen(candidate))) continue;
                best = metres;
                door = candidate;
            }
            if (door == null) return false;
            Vector3 centre = door.transform.position;
            Vector3 through = Vector3.ProjectOnPlane(centre - frontier, Vector3.up);
            if (through.sqrMagnitude < .01f) through = Vector3.ProjectOnPlane(door.transform.forward, Vector3.up);
            through.Normalize();
            return DoorSide(centre - through * DoorSideMetres, frontier.y, out near)
                && DoorSide(centre + through * DoorSideMetres, frontier.y, out far);
        }

        private static bool DoorSide(Vector3 point, float height, out Vector3 side)
        {
            point.y = height;
            bool found = NavMesh.SamplePosition(point, out NavMeshHit hit, DoorSideSample, NavMesh.AllAreas);
            side = hit.position;
            return found;
        }

        /// <summary>
        /// A closed, operable gate within reach, or false. Reach is the walked metres from the body:
        /// the stop it settles at short of its route's end (stoppingDistance plus the arrival table's
        /// settle slack) plus <see cref="PartialPathProbeExtension"/>. On the path the gate must stand
        /// across the route; past a partial route's end it may stand in any direction, because the
        /// door whose carve ended the route is beside that end, not necessarily toward the goal. A
        /// gate that reads open is walked through.
        /// </summary>
        internal bool TryCreatePathTask(Vector3 goal, out BlacksiteInteractionTask task)
        {
            task = null;
            if (_soldier == null || _agent == null || !_agent.enabled || !_agent.isOnNavMesh
                || !_agent.hasPath || _agent.pathPending)
            {
                return false;
            }

            float reach = _agent.stoppingDistance + BlacksiteArrivalPolicy.SettleSlack + PartialPathProbeExtension;
            if (!TryFindGateOnRoute(reach, out DoorLock door, out AnimatedObjectTrigger animated))
            {
                _gateInHand = null;
                return false;
            }

            Component gate = (Component)door ?? animated;
            if (ReferenceEquals(gate, _gateInHand))
                return false;
            _gateInHand = null;

            string label = door != null
                ? $"door '{door.gameObject.name}'"
                : $"path gate '{animated.gameObject.name}'";
            task = new BlacksiteInteractionTask(
                BlacksiteInteractionKind.Door,
                gate,
                _soldier.transform.position,
                goal,
                label)
            {
                Context = new DoorContext
                {
                    Door = door,
                    AnimatedTrigger = animated ?? ResolveAnimatedTrigger(door)
                }
            };
            return true;
        }

        /// <summary>
        /// The one act: unlock if locked, open if closed, through vanilla's own replicated calls.
        /// Complete on the tick the operation ran; Failed only when vanilla could not run it. No
        /// confirm wait and no re-issue: the route the navigator holds is what the body follows.
        /// </summary>
        internal BlacksiteInteractionTickResult Tick(
            BlacksiteInteractionTask task,
            out Vector3 issuedDestination)
        {
            issuedDestination = _soldier != null ? _soldier.transform.position : Vector3.zero;
            if (task == null || !(task.Context is DoorContext context))
                return BlacksiteInteractionTickResult.Failed;

            Component gate = (Component)context.Door ?? context.AnimatedTrigger;
            bool wasLocked = context.Door != null && context.Door.isLocked;
            string result;
            // A dead or stunned host member can still be held on its route; the failure names itself.
            if (_soldier == null || _soldier.IsWorldInteractionInterrupted)
                result = "interrupted";
            else
            {
                task.TransitionTo(BlacksiteInteractionPhase.Perform);
                _gateInHand = gate;
                result = IssueOpen(context, task.Label) ? "ok" : context.Failure;
                // #1305 E3: one line per attempted use, whatever its outcome.
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("Door",
                        $"member={_soldier.MemberIndex}, door={gate.name}, at={BlacksiteDiagnosticFormat.V(gate.transform.position)}"
                        + $", locked={wasLocked}, action={(wasLocked ? "unlock" : "open")}, result={result}");
            }
            if (result != "ok")
            {
                task.FailureReason = result;
                task.TransitionTo(BlacksiteInteractionPhase.Failed);
                return BlacksiteInteractionTickResult.Failed;
            }

            task.OperationIssued = true;
            task.TransitionTo(BlacksiteInteractionPhase.Complete);
            return BlacksiteInteractionTickResult.Complete;
        }

        /// <summary>
        /// The first operable gate along the agent's live path within <paramref name="reach"/>
        /// walked metres of the body or, on a partial path, the nearest one beside its end.
        /// </summary>
        private bool TryFindGateOnRoute(float reach, out DoorLock door, out AnimatedObjectTrigger animated)
        {
            door = null;
            animated = null;
            int count = _agent.path.GetCornersNonAlloc(PathCorners);
            if (count == 0)
                return false;
            float walked = 0f;
            for (int i = 1; i < count && walked <= reach; i++)
            {
                if (TryProbeSegment(PathCorners[i - 1], PathCorners[i], out door, out animated, out float along))
                    return walked + along <= reach;
                walked += Vector3.Distance(PathCorners[i - 1], PathCorners[i]);
            }
            if (walked > reach || _agent.pathStatus != NavMeshPathStatus.PathPartial)
                return false;

            // #1305 E3: the same surface reach past the end, in every direction. No ClosestPoint: it
            // is unsupported on non-convex MeshColliders.
            Vector3 centre = PathCorners[count - 1] + Vector3.up * PathProbeHeight;
            int interactableLayer = InteractableObjectLayer;
            int hits = Physics.OverlapSphereNonAlloc(
                centre,
                reach - walked + PathProbeRadius,
                TailHits,
                PhysicalMask | (1 << interactableLayer),
                QueryTriggerInteraction.Ignore);
            float best = float.PositiveInfinity;
            for (int i = 0; i < hits; i++)
            {
                Collider collider = TailHits[i];
                if (collider == null)
                    continue;
                float distance = collider.bounds.SqrDistance(centre);
                if (distance >= best
                    || !TryOwnGate(collider, interactableLayer, out DoorLock candidateDoor, out AnimatedObjectTrigger candidateAnimated))
                    continue;
                door = candidateDoor;
                animated = candidateAnimated;
                best = distance;
            }
            return door != null || animated != null;
        }

        private static bool TryProbeSegment(
            Vector3 from,
            Vector3 to,
            out DoorLock door,
            out AnimatedObjectTrigger animated,
            out float along)
        {
            door = null;
            animated = null;
            along = 0f;
            Vector3 delta = to - from;
            float distance = delta.magnitude;
            if (distance < 0.05f)
                return false;

            Vector3 origin = from + Vector3.up * PathProbeHeight;
            // #874 S8.7: the physical mask, solid bodies only - a thing that cannot block a body
            // cannot be the thing blocking this one. #874 (asylum door): PLUS the InteractableObject
            // layer, where every DoorLock prefab puts its one solid leaf collider and which
            // collidersAndRoomMaskAndDefault (0x42000901) does not contain.
            int interactableLayer = InteractableObjectLayer;
            int mask = PhysicalMask | (1 << interactableLayer);
            int count = Physics.SphereCastNonAlloc(
                origin,
                PathProbeRadius,
                delta / distance,
                PathHits,
                distance,
                mask,
                QueryTriggerInteraction.Ignore);
            float bestDistance = float.PositiveInfinity;
            for (int i = 0; i < count; i++)
            {
                Collider collider = PathHits[i].collider;
                if (collider == null || PathHits[i].distance >= bestDistance
                    || !TryOwnGate(collider, interactableLayer, out DoorLock candidateDoor, out AnimatedObjectTrigger candidateAnimated))
                    continue;
                door = candidateDoor;
                animated = candidateAnimated;
                bestDistance = PathHits[i].distance;
            }

            along = bestDistance;
            return door != null || animated != null;
        }

        /// <summary>
        /// #1233 S5 (rule 8): the gate that owns a collider the probe met, read off the collider
        /// whatever the leaf is named and however deep the prefab nests it. A DoorLock lives only on
        /// a door, so any DoorLock above the collider owns it, locked or not. A gate that already reads
        /// open is walked through, not a gate in the way. #874 (asylum door): on the InteractableObject
        /// layer only a DoorLock is a gate - the solid layer-9 things that are not doors (the Rubber
        /// Rooms trap triggers, TheDoorOfFUCK/Trigger1|2) are not ours to operate.
        /// </summary>
        private static bool TryOwnGate(Collider collider, int interactableLayer, out DoorLock door, out AnimatedObjectTrigger animated)
        {
            door = collider.GetComponentInParent<DoorLock>();
            if (door != null)
            {
                animated = ResolveAnimatedTrigger(door);
                return door.isLocked || !DoorIsOpen(door);
            }
            animated = collider.gameObject.layer == interactableLayer ? null : AnimatedOwnerOf(collider);
            if (animated != null && (!IsUsablePathGate(animated) || animated.isBool && animated.boolValue))
                animated = null;
            return animated != null;
        }

        /// <summary>
        /// #874 S8.7 / #1233 S5: the trigger that owns a hit collider is the one whose ANIMATED
        /// BODY the collider belongs to - the nearest AnimatedObjectTrigger above it whose
        /// <c>triggerAnimator</c> transform is the collider's ancestor. A trigger further up the
        /// same scene branch that animates something else (the Experimentation exterior slab of the
        /// 2026-09-09 run, operated 22 times through a trigger above it) does not own the slab, and
        /// a trigger whose animator is a sibling (the Rubber Rooms trap) does not own its own solid
        /// body either. Depth is not the test; the physical link is.
        /// </summary>
        private static AnimatedObjectTrigger AnimatedOwnerOf(Collider collider)
        {
            Transform leaf = collider.transform;
            for (Transform t = leaf; t != null; t = t.parent)
            {
                AnimatedObjectTrigger candidate = t.GetComponent<AnimatedObjectTrigger>();
                if (candidate == null)
                    continue;
                Animator animator = candidate.triggerAnimator;
                return animator != null && leaf.IsChildOf(animator.transform) ? candidate : null;
            }
            return null;
        }

        private static int PhysicalMask => StartOfRound.Instance != null
            ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
            : ~0;

        private const int InteractableObjectLayerFallback = 9;
        private static int _interactableObjectLayer = -1;

        /// <summary>
        /// #874 (asylum door). The layer vanilla puts door leaves on, resolved by name once; 9 when
        /// the name is missing (it is layer 9 in every shipped build).
        /// </summary>
        private static int InteractableObjectLayer
        {
            get
            {
                if (_interactableObjectLayer < 0)
                {
                    int resolved = LayerMask.NameToLayer("InteractableObject");
                    _interactableObjectLayer = resolved >= 0 ? resolved : InteractableObjectLayerFallback;
                }
                return _interactableObjectLayer;
            }
        }

        /// <summary>
        /// #1179 P03 (F03). The one authoritative gate operation: unlock a key-locked ordinary
        /// facility door, then open it. Authority belongs to the caller - <see cref="Tick"/> only
        /// reaches this while <c>BlacksiteSoldierAI.IsWorldInteractionInterrupted</c> is false,
        /// and that property's first clause is <c>!IsServerAuthority()</c>, so this is host-only.
        ///
        /// Every mutation below is a vanilla one that replicates itself to connected peers:
        ///   <c>DoorLock.UnlockDoorSyncWithServer</c> runs <c>UnlockDoor()</c> locally and sends
        ///     <c>UnlockDoorServerRpc</c>, which the host executes into <c>UnlockDoorClientRpc</c>
        ///     and therefore <c>UnlockDoor()</c> on every peer. It is the same sentence
        ///     <c>DoorLock.Update</c> runs when a lock-pick finishes.
        ///   <c>AnimatedObjectTrigger.TriggerAnimationNonPlayer</c> sends <c>UpdateAnimServerRpc</c>
        ///     -> <c>UpdateAnimClientRpc</c>, which moves the leaf on every peer.
        ///   <c>DoorLock.OpenDoorAsEnemyServerRpc</c> -> <c>OpenDoorAsEnemyClientRpc</c> ->
        ///     <c>OpenDoorAsEnemy()</c>, which sets the open state and the obstacle on every peer.
        /// No key item is consumed, nothing is faked, and no unlocked flag is written client-side.
        /// </summary>
        private static bool IssueOpen(DoorContext context, string label)
        {
            if (context == null)
                return false;

            try
            {
                if (context.Door != null && context.Door.isLocked)
                {
                    if (!CanUnlockDoor(context.Door))
                    {
                        context.Failure = "noTrigger";
                        return false;
                    }
                    context.Door.UnlockDoorSyncWithServer();
                    // The vanilla call clears isLocked itself. If something else on this
                    // installation kept it locked, the door was not opened and must not be claimed.
                    if (context.Door.isLocked)
                    {
                        context.Failure = "stillLocked";
                        return false;
                    }
                    context.Unlocked = true;
                }

                AnimatedObjectTrigger animated = context.AnimatedTrigger;
                if (animated != null && (!animated.isBool || !animated.boolValue))
                {
                    animated.TriggerAnimationNonPlayer(
                        playSecondaryAudios: false,
                        overrideBool: true,
                        setBoolFalse: false);
                }

                if (context.Door != null && !DoorIsOpen(context.Door))
                {
                    context.Door.OpenDoorAsEnemyServerRpc();
                    if (!DoorIsOpen(context.Door))
                        context.Door.OpenDoorAsEnemy();
                }
                return true;
            }
            catch (Exception e)
            {
                // Vanilla's own call decides what a door can take: UnlockDoor throws at
                // doorLockSFX.Stop() before it mutates anything, and the throw is reported.
                context.Failure = "error:" + e.GetType().Name;
                return false;
            }
        }

        /// <summary>
        /// #1305 E3: the one guard before vanilla's own <c>DoorLock.UnlockDoor</c>, which un-carves
        /// the obstacle before it writes <c>doorTrigger</c>, so a trigger-less door would be left
        /// half-unlocked. Every vanilla door has one; anything else vanilla's call decides.
        /// </summary>
        private static bool CanUnlockDoor(DoorLock door) => door != null && door.doorTrigger != null;

        private static AnimatedObjectTrigger ResolveAnimatedTrigger(DoorLock door)
        {
            return door != null
                ? door.GetComponent<AnimatedObjectTrigger>()
                    ?? door.GetComponentInParent<AnimatedObjectTrigger>()
                    ?? door.GetComponentInChildren<AnimatedObjectTrigger>(true)
                : null;
        }

        private static bool IsUsablePathGate(AnimatedObjectTrigger animated)
        {
            if (animated == null || animated.triggerByChance || animated.triggerAnimator == null)
                return false;
            InteractTrigger trigger = animated.GetComponent<InteractTrigger>()
                ?? animated.GetComponentInParent<InteractTrigger>()
                ?? animated.GetComponentInChildren<InteractTrigger>(true);
            return trigger != null
                && trigger.interactable
                && !trigger.holdInteraction
                && !trigger.isLadder;
        }

        private static bool DoorIsOpen(DoorLock door)
        {
            if (door == null)
                return false;

            EnsureCompatibilityResolved();
            try
            {
                if (_doorOpenedField != null
                    && _doorOpenedField.GetValue(door) is bool fieldValue)
                {
                    return fieldValue;
                }
                if (_doorOpenedProperty != null
                    && _doorOpenedProperty.GetValue(door, null) is bool propertyValue)
                {
                    return propertyValue;
                }
            }
            catch (Exception e)
            {
                if (!_loggedMissingDoorState)
                {
                    _loggedMissingDoorState = true;
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Blacksite] Door state adapter failed: {e.Message}");
                }
            }
            return false;
        }

        private static void EnsureCompatibilityResolved()
        {
            if (_compatibilityResolved)
                return;

            _compatibilityResolved = true;
            const BindingFlags flags = BindingFlags.Public
                | BindingFlags.NonPublic
                | BindingFlags.Instance;
            _doorOpenedField = typeof(DoorLock).GetField("isDoorOpened", flags)
                ?? typeof(DoorLock).GetField("isDoorOpen", flags);
            _doorOpenedProperty = typeof(DoorLock).GetProperty("isDoorOpened", flags)
                ?? typeof(DoorLock).GetProperty("isDoorOpen", flags);
            if (_doorOpenedField == null && _doorOpenedProperty == null)
            {
                _loggedMissingDoorState = true;
                ContractsBootstrap.Log?.LogWarning(
                    "[MoonContracts.Blacksite] Door interaction adapter disabled logical-state confirmation: " +
                    "DoorLock open state member is missing.");
            }
        }
    }
}
