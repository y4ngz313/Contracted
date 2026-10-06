using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

using Y4NGZCompany.Escalations.BlacksiteRetrieval;

namespace Y4NGZCompany.Contracts.Whistleblower
{
    /// <summary>
    /// #963 S14 U1. Everything that happens to the witness while a Blacksite escort has him: the
    /// one replicated owner, the attachment, the releases and the ship hold.
    ///
    /// #1293 R2: custody is an attachment, not a leash. While <see cref="_custodyEscort"/> names
    /// a living carrier, every peer writes his body from that carrier's transform in LateUpdate -
    /// the rider model of BlacksiteShipController.ApplyPassengers - so one writer holds his body,
    /// and there is no follow to pace, no seam to carry him across and no distance to break on.
    ///
    /// Host authority throughout: every server method here refuses off the server, and a client
    /// reads only the replicated _custodyEscort. No RPC is added.
    /// </summary>
    public sealed partial class WhistleblowerEnemyAI
    {
        /// <summary>
        /// The escort's NetworkObjectId, or ulong.MaxValue for nobody. Exactly the shape of the
        /// cart's _npcPusher and of this class's three existing variables: Everyone read, Server
        /// write, no RPC.
        /// </summary>
        private readonly NetworkVariable<ulong> _custodyEscort = new NetworkVariable<ulong>(
            ulong.MaxValue,
            NetworkVariableReadPermission.Everyone,
            NetworkVariableWritePermission.Server);

        // Server-only custody bookkeeping. None of it is replicated: the escort id above is all a
        // client needs, and every decision below is taken on the host.
        private int _custodyEscortMember = -1;
        private float _custodyTakenAt;
        private BlacksiteCustodyBreak _custodyLastBreak = BlacksiteCustodyBreak.None;
        private float _custodyLastBreakAt = float.NegativeInfinity;
        // #965 S14 U3: true while the ship's passenger seat holds his body (record Loaded). #1293 R2:
        // custody ended at the seat; his own behaviour waits for LeaveShipHoldServer.
        private bool _custodyHeldAboard;

        // #1293 R2: the attachment as THIS peer applies it - whether it is applied, the carrier it
        // writes him from, the seat it wrote last frame, and that seat's flat per-frame motion,
        // which is what his gait reads while he is held. The flag is kept apart from the carrier
        // reference because a destroyed carrier reads null to Unity before the escort clear lands.
        private bool _attached;
        private BlacksiteSoldierAI _attachedCarrier;
        private Vector3 _attachedSeat;
        private Vector3 _attachedVelocity;
        // His solid root capsule (WhistleblowerEnemyRegistry.EnsureBodyCollider), off while held.
        private CapsuleCollider _attachedBodyCollider;

        // #1293 R2: where he is held, metres in his carrier's yaw frame: 0.55 m behind him and
        // 0.35 m to his left, held by the arm.
        private static readonly Vector3 CustodySeat = new Vector3(-0.35f, 0f, -0.55f);
        // How far from his body a release looks for navmesh to hand his agent back on.
        // Derivation: BlacksiteSoldierAI.TransferThroughEntranceServer samples the cart-pushing
        // endpoint with ResolveNavMeshPosition(..., 1f).
        private const float CustodyNavSampleRadius = 1f;
        // How far his carrier's own nav position may be from his body and still be the hand-off
        // point: three seat offsets (0.65 m each), so a carrier on navmesh beside him qualifies and
        // one on a rope, a link or a cabin floor metres away does not.
        private const float CustodyCarrierBindMetres = 2f;
        // True while a release or the seat handed his body back with no navmesh under it: his
        // agent stays decoupled and each of his server ticks retries the bind in place.
        private bool _agentUnbound;

        /// <summary>
        /// #1293 R2: host only - true while that hand-off is still unbound. The board offers him to
        /// nobody meanwhile (AssignReservations) and no take can attach him.
        /// </summary>
        internal bool IsAgentUnbound => _agentUnbound;

        /// <summary>True while a Blacksite escort holds him.</summary>
        internal bool IsInCustody => _custodyEscort.Value != ulong.MaxValue;

        /// <summary>True while this particular soldier holds him.</summary>
        internal bool IsEscortedBy(BlacksiteSoldierAI escort)
            => escort != null && _custodyEscort.Value == escort.NetworkObjectId;

        /// <summary>The escort, resolved through the spawn manager, or null.</summary>
        internal BlacksiteSoldierAI CustodyEscort => IsInCustody
            ? BlacksiteObjectiveManifestController.Resolve(_custodyEscort.Value)?.GetComponent<BlacksiteSoldierAI>()
            : null;

        /// <summary>True while he is in the facility. The observation path tests against it.</summary>
        internal bool IsInsideFacility => !isOutside;

