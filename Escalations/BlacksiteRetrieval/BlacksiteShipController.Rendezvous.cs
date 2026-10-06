using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Contracts._Shared;
using Y4NGZCompany.Contracts.Payload;
using Y4NGZCompany.Contracts.ShadowRaid;
using Y4NGZCompany.Escalations._Shared;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteShipController
    {
        private readonly NetworkVariable<Vector3> _rendezvous = new NetworkVariable<Vector3>();
        private readonly NetworkVariable<ulong> _cargoContainerId = new NetworkVariable<ulong>(ulong.MaxValue);
        private readonly NetworkVariable<Vector3> _cargoFrom = new NetworkVariable<Vector3>();
        private readonly NetworkVariable<Vector3> _cargoGround = new NetworkVariable<Vector3>();
        private readonly NetworkVariable<double> _cargoMotionAt = new NetworkVariable<double>();
        // Hooked at the cable foot (or standing, on the ground), deployment drop, pickup winch,
        // attached to ship, parked unhooked while a hover ship is away (BlacksiteCablePhase).
        // Never despawn/recreate contents.
        private readonly NetworkVariable<byte> _cargoMotion = new NetworkVariable<byte>();
        // Read-only accessor for the access check and the native cargo rows. Replicated already,
        // so it reads the same on every peer; nothing here changes the motion itself. It is not
        // an input to the belt's activity status (#874 S6: a stowed crate is not a loaded objective).
        internal byte CargoMotion => _cargoMotion.Value;
        // #874 Q4 (2026-09-24): the one extraction sequence's current step (BlacksiteExtractionStep).
        // Host-decided, replicated so a peer and a late joiner read the step the host is in; the hull
        // is spawned per round, so the next round starts at Raid with no reset to forget.
        private readonly NetworkVariable<byte> _extractionStep = new NetworkVariable<byte>();
        // #874 Q4 (Lawson's decision 2): the cable HOOK's trip on a hover station - which object,
        // from where, to which ship-local point on the deck, when it started, and for a prop which
        // rack slot and kind it will stand in. Every peer derives the hook's pose from these exactly
        // as it derives the crate's from _cargoFrom/_cargoMotionAt, so a late joiner sees the same
        // point on the rope. A prop rides the hook as its anchor (CargoAnchor); the cart's own
        // NetworkTransform carries its pose, written by the host from the same trip.
        private readonly NetworkVariable<ulong> _hookObject = new NetworkVariable<ulong>(ulong.MaxValue);
        private readonly NetworkVariable<Vector3> _hookFrom = new NetworkVariable<Vector3>();
        private readonly NetworkVariable<Vector3> _hookTargetLocal = new NetworkVariable<Vector3>();
        private readonly NetworkVariable<double> _hookMotionAt = new NetworkVariable<double>();
        private readonly NetworkVariable<int> _hookSlot = new NetworkVariable<int>(-1);
        private readonly NetworkVariable<byte> _hookKind = new NetworkVariable<byte>();
        internal BlacksiteExtractionStep ExtractionStep => (BlacksiteExtractionStep)_extractionStep.Value;
        // Server-only: when the current step was entered, and the hooked cart's rotation when it
        // left the ground.
        private float _extractionStepAt;
        private Quaternion _hookRotation = Quaternion.identity;
        internal bool HookBusy => _hookObject.Value != ulong.MaxValue;
        /// <summary>#874 Q4: this object is on the ship's cable hook right now.</summary>
        internal bool CableHolds(ulong objectId) => objectId != ulong.MaxValue && _hookObject.Value == objectId;
        // The cart is never drawn through an anchor (its NetworkTransform carries it), so only a prop
        // is re-anchored to the hook.
        private bool HookCarries(int slot, BlacksiteObjectiveKind kind)
            => HookBusy && kind != BlacksiteObjectiveKind.Payload && _hookSlot.Value == slot && _hookKind.Value == (byte)kind;
        private Transform _cableHook;
        private Transform CableHookAnchor
        {
            get
            {
                if (_cableHook == null) { var go = new GameObject("BlacksiteCableHook"); go.transform.SetParent(transform, false); _cableHook = go.transform; }
                return _cableHook;
            }
        }
        // #874: 0 pending (site not yet proven with the container present), 1 proven on the ground,
        // 2 retained aboard because no site clears the reserved lanes. Replicated: the squad's
        // egress admission reads it on every peer that ticks a member.
        private readonly NetworkVariable<byte> _cargoLayout = new NetworkVariable<byte>();
        private readonly List<BlacksiteSoldierAI> _rendezvousMembers = new List<BlacksiteSoldierAI>();
        // #942 S11 U4: _lastRendezvousPhase exists so the recalled ship's touchdown can be observed
        // from this partial, which owns the rendezvous tick, rather than from the phase-flight block
        // in BlacksiteShipController.cs.
        private BlacksiteShipPhase _lastRendezvousPhase = BlacksiteShipPhase.Inbound;
        // #1219 A1: the hover station rule's host-only state. _squadOffSince is when nobody living
        // and uncaptured was left aboard (negative while somebody is). The hull is spawned per
        // round, so nothing here resets.
        private float _squadOffSince = -1f;
        private int _stationHoldReported = int.MinValue;
        private float _nextStationHoldLog, _stationHoldRepeatAt;
        // #874 (2026-09-23): a held ship restates itself on this cadence even when nothing changed.
        // Round 1 of that session held 210 s after its last line, so the hold read as silence.
        private const float HoldingStationRepeatSeconds = 30f;
        // #1219 A1: the extraction sequence's one wait, host-only and per hull like the rest of this
        // state; only the step byte and the phase replicate. #1293 R2: the CargoLift step holds no
        // wait (it ends when nothing is owed) and the lift-off veto is a fold with no clock.
        private BlacksiteWait _shipReadyWait;
        // #1262 P2: each living member's boarding fact, aligned with _departureMembers, and the hold line's signature of them.
        private BlacksiteBoardingFact[] _boardingFacts = new BlacksiteBoardingFact[BlacksiteExtractionSequence.MaxSquadMembers];
        private int _boardingHoldSignature = int.MinValue;
        private float _nextBoardingHoldLog, _boardingHoldRepeatAt, _nextDepartureRefusedLog;
        private readonly List<BlacksiteSoldierAI> _departureMembers = new List<BlacksiteSoldierAI>();
        /// <summary>#1219 A1: the one departure authority's current answer, decided on the ship tick during the climb and
        /// executed by MoonEscalationState.UpdateBlacksiteExtraction. Host-only; never replicated.</summary>
        internal BlacksiteDepartureVerdict DepartureVerdict { get; private set; }
        private readonly Dictionary<int, Transform> _containerSlots = new Dictionary<int, Transform>();
        private ShadowRaidCargoCrate _cargoContainer;
        private bool _wentOffsite;
        private float _nextRendezvousCheck, _nextCargoDeploy;
        private int _cargoAttempt;
        private string _cargoSacrificed = "none";
        private Vector3 _cargoFootCenter, _cargoFootHalf, _cargoHookLocal;
        private const float ContainerTravelSpeed = 9f;
        internal Vector3 RendezvousPosition => _rendezvous.Value;
        // Returning orders target the ship; passage routing proves the intervening ground and climb.
        internal Vector3 MissionRallyPosition => IsGrounded && _mission != null && _mission.Objectives.Returning
            ? BoardingPosition : RendezvousPosition;
        internal ShadowRaidCargoCrate CargoContainer
        {
            get
            {
                if (_cargoContainer == null && _cargoContainerId.Value != ulong.MaxValue)
                {
                    _cargoContainer = BlacksiteObjectiveManifestController.Resolve(_cargoContainerId.Value)?.GetComponent<ShadowRaidCargoCrate>();
                    if (_cargoContainer != null)
                        MeasureCargo(ContractAssetLoader.BlacksiteCratePrefab ?? _cargoContainer.gameObject);
                }
                return _cargoContainer;
            }
        }
        // #874 Q4 (2026-09-24): boarding is open. Every consumer keeps its name - the navigator's
        // board leg, BlacksiteSquadDirector.CanMemberBoard, TryBeginDepartureServer and the
        // extraction tick - and the answer is now the extraction sequence's own step: PersonnelClimb, entered
        // when the cargo lift is over (everything staged aboard, or its budget spent). It used to be
        // a custody predicate over staged cargo, which on 23 September round 1 held the squad on a
        // guard ring for 48 s waiting for an apparatus nothing was going to lift (22358). Replicated
        // through the step byte, so it answers the same on every peer.
        internal bool CargoReadyForDeparture => _mission == null || BlacksiteExtractionSequence.BoardingOpen(ExtractionStep);
        // #874 (2026-09-23): a crate PARKED while a hover ship is away is still the ground cache -
        // carriers load into it and the recall lifts it - so this reads the moon, not the hook.
        internal bool ContainerOnGround => CargoContainer != null
            && BlacksiteCablePhase.OnMoon(_cargoMotion.Value) && _cargoLayout.Value == 1;
        // The container was deployed nowhere: staging falls back to the rendezvous point and the
        // squad is never held waiting for a container that will not arrive.
        internal bool CargoRetained => _cargoLayout.Value == 2;
        /// <summary>
        /// #874 (2026-09-22, Rend): the crate is retained AND physically standing on its deck
        /// stand, which is the only state its slots can be loaded in.
        ///
        /// <see cref="CargoRetained"/> alone is the LAYOUT decision - no ground site clears the
        /// reserved lanes - and it is true from the moment that decision is taken, including the
        /// seconds a hovering ship's crate spends climbing its own rope and the case where no
        /// crate was ever spawned at all (no prefab). A loader may only be walked to slots that
        /// exist and are standing still, so the delivery continuation reads this and never the
        /// layout byte on its own.
        /// </summary>
        internal bool CargoRetainedAboard => CargoRetained && CargoContainer != null
            && _cargoMotion.Value == BlacksiteCablePhase.Aboard;
        /// <summary>
        /// #874 (2026-09-22, Rend): where a loader stands to put cargo INTO the retained crate.
        ///
        /// It is <see cref="CargoLoadingPosition"/>'s own relationship to a GROUND crate -
        /// BlacksiteCargoLayout.LoadingReach from the box - applied to the box on the deck, so
        /// the release gate, the carry order's arrival contract and
        /// <c>ContainsContainerCargo</c>'s 1.6 m hold test all keep the margins the ground path
        /// has always had.
        ///
        /// FORWARD is not a choice: the deck stand's own derivation (BlacksiteShipController
        /// .CargoDeckLocal) measured 3.425 m of free lane forward of the crate and 0.200 m or
        /// less inboard, aft and outboard, so the only side of that box a man fits beside is the
        /// one facing the door he came in through.
        ///
        /// The point is then resolved onto the DECK's own navigation area, within half a body of
        /// where the geometry put it. The stand is aft of every boarding row (BoardingRows stops
        /// at ship-local z 2.7 on a level hull), so it is deck a returning squad never otherwise
        /// walks, and a loader ordered to a point with no navigation under it would stand at the
        /// door. This is a GROUND-ship deck delivery only: a hover ship never routes a delivery
        /// here (#1219 B5, the rope foot instead). Sampling is not a fallback: with no deck
        /// navigation beside the crate this still answers the geometric point and the loader
        /// fails to arrive. #1219 B3: a carrier keeps custody until an event, so what ends that
        /// delivery is the progress ledger's stalled answer, which drops the item, or (#1293 R2) the
        /// write-off of owed cargo nobody can still haul after the recall.
        /// </summary>
        internal Vector3 RetainedCargoLoadingPosition
        {
            get
            {
                var crate = CargoContainer;
                if (crate == null) return BoardingPosition;
                Vector3 forward = Vector3.ProjectOnPlane(transform.forward, Vector3.up).normalized;
                Vector3 beside = crate.transform.position + forward * BlacksiteCargoLayout.LoadingReach;
                return NavMesh.SamplePosition(beside, out var deck, BlacksitePhysicalStep.BodyRadius * 2f,
                    BlacksiteShipNavigation.DeckAreaMask) ? deck.position : beside;
            }
        }
        // Ground-mode personnel wait for the layout; hover personnel descend before the cache.
        internal bool CargoLayoutReady => _cargoLayout.Value != 0;
        // Where a carrier or a cart is sent to put something into the cache.
        //
        // #874 F04 (review B1): the RETAINED term this issue briefly added is GONE, with the
        // premature retention that needed it. The motion term already answers the whole question -
        // a crate that is not standing on the moon is not a ground cache, whether it never
        // deployed, is on the rope or is on the deck stand - and a second term that could be true
        // WHILE the crate stood loadable on the touchdown is what moved a carrier's approach gate
        // and the manifest's release point off a crate that was still there. This is the pre-#874
        // predicate, widened on 2026-09-23 only by the PARKED crate a hover ship leaves on the moon
        // while it is away, which is loadable exactly as before.
        internal Vector3 CargoLoadingPosition
            => CargoContainer == null || !BlacksiteCablePhase.OnMoon(_cargoMotion.Value)
                ? RendezvousPosition
                : BlacksiteCargoLayout.LoadingPoint(_cargoGround.Value, _landingRotation, IsHovering);
        // #874 F04 / S3 (plan §3 rule 3): the cache is a participant in the ship's one FIFO under
        // its own network object id, so a member waiting for the rope waits behind an owner
        // [UAT][Broker] prints. The flag is "this cache has a CLAIM in the lane" (holder or waiter),
        // so a release also takes a refused request back out of the order.
        private bool _cargoCableClaim;
        private string _cableWaitReported;
        private float _nextCableWaitLog;
        private bool TryHoldCargoCableLane(float now, out BlacksiteBrokerVerdict verdict)
        {
            verdict = default;
            if (!HasCargoContainer) return false;
            BindAccessQueues();
            verdict = Ladder.RequestVerdict(_cargoContainerId.Value, now);
            _cargoCableClaim = true;
            return verdict.Admitted;
        }
        private void ReleaseCargoCableLane()
        {
            if (!_cargoCableClaim) return;
            _cargoCableClaim = false;
            if (HasCargoContainer) Ladder.Release(_cargoContainerId.Value);
        }
        private static string CableLaneRefusal(in BlacksiteBrokerVerdict rope)
            => $"the cable lane refused the cache ({rope.Reason}); waited {rope.WaitedFor:0.0}s, place {rope.Place}";
        // One line per change of reason, rate-limited on the cadence the holding-station line
        // uses, so a cable that is legitimately busy is visible without becoming a per-frame log.
        private void NoteCableWait(string what, string why)
        {
            if (_cableWaitReported == why && Time.time < _nextCableWaitLog) return;
            _cableWaitReported = why;
            _nextCableWaitLog = Time.time + BlacksiteObjectivePolicy.SquadMoveSampleSeconds;
            BlacksiteDiagnostics.Log("Rendezvous",
                $"{what} waiting on the shared cable: {why}; motion={_cargoMotion.Value}, layout={_cargoLayout.Value}, mode={LandingModeToken}.");
        }
        private double CargoNow => NetworkManager != null ? NetworkManager.ServerTime.Time : Time.timeAsDouble;
        internal Transform ContainerSlot(int slot)
        {
            if (CargoContainer == null) return null;
            if (!_containerSlots.TryGetValue(slot, out var anchor) || anchor == null)
            {
                anchor = new GameObject("RecoveredItem" + slot).transform;
                anchor.SetParent(CargoContainer.transform, false);
                anchor.localPosition = new Vector3((slot % 3 - 1) * .3f, .48f + (slot / 9) * .12f, (slot / 3 % 3 - 1) * .2f);
                _containerSlots[slot] = anchor;
            }
            return anchor;
        }
        internal bool ContainsContainerCargo(Vector3 point) => CargoContainer != null
            && Vector3.Distance(point, CargoContainer.transform.position + Vector3.up * .5f) < 1.6f;

        private void TickRendezvousServer()
        {
            if (_mission == null || !_mission.IsInitialized) return;
            // #874 Q4: the one extraction sequence advances first, so every branch below reads the
            // step this tick decided.
            TickExtractionSequenceServer();
            // Create the one original cache aboard. Site refusal never despawns it and never
            // manufactures a replacement; the same object and recovered contents make the trip.
            PrepareCargoContainer();
            // Touchdown waits for carving before proving access; pending proof is not site refusal.
            if (_deploymentNotified && !PassageProven && IsGrounded && _cargoLayout.Value == 0)
                RetainCargoContainer("the station has no proven personnel access");
            TickCargoLoweringServer();
            if (BlacksiteCablePhase.OnCable(_cargoMotion.Value))
            {
                // #874 F04: the trip renews the cache's own lane every tick it is on the rope, so the
                // lane's 2 s stale sweep can never hand the rope to a body while a crate hangs on it.
                TryHoldCargoCableLane(Time.time, out _);
                CargoTripPosition(CargoNow, out bool complete);
                if (complete)
                {
                    // #874 Q5: the hover cable only just set the container down on the rope foot
                    // TryCableSite proved before the trip began; the layout is decided by that
                    // proof, never re-proved (and never hauled back up) after the fact.
                    if (_cargoMotion.Value == BlacksiteCablePhase.Lowering) DecideCargoLayout();
                    bool raised = _cargoMotion.Value == BlacksiteCablePhase.Raising;
                    _cargoMotion.Value = raised ? BlacksiteCablePhase.Aboard : BlacksiteCablePhase.Ground;
                    // The rope is free the instant the trip ends - set down on the touchdown, or
                    // back on the deck stand - and the lane goes back with it.
                    ReleaseCargoCableLane();
                }
            }
            // Clear the shared foot before the returning squad can claim the rope.
            //
            // #874 Q4 (2026-09-24): the going-home lift is the extraction sequence's CargoLift step.
            // #1293 R2: the crate waits only for owed cargo whose delivery still ends in it
            // (CargoOwedIntoCrate); the climb opens only once the crate is aboard.
            // #874 S3 (plan §3 rule 3): the lift is a FIFO turn like any other, requested every
            // tick it is wanted and handed back the tick it is not.
            var step = ExtractionStep;
            bool liftClear = step >= BlacksiteExtractionStep.CargoLift && step < BlacksiteExtractionStep.Departed
                && (step == BlacksiteExtractionStep.PersonnelClimb || !_mission.Objectives.CargoOwedIntoCrate);
            if (IsGrounded && CargoContainer != null && BlacksiteCablePhase.AtCableFoot(_cargoMotion.Value) && liftClear)
                RecoverCargoContainer();
            else if (!OwesLowering && !BlacksiteCablePhase.OnCable(_cargoMotion.Value))
                ReleaseCargoCableLane();
            TickCableHookServer();
            if (Phase == BlacksiteShipPhase.LeavingRendezvous)
            {
                // #1233 S7: the departure's flight, up the proven column and out along the leg; only a
                // hover ship leaves station (#874 Q4), and the recall flies the same corridor back.
                float t = Mathf.Clamp01((Time.time - _began) / _duration);
                _thrust.Value = .4f + .6f * t;
                FlyCorridorServer(1f - t);
                if (t >= 1f) _phase.Value = (byte)BlacksiteShipPhase.Offsite;
            }
            if (Time.time < _nextRendezvousCheck) return;
            _nextRendezvousCheck = Time.time + .25f;
            // #942 S11 U4: Returning -> Landed had no line at all, so a log could show the recall
            // and then nothing until the squad started boarding. The touchdown states itself now.
            if (_lastRendezvousPhase == BlacksiteShipPhase.Returning && Phase == BlacksiteShipPhase.Landed)
                BlacksiteDiagnostics.Log("Rendezvous", $"ship grounded again; mode={LandingModeToken}");
            _lastRendezvousPhase = Phase;
            TickDepartureVerdictServer(Time.time);
            // #1219 A1: the explicit pickup request is the extraction sequence's own step (entered
            // this frame by TickExtractionSequenceServer above), not a second read of the manifest.
            if (ExtractionStep >= BlacksiteExtractionStep.PickupRequested)
            {
                // The station rule is over for the round: a pickup supersedes it.
                if (Phase == BlacksiteShipPhase.Offsite)
                {
                    _began = Time.time; _duration = 12f;
                    _phase.Value = (byte)BlacksiteShipPhase.Returning; _thrust.Value = 1f;
                    // #942 S11 U4: AnnouncePickup() moved to the boarding order in
                    // BlacksiteObjectiveManifestController, which is where the pickup is actually
                    // committed. This branch is reachable only from Offsite, so only a hover ship
                    // (the one mode that leaves station) ever prints it.
                    BlacksiteDiagnostics.Log("Rendezvous", $"pickup requested; ship returning to station; ground cache and soldiers retained, mode={LandingModeToken}");
                }
                return;
            }
            if (_wentOffsite || !IsGrounded || !_deploymentNotified) return; // not-a-wait: the station rule starts at touchdown; the inbound flight is bounded by ShipLandingTimeoutSeconds
            // #874 Q4 / #1219 D3: a ground ship stays landed for the whole raid, so it has no
            // station rule and prints no holding-station line.
            if (!IsHovering) return;
            // #1219 A1: an empty roster while the deploy slice is still releasing the stowed squad is
            // a squad not yet on the rope, not a squad off it, so the grace does not start until the
            // drain ends (it clears on completion, a failed member and an abort alike).
            if (_mission.SoldierDeploymentPending) return; // not-a-wait: the deploy drain clears on completion, a failed spawn and an abort; the station rule runs after it
            TickStationServer(Time.time);
        }
        // #942 S11 U4: which deployment the landing search chose, in the line contract's spelling.
        // Both modes end the inbound flight in the same Landed phase and IsGrounded is true for a
        // hovering ship too, so this single replicated bool is the only thing that separates them.
        private string LandingModeToken => IsHovering ? "hover" : "ground";
        /// <summary>
        /// #1219 A1 (D2): the hover station rule, decided by
        /// <see cref="BlacksiteExtractionSequence.StationLeave"/> on facts measured here. It replaces
        /// the S8.3 departure vote (raiders inside or stranded, guards posted, engagement, cargo
        /// layout, doorway and body-column probes): round 3 of 2026-09-26 held station six minutes
        /// past its last rope exit on guardsUnposted=2. The squad being off the rope starts a
        /// <see cref="BlacksiteExtractionSequence.StationLeaveGraceSeconds"/> grace; only something
        /// physical on the rope or a player on the hull holds past it, each bounded by its own owner.
        /// #1233 S4 (rule 1): "aboard" is the ship's live answer (<see cref="MembersAboard"/>).
        /// </summary>
        private void TickStationServer(float now)
        {
            int aboard = MembersAboard(out _);
            if (aboard != 0) _squadOffSince = -1f;
            else if (_squadOffSince < 0f) _squadOffSince = now;
            float sinceOff = aboard == 0 ? now - _squadOffSince : -1f;
            BlacksiteStationVeto vetoes = StationVetoes(now);
            if (!BlacksiteExtractionSequence.StationLeave(sinceOff, vetoes)) { NoteStationHold(now, aboard, sinceOff, vetoes); return; } // not-a-wait: ends on live presence - nobody aboard (MembersAboard, the ship's one aboard answer) for the grace and no physical veto, each bounded by its own owner (ship FIFO hold, cable trip, the player)
            _wentOffsite = true; _doorOpen.Value = false;
            _duration = 12f; _began = now; ReleaseNavigation();
            _phase.Value = (byte)BlacksiteShipPhase.LeavingRendezvous;
            UnhookGroundCargoServer();
            // A crate still waiting for the rope foot to clear is not a veto; it stays aboard, so
            // every consumer reads a decided layout while the ship is away.
            if (_cargoLayout.Value == 0) RetainCargoContainer("the ship left station before the rope foot cleared");
            BlacksiteDiagnostics.Log("Rendezvous",
                $"ship leaving station; squad off the rope {sinceOff:0.0}s, grace={BlacksiteExtractionSequence.StationLeaveGraceSeconds:0}s, mode={LandingModeToken}");
        }
        /// <summary>
        /// #1219 A1: the station vetoes, measured. A crate or hook holding the ship FIFO is not a
        /// member on the rope (round 3 t=1837.62: ladder=occupant was the crate waiting to lower);
        /// the cable carrying cargo is its own term.
        /// </summary>
        private BlacksiteStationVeto StationVetoes(float now)
        {
            var vetoes = BlacksiteStationVeto.None;
            if (Ladder.HasOccupant(now) && Ladder.TryGetAdmittedHead(out ulong head)
                && head != _cargoContainerId.Value && head != _hookObject.Value) vetoes |= BlacksiteStationVeto.MemberOnRope;
            if (RappelUsedByPlayer) vetoes |= BlacksiteStationVeto.PlayerOnRope;
            if (CargoUsesCable || HookBusy) vetoes |= BlacksiteStationVeto.CargoOnCable;
            if (PlayerAboardShip) vetoes |= BlacksiteStationVeto.PlayerAboard;
            return vetoes;
        }
        // #942 S11 U4 / #874 (2026-09-23): a held ship says so once per change of what holds it,
        // no faster than SquadMoveSampleSeconds, and restates itself every
        // HoldingStationRepeatSeconds so a long hold never reads as silence. The signature is only
        // stamped once a line is written, so a change caught by the rate limit is printed next.
        private void NoteStationHold(float now, int aboard, float sinceOff, BlacksiteStationVeto vetoes)
        {
            int signature = aboard * 32 + (int)vetoes;
            if (now < _nextStationHoldLog) return;
            if (signature == _stationHoldReported && now < _stationHoldRepeatAt) return;
            _stationHoldReported = signature;
            _nextStationHoldLog = now + BlacksiteObjectivePolicy.SquadMoveSampleSeconds;
            _stationHoldRepeatAt = now + HoldingStationRepeatSeconds;
            BlacksiteDiagnostics.Log("Rendezvous",
                $"ship holding station; aboard={aboard}, sinceOff={(sinceOff < 0f ? "none" : sinceOff.ToString("0.0"))}, "
                + $"veto={BlacksiteExtractionSequence.Label(vetoes)}, mode={LandingModeToken}");
        }
        /// <summary>
        /// #874 (2026-09-23): a hover ship leaving station lets go of the crate standing hooked at
        /// its cable foot. The crate stays where it is, the ground cache it already was; it simply
        /// stops being on the rope's end, so the rope retracts with the ship, a late joiner sees a
        /// crate on the ground and no cable, and nothing in the round reads it as a rope occupant.
        /// A crate on the rope is never let go - the station rule's CargoOnCable veto holds the ship
        /// on station while one is - and a ground ship has no cable to unhook from.
        /// </summary>
        private void UnhookGroundCargoServer()
        {
            if (!IsHovering || !HasCargoContainer) return;
            byte motion = _cargoMotion.Value, parked = BlacksiteCablePhase.Unhook(motion);
            if (parked == motion) return;
            _cargoMotion.Value = parked;
            // A refused lift can have left the cache waiting in the ladder lane; a crate the ship
            // has let go of has no claim on a rope that is leaving with it.
            ReleaseCargoCableLane();
            BlacksiteDiagnostics.Log("Rendezvous", $"original cargo container unhooked and parked on the moon: id={_cargoContainerId.Value}, ground={_cargoGround.Value}, layout={_cargoLayout.Value}.");
        }
        /// <summary>
        /// #874 (2026-09-23): the recalled hover ship is back over its touchdown and picks the parked
        /// crate up again. Hooked at the foot is the phase the recovery branch lifts from; the
        /// extraction sequence lifts it before boarding opens, as on a ship that never left.
        /// Called in the statement that lands the ship; a no-op on every other landing.
        /// </summary>
        private void RehookParkedCargoServer()
        {
            byte motion = _cargoMotion.Value, hooked = BlacksiteCablePhase.Rehook(motion);
            if (hooked == motion) return;
            _cargoMotion.Value = hooked;
            BlacksiteDiagnostics.Log("Rendezvous", $"parked cargo container re-hooked for the lift: id={_cargoContainerId.Value}, ground={_cargoGround.Value}, mode={LandingModeToken}.");
        }
        /// <summary>
        /// #874 Q4: advance the one extraction sequence by at most one step. The facts are all the
        /// host's own: the recall the manifest committed, the hull's phase and mode, the rope, and
        /// whether anything is still to be lifted. #1219 A1: ShipReady holds a finite wait and runs
        /// its named fallback at the deadline; #1293 R2: CargoLift ends when nothing is owed (the
        /// lift budget is deleted); the climb ends on the departure verdict (TickDepartureVerdictServer).
        /// </summary>
        private void TickExtractionSequenceServer()
        {
            var step = ExtractionStep;
            if (step == BlacksiteExtractionStep.Departed) return;
            float now = Time.time;
            var facts = new BlacksiteExtractionFacts(_mission.Objectives.Returning, IsHovering, IsGrounded,
                cableDown: true, ExtractionCargoPending, Phase == BlacksiteShipPhase.Departed, _shipReadyWait.Expired(now));
            var next = BlacksiteExtractionSequence.Next(step, facts);
            if (next != step)
            {
                if (next == BlacksiteExtractionStep.ShipReady && !BlacksiteExtractionSequence.Ready(facts))
                {
                    // The ShipReady fallback: the sequence goes on without the ship's own readiness.
                    BlacksiteDiagnostics.Log("Wait", _shipReadyWait.ExpiredLine(now,
                        $"onStation={IsGrounded}, phase={Phase}"));
                    ForceArrivalServer();
                }
                EnterExtractionStepServer(next);
            }
        }
        // The ship's half of CargoPending: the crate is not aboard yet, or the hook is carrying;
        // #1293 R2: the manifest's half is AnyCargoOwed.
        private bool ExtractionCargoPending => HookBusy
            || (HasCargoContainer && _cargoMotion.Value != BlacksiteCablePhase.Aboard)
            || _mission.Objectives.AnyCargoOwed;
        /// <summary>
        /// #874 Q4: the one writer of the step, and its one [UAT][Extraction] line per transition.
        /// Departed calls the manifest's existing ConfirmDepartureServer, which is idempotent.
        /// #1219 A1: it is also where the step waits open and close, so ShipReady is never entered
        /// without its deadline.
        /// </summary>
        internal void EnterExtractionStepServer(BlacksiteExtractionStep next)
        {
            if (!IsServer || _mission == null) return;
            var from = ExtractionStep;
            if (from == next || from == BlacksiteExtractionStep.Departed) return;
            float now = Time.time;
            float since = now - _extractionStepAt;
            _extractionStep.Value = (byte)next; _extractionStepAt = now;
            // A step past the wait's own step closes it: satisfied, or its expiry already printed.
            if (next >= BlacksiteExtractionStep.ShipReady) _shipReadyWait = default;
            BlacksiteDiagnostics.Log("Extraction", $"step={BlacksiteExtractionSequence.Token(next)} since={since:0.0} "
                + $"from={BlacksiteExtractionSequence.Token(from)} mode={LandingModeToken} phase={Phase} "
                + $"crate={(HasCargoContainer ? _cargoMotion.Value.ToString() : "none")} hook={(HookBusy ? _hookObject.Value.ToString() : "none")} "
                + $"cargoPending={_mission.Objectives.AnyCargoOwed}");
            OpenStepWaitServer(next, now);
            // #1293 R3: boarding opening is the outcome a BoardingClosed refusal waits on: all decide.
            if (!BlacksiteExtractionSequence.BoardingOpen(from) && BlacksiteExtractionSequence.BoardingOpen(next)
                && _mission.SquadDirector != null)
            {
                _mission.SquadDirector.CollectLivingMembers(_rendezvousMembers);
                for (int i = 0; i < _rendezvousMembers.Count; i++)
                    if (!_rendezvousMembers[i].HasBoardedShip)
                        _rendezvousMembers[i].RequestDecision(BlacksiteDecisionCause.Outcome);
                _mission.Objectives.Planner.MarkAllDirty();
            }
            if (next == BlacksiteExtractionStep.Departed) _mission.Objectives.ConfirmDepartureServer();
        }
        /// <summary>
        /// #1219 A1: the wait a waiting step opens on entry - ShipReady at the pickup request (the
        /// landing bound). #1293 R2: CargoLift opens none; it ends when nothing is owed. #1233 S4
        /// (rule 4): the climb opens none; it ends on the departure verdict.
        /// </summary>
        private void OpenStepWaitServer(BlacksiteExtractionStep step, float now)
        {
            switch (step)
            {
                case BlacksiteExtractionStep.PickupRequested:
                    _shipReadyWait = BlacksiteWait.Open(BlacksiteWaitKind.ShipReady, now, BlacksiteExtractionSequence.ShipLandingTimeoutSeconds);
                    BlacksiteDiagnostics.Log("Wait", _shipReadyWait.OpenLine($"mode={LandingModeToken}"));
                    break;
            }
        }
        /// <summary>
        /// #1219 A1 / #1262 P2 (D2): the departure decision on the 0.25 s tick during the climb: each living member's
        /// <see cref="BlacksiteBoardingFact"/>, then <see cref="BlacksiteExtractionSequence.Departure"/>; the escalation executes
        /// <see cref="DepartureVerdict"/>.
        /// </summary>
        private void TickDepartureVerdictServer(float now)
        {
            var step = ExtractionStep;
            if (step != BlacksiteExtractionStep.PersonnelClimb || Phase == BlacksiteShipPhase.Departing) { DepartureVerdict = BlacksiteDepartureVerdict.Hold; return; }
            MoonEscalationState.CollectActiveBlacksiteSoldiers(_departureMembers);
            int living = _departureMembers.Count;
            if (_boardingFacts.Length < living) _boardingFacts = new BlacksiteBoardingFact[living];
            for (int i = 0; i < living; i++) _boardingFacts[i] = BoardingFact(_departureMembers[i]);
            var verdict = BlacksiteExtractionSequence.Departure(step, new System.ReadOnlySpan<BlacksiteBoardingFact>(_boardingFacts, 0, living));
            DepartureVerdict = verdict;
            if (verdict == BlacksiteDepartureVerdict.Hold) NoteBoardingHold(now, now - _extractionStepAt, living);
        }
        // #1262 P2 (D2): what holds one living member, cheapest first: aboard (HasBoardedShip); crossing (a ship
        // crossing, the FIFO held, or any body writer but the agent: link jump, world ladder, cart coupling, capture
        // seat, each self-bounded); queued (IsQueuedAtShipLink or a FIFO waiter: [ShipPassage] queue=waiting); an
        // elevator link task (its holder or a waiter in its lane); an entrance task. #1293 R2: a navigator stranded (no navmesh to bind
        // where the body stands) is Stranded. Only then the route service: a Reachable Route to the board link from where he
        // stands, in either world (#1305 G1, M5: a walk off a link the mover refuses is no route). The writer
        // check follows elevator/entrance, whose rides also take the body.
        private BlacksiteBoardingFact BoardingFact(BlacksiteSoldierAI member)
        {
            if (member.HasBoardedShip) return BlacksiteBoardingFact.Boarded;
            ulong id = member.NetworkObjectId;
            var nav = member.Navigator;
            if (nav.IsCrossingShipLink || Ladder.Resource.Holds(id)) return BlacksiteBoardingFact.Crossing;
            if (nav.IsQueuedAtShipLink || Ladder.Resource.IsWaiting(id)) return BlacksiteBoardingFact.Queued;
            var task = nav.Interactions;
            if (task != null && task.HasActiveTask && task.ActiveKind == BlacksiteInteractionKind.Elevator)
                return BlacksiteBoardingFact.Elevator;
            if (task != null && task.IsEntranceTraversalActive) return BlacksiteBoardingFact.Entrance;
            if (nav.BodyWriter != BlacksiteBodyWriter.Agent) return BlacksiteBoardingFact.Crossing;
            if (nav.IsStranded) return BlacksiteBoardingFact.Stranded;
            return BlacksiteReachability.Route(BlacksiteTraveller.Soldier(member), member.transform.position, BoardingLinkStart,
                goalOutside: true).Reachable
                ? BlacksiteBoardingFact.Route : BlacksiteBoardingFact.Stranded;
        }
        // #874 (2026-09-20) / #1262 P2: which fact holds each unboarded member, restated when the facts
        // change (no faster than SquadMoveSampleSeconds) and at least every HoldingStationRepeatSeconds,
        // so a held climb never reads as silence.
        private void NoteBoardingHold(float now, float climb, int living)
        {
            if (!BlacksiteDiagnostics.Enabled) return;
            int signature = living;
            for (int i = 0; i < living; i++) signature = signature * 31 + (int)_boardingFacts[i];
            if (signature != _boardingHoldSignature ? now < _nextBoardingHoldLog : now < _boardingHoldRepeatAt) return;
            _boardingHoldSignature = signature;
            _nextBoardingHoldLog = now + BlacksiteObjectivePolicy.SquadMoveSampleSeconds;
            _boardingHoldRepeatAt = now + HoldingStationRepeatSeconds;
            string waiting = string.Empty; int unboarded = 0;
            for (int i = 0; i < living; i++)
                if (_boardingFacts[i] != BlacksiteBoardingFact.Boarded)
                    waiting += (unboarded++ > 0 ? " " : string.Empty) + _departureMembers[i].MemberIndex + ":" + BlacksiteExtractionSequence.Token(_boardingFacts[i]);
            BlacksiteDiagnostics.Log("Extraction", $"boarding hold; elapsed={climb:0.0}, unboarded={unboarded}/{living}, waitingFor={waiting}");
        }
        /// <summary>
        /// #874 (2026-09-20): who the transport is still waiting for, and what each of them is
        /// doing about it: <c>&lt;memberIndex&gt;:&lt;reason&gt;</c> joined by spaces, or <c>none</c>.
        /// The two cargo reasons are named FIRST and separately from "distance", because they are
        /// the only ones the transport cannot act on and the only ones that are somebody else's
        /// bound: a hauling member is held by the cart until it releases him (PayloadCoupling owns
        /// that), and a carrying member is walking a record in under the cargo-load gate.
        /// #1219 A3: a member standing at the ship whose boarding the director refused is
        /// <c>refused:&lt;reason&gt;</c>, not distance.
        /// </summary>
        internal static string UnboardedMemberList(IReadOnlyList<BlacksiteSoldierAI> members)
        {
            string waiting = string.Empty;
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                if (member == null || member.isEnemyDead || member.HasBoardedShip) continue;
                string refusal = member.BoardingRefusal;
                string why = member.IsPushingPayload ? "hauling"
                    : member.IsObjectiveCarrier ? "carrying"
                    : (member.IsObjectiveCombatActive || member.IsCombatEngaged) ? "combat"
                    : !member.isOutside ? "inside"
                    : refusal != null ? "refused:" + refusal
                    : "distance";
                waiting += (waiting.Length > 0 ? " " : string.Empty) + member.MemberIndex + ":" + why;
            }
            return waiting.Length > 0 ? waiting : "none";
        }

        /// <summary>
        /// #874 Q4 (Lawson's decision 2): the HOVER cable lifts every staged item - the cart hooked
        /// at its handle, lifted vertically and stowed on the deck, a prop to its rack slot - one at
        /// a time, after the crate (the crate stands at the foot of the same rope and goes first).
        /// The hook holds the ship ladder lane under the object's id for the whole trip, exactly as
        /// the crate does, so no body is on the rope with it and nobody rides it.
        /// </summary>
        private void TickCableHookServer()
        {
            if (HookBusy) // not-a-wait: the hook keeps the rope only for its own trip, which ends on its own event (EndCableHookTrip: the lift completes, or CableLiftValid fails below)
            {
                ulong hooked = _hookObject.Value;
                if (!IsHovering || !IsGrounded || !_mission.Objectives.CableLiftValid(hooked)) // not-a-wait: ends the hook trip, never holds it
                { EndCableHookTrip(hooked, completed: false); return; }
                Ladder.Request(hooked, Time.time);
                Vector3 at = HookTripPosition(CargoNow, out bool complete);
                var cart = BlacksiteObjectiveManifestController.Resolve(hooked)?.GetComponent<Y4NGZCompany.Contracts.Payload.PayloadCartController>();
                if (cart != null)
                {
                    // Straight up in the cart's own yaw, then squared to the hull while it is drawn in.
                    float rise = Mathf.Abs(transform.TransformPoint(BlacksiteRappelGeometry.CableTopLocal).y - _hookFrom.Value.y);
                    float along = Mathf.Max(0f, (float)(CargoNow - _hookMotionAt.Value) * ContainerTravelSpeed);
                    Quaternion pose = Quaternion.Slerp(_hookRotation, transform.rotation, Mathf.Clamp01((along - rise) / Mathf.Max(1f, rise)));
                    cart.SetBlacksiteCablePoseServer(at, pose);
                }
                if (complete) EndCableHookTrip(hooked, completed: true);
                return;
            }
            var step = ExtractionStep;
            if (!IsHovering || !IsGrounded || step < BlacksiteExtractionStep.CargoLift || step == BlacksiteExtractionStep.Departed) return; // not-a-wait: the hook runs from CargoLift on (CargoLift and PersonnelClimb); CargoLift ends when nothing is owed (#1293 R2), and in PersonnelClimb the lift-off veto holds on HookBusy only while the trip itself lasts
            if (HasCargoContainer && _cargoMotion.Value != BlacksiteCablePhase.Aboard) return; // not-a-wait: the crate goes up first (RecoverCargoContainer), held only by owed cargo bound for it, whose haulers the ledger judges
            if (!_mission.Objectives.TryNextCableLift(DropLandingPosition, out ulong next)) return;
            if (!CableClearFor(lowering: false, out string blocked)) { NoteCableWait("cable hook", blocked); return; } // not-a-wait: a rising load waits only for a player on the rope (the player ends it) or another lane holder, whose crossing or trip ends it, or whom the progress ledger answers when he stops progressing
            BindAccessQueues();
            var rope = Ladder.RequestVerdict(next, Time.time);
            if (!rope.Admitted) // not-a-wait: a queued hook moves when the lane's holder ends its crossing or trip, goes stale, or is answered by the progress ledger
            {
                NoteCableWait("cable hook", $"the cable lane refused the hook ({rope.Reason}); waited {rope.WaitedFor:0.0}s, place {rope.Place}");
                return;
            }
            var obj = BlacksiteObjectiveManifestController.Resolve(next);
            if (obj == null || !_mission.Objectives.BeginCableLiftServer(next, out int slot, out BlacksiteObjectiveKind kind))
            { Ladder.Release(next); return; }
            // The deck target is taken before the hook is published, so CargoAnchor still answers
            // the rack slot (or the cart stand) rather than the hook itself.
            Transform stand = CargoAnchor(slot, kind);
            Vector3 target = stand != null ? stand.position : CargoDeckAnchor.position;
            _hookRotation = obj.transform.rotation;
            _hookFrom.Value = obj.transform.position;
            _hookTargetLocal.Value = transform.InverseTransformPoint(target);
            _hookSlot.Value = slot; _hookKind.Value = (byte)kind;
            _hookMotionAt.Value = CargoNow;
            _hookObject.Value = next;
            BlacksiteDiagnostics.Log("Rendezvous", $"cable hook lifting {kind} object={next} from {_hookFrom.Value} to the deck at {target}; mode={LandingModeToken}.");
        }
        private void EndCableHookTrip(ulong hooked, bool completed)
        {
            if (completed) _mission.Objectives.CompleteCableLiftServer(hooked);
            else
            {
                // A cart the hook lets go of mid-trip is handed back to its own motion where it is.
                var cart = BlacksiteObjectiveManifestController.Resolve(hooked)?.GetComponent<Y4NGZCompany.Contracts.Payload.PayloadCartController>();
                if (cart != null && cart.IsOnBlacksiteCable) cart.CancelBlacksiteMagnetServer();
            }
            Ladder.Release(hooked);
            _hookObject.Value = ulong.MaxValue; _hookSlot.Value = -1;
            BlacksiteDiagnostics.Log("Rendezvous", $"cable hook {(completed ? "set down" : "released")} object={hooked}; mode={LandingModeToken}.");
        }
        // The hook's pose on every peer, from the replicated trip: up to the rope head, then along
        // the deck to the stand - the crate's own path and speed.
        private Vector3 HookTripPosition(double now, out bool complete)
            => CableTrip(_hookFrom.Value, transform.TransformPoint(_hookTargetLocal.Value), _hookMotionAt.Value, now, out complete);
        private void ApplyCableHook()
        {
            if (!HookBusy) return;
            CableHookAnchor.SetPositionAndRotation(HookTripPosition(CargoNow, out _), transform.rotation);
        }
        // #1305 H1: the hull's magnet - the MagnetPoint child of its GiantCylinderMagnet, whose
        // forward points away from the hull - and its lever, resolved in OnNetworkSpawn. The magnet
        // is a separate device from the rappel cable: no ladder lane, not gated on the cache's trip.
        private Transform _magnetPoint, _magnetLeverTransform;
        /// <summary>#1305 H1 (R20): the hull's magnet pulls only while the hull stands on the ground.</summary>
        public bool LeverOn => IsGrounded && !IsHovering && _magnetLever.Value;
        public Transform MagnetPoint => _magnetPoint;
        public Vector3 OutwardSide => MagnetRule.Outward(_magnetPoint);
        public float Reach => MagnetRule.Reach;
        public Pose DockPose => MagnetRule.Dock(_magnetPoint);
        /// <summary>#1305 H1: host only, on a grounded hull: flips the magnet lever.</summary>
        internal void PullMagnetLeverServer()
        {
            if (!IsServer || !IsGrounded || IsHovering) return;
            _magnetLever.Value = !_magnetLever.Value;
            BlacksiteDiagnostics.Log("ShipAccess", $"magnet lever {(_magnetLever.Value ? "on" : "off")}.");
        }
        private InteractTrigger _magnetLeverTrigger;
        // #1305 H1: every peer, a late joiner included, reads the lever's state off the replicated value.
        private void ApplyMagnetLever()
        {
            if (_magnetLeverTrigger == null) return;
            string tip = _magnetLever.Value ? "Magnet lever (on) : [E]" : "Magnet lever (off) : [E]";
            if (_magnetLeverTrigger.hoverTip != tip) _magnetLeverTrigger.hoverTip = tip;
        }
        [ServerRpc(RequireOwnership = false)]
        private void UseMagnetLeverServerRpc(ServerRpcParams rpc = default)
        {
            var player = StartOfRound.Instance?.allPlayerScripts.FirstOrDefault(p => p != null && p.actualClientId == rpc.Receive.SenderClientId);
            if (player == null || player.isPlayerDead || !IsGrounded || _magnetLeverTransform == null
                || Vector3.Distance(player.transform.position, _magnetLeverTransform.position) > 4f) return;
            PullMagnetLeverServer();
        }
        /// <summary>#1305 H1: where a body stands to pull the lever - the deck navigation beside it, facing it.</summary>
        internal Pose MagnetLeverPose
        {
            get
            {
                if (_magnetLeverTransform == null) return new Pose(BoardingPosition, transform.rotation);
                Vector3 lever = _magnetLeverTransform.position;
                // Off a baked deck the lever's own point, dropped to deck height, stands in for the sample.
                Vector3 stand = NavMesh.SamplePosition(lever, out var deck, 2f, BlacksiteShipNavigation.DeckAreaMask)
                    ? deck.position : new Vector3(lever.x, transform.position.y + .12f, lever.z);
                // Standing right under it, he faces the wall it hangs on.
                Vector3 facing = Vector3.ProjectOnPlane(lever - stand, Vector3.up);
                if (facing.sqrMagnitude < .0001f) facing = Vector3.ProjectOnPlane(Vector3.ProjectOnPlane(lever - transform.position, transform.forward), Vector3.up);
                return new Pose(stand, Quaternion.LookRotation(facing, Vector3.up));
            }
        }
        // #1305 H1: the hull's magnet at the station pose (server-only, like LandingPosition), so a
        // measure against it never reads the hull in flight.
        private void StationMagnet(out Vector3 position, out Vector3 outward)
        {
            position = Matrix4x4.TRS(_landingPosition, _landingRotation, transform.lossyScale)
                .MultiplyPoint3x4(transform.InverseTransformPoint(_magnetPoint.position));
            outward = MagnetRule.Outward(_landingRotation * (Quaternion.Inverse(transform.rotation) * _magnetPoint.rotation));
        }
        // The dock approach, cached per station pose: CargoDeliveryPoint reads it per record per tick.
        // Only a sampled floor point is cached; a missed sample is not, so the next read samples again.
        private Pose _dockApproachStation;
        private Vector3? _dockApproach;
        /// <summary>
        /// #1305 H1 (D23): where a cart is delivered to a GROUND station - the ground 2.5 m out from
        /// the magnet along its outward side, inside the field the magnet pulls from; the floor point
        /// beside navigation there, else that point unsampled. Station pose; host only.
        /// </summary>
        internal Vector3 MagnetDockApproach
        {
            get
            {
                if (_magnetPoint == null) return CargoLoadingPosition;
                if (_dockApproach.HasValue && _dockApproachStation.position.Equals(_landingPosition)
                    && _dockApproachStation.rotation.Equals(_landingRotation)) return _dockApproach.Value;
                StationMagnet(out Vector3 magnet, out Vector3 outward);
                Vector3 approach = magnet + outward * 2.5f;
                approach.y = _landingPosition.y - FootToDeck;
                int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
                if (!BlacksiteLandingGeometry.TryFloorNav(approach, mask, out var floor, 1f)) return approach;
                _dockApproachStation = new Pose(_landingPosition, _landingRotation);
                _dockApproach = floor;
                return floor;
            }
        }
        /// <summary>
        /// #1219 D3 / #1305 H1: how far from <see cref="CargoLoadingPosition"/> the cable hook reaches
        /// for staged cargo at the rope foot. A prop staged at a ground crate is measured on the same reach.
        /// </summary>
        internal const float CableHookReachMetres = 8f;
        internal bool WithinCableHookReach(Vector3 position)
            => Vector3.Distance(position, CargoLoadingPosition) <= CableHookReachMetres;
        /// <summary>
        /// #1305 H1: whether one of this ship's own devices lifts a cart standing at
        /// <paramref name="position"/> - the reach a staged cart is measured against every tick
        /// (#1293 R2) and the answer a stuck cart outside gets (#1219 D3). Hover: the cable hook's
        /// reach. Ground: the field of the hull's magnet (<see cref="MagnetRule"/>). Both measured
        /// against the station, not the hull's live pose, so the answer holds while the ship is away:
        /// the record then stages and the recall brings the ship to lift it. Host only.
        /// </summary>
        internal bool WithinPayloadLiftReach(Vector3 position)
        {
            if (IsHovering) return WithinCableHookReach(position);
            if (_magnetPoint == null) return false;
            StationMagnet(out Vector3 magnet, out Vector3 outward);
            return MagnetRule.InField(magnet, outward, MagnetRule.Reach, position);
        }
        private bool MeasureCargo(GameObject source)
        {
            if (!BlacksiteCargoLayout.TryFootprint(source, out _cargoFootCenter, out _cargoFootHalf))
                return false;
            _cargoHookLocal = new Vector3(_cargoFootCenter.x, _cargoFootCenter.y + _cargoFootHalf.y, _cargoFootCenter.z);
            return true;
        }

        private void PrepareCargoContainer()
        {
            if (_cargoContainerId.Value != ulong.MaxValue || CargoRetained) return;
            var prefab = ContractAssetLoader.BlacksiteCratePrefab;
            if (prefab == null) { RetainCargoContainer("no cargo container prefab is loaded"); return; }
            if (!MeasureCargo(prefab))
            { RetainCargoContainer("the cargo container prefab carries no measurable footprint"); return; }
            var go = Instantiate(prefab, CargoDeckAnchor.position, transform.rotation);
            var obj = go.GetComponent<NetworkObject>(); var crate = go.GetComponent<ShadowRaidCargoCrate>();
            if (obj == null || crate == null)
            { Destroy(go); RetainCargoContainer("the cargo prefab has no networked cache component"); return; }
            _cargoMotion.Value = 3;
            _cargoFrom.Value = CargoDeckAnchor.position;
            obj.Spawn(); crate.ConfigureRetrievalCargoServer();
            MoonEscalationState.RegisterSpawnedObject(go);
            _cargoContainer = crate; _cargoContainerId.Value = obj.NetworkObjectId;
            BlacksiteDiagnostics.Log("Rendezvous", $"original cargo container aboard: id={obj.NetworkObjectId}.");
        }

        // #874 S3 (plan §3 rule 3): the crate is a participant in the ship FIFO for its lowering.
        // A GROUND crate's site is beside the hull, so it is item 0 from touchdown: it holds the lane
        // until its layout is decided, which is what keeps ground personnel on the deck until the
        // layout is known. A HOVER crate's site IS the rope foot - every member's crossing ends
        // there - so it asks for the lane only once nobody living and free is still aboard.
        // #1219 A3: the ground crate asks from IsGrounded, not from its first rendezvous tick, so
        // its claim on the lane is taken once there is a lane to take. It is still item 0: the only
        // other requesters are soldier crossings (BlacksiteNavigator.TryBeginShipCrossing), and
        // stowed soldiers stay seated on passenger hold until the deploy slice releases them after
        // NotifyBlacksiteShipLanded; the cable hook is hover-only.
        private bool OwesLowering => HasCargoContainer && _cargoLayout.Value == 0
            && _cargoMotion.Value == BlacksiteCablePhase.Aboard && !_mission.Objectives.Returning;
        private void TickCargoLoweringServer()
        {
            if (!OwesLowering) return; // not-a-wait: no lowering is owed, so nothing is held
            bool ready = PassageProven && IsGrounded;
            string aboard = null, blocked = null;
            // #874 S3 (plan §3 rule 3): the holder is the one IN the crossing. A hover crate that
            // cannot lower right now (a body or a player at the rope foot) is not in it, so it stands
            // out of the order that tick and the member at the foot crosses first.
            bool wants = IsHovering ? ready && SquadOffShip(out aboard) && RopeFootClear(lowering: true, out blocked) : IsGrounded;
            bool holds = false;
            BlacksiteBrokerVerdict rope = default;
            if (wants) holds = TryHoldCargoCableLane(Time.time, out rope);
            else ReleaseCargoCableLane();
            if (!ready || Time.time < _nextCargoDeploy) return; // not-a-wait: nothing waits on the deployment itself; a crate without proven access is retained at the deployment notice, and one still owed when the squad returns is released (OwesLowering). A ground crate does hold the ShipLadder lane through its deploy retries, so ground personnel queue behind it, bounded by BlacksiteCargoLayout.MaxAttempts x RetrySeconds (10 x 0.5 s) before RetainCargoContainer, after which nothing re-asserts the crate's claim and the stale heartbeat frees the lane
            _nextCargoDeploy = Time.time + BlacksiteCargoLayout.RetrySeconds;
            if (aboard != null) NoteCableWait("cargo lowering", aboard);
            else if (blocked != null) NoteCableWait("cargo lowering", blocked);
            else if (!holds) NoteCableWait("cargo lowering", CableLaneRefusal(rope));
            else DeployCargoContainer();
        }
        private bool SquadOffShip(out string aboard) => MembersAboard(out aboard) == 0;
        // #1219 A1 / #1233 S4 (rule 1): how many living, uncaptured members are aboard the ship now
        // (Aboard, by live body position), and a sentence naming the first; -1 while the squad
        // roster is not available.
        private int MembersAboard(out string first)
        {
            first = "the squad roster is not available";
            var director = _mission.SquadDirector;
            if (director == null) return -1;
            first = null;
            int aboard = 0;
            director.CollectLivingMembers(_rendezvousMembers);
            for (int i = 0; i < _rendezvousMembers.Count; i++)
            {
                BlacksiteSoldierAI member = _rendezvousMembers[i];
                if (member == null || !Aboard(member.transform.position) || BlacksiteVanillaMonsters.VictimCaptured(member)) continue;
                if (first == null) first = "member " + member.MemberIndex + " is still aboard";
                aboard++;
            }
            return aboard;
        }

        /// <summary>
        /// #874 F04 / S3: is the rope physically clear for a cargo trip that holds the lane (or,
        /// for the hook, is about to ask for it)? The rope foot must be clear (<see cref="RopeFootClear"/>)
        /// and a lane holder that is not this crate owns the rope.
        /// </summary>
        private bool CableClearFor(bool lowering, out string why)
        {
            if (!RopeFootClear(lowering, out why)) return false;
            if (Ladder.HasOccupant(Time.time) && Ladder.TryGetAdmittedHead(out ulong head) && head != _cargoContainerId.Value)
            { why = "the cable lane is held by " + head; return false; }
            return true;
        }
        /// <summary>
        /// The facts the FIFO cannot see: a player climbing the column, and - for a LOWERING, which
        /// comes down onto whatever stands there - a body inside the touchdown footprint. A RISING
        /// load leaves the ground and owes a body beside it nothing.
        /// </summary>
        private bool RopeFootClear(bool lowering, out string why)
        {
            why = null;
            if (RappelUsedByPlayer) { why = "a player is on the rope"; return false; }
            if (!BlacksiteCablePhase.OwesFootprintClearance(lowering)) return true;
            if (!_deploymentNotified) { why = "the deployment has not been announced"; return false; }
            if (_mission == null) return true;
            var director = _mission.SquadDirector;
            if (director == null) return true;
            director.CollectLivingMembers(_rendezvousMembers);
            float clearance = BlacksiteCargoLayout.Diagonal(_cargoFootHalf) + BlacksiteCargoLayout.LaneClearance;
            for (int i = 0; i < _rendezvousMembers.Count; i++)
            {
                BlacksiteSoldierAI member = _rendezvousMembers[i];
                if (member != null && member.isOutside
                    && (member.transform.position - DropLandingPosition).sqrMagnitude < clearance * clearance)
                { why = "member " + member.MemberIndex + " stands inside the cargo footprint at the touchdown"; return false; }
            }
            return true;
        }

        private void DeployCargoContainer()
        {
            if (CargoContainer == null) return;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            if (!TryCargoGround(mask, _cargoFootCenter, _cargoFootHalf, out var floor, out var why))
            {
                if (++_cargoAttempt < BlacksiteCargoLayout.MaxAttempts)
                { BlacksiteDiagnostics.Log("Rendezvous", $"no clear cargo site (attempt {_cargoAttempt}/{BlacksiteCargoLayout.MaxAttempts}): {why}."); return; }
                RetainCargoContainer(why);
                return;
            }
            _cargoGround.Value = floor;
            _cargoFrom.Value = CargoContainer.transform.position;
            _cargoMotionAt.Value = CargoNow;
            _cargoMotion.Value = IsHovering ? BlacksiteCablePhase.Lowering : BlacksiteCablePhase.Ground;
            if (!IsHovering)
            {
                CargoContainer.transform.position = floor;
                DecideCargoLayout();
                ReleaseCargoCableLane();
            }
            BlacksiteDiagnostics.Log("Rendezvous", $"original cargo container deploying: id={_cargoContainerId.Value}, touchdown={DropLandingPosition}, ground={floor}, sharedCable={IsHovering}{(why == null ? "" : ", " + why)}.");
        }

        private void RecoverCargoContainer()
        {
            if (CargoContainer == null || !BlacksiteCablePhase.AtCableFoot(_cargoMotion.Value)) return; // not-a-wait: no crate at the cable foot, nothing to recover
            // #874 S3 (plan §3 rule 3): the lift is a FIFO turn. The crate joins the order first, so
            // members already waiting cross ahead of it, and raises only as the holder. A player on
            // the rope takes the crate out of the order until the rope is free.
            if (IsHovering && !RopeFootClear(lowering: false, out string blocked)) // not-a-wait: a rising crate waits only for a player on the rope, who ends it
            { ReleaseCargoCableLane(); NoteCableWait("cargo recovery", blocked); return; }
            if (!TryHoldCargoCableLane(Time.time, out BlacksiteBrokerVerdict rope)) // not-a-wait: a queued crate moves when the lane's holder ends its crossing, goes stale, or is answered by the progress ledger
            { NoteCableWait("cargo recovery", CableLaneRefusal(rope)); return; }
            // #874 F04 (initial-network-review B1): the ground layout closes HERE, on the one path
            // that actually starts the lift, and never on a deferral: a deferred crate stays
            // loadable and keeps its proven layout.
            _cargoLayout.Value = 2;
            _cargoFrom.Value = CargoContainer.transform.position;
            _cargoMotionAt.Value = CargoNow;
            _cargoMotion.Value = IsHovering ? BlacksiteCablePhase.Raising : BlacksiteCablePhase.Aboard;
            if (!IsHovering) ReleaseCargoCableLane();
            BlacksiteDiagnostics.Log("Rendezvous", $"original cargo container returning aboard: id={_cargoContainerId.Value}, sharedCable={IsHovering}.");
        }
        private void RetainCargoContainer(string why)
        {
            if (_cargoLayout.Value == 2) return;
            _cargoLayout.Value = 2;
            BlacksiteDiagnostics.Log("Rendezvous", $"cargo container retained aboard after {_cargoAttempt} attempts ({why}); sacrificed={_cargoSacrificed}. Squad released; staging falls back to the rendezvous point.");
        }
        // #1305 H4 (R24, ruling 4): the cart oracle's verdict on the station's dock approach (null: proven or not asked).
        internal string DockApproachRefusal { get; private set; }
        private bool TryCargoGround(int mask, Vector3 footCenter, Vector3 footHalf, out Vector3 floor, out string reason)
        {
            DockApproachRefusal = null;
            if (IsHovering)
            {
                _cargoSacrificed = "shared touchdown sequenced with personnel";
                // #874 Q5: the station's own lanes (#1305: the sealed plan's exit and ground route); the rope foot is proven here.
                Vector3 forward = Vector3.ProjectOnPlane(_landingRotation * Vector3.forward, Vector3.up).normalized;
                var cableLanes = BlacksiteCargoLayout.CableLanes(forward, RendezvousPosition, ExitPosition, _groundRoute, CartStagingPosition, _accessEntrance);
                return BlacksiteCargoLayout.TryCableSite(DropLandingPosition, _landingRotation, RendezvousPosition, cableLanes,
                    footCenter, footHalf, mask, out floor, out reason);
            }
            var lanes = BlacksiteCargoLayout.Reserve(transform, Access, RendezvousPosition, ExitPosition, _groundRoute, CartStagingPosition, _accessEntrance);
            // #1305 H4 (R24): a ground cart goes to the magnet dock approach (D23), not a crate site, so the cart's own
            // oracle (G4 R13) proves that one target, from the main entrance's outside exit in the yaw the crossing gives it.
            string cartRoute = PayloadContract.ActiveCart == null ? "none" : "skipped (no main entrance exit)";
            for (int i = 0; PayloadContract.ActiveCart != null && i < BlacksiteEntranceInteraction.RouteEntranceCount; i++)
                if (BlacksiteEntranceInteraction.TryGetRoutePair(i, outside: false, main: true, RendezvousPosition, out var door, out _, out _)
                    && BlacksiteEntranceInteraction.TryResolveExitEndpoint(door, out var outer, out Vector3 from, out _))
                {
                    var yaw = Quaternion.LookRotation(Vector3.ProjectOnPlane(BlacksiteEntranceInteraction.ExitForward(outer), Vector3.up).normalized);
                    DockApproachRefusal = PayloadCartRoute.Prove(from, yaw, MagnetDockApproach, BlacksiteReachability.SoldierAreaMask, CargoCartScratch, null, out string refusal)
                        ? null : PayloadCartRoute.Describe(refusal, CargoCartScratch);
                    cartRoute = DockApproachRefusal ?? "proven";
                    break;
                }
            bool sited = BlacksiteCargoLayout.TrySite(transform, _landingRotation, RendezvousPosition, lanes, footCenter, footHalf, mask,
                _cargoAttempt, out floor, out _cargoSacrificed, out reason);
            if (sited) reason += " cartRoute=" + cartRoute;
            return sited;
        }
        private static readonly PayloadCartRouteScratch CargoCartScratch = new PayloadCartRouteScratch();
        // #874 Q5 (F2): the crate stands on the site TryCableSite/TrySite proved before it left the
        // deck, so the layout is decided here; nothing re-judges a site that passed.
        private void DecideCargoLayout()
        {
            _cargoLayout.Value = 1;
            BlacksiteDiagnostics.Log("Rendezvous", $"cargo layout proven at site choice: ground={_cargoGround.Value}, sacrificed={_cargoSacrificed}, mode={LandingModeToken}.");
        }
        private Vector3 CargoTripPosition(double now, out bool complete)
        {
            // Move to/from the head of the personnel rope before crossing to the deck stand.
            Vector3 head = transform.TransformPoint(BlacksiteRappelGeometry.CableTopLocal);
            Vector3 target = _cargoMotion.Value == BlacksiteCablePhase.Lowering ? _cargoGround.Value : CargoDeckAnchor.position;
            return CablePath(_cargoFrom.Value, head, head, target, _cargoMotionAt.Value, now, out complete);
        }
        // #874 Q4: the hook is winched STRAIGHT UP from where it took the load to the rope head's
        // height, then drawn in to the head and across the deck to the stand - the crate's speed,
        // one path shape for both, derived on every peer from the replicated start.
        private Vector3 CableTrip(Vector3 from, Vector3 target, double startedAt, double now, out bool complete)
        {
            Vector3 head = transform.TransformPoint(BlacksiteRappelGeometry.CableTopLocal);
            return CablePath(from, new Vector3(from.x, head.y, from.z), head, target, startedAt, now, out complete);
        }
        private static Vector3 CablePath(Vector3 a, Vector3 b, Vector3 c, Vector3 d, double startedAt, double now, out bool complete)
        {
            float ab = Vector3.Distance(a, b), bc = Vector3.Distance(b, c), cd = Vector3.Distance(c, d);
            float travelled = Mathf.Max(0f, (float)(now - startedAt) * ContainerTravelSpeed);
            complete = travelled >= ab + bc + cd;
            if (travelled < ab) return Vector3.Lerp(a, b, travelled / Mathf.Max(.001f, ab));
            travelled -= ab;
            if (travelled < bc) return Vector3.Lerp(b, c, travelled / Mathf.Max(.001f, bc));
            return Vector3.Lerp(c, d, Mathf.Clamp01((travelled - bc) / Mathf.Max(.001f, cd)));
        }
        private void ApplyRendezvousReplica()
        {
            if (CargoContainer == null) return;
            // #943 S11 U5: the same deck stand on every peer; the anchor is a ship-local lookup.
            // #874 F04: every peer reads the phase through the one rule the host decides with, so
            // a replica can never disagree with the host about where the crate is on the rope.
            Vector3 position = BlacksiteCablePhase.OnCable(_cargoMotion.Value)
                ? CargoTripPosition(CargoNow, out _)
                : _cargoMotion.Value == BlacksiteCablePhase.Aboard ? CargoDeckAnchor.position : _cargoGround.Value;
            CargoContainer.transform.SetPositionAndRotation(position, transform.rotation);
        }
    }
}