        /// <summary>
        /// Takes the witness into custody. Refuses off the server, with the escort switched off,
        /// when either body is dead or down, while his agent is unbound (#1293 R2), when somebody
        /// already holds him, when the escort is further away than the take reach, and inside the
        /// re-take backoff a break stamped.
        /// </summary>
        internal bool TryBeginCustodyServer(BlacksiteSoldierAI escort)
        {
            if (!IsServer || !BlacksiteCustodySettings.EscortEnabled)
                return false;
            if (escort == null || escort.isEnemyDead || escort.ReplicatedDead || escort.ReplicatedStunned)
                return false;
            // #1293 R2: an unbound agent is a body off the navmesh; no take attaches him until it binds.
            if (isEnemyDead || Neutralized || _knockdownActive || _knockedDown.Value || _agentUnbound)
                return false;

            float reach = Vector3.Distance(escort.transform.position, transform.position);
            BlacksiteCustodyStage stage = BlacksiteCustodyPolicy.Stage(
                IsInCustody,
                _custodyLastBreak,
                Time.time - _custodyLastBreakAt);
            if (!BlacksiteCustodyPolicy.CanBegin(stage, witnessAlive: true, escortAlive: true, takeDistanceMetres: reach))
                return false;

            SetCustodyEscortServer(escort);
            _custodyTakenAt = Time.time;

            // Everything he was doing on his own stops here: the roam search, an escape search in
            // flight, the combat target and the burst state. Discovery and the escape rolls are
            // suppressed by the state itself (UpdateServerBehaviour dispatches CustodyState).
            StopRoamServer();
            CancelEscapeSearch();
            _quietRetreat = false;
            _fugitiveRetreat = false;
            _combatTarget = null;
            ResetBurstState();
            SwitchToBehaviourState(CustodyState);
            // #1293 R2: his agent stands down for the whole hold; the attachment writes his body.
            StandDownAgentServer();

            BlacksiteDiagnostics.Log("Custody",
                $"event=taken, witness={NetworkObjectId}, member={_custodyEscortMember}"
                + $", inside={IsInsideFacility}, reach={reach:0.00}"
                + $", point={BlacksiteDiagnosticFormat.V(transform.position)}");
            return true;
        }

        /// <summary>
        /// Ends custody and says why: a real event (#1293 R2) - his death, a knockdown, the
        /// escort's death or stun - or the board letting go (Cleanup). His agent takes his body
        /// back where the attachment left it, and his own behaviour decides what he does next:
        /// his death owns the body, inside he is back in hiding, outside he cowers.
        /// </summary>
        /// <param name="cause">Why custody ended.</param>
        /// <param name="attempt">
        /// #964 S14 U2: the manifest's attempt generation for the witness record, when the break
        /// ORIGINATES in the board (a Drop, a DropCarrier, a mission cleanup). It is appended at
        /// the END of the event=broken line and omitted entirely at -1.
        /// </param>
        internal void ReleaseCustodyServer(BlacksiteCustodyBreak cause, int attempt = -1)
        {
            if (!IsServer || !IsInCustody)
                return;

            BlacksiteSoldierAI escort = CustodyEscort;
            float sinceTaken = Time.time - _custodyTakenAt;
            int member = _custodyEscortMember;
            BlacksiteCustodyRelease release = BlacksiteCustodyPolicy.Release(cause, IsInsideFacility);
            // #1293 R2: the hand-off bind runs while the escort still resolves, so his nav
            // position is in reach; a dead witness's body is left to the path that killed him.
            float fell = 0f;
            bool bound = release != BlacksiteCustodyRelease.DeathOwnsBody && BindAgentAfterHoldServer(escort, out fell);

            SetCustodyEscortServer(null);
            _custodyLastBreak = cause;
            _custodyLastBreakAt = Time.time;

            BlacksiteDiagnostics.Log("Custody",
                $"event=broken, witness={NetworkObjectId}, member={member}"
                // #1158: the world he is released into, under the key the event=taken line uses.
                + $", cause={BlacksiteCustodyPolicy.BreakToken(cause)}, inside={IsInsideFacility}"
                + $", bound={bound}" + (fell > 0f ? $", fell={fell:0.00}" : string.Empty)
                + $", sinceTaken={sinceTaken:0.0}s"
                // #964 S14 U2: appended at the END, and only when the board is the one letting go.
                + (attempt >= 0 ? $", attempt={attempt}" : string.Empty));

            // A witness already moved out of CustodyState by the path that broke custody keeps the
            // state that path gave him.
            if (currentBehaviourStateIndex == CustodyState)
                EnterReleaseBehaviourServer(release);
        }

        /// <summary>#1158, #1293 R2: his own state after a release; custody adds none of its own.</summary>
        private void EnterReleaseBehaviourServer(BlacksiteCustodyRelease release)
        {
            switch (release)
            {
                case BlacksiteCustodyRelease.DeathOwnsBody:
                    return;
                case BlacksiteCustodyRelease.Hiding:
                    // Inside: a knockdown owns him until he stands up, and an escort may take him
                    // again after the backoff.
                    SwitchToBehaviourState(HidingState);
                    return;
                default:
                    // Outside: he keeps the spot the release left him on. A knockdown out there
                    // lands here too, and the stand-up then finds him already cowering.
                    EnterCowerServer();
                    return;
            }
        }

        /// <summary>
        /// #1293 R3: the one host write of the escort. A change is a Custody decision for the
        /// escort who held him and the escort who holds him now; RequestDecision refuses a dead
        /// or unconfigured member, and an unchanged escort raises nothing.
        /// </summary>
        private void SetCustodyEscortServer(BlacksiteSoldierAI escort)
        {
            ulong id = escort != null ? escort.NetworkObjectId : ulong.MaxValue;
            if (_custodyEscort.Value == id)
                return;
            BlacksiteSoldierAI previous = CustodyEscort;
            _custodyEscort.Value = id;
            _custodyEscortMember = escort != null ? escort.MemberIndex : -1;
            if (previous != null)
                previous.RequestDecision(BlacksiteDecisionCause.Custody);
            if (escort != null)
                escort.RequestDecision(BlacksiteDecisionCause.Custody);
        }

        /// <summary>
        /// #1293 R2: his agent stops writing his body - stopped, path cleared, decoupled from the
        /// transform - for as long as something else holds it (the attachment, then the ship's
        /// seat). It stays enabled: vanilla EnemyAI.Update re-enables an owner's agent every frame.
        /// A hold owns the body, so a hand-off still waiting for navmesh is void.
        /// </summary>
        private void StandDownAgentServer()
        {
            _agentUnbound = false;
            StopAgent();
            if (agent == null)
                return;
            agent.updatePosition = false;
            agent.updateRotation = false;
        }

        /// <summary>
        /// #1293 R2: the hand-off bind. His agent takes the body back on the nearest navmesh within
        /// <see cref="CustodyNavSampleRadius"/> of where the hold left him, else on his carrier's
        /// own nav position when it is within <see cref="CustodyCarrierBindMetres"/> of him, else -
        /// let go in mid-air, a carrier killed on the hover rope or mid-link - on the floor he falls
        /// to (<see cref="TryFloorBelow"/>). With none, nothing moves: his agent stays decoupled
        /// (re-coupling it would snap the body to the agent's stale point) and
        /// <see cref="RetryAgentBindServer"/> retries each tick.
        /// </summary>
        private bool BindAgentAfterHoldServer(BlacksiteSoldierAI carrier, out float fell)
        {
            _agentUnbound = !TryBindAgentServer(carrier, out fell);
            return !_agentUnbound;
        }

        private bool TryBindAgentServer(BlacksiteSoldierAI carrier, out float fell)
        {
            fell = 0f;
            if (agent == null || !agent.enabled)
                return false;
            Vector3 body = transform.position;
            Vector3 point;
            if (NavMesh.SamplePosition(body, out NavMeshHit hit, CustodyNavSampleRadius, NavMesh.AllAreas))
                point = hit.position;
            else if (carrier != null && carrier.agent != null && carrier.agent.enabled && carrier.agent.isOnNavMesh
                && Vector3.Distance(carrier.agent.nextPosition, body) <= CustodyCarrierBindMetres)
                point = carrier.agent.nextPosition;
            else if (TryFloorBelow(body, out point))
                fell = body.y - point.y;
            else
                return false;
            if (!agent.Warp(point))
            {
                fell = 0f;
                return false;
            }
            agent.updatePosition = true;
            agent.updateRotation = true;
            return true;
        }

        /// <summary>
        /// #1293 R2: a body let go in mid-air falls. The first floor straight below him - the
        /// room/collider mask his line-of-sight test uses, triggers ignored - within the tallest
        /// rope column the ship hangs (<see cref="BlacksiteRappelGeometry.MaxHeight"/>, 18 m), and
        /// navmesh within <see cref="CustodyNavSampleRadius"/> of where he lands. The ray starts
        /// half a metre above his feet so a body resting on a collider still finds it.
        /// </summary>
        private static bool TryFloorBelow(Vector3 body, out Vector3 floor)
        {
            floor = body;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (!Physics.Raycast(body + Vector3.up * 0.5f, Vector3.down, out RaycastHit ground,
                    BlacksiteRappelGeometry.MaxHeight + 0.5f, mask, QueryTriggerInteraction.Ignore))
                return false;
            if (!NavMesh.SamplePosition(ground.point, out NavMeshHit hit, CustodyNavSampleRadius, NavMesh.AllAreas))
                return false;
            floor = hit.position;
            return true;
        }

        /// <summary>
        /// #1293 R2: one of his server ticks while a hand-off is unbound - the same bind again (in
        /// place, then the floor below), no clock. Returns false while he is still unbound, and his
        /// own behaviour waits: his agent is decoupled, so anything it steered would only mime a
        /// walk on the spot.
        /// </summary>
        private bool RetryAgentBindServer()
        {
            if (!_agentUnbound)
                return true;
            if (!BindAgentAfterHoldServer(null, out float fell))
            {
                StopAgent();
                return false;
            }

            BlacksiteDiagnostics.Log("Custody",
                $"event=bound, witness={NetworkObjectId}, bound=True"
                + (fell > 0f ? $", fell={fell:0.00}" : string.Empty)
                + $", point={BlacksiteDiagnosticFormat.V(transform.position)}");
            return true;
        }

        /// <summary>#965 S14 U3 (Stage 14 integration): the ship's passenger seat holds his body.</summary>
        internal bool IsHeldAboard => _custodyHeldAboard;

        /// <summary>
        /// #965 S14 U3 (Stage 14 integration): the manifest has seated him aboard the Blacksite
        /// ship (BlacksiteObjectiveManifestController.HoldWitnessAboardServer registers the seat
        /// first). The transport's passenger registry writes his transform from here on.
        ///
        /// #1293 R2: the seat is the carrier's deliberate release. The attachment ends here, in
        /// the same server step in which the passenger seat takes his body, so exactly one writer
        /// holds him; no break is stamped (nobody took him out of anybody's hands) and his own
        /// behaviour stays suspended in CustodyState until <see cref="LeaveShipHoldServer"/>.
        /// </summary>
        internal void EnterShipHoldServer()
        {
            if (!IsServer) return;
            _custodyHeldAboard = true;
            SetCustodyEscortServer(null);
            StandDownAgentServer();
        }

        /// <summary>
        /// #965 S14 U3 (Stage 14 integration): the record left Loaded and the transport has already
        /// stopped writing his transform (the board releases the passenger first). His agent takes
        /// the body back where he sits (<see cref="BindAgentAfterHoldServer"/>), and #1293 R2 his
        /// own behaviour takes him back: custody ended at the seat.
        /// </summary>
        internal void LeaveShipHoldServer()
        {
            if (!IsServer) return;
            _custodyHeldAboard = false;
            bool bound = BindAgentAfterHoldServer(null, out float fell);
            if (!bound || fell > 0f)
                BlacksiteDiagnostics.Log("Custody",
                    $"event=bound, witness={NetworkObjectId}, bound={bound}"
                    + (fell > 0f ? $", fell={fell:0.00}" : string.Empty)
                    + $", point={BlacksiteDiagnosticFormat.V(transform.position)}");
            if (!IsInCustody && currentBehaviourStateIndex == CustodyState)
                EnterReleaseBehaviourServer(BlacksiteCustodyPolicy.Release(BlacksiteCustodyBreak.Cleanup, IsInsideFacility));
        }

        /// <summary>
        /// One server tick of custody: the real events that end it (#1293 R2). Called from
        /// UpdateServerBehaviour's CustodyState case, which the knockdown short-circuit already
        /// sits in front of. Nothing here moves him: the attachment does.
        /// </summary>
        private void UpdateCustodyServer()
        {
            // #965 S14 U3: aboard, the ship's seat holds him.
            if (_custodyHeldAboard)
                return;

            BlacksiteSoldierAI escort = CustodyEscort;
            if (escort == null)
            {
                // Despawned, or the id no longer resolves: the same outcome as a dead escort, and
                // it is logged with a cause like every other release.
                ReleaseCustodyServer(BlacksiteCustodyBreak.EscortDead);
                return;
            }

            BlacksiteCustodyBreak cause = BlacksiteCustodyPolicy.Decide(
                escortAlive: !escort.isEnemyDead && !escort.ReplicatedDead,
                escortStunned: escort.ReplicatedStunned,
                knockedDown: _knockedDown.Value,
                witnessDead: isEnemyDead,
                breakOnKnockdown: BlacksiteCustodySettings.CustodyBrokenByKnockdown);
            if (cause != BlacksiteCustodyBreak.None)
                ReleaseCustodyServer(cause);
        }

        /// <summary>
        /// #1293 R2: the attachment, on every peer and a late joiner included, keyed on the
        /// replicated escort id and never on the behaviour state. While it names a living carrier
        /// this peer resolves, his pose is the carrier's seat (<see cref="CustodySeat"/> in the
        /// carrier's yaw). Nothing here writes serverPosition: the host's vanilla position sync
        /// publishes the body this writes, so a release hands off from where he actually is.
        /// </summary>
        private void LateUpdate()
        {
            BlacksiteSoldierAI carrier = AttachmentCarrier();
            if (carrier == null)
            {
                if (_attached)
                    DetachFromCarrier();
                return;
            }

            Transform anchor = carrier.transform;
            Quaternion yaw = Quaternion.Euler(0f, anchor.eulerAngles.y, 0f);
            Vector3 seat = anchor.position + yaw * CustodySeat;
            if (!_attached || !ReferenceEquals(_attachedCarrier, carrier))
                AttachToCarrier(carrier);
            else
                _attachedVelocity = Vector3.ProjectOnPlane(seat - _attachedSeat, Vector3.up) / Mathf.Max(Time.deltaTime, 0.001f);
            _attachedSeat = seat;
            transform.SetPositionAndRotation(seat, yaw);
            _lastPosition = seat;
            // His world is his carrier's on the host only: a soldier's isOutside is host-local
            // (clients keep the prefab default), exactly as the deleted portal crossing was.
            if (IsServer && isOutside != carrier.isOutside)
                SetCustodyWorld(carrier.isOutside);
        }

        // The carrier this peer writes him from: the replicated escort, alive, resolved here.
        private BlacksiteSoldierAI AttachmentCarrier()
        {
            if (!IsInCustody || isEnemyDead)
                return null;
            BlacksiteSoldierAI carrier = _attachedCarrier != null && _attachedCarrier.NetworkObjectId == _custodyEscort.Value
                ? _attachedCarrier
                : CustodyEscort;
            return carrier != null && !carrier.isEnemyDead && !carrier.ReplicatedDead ? carrier : null;
        }

        private void AttachToCarrier(BlacksiteSoldierAI carrier)
        {
            _attached = true;
            _attachedCarrier = carrier;
            _attachedVelocity = Vector3.zero;
            if (_attachedBodyCollider == null)
                _attachedBodyCollider = GetComponent<CapsuleCollider>();
            // His solid capsule must not push his carrier or the squad; weapon sweeps still land
            // on his trigger child (WhistleblowerEnemyRegistry.EnsureCollisionChild).
            if (_attachedBodyCollider != null && !_attachedBodyCollider.isTrigger)
                _attachedBodyCollider.enabled = false;
        }

        private void DetachFromCarrier()
        {
            _attached = false;
            _attachedCarrier = null;
            _attachedVelocity = Vector3.zero;
            if (_attachedBodyCollider != null && !_attachedBodyCollider.isTrigger)
                _attachedBodyCollider.enabled = true;
            _lastPosition = transform.position;
        }

        /// <summary>
        /// #1293 R2: host only - while he is held his world is his carrier's. Vanilla's own flip
        /// first (it writes the flag, then reads node sets, and throws when a level has none for
        /// that world), then the node set his own searches read once he is released.
        /// </summary>
        private void SetCustodyWorld(bool outside)
        {
            bool wasOutside = isOutside;
            try
            {
                SetEnemyOutside(outside);
            }
            catch
            {
                isOutside = outside;
            }

            GameObject[] nodes = RoundManager.Instance != null
                ? (outside ? RoundManager.Instance.outsideAINodes : RoundManager.Instance.insideAINodes)
                : null;
            if (nodes != null && nodes.Length > 0)
                allAINodes = nodes;
            BlacksiteDiagnostics.Log("Custody",
                $"event=world, witness={NetworkObjectId}, member={_custodyEscortMember}"
                + $", outside={wasOutside}->{outside}");
        }

        // ----------------------------------------------------------------- #874 F02: recognition
        //
        // Everything below is the half of the escort that never existed: the witness's own side of
        // being retrieved. Until now he was indifferent to the squad, so the only way custody could
        // ever start was for a soldier to corner a walking man and hold himself inside 1.2 m of him
        // for a continuous 1.1 s (BlacksiteObjectiveManifestController.TryAcquireOnDwellServer).
        // Neither the 2026-09-20 nor the 2026-09-21 run ever completed that chase on a moving
        // target, and the one take that did land (2026-09-21 t=490.62) only happened because the
        // witness had walked into the operator.
        //
        // It adds no radius and no dwell. The take gate, the 2.2 m reach, the line of sight, the
        // world test, the re-take backoff and the custody policy are all the ones that shipped;
        // what changed is that the last two metres are closed by the man who wants to be found.

        // The soldier he has recognised, the last time a real line of sight confirmed it, and the
        // throttle the recognition scan runs on. Server-only, like every other custody field here:
        // a client derives the walk from the replicated behaviour state.
        private BlacksiteSoldierAI _rallyEscort;
        private float _rallySeenAt = float.NegativeInfinity;
        private float _nextRallySampleAt;
        private Vector3 _rallyIssuedPoint;
        private bool _rallyHasIssuedPoint;
        private bool _rallyStanding;
        private bool _rallyAnnounced;

        // How far he recognises a Blacksite soldier. Derivation: BlacksiteSightPolicy.VisionRange,
        // the 15 m at which a soldier acquires him (BlacksiteSoldierAI.EnemyVisionRange is the
        // same number for an enemy contact). Recognition is mutual by construction, so this is not
        // a new detection radius: it is the one the squad already has, read from the other end. No
        // range anywhere is widened, and nothing here sees through a wall or into another world.
        private const float RallySightDistance = BlacksiteSightPolicy.VisionRange;
        // The height the recognition ray is cast at, from his chest to the soldier's. Derivation:
        // the board's own custody reach casts from the escort's chest at +1.2
        // (BlacksiteObjectiveManifestController.HasPhysicalAccess), so the same height is used at
        // both ends here - two standing men, one ray between their chests, which is also what the
        // squad's own enemy sight does (BlacksiteSoldierAI.HasLineOfSight, eye to target +1.1).
        private const float RallyEyeHeight = 1.2f;
        // A recognition survives a pillar for as long as a sighting survives a glance away
        // (SightingHoldSeconds, what the scan node and the contract card already hold one for).
        // Past it the soldier is not "seen" any more and the walk is given up, which is what keeps
        // "unseen soldiers do not attract him" true of a man who has started walking.
        private const float RallyRecognitionHoldSeconds = SightingHoldSeconds;
        // #1293 R2: the walk's own hold band. He stands inside it, which is what lets the board's
        // 1.1 s dwell complete. Derivation: the reach at
        // which the cart counts a body as joined to it (PayloadCartController.NpcAttachDistance
        // .85, two of his agent radii .44 rounded down), inside the 1.2 m take gate with margin.
        private const float RallyHoldMetres = 0.85f;
        // He walks again only past the take reach, so the band has hysteresis and cannot flicker.
        private const float RallyResumeMetres = BlacksiteCustodySettings.CustodyTakeDistance;
        // Re-issue the destination only when the soldier has actually moved. Derivation: this
        // class's own updatePositionThreshold .35 (Start), the distance at which his position is
        // already considered to have changed for replication.
        private const float RallyReissueMetres = 0.35f;
        // His walk to a soldier, the shipped pace: his cornered speed times .95, 3.37 m/s - what
        // BlacksiteCustodyPolicy.FollowSpeed(CorneredSpeed, 0) returned before #1293 R2 deleted it.
        // A shade under his free 3.55 so he eases into the hold band instead of pressing into
        // the soldier.
        private const float RallyPaceMultiplier = 0.95f;

        /// <summary>
        /// #874 F02. One recognition sample, at the observer sample's own 10 Hz. Returns the
        /// soldier he should walk to, or null.
        ///
        /// Who is eligible is the board's own notion of a member who could actually take him -
        /// alive, on his feet, not stowed aboard, not answering a fight - plus the two things a
        /// man's eyes add: the same world, and an unobstructed line from his eyes to the soldier's
        /// chest.
        ///
        /// Who is CHOSEN is the board's, not his: if the manifest has reserved a member for him,
        /// that member is the only one who can take him (Acquire offers custody to the record's
        /// carrier and nobody else), so walking to anyone else would be walking to a refusal. With
        /// no reservation yet - the squad has not seen him, which is exactly the case this fixes -
        /// he walks to the nearest eligible soldier in range, ties broken by member index so two
        /// equidistant soldiers can never alternate.
        ///
        /// The candidate is chosen on distance first and the ray is spent ONCE, on him. A sample
        /// that casts per candidate is a sensing budget that grows with the squad for no gain: the
        /// nearest man behind a wall simply means nobody is recognised this tenth of a second, and
        /// he keeps roaming until one of them steps into the open. Per-frame continuity is not
        /// permission for a per-frame sweep and this is neither.
        /// </summary>
        private BlacksiteSoldierAI FindRallyEscortServer()
        {
            RoundManager round = RoundManager.Instance;
            System.Collections.Generic.List<EnemyAI> enemies = round != null ? round.SpawnedEnemies : null;
            if (enemies == null)
                return null;

            BlacksiteSoldierAI nearest = null;
            float nearestDistance = float.MaxValue;
            BlacksiteObjectiveManifestController board = null;
            bool boardAsked = false;
            for (int i = 0; i < enemies.Count; i++)
            {
                if (!(enemies[i] is BlacksiteSoldierAI soldier) || !IsRallyEligible(soldier))
                    continue;
                if (!boardAsked)
                {
                    boardAsked = true;
                    board = soldier.TryGetMission(out BlacksiteRetrievalMissionController mission)
                        ? mission.Objectives
                        : null;
                }
                float distance = Vector3.Distance(transform.position, soldier.transform.position);
                if (distance > RallySightDistance)
                    continue;
                if (distance < nearestDistance
                    || (distance == nearestDistance && nearest != null && soldier.MemberIndex < nearest.MemberIndex))
                {
                    nearestDistance = distance;
                    nearest = soldier;
                }
            }

            // The reserved operator outranks proximity, but he still has to be one this man can
            // SEE: a member the board picked from across the facility is not a reason to walk into
            // a wall. He keeps roaming until that member has actually reached him, which is what
            // the board sent it to do.
            BlacksiteSoldierAI candidate = board != null ? board.OperatorForWitness(NetworkObjectId) : null;
            if (candidate != null)
            {
                if (!IsRallyEligible(candidate)
                    || Vector3.Distance(transform.position, candidate.transform.position) > RallySightDistance)
                    return null;
            }
            else
            {
                candidate = nearest;
            }
            return candidate != null && HasRallyLineOfSight(candidate) ? candidate : null;
        }

        /// <summary>
        /// #874 F02. A soldier who could take him if he stood next to one. These are the board's
        /// own reservation terms for a member that is free to act (AssignReservations' predicate:
        /// alive, not stunned, not boarded, not fighting) plus the same-world test every custody
        /// path already makes. A ship guard is deliberately NOT excluded: the guard is excluded
        /// from being RESERVED, and walking to the nearest man in a squad is not a claim about who
        /// the board will pick.
        /// </summary>
        private bool IsRallyEligible(BlacksiteSoldierAI soldier)
            => soldier != null
               // #1283 S7a: an un-configured soldier is still stowed aboard the inbound aircraft.
               && soldier.HasReplicatedIdentity
               && !soldier.isEnemyDead
               && !soldier.ReplicatedDead
               && !soldier.ReplicatedStunned
               && !soldier.HasBoardedShip
               && !soldier.IsObjectiveCombatActive
               && soldier.isOutside == isOutside;

        /// <summary>
        /// #874 F02. The wall test, and the only thing standing between recognition and
        /// omniscience.
        ///
        /// It is BlacksiteSoldierAI.HasLineOfSight in the witness's units, deliberately: one
        /// allocation-free Physics.Linecast against StartOfRound.collidersAndRoomMaskAndDefault
        /// with triggers ignored, accepted when the first thing the ray meets is the soldier
        /// himself. The end point is inside the soldier's own capsule, so a plain "did it hit
        /// anything" test would answer false in an empty room; the squad's own sight rule solves
        /// that the same way and this must not solve it differently.
        /// </summary>
        private bool HasRallyLineOfSight(BlacksiteSoldierAI soldier)
        {
            if (soldier == null)
                return false;
            Vector3 eye = transform.position + Vector3.up * RallyEyeHeight;
            Vector3 chest = soldier.transform.position + Vector3.up * RallyEyeHeight;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (!Physics.Linecast(eye, chest, out RaycastHit hit, mask, QueryTriggerInteraction.Ignore))
                return true;
            EnemyAI met = hit.collider != null ? hit.collider.GetComponentInParent<EnemyAI>() : null;
            return met == soldier;
        }

        /// <summary>
        /// #874 F02. Whether he may look for a soldier at all this tick, and if so whether he
        /// found one. Called from the two states a free witness is actually in - hiding (which is
        /// the roam) and the outside cower - and from the walk itself, so the scan runs on one
        /// 10 Hz throttle and nowhere else. Not a new Update writer: the behaviour switch that
        /// already owns him calls it.
        ///
        /// The admission is <see cref="BlacksiteCustodyPolicy.CanApproachEscort"/>: the escort
        /// setting, a body that can walk, no player at hand, and the custody stage Free - which is
        /// where the post-break cooldown lives, and it is the SAME 15 s backoff that refuses the
        /// take, so a rescue cannot be undone from either end.
        /// </summary>
        private bool TryFindRallyEscortServer(out BlacksiteSoldierAI escort)
        {
            escort = null;
            if (!IsServer)
                return false;
            // The danger term reads the discovery sample that is already being taken at 10 Hz for
            // the scan node and the card: a player with a line of sight inside talking distance.
            bool playerAtHand = _cachedAnyObserver
                && _cachedObserver != null
                && _cachedObserverDistance <= DiscoveryDistance;
            bool ableToWalk = !isEnemyDead
                && !Neutralized
                && !_knockdownActive
                && !_knockedDown.Value
                && agent != null
                && agent.enabled;
            if (!BlacksiteCustodyPolicy.CanApproachEscort(
                    BlacksiteCustodyPolicy.Stage(IsInCustody, _custodyLastBreak, Time.time - _custodyLastBreakAt),
                    BlacksiteCustodySettings.EscortEnabled,
                    ableToWalk,
                    playerAtHand))
            {
                _rallyEscort = null;
                _rallySeenAt = float.NegativeInfinity;
                return false;
            }

            if (Time.time >= _nextRallySampleAt)
            {
                _nextRallySampleAt = Time.time + ObserverSampleIntervalSeconds;
                BlacksiteSoldierAI seen = FindRallyEscortServer();
                if (seen != null)
                {
                    _rallyEscort = seen;
                    _rallySeenAt = Time.time;
                }
                else if (_rallyEscort != null && !IsRallyEligible(_rallyEscort))
                {
                    // A dead, stunned, boarded or fighting escort is not a contact he is holding
                    // through a pillar: he has to find somebody else.
                    _rallyEscort = null;
                    _rallySeenAt = float.NegativeInfinity;
                }
            }

            if (_rallyEscort == null || Time.time - _rallySeenAt > RallyRecognitionHoldSeconds)
            {
                _rallyEscort = null;
                return false;
            }

            escort = _rallyEscort;
            return true;
        }

        /// <summary>
        /// #874 F02. Enter the walk. Everything he was doing on his own stops, exactly as it does
        /// when custody begins, except that no escape is spent and no contract event is raised:
        /// he has not been taken, he has decided to go.
        /// </summary>
        private void EnterRallyServer(BlacksiteSoldierAI escort)
        {
            if (currentBehaviourStateIndex == RallyState)
                return;
            StopRoamServer();
            CancelEscapeSearch();
            ResetBurstState();
            _quietRetreat = false;
            _fugitiveRetreat = false;
            _combatTarget = null;
            _rallyHasIssuedPoint = false;
            _rallyStanding = false;
            _rallyAnnounced = false;
            SwitchToBehaviourState(RallyState);
            StopAgent();

            BlacksiteDiagnostics.Log("Custody",
                $"event=rally, witness={NetworkObjectId}, member={escort.MemberIndex}"
                + $", inside={IsInsideFacility}, range={Vector3.Distance(transform.position, escort.transform.position):0.00}"
                + $", point={BlacksiteDiagnosticFormat.V(transform.position)}");
        }

        /// <summary>
        /// #874 F02. Give the walk up and hand him back to the behaviour he would have had without
        /// it: the roam inside, the cower outside. The split is the release rule's own - outside
        /// there is nothing to roam to and the run's settle test is satisfied by his first step -
        /// so a witness who loses sight of the squad on the moon stands where he is rather than
        /// sweeping off across it.
        /// </summary>
        private void LeaveRallyServer(string reason)
        {
            if (currentBehaviourStateIndex != RallyState)
                return;
            _rallyEscort = null;
            _rallySeenAt = float.NegativeInfinity;
            _rallyHasIssuedPoint = false;
            _rallyStanding = false;
            BlacksiteDiagnostics.Log("Custody",
                $"event=rally, witness={NetworkObjectId}, member=-1, gaveUp={reason}"
                + $", inside={IsInsideFacility}, point={BlacksiteDiagnosticFormat.V(transform.position)}");
            if (!IsInsideFacility)
            {
                EnterCowerServer();
                return;
            }
            SwitchToBehaviourState(HidingState);
            StopAgent();
        }

        /// <summary>
        /// #874 F02. One server tick of the walk, dispatched by UpdateServerBehaviour's RallyState
        /// case.
        ///
        /// Danger first and by the same test hiding uses, so a crew that finds him mid-walk still
        /// gets the discovery it has always got: assess and run inside, cower outside. Then the
        /// recognition, which is also the cooldown and the escort setting. Then the approach: he
        /// walks to the soldier's body and STANDS inside the hold band, which is what makes the
        /// board's 1.1 s dwell completable at all - a man standing still inside 1.2 m of a soldier
        /// who is walking up to him is the case the dwell was written for and never saw.
        ///
        /// The hold band and its hysteresis (0.85 / 1.2) satisfy the take gate's 1.2 m with margin
        /// rather than on the boundary. The destination is re-issued only when the soldier has
        /// actually moved, on a 0.35 m threshold, so a walking squad does not cost a
        /// SetDestination per frame.
        /// </summary>
        private void UpdateRallyServer()
        {
            if (TryDiscoveredServer(out var discoveredBy))
            {
                _discoverySince = 0f;
                _rallyEscort = null;
                _rallySeenAt = float.NegativeInfinity;
                _rallyHasIssuedPoint = false;
                _rallyStanding = false;
                EnterCorneredServer(discoveredBy, assess: true);
                return;
            }

            if (!TryFindRallyEscortServer(out BlacksiteSoldierAI escort))
            {
                LeaveRallyServer("lostContact");
                return;
            }

            float range = Vector3.Distance(transform.position, escort.transform.position);
            if (_rallyStanding ? range < RallyResumeMetres : range <= RallyHoldMetres)
            {
                if (!_rallyStanding)
                {
                    _rallyStanding = true;
                    _rallyHasIssuedPoint = false;
                }
                SetAgentSpeed(0f, stopped: true, stoppingDistance: RallyHoldMetres);
                if (agent != null)
                    agent.updateRotation = true;
                if (!_rallyAnnounced)
                {
                    _rallyAnnounced = true;
                    BlacksiteDiagnostics.Log("Custody",
                        $"event=rally, witness={NetworkObjectId}, member={escort.MemberIndex}"
                        + $", waiting=True, range={range:0.00}"
                        + $", point={BlacksiteDiagnosticFormat.V(transform.position)}");
                }
                return;
            }

            _rallyStanding = false;
            _rallyAnnounced = false;
            if (agent != null)
                agent.updateRotation = true;
            SetAgentSpeed(CorneredSpeed * RallyPaceMultiplier, stopped: false, stoppingDistance: RallyHoldMetres);
            Vector3 goal = escort.transform.position;
            if (!_rallyHasIssuedPoint
                || Vector3.Distance(goal, _rallyIssuedPoint) > RallyReissueMetres)
            {
                _rallyIssuedPoint = goal;
                _rallyHasIssuedPoint = true;
                SetDestinationToPosition(goal, checkForPath: false);
            }

            CommitDestination();
        }
    }
}
