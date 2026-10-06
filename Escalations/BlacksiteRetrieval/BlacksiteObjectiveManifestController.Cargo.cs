using System.Collections.Generic;
using System.Linq;
using Unity.Netcode;
using UnityEngine;
using Y4NGZCompany.Contracts.Payload;
using Y4NGZCompany.Contracts.ShadowRaid;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteObjectiveManifestController
    {
        // #874 Q4 (2026-09-24): the delivery window (_deliveryStarted), the optional window
        // (_loadingStarted) and their shared 60 s are DELETED. Both measured from something other
        // than the ship being there to lift the cargo - staging and the recall - so on 23 September
        // round 1 the delivery window abandoned a staged cart the absent ship could not load (log
        // 15521-15523) and the optional window then held boarding 48 s for an apparatus nothing
        // would lift (22358). #1293 R2: the lift budget that replaced them is deleted too. The
        // CargoLift step ends when nothing is owed (AnyCargoOwed); owed cargo stops being owed by
        // going aboard or by an event that writes it off, never by a clock.
        /// <summary>
        /// #874 Q4 (2026-09-24): the round's objective is staged (within the ship's lift reach,
        /// measured by Refresh) with nobody holding it. This is the pickup request
        /// (<see cref="BlacksiteReturnRule.PrimaryStaged"/>): the ship is recalled and the extraction
        /// sequence lifts it, instead of a 60 s window waiting for a ship that was away.
        /// </summary>
        internal bool PrimaryAwaitingPickup
        {
            get
            {
                for (int i = 0; i < Records.Count; i++)
                    if (StagedPrimaryDelivery(Records[i]) && !CrewHoldsObjective(Records[i])) return true;
                return false;
            }
        }
        // #874 Q4 (2026-09-24): CargoHoldsBoarding is DELETED. It was the "custody" gate every
        // boarding consumer read through BlacksiteShipController.CargoReadyForDeparture, and while it
        // was true BoardingApproach sent the squad to a guard ring drawn on top of the staged cart
        // (23 September round 1, veto:PayloadCart 19276...). Boarding is now a STEP of the one
        // extraction sequence (PersonnelClimb), entered when the cargo lift is over.
        /// <summary>
        /// #1293 R2: the ONE answer to "does the squad still owe the ship this cargo". A cargo record
        /// (not a witness, not the ship's own loot) whose object exists, that is neither aboard
        /// (Loaded, Extracted) nor written off (Unavailable), and that stands on the surface: outside
        /// the facility - so a recalled squad never re-raids for an inside cart - and not stowed in
        /// the crew's ship. A player's hands or handle do not end it: holding is a lifter's "free"
        /// test, not a second owed judge.
        /// </summary>
        internal bool CargoOwed(BlacksiteObjectiveSnapshot r) => CargoOwed(r, Resolve(r.ObjectId));
        private static bool CargoOwed(BlacksiteObjectiveSnapshot r, NetworkObject obj)
            => r.Kind != BlacksiteObjectiveKind.Witness && r.Kind != BlacksiteObjectiveKind.Loot && obj != null
               && r.Phase != BlacksiteObjectivePhase.Loaded && r.Phase != BlacksiteObjectivePhase.Extracted
               && r.Phase != BlacksiteObjectivePhase.Unavailable
               && BlacksiteWorld.Outside(obj, r.Outside) && !CrewSecured(obj.GetComponent<GrabbableObject>());
        /// <summary>
        /// #1293 R2: the manifest's half of the extraction sequence's CargoPending fact (the ship adds
        /// the crate and the hook): any record still owed. Indexed: the ship asks it on its own tick.
        /// </summary>
        internal bool AnyCargoOwed
        {
            get
            {
                for (int i = 0; i < Records.Count; i++) if (CargoOwed(Records[i])) return true;
                return false;
            }
        }
        /// <summary>
        /// #1293 R2: owed cargo whose delivery still ends IN the ground crate, so the crate may not go
        /// up yet - every owed prop but one already staged on a HOVER station, which the hook lifts
        /// after the crate. A cart never goes in the crate: the magnet and the hook take it.
        /// </summary>
        internal bool CargoOwedIntoCrate
        {
            get
            {
                for (int i = 0; i < Records.Count; i++)
                {
                    var r = Records[i];
                    if (r.Kind == BlacksiteObjectiveKind.Payload
                        || (r.Phase == BlacksiteObjectivePhase.Staged && Ship != null && Ship.IsHovering)) continue;
                    if (CargoOwed(r)) return true;
                }
                return false;
            }
        }
        /// <summary>
        /// #1293 R2: Staged is a measurement, not a memory: an owed record nobody's NPC hauler holds is
        /// Staged while it stands within the ship's lift reach, whoever pushed it there, and Available
        /// out of it. Refresh asks this every host tick. #1305 H1: a cart's reach is the payload lift
        /// (<see cref="BlacksiteShipController.WithinPayloadLiftReach"/>: the station magnet's field on
        /// the ground, the cable hook's reach in hover); a prop's is the hook's reach from the loading
        /// point (<see cref="BlacksiteShipController.WithinCableHookReach"/>). A cart is measured at its
        /// Live world row (<see cref="PublishCargoRowsServer"/>), so a cart with no row is not in reach;
        /// a prop at its own transform. <paramref name="at"/> is the measured point.
        /// </summary>
        private bool WithinLiftReach(BlacksiteObjectiveSnapshot r, NetworkObject obj, out Vector3 at)
        {
            at = obj != null ? obj.transform.position : r.Position;
            if (Ship == null || !CargoOwed(r, obj)) return false;
            if (r.Kind != BlacksiteObjectiveKind.Payload) return Ship.WithinCableHookReach(at);
            if (!TryLiveRow(r.ObjectId, out var row)) return false;
            at = row.Position;
            return Ship.WithinPayloadLiftReach(at);
        }
        /// <summary>
        /// #874 (2026-09-22, Rend): a kind whose delivery is a PROP carried into the hold.
        ///
        /// A witness walks aboard on his own legs and becomes Loaded when his escort completes his
        /// board crossing (#1219 C2, Witness.cs); a cart is lifted by the ship (magnet on the ground, cable in hover).
        /// Neither is ever walked into a crate. Everything else the round can call its primary is a
        /// thing in a pair of hands, and a thing in a pair of hands is what the retained crate's
        /// slots hold.
        /// </summary>
        private static bool WalksCargoAboard(BlacksiteObjectiveKind kind)
            => IsPrimaryObjective(kind)
               && kind != BlacksiteObjectiveKind.Witness && kind != BlacksiteObjectiveKind.Payload;
        /// <summary>
        /// #874 (2026-09-22, Rend): the round's own objective is staged at the rendezvous,
        /// whatever it is waiting to be lifted by. #874 Q4: this is the pickup request
        /// (<see cref="PrimaryAwaitingPickup"/>). A witness is excluded because he never stages.
        /// </summary>
        private static bool StagedPrimaryDelivery(BlacksiteObjectiveSnapshot r)
            => IsPrimaryObjective(r.Kind) && r.Kind != BlacksiteObjectiveKind.Witness
               && r.Phase == BlacksiteObjectivePhase.Staged;
        /// <summary>
        /// #874 (2026-09-22, Rend): a CREW member has the round's objective, so the squad's
        /// rendezvous delivery does not own it and may not write it off.
        ///
        /// The three ways a player can hold it are the three the board already refuses a
        /// reservation for (BlacksiteObjectivePolicy.CanReserve): in his hands, stowed in the ship
        /// room or the elevator, and on the other end of the cart handle. #1293 R2: it is every
        /// lifter's "free" test (the loader, the magnet, the hook) and the pickup request's; whether
        /// the squad still owes the cargo is <see cref="CargoOwed(BlacksiteObjectiveSnapshot)"/>.
        /// </summary>
        private bool CrewHoldsObjective(BlacksiteObjectiveSnapshot r)
        {
            var obj = Resolve(r.ObjectId);
            if (obj == null) return false;
            var prop = obj.GetComponent<GrabbableObject>();
            if (prop != null && (PlayerHeld(prop) || CrewSecured(prop))) return true;
            var cart = obj.GetComponent<PayloadCartController>();
            return cart != null && cart.IsAttached;
        }
        /// <summary>
        /// #874 (2026-09-22, Rend): this record's delivery continues INTO the crate on the deck.
        ///
        /// When no ground site clears the reserved lanes the crate comes back aboard and stays
        /// there, and until this unit that was the end of the round's objective: the carrier put
        /// the original down at the guarded rendezvous (there being no ground box to load into),
        /// TickRendezvousLoading skipped every retained record outright, and the ship left with
        /// zero extracted. The continuation is the same delivery finished in the only place the
        /// box now is - a loader carries the ORIGINAL aboard and seats it in the crate's own slot
        /// - so identity, custody and the carry -> load -> extracted record are unchanged.
        ///
        /// The phases are every one a delivery can still be in: Available (the original went
        /// loose when its carrier died), Reserved (a loader is walking to it), Carried (a loader
        /// has it) and Staged (it is on the ground at the rendezvous). Loaded, Extracted and
        /// Unavailable are terminal, which is also what makes a written-off delivery unable to
        /// restart: AbandonCargo writes Unavailable and nothing here answers again.
        ///
        /// #874 Q4 (2026-09-24): once the pickup is requested, OPTIONAL cargo already in the
        /// squad's hands or at the rendezvous takes the same route, because "every staged item"
        /// goes home and the crate on the deck is the only place it can go. #1293 R2: loose owed
        /// cargo of any kind is reserved after the recall too (AssignReservations).
        ///
        /// #1219 B5: never on a HOVER ship. There nothing staged is walked aboard (Lawson's
        /// decision 2): the carrier stages at the rope foot and the cable hook lifts it
        /// (<see cref="TryNextCableLift"/>). The deck is ~14 m up, so a hover delivery routed to it
        /// ended a Partial path on the ground (26 September round 3, member 0, 47 s at the foot).
        /// </summary>
        private bool RetainedCargoDelivery(BlacksiteObjectiveSnapshot r)
            // Kind and phase first: they are two enum comparisons, and the ship terms behind them
            // resolve a component and read NetworkVariables. This is asked per record per tick
            // by the loading tick and per carry tick through CargoDeliveryPoint.
            => (WalksCargoAboard(r.Kind) || (Returning && OptionalCargo(r.Kind) && r.Phase != BlacksiteObjectivePhase.Available))
               && r.Phase != BlacksiteObjectivePhase.Loaded
               && r.Phase != BlacksiteObjectivePhase.Extracted
               && r.Phase != BlacksiteObjectivePhase.Unavailable
               && Ship != null && !Ship.IsHovering && Ship.CargoRetainedAboard;
        // #874 Q4: salvage, apparatus and survey drones - the cargo the squad may bring home
        // besides its primary. Loot is the ship's own rack stock and never travels.
        private static bool OptionalCargo(BlacksiteObjectiveKind kind)
            => !IsPrimaryObjective(kind) && kind != BlacksiteObjectiveKind.Loot;
        /// <summary>
        /// #874 (Rend): where a carrier takes THIS record to put it down - one resolver for the order producers (TryCargoOrder,
        /// the planner's Carried branch, Refresh's carry goal) and TryStageOrLoad's release gate. The deck point for a GROUND ship
        /// with its crate retained aboard; a CART on a GROUND ship the magnet side (#1305 H1 D23: MagnetDockApproach, in the
        /// field); else CargoLoadingPosition (#1219 B5: in HOVER always, the ground point at the rope foot, never the deck).
        /// </summary>
        internal Vector3 CargoDeliveryPoint(BlacksiteObjectiveSnapshot r)
            => RetainedCargoDelivery(r) ? Ship.RetainedCargoLoadingPosition
                : r.Kind == BlacksiteObjectiveKind.Payload && !Ship.IsHovering ? Ship.MagnetDockApproach
                : Ship.CargoLoadingPosition;
        // #964 S14 U2: whether a Loaded record is still aboard - the difference between kinds is
        // only WHICH volume. An item is carried into the hold and placed, so the test is the cargo
        // bounds; a witness is seated in the boarding area, so his test is the boarding area or the
        // hold. It carries no IsGrounded term: Refresh's Loaded arm asks this while the hull is
        // still on station. (#1219 C2: Carried becomes Loaded only by the escort's board crossing.)
        // #874 Q4: an item on the ship's cable hook is aboard the lift: it is Loaded from the moment
        // it is hooked, and the hold test would otherwise hand it back to the moon mid-air.
        private bool CargoContains(BlacksiteObjectiveSnapshot r, Vector3 position) => r.Kind == BlacksiteObjectiveKind.Payload
            ? (Resolve(r.ObjectId)?.GetComponent<PayloadCartController>()?.IsOnBlacksiteMagnet(Ship.NetworkObjectId) ?? false)
            : r.Kind == BlacksiteObjectiveKind.Witness
            ? Ship.ContainsCargo(position) || BlacksiteShipNavigation.InBoardingArea(Ship.transform, position)
            : Ship.CableHolds(r.ObjectId)
            || (r.ContainerCargo ? Ship.ContainsContainerCargo(position) : Ship.ContainsCargo(position));
        // #874 (2026-09-20): VerifyHaul and its handle probe are DELETED, with the 15 s operator
        // bench they armed. They were a second owner of "is this pair still hauling", measured
        // from outside the coupling on a three-second window, and PayloadCoupling's
        // TryFollowNpcServer is now the whole answer: true while the pair is genuinely attached,
        // including a legitimate stationary or blocked wait at the handle, false once the cart
        // has actually released. Refresh's Carried arm reads exactly that and nothing else. The
        // cart's own block, recovery and release are its own, and cycling a fresh operator into
        // unchanged geometry is what the bench used to do.
        private bool TryStageOrLoad(int index, BlacksiteObjectiveSnapshot r, BlacksiteSoldierAI carrier,
            NetworkObject obj, GrabbableObject prop, PayloadCartController cart)
        {
            // #964 S14 U2: a witness never stages. Staging is a release onto the ground beside the
            // rendezvous container so a loader can put the thing INTO it; a man is walked aboard
            // instead, and Carried becomes Loaded by his escort's completed board crossing (#1219
            // C2), not here. This short-circuit is what makes Staged unreachable for a witness
            // record, and it is why TickRendezvousLoading below never has one to load.
            if (r.Kind == BlacksiteObjectiveKind.Witness) return false;
            // #948 S12 U3: an egg is never cargo. The registration filter already refuses to list
            // one, so reaching here means something else put it in the manifest; the refusal is a
            // named pickupRejected event rather than a silent skip.
            if (ProtectedEggPickup(r, prop, carrier)) return false;
            // #874 (2026-09-22, Rend): a delivery bound for the crate on the DECK is not released
            // out here at all. The old rule said "a retained container has no ground box, so
            // recovered items stage at the rendezvous point instead", and that sentence is what
            // put drive 1094 on the ground at (-11.34,-19.11,-166.93) with nothing that would
            // ever pick it up again. The box is not gone; it is aboard, with its slots, so the
            // carrier keeps the original and the release happens beside it on the deck.
            // #1219 B5: the release point is CargoDeliveryPoint's, the one resolver the orders read;
            // in hover that is the rope-foot ground point, so the item stages there for the hook.
            bool aboard = RetainedCargoDelivery(r);
            Vector3 release = CargoDeliveryPoint(r);
            // #1293 R2: a cart stages for the magnet or the hook whatever the crate is doing; only a
            // prop needs a crate to stage beside (on the moon) or to be walked to (retained).
            if (!(cart != null || Ship.ContainerOnGround || Ship.CargoRetained)
                || Vector3.Distance(carrier.transform.position, release) > 1.5f) return false;
            // Standing beside the crate is not, by itself, being inside the hull - but the test
            // for that is the DECK, not the boarding area (|x| < 1.7, z > 1.9 ship-local): the
            // crate's deck stand is at (-1.9, -0.04, -2.906) with its loading side forward of that,
            // so no point within this gate's 1.5 m can also be inside the boarding volume, and a
            // boarding-area gate here could never fire on a ground landing.
            // OnDeck is the same strip RetainedCargoLoadingPosition is sampled onto and the one
            // the passage itself uses to decide a body is on the ship. `aboard` is only ever true
            // on a ground ship (#1219 B5). IsGrounded is kept from the old test so a hull that has
            // committed to leaving accepts nothing.
            if (aboard && !(Ship.IsGrounded && Ship.OnDeck(carrier.transform.position))) return false;
            if (cart != null) cart.ReleaseNpcPusherServer();
            Vector3 point = BlacksiteDriveCarrier.ResolveReachableDropPoint(carrier.transform.position + carrier.transform.forward * .65f);
            // The slot IS the load. A record may only be called Loaded when a real anchor took the
            // object, so the anchor is resolved first and `loading` is what it answered - never a
            // phase written over an object still lying at a carrier's feet.
            var anchor = aboard || ((IsPrimaryObjective(r.Kind) || (Returning && r.ContainerCargo))
                && cart == null && Ship.ContainerOnGround)
                ? Ship.ContainerSlot(r.Slot) : null;
            bool loading = anchor != null;
            if (anchor != null) point = anchor.position;
            ReleaseCarriedRecord(index, r, carrier, obj, prop, point, cart != null ? cart.transform.position : point, loading, true,
                aboard ? "loaded into the retained container aboard" : loading ? "loaded into retrieval container" : "staged at guarded rendezvous",
                aboard ? ", into the retained container aboard the transport." : ".");
            carrier.CancelExtractionToRecoverDrive(); carrier.AnnounceCargo();
            return true;
        }

        /// <summary>
        /// #1219 C2: the custody event of a completed board crossing. Every record this member
        /// carries up is loaded by the crossing itself: his witness (LoadWitnessRecordServer) and
        /// his prop, into its slot. A payload is excluded - a coupled pusher cannot begin a ship
        /// crossing, the coupling is his body writer. Host-only; the manifest list is the one writer.
        /// </summary>
        internal void LoadCarriedByBoardCrossingServer(BlacksiteSoldierAI carrier)
        {
            if (_mission == null || !_mission.IsServer || Ship == null || carrier == null) return;
            bool loadedProp = false;
            for (int i = 0; i < Records.Count; i++)
            {
                BlacksiteObjectiveSnapshot r = Records[i];
                if (r.Phase != BlacksiteObjectivePhase.Carried || r.CarrierId != carrier.NetworkObjectId
                    || r.Kind == BlacksiteObjectiveKind.Payload) continue;
                if (r.Kind == BlacksiteObjectiveKind.Witness)
                {
                    if (LoadWitnessRecordServer(ref r, carrier)) Records[i] = r;
                    continue;
                }
                NetworkObject obj = Resolve(r.ObjectId);
                if (obj == null) continue;
                // The slot the loader would have put it in: the retained crate's on a ground deck,
                // else the hold's own cargo anchor.
                bool retained = RetainedCargoDelivery(r);
                Transform anchor = retained ? Ship.ContainerSlot(r.Slot) : Ship.CargoAnchor(r.Slot, r.Kind);
                if (anchor == null) continue;
                // Not CancelExtractionToRecoverDrive: that is the un-board, and he is boarding.
                ReleaseCarriedRecord(i, r, carrier, obj, obj.GetComponent<GrabbableObject>(), anchor.position, anchor.position,
                    true, retained, "loaded aboard with its carrier", ", boarded with its carrier.");
                loadedProp = true;
            }
            // TryStageOrLoad's CargoDeposited voice, once per crossing.
            if (loadedProp) carrier.AnnounceCargo();
        }

        /// <summary>
        /// The one release of a carried record: the carrier puts it down at <paramref name="point"/>
        /// (a slot, when <paramref name="loading"/>), the authored release flies it there so Refresh's
        /// Loaded arm does not hand it back mid-flight, the haul completes, and one [UAT][Cargo] line
        /// names it. Shared by TryStageOrLoad and the board crossing's custody event (#1219 C2).
        /// </summary>
        private void ReleaseCarriedRecord(int index, BlacksiteObjectiveSnapshot r, BlacksiteSoldierAI carrier, NetworkObject obj,
            GrabbableObject prop, Vector3 point, Vector3 position, bool loading, bool containerCargo, string dropReason, string logSuffix)
        {
            obj.GetComponent<BreachHardDriveItem>()?.DropFromSoldierServer(point, dropReason);
            BlacksiteCarryPresentation.BeginRelease(prop, point);
            CompleteHaul(r); // Successful staging/loading completes the haul (#1219 A4: and clears the failure history).
            r.Phase = loading ? BlacksiteObjectivePhase.Loaded : BlacksiteObjectivePhase.Staged;
            // #1219 B1: staged or loaded beside the carrier, so in the carrier's world (outside).
            r.ContainerCargo = containerCargo; r.CarrierId = NoCarrier; r.Outside = BlacksiteWorld.Outside(carrier);
            r.Position = position;
            Records[index] = r; _retryAt.Remove(r.ObjectId); ReconcileDriveCarriers();
            // #1066 U8: the one objective completion that is unambiguously the SQUAD's. A member
            // physically hauled this record to the ship and staged or loaded it, so the attempt
            // above closed Completed because of something a member did. The two terminal closes in
            // Refresh - a delivered cart and a neutralized witness - are the CREW completing the
            // contract, and crediting those would re-earn "sector is secure" for a squad that did
            // nothing, which is the exact case the reassurance gate exists to silence.
            _mission.SquadDirector?.NoteSquadProgress();
            BlacksiteDiagnostics.Log("Cargo", $"{(loading ? "loaded" : "staged")} {r.Kind} object={r.ObjectId}, member={carrier.MemberIndex}, position={point}"
                + logSuffix);
        }
        // #1305 H4 (R24): THE derivation of a member's cargo order - the record it follows, or -1; ResolveIntent carries
        // it, so the order never outlives it. Carrier, after the recall (#1146): Reserved and owed, or Carried (#1305 G4:
        // a withdrawal boards a prop in hands). Lever: the owed cart on a grounded magnet hull, for its one puller.
        internal int CargoRole(BlacksiteSoldierAI soldier, out bool lever)
        {
            lever = false; int cart = -1;
            for (int i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                if (r.Kind == BlacksiteObjectiveKind.Witness) continue; // #964 S14 U2: an escort's custody walk, not cargo.
                if (r.CarrierId != soldier.NetworkObjectId)
                {
                    if (cart < 0 && r.Kind == BlacksiteObjectiveKind.Payload && CargoOwed(r)) cart = i;
                    continue;
                }
                if (Returning && (r.Phase == BlacksiteObjectivePhase.Reserved ? CargoOwed(r)
                    : r.Phase == BlacksiteObjectivePhase.Carried && (r.ContainerCargo || r.Kind == BlacksiteObjectiveKind.Payload
                        || !(_withdrawalCommitted || soldier.IsRaidWithdrawing)))) return i;
            }
            var ship = Ship;
            if (cart < 0 || ship == null || !ship.IsGrounded || ship.IsHovering || ship.MagnetPoint == null
                || Planner.LeverPuller() != soldier) return -1;
            lever = true; return cart;
        }
        // #1305 H4 (R24): CargoRole's order: the pickup, the delivery under TryStageOrLoad's gate, or the lever walk (hull door open).
        internal bool TryCargoOrder(BlacksiteSoldierAI soldier, out BlacksiteMemberOrder order)
        {
            int index = CargoRole(soldier, out bool lever); order = default;
            if (index < 0) return false;
            var r = Records[index];
            if (lever)
            {
                Pose pose = Ship.MagnetLeverPose; Vector3 facing = pose.position + pose.forward; Ship.EnsureAccessServer();
                order = BlacksiteSquadPlanner.MakeOrder(BlacksiteSquadOrderKind.RecoverDrive, pose.position, facing,
                    new BlacksiteApproachProfile(CargoLoadStopDistance, CargoLoadReachDistance, facing, requiresFacing: true));
                return true;
            }
            if (r.Phase == BlacksiteObjectivePhase.Reserved) { order = PickupOrder(r, soldier); return true; }
            Vector3 goal = CargoDeliveryPoint(r);
            order = BlacksiteSquadPlanner.MakeOrder(r.ContainerCargo ? BlacksiteSquadOrderKind.RecoverDrive : BlacksiteSquadOrderKind.Extract,
                goal, r.Position, new BlacksiteApproachProfile(CargoLoadStopDistance, CargoLoadReachDistance, goal, requiresFacing: false));
            return true;
        }
        // The walk to a reserved pickup, with the action's own gate in the order: the prop's pickup ring, or #1305 G4
        // the cart's handle answer - its stand under the handle profile when Pose, else the answer's point; never the cart's row.
        private BlacksiteMemberOrder PickupOrder(BlacksiteObjectiveSnapshot r, BlacksiteSoldierAI soldier)
        {
            var cart = r.Kind == BlacksiteObjectiveKind.Payload ? Resolve(r.ObjectId)?.GetComponent<PayloadCartController>() : null;
            Vector3 goal; BlacksiteApproachProfile profile = default;
            if (cart == null) { goal = PickupApproach(r, soldier); profile = new BlacksiteApproachProfile(PickupStopDistance, PickupReachDistance, r.Position); }
            else if (TryHandleApproach(r, cart, soldier, out goal) == HandleApproach.Pose)
                profile = HandleProfile(cart.transform.position, IsHandleApproachRelocated(cart, goal));
            return BlacksiteSquadPlanner.MakeOrder(BlacksiteSquadOrderKind.RecoverDrive, goal, r.Position, profile);
        }
        // The loader dispatch: staged cargo walked INTO the ground crate or the crate on the deck (#874 Rend); a staged cart
        // is the magnet's. No clock (#874 Q4); in HOVER the cable lifts it all. #1293 R2: owed, Staged (Refresh) and free.
        private void TickRendezvousLoading()
        {
            if (Ship.IsHovering) return;
            for (int i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                if (r.Phase != BlacksiteObjectivePhase.Staged || (!Returning && !IsPrimaryObjective(r.Kind))) continue;
                var obj = Resolve(r.ObjectId);
                if (!CargoOwed(r, obj) || CrewHoldsObjective(r)) continue;
                // #948 S12 U3: and no loader is ever dispatched to one.
                if (BlacksiteHazardAwareness.IsProtectedEgg(obj)) continue;
                var cart = obj.GetComponent<PayloadCartController>();
                if (cart != null) { TickStagedCart(i, r, cart); continue; } // #839: a cart is never container cargo.
                if (!(Ship.ContainerOnGround || Ship.CargoRetained)) continue;
                // #874 (Rend): with the crate aboard an eligible delivery is walked to its deck slot.
                bool aboard = RetainedCargoDelivery(r);
                if (!aboard && Ship.CargoRetained) continue;
                // On top of CouldEverLoad: a guard called to a report or a player is not a loader.
                var loader = _members.Where(s => CouldEverLoad(s) && s.isOutside && !s.IsAboardShip && !s.ReplicatedStunned
                    && !GuardCalledIn(s.NetworkObjectId) && !HasPlayerResponse(s)
                    && !s.IsObjectiveCombatActive && !Snapshots.Any(other => other.CarrierId == s.NetworkObjectId))
                    .OrderBy(s => Vector3.Distance(s.transform.position, r.Position)).FirstOrDefault();
                if (loader == null) continue;
                r.CarrierId = loader.NetworkObjectId; r.Phase = BlacksiteObjectivePhase.Reserved;
                // Container cargo from the reservation onward: it anchors the original to the crate's slot once
                // seated, and TryCargoOrder reads it to send the loader to the pickup, then the delivery point.
                r.ContainerCargo = true;
                Records[i] = r;
                BeginAttempt(r, loader.NetworkObjectId); _pickupAt.Remove(r.ObjectId);
                BlacksiteDiagnostics.Log("Cargo", $"container loader member={loader.MemberIndex}, original={r.ObjectId}, attempt={_jobs[r.ObjectId].Generation}"
                    + (aboard ? ", walking the original aboard to the retained container." : "."));
            }
        }
        // #1305 H1/H4 (R24): a staged cart on a ground station. Docked: Loaded. Ruling 4: a refused dock approach
        // (TryCargoGround) writes it off at staging. Lever off: the elected puller at the lever pulls it. Ruling 3: lever
        // on, the cart's own closing on the dock is its progress fact; one monitor window without a new best writes it off.
        private void TickStagedCart(int index, BlacksiteObjectiveSnapshot r, PayloadCartController cart)
        {
            var ship = Ship;
            if (cart.IsOnBlacksiteMagnet(ship.NetworkObjectId)) { LoadCartRecord(index, cart, "onto the ship magnet"); return; }
            if (ship.DockApproachRefusal != null)
            { AbandonCargo(index, BlacksiteObjectiveCloseCause.NoCartRoute, "dock approach unproved at the station: " + ship.DockApproachRefusal); return; }
            if (!WithinLiftReach(r, cart.NetworkObject, out _)) return; // staging's own measure: the cart's live row
            if (!ship.LeverOn)
            {
                var puller = ship.IsGrounded ? Planner.LeverPuller() : null;
                if (puller == null || Vector3.Distance(puller.transform.position, ship.MagnetLeverPose.position) > CargoLoadReachDistance) return;
                ship.PullMagnetLeverServer();
                if (ship.LeverOn) BlacksiteDiagnostics.Log("Cargo", $"magnet lever pulled by member={puller.MemberIndex} for object={r.ObjectId}.");
                return;
            }
            if (!ObserveNow(r.ObjectId)) return;
            if (!_jobs.TryGetValue(r.ObjectId, out var job)) _jobs.Add(r.ObjectId, job = new BlacksiteRaidJob()); // never Job(r): it reopens an attempt
            job.Tick(Time.time, BlacksiteJobPhase.Load, Vector3.ProjectOnPlane(ship.DockPose.position - cart.MagnetPoint.position, Vector3.up).magnitude, gain: .05f);
            if (job.ObservesWithoutProgress >= BlacksiteMotionMonitor.WindowSeconds / ObserveSeconds)
                AbandonCargo(index, BlacksiteObjectiveCloseCause.NoCartRoute, $"magnet pull made no progress in {job.ObservesWithoutProgress} observes");
        }
        // One writer for "the cart is on the ship", whichever device put it there: the ship-local
        // pose is what LateUpdateReplica pins a Loaded cart to once nothing else holds it.
        private void LoadCartRecord(int index, PayloadCartController cart, string how)
        {
            var r = Records[index];
            r.Phase = BlacksiteObjectivePhase.Loaded; r.ContainerCargo = false; r.CarrierId = NoCarrier;
            r.CargoLocalPosition = Ship.transform.InverseTransformPoint(cart.transform.position);
            r.CargoLocalYaw = Mathf.DeltaAngle(Ship.transform.eulerAngles.y, cart.transform.eulerAngles.y);
            Records[index] = r;
            BlacksiteDiagnostics.Log("Cargo", $"loaded {r.Kind} object={r.ObjectId} {how}, local={r.CargoLocalPosition}, yaw={r.CargoLocalYaw:0.0}.");
            // #1271 S8: whichever device loaded it, the cart has no carrier to report it; any member does.
            BlacksiteSoldierAI caller = _members.FirstOrDefault(s => s != null);
            if (caller != null) caller.AnnounceCargo();
        }
        private int RecordIndex(ulong objectId)
        {
            for (int i = 0; i < Records.Count; i++) if (Records[i].ObjectId == objectId) return i;
            return -1;
        }
        /// <summary>
        /// #874 Q4 (Lawson's decision 2): the next item the HOVER cable lifts, nearest the rope foot
        /// first, any kind but a protected egg. #1293 R2: the one lifter's question - owed, Staged
        /// (within the lift reach Refresh measures) and free (no player hand or handle on it, no NPC
        /// on the cart).
        /// </summary>
        internal bool TryNextCableLift(Vector3 foot, out ulong objectId)
        {
            objectId = NoCarrier;
            float best = float.PositiveInfinity;
            for (int i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                if (r.Phase != BlacksiteObjectivePhase.Staged) continue;
                var obj = Resolve(r.ObjectId);
                if (!CargoOwed(r, obj) || CrewHoldsObjective(r) || BlacksiteHazardAwareness.IsProtectedEgg(obj)) continue;
                var cart = obj.GetComponent<PayloadCartController>();
                if (cart != null && cart.IsNpcAttached) continue;
                float distance = Vector3.Distance(obj.transform.position, foot);
                if (distance >= best) continue;
                best = distance; objectId = r.ObjectId;
            }
            return objectId != NoCarrier;
        }
        /// <summary>
        /// #874 Q4: the hook takes the item. A cart is claimed by the cable
        /// (<see cref="PayloadCartController.HoldOnBlacksiteCableServer"/>) and its record stays
        /// Staged until it stands on the deck - the claim is what Refresh's staged test already
        /// exempts from "moved away". A prop is Loaded at once, with no container: its anchor is
        /// the hook for the length of the trip (BlacksiteShipController.CargoAnchor), which is how
        /// every peer, a late joiner included, draws it on the cable from the replicated record.
        /// </summary>
        internal bool BeginCableLiftServer(ulong objectId, out int slot, out BlacksiteObjectiveKind kind)
        {
            slot = 0; kind = default;
            int index = RecordIndex(objectId);
            if (index < 0) return false;
            var r = Records[index];
            if (r.Phase != BlacksiteObjectivePhase.Staged) return false;
            var obj = Resolve(objectId); if (obj == null) return false;
            slot = r.Slot; kind = r.Kind;
            var cart = obj.GetComponent<PayloadCartController>();
            if (cart != null)
            {
                if (!cart.HoldOnBlacksiteCableServer(Ship)) return false;
            }
            else
            {
                if (obj.GetComponent<GrabbableObject>() == null) return false;
                r.Phase = BlacksiteObjectivePhase.Loaded; r.ContainerCargo = false; r.CarrierId = NoCarrier;
                Records[index] = r;
            }
            BlacksiteDiagnostics.Log("Cargo", $"hooked {r.Kind} object={objectId} on the ship's cable at {obj.transform.position}.");
            return true;
        }
        /// <summary>#874 Q4: the item is still the hook's: its record has not been taken back by a player or the world.</summary>
        internal bool CableLiftValid(ulong objectId)
        {
            int index = RecordIndex(objectId);
            if (index < 0) return false;
            var obj = Resolve(objectId); if (obj == null) return false;
            var cart = obj.GetComponent<PayloadCartController>();
            return cart != null
                ? cart.IsOnBlacksiteCable && Records[index].Phase == BlacksiteObjectivePhase.Staged
                : Records[index].Phase == BlacksiteObjectivePhase.Loaded && !PlayerHeld(obj.GetComponent<GrabbableObject>());
        }
        /// <summary>
        /// #874 Q4: the trip is over and the item stands aboard. A cart is seated on the deck
        /// exactly as the magnet seats it and its record becomes Loaded; a prop already is, and its
        /// anchor is the rack slot again now the hook has let go.
        /// </summary>
        internal void CompleteCableLiftServer(ulong objectId)
        {
            int index = RecordIndex(objectId);
            if (index < 0) return;
            var cart = Resolve(objectId)?.GetComponent<PayloadCartController>();
            if (cart != null)
            {
                if (cart.SeatOnBlacksiteDeckServer(Ship)) LoadCartRecord(index, cart, "up the ship's cable onto the deck");
                return;
            }
            BlacksiteDiagnostics.Log("Cargo", $"loaded {Records[index].Kind} object={objectId} up the ship's cable into its rack slot.");
        }
        /// <summary>
        /// #1293 R2: a member who could ever load staged cargo - alive, not boarded and not stranded
        /// (a stranded member's body has no navmesh to walk, so he cannot reach the pickup and is
        /// answered only on entry). The loader dispatch adds the availability of the moment to it;
        /// the write-off reads it alone.
        /// </summary>
        private static bool CouldEverLoad(BlacksiteSoldierAI s)
            => s != null && !s.isEnemyDead && !s.HasBoardedShip && !s.Navigator.IsStranded;
        /// <summary>
        /// #1293 R2: after the recall, owed cargo only a member can move - anything loose, or a prop
        /// staged for a ground ship's loader - is written off on the event that leaves it no possible
        /// hauler: no member who could ever load it (<see cref="CouldEverLoad"/>: alive, not boarded,
        /// not stranded; guards load too) and, for loose cargo, none who is also not a ship guard (#1305 G4:
        /// AssignReservations' owed-cargo set; withdrawing excludes nobody after the recall). The CargoLift step
        /// then ends on it instead of waiting for nobody. A staged cart or hover stage is the ship device's.
        /// </summary>
        private void WriteOffUnhauledCargo()
        {
            bool loader = false, hauler = false;
            foreach (var s in _members)
            {
                // Not the loader dispatch's moment terms (outside, on the deck, stunned, called in, in
                // combat, carrying another record): each ends on its own event - a member on the deck
                // unboarded is given Egress, a Deploy order off it (SquadPlanner ResolveIntent and its
                // Egress order) - and the write-off cannot be undone, so it never counts one.
                if (!CouldEverLoad(s)) continue;
                loader = true;
                if (!s.IsShipGuard) { hauler = true; break; }
            }
            if (hauler) return;
            for (int i = 0; i < Records.Count; i++)
            {
                var r = Records[i];
                bool unhauled = r.Phase == BlacksiteObjectivePhase.Available
                    || (!loader && r.Phase == BlacksiteObjectivePhase.Staged && r.Kind != BlacksiteObjectiveKind.Payload && !Ship.IsHovering);
                if (unhauled && CargoOwed(r)) AbandonCargo(i, BlacksiteObjectiveCloseCause.Abandoned, loader ? "no living member can be reserved to haul it" : "no living member can load it");
            }
        }
        /// <summary>
        /// The ONE write-off of owed cargo the squad cannot deliver (#874 Rend; #1293 R2: reached on
        /// the event that makes it so, never on a clock). Unavailable is what "abandoned" means to
        /// every reader the board already has: CargoOwed and RecordOutstanding stop counting it,
        /// CanReserve refuses it and Refresh returns on it. Nothing departs, teleports or is marked
        /// loaded: the attempt closes Failed and a mission objective is reported lost, once, under <paramref name="cause"/>.
        /// </summary>
        private void AbandonCargo(int index, BlacksiteObjectiveCloseCause cause, string reason)
        {
            var r = Records[index];
            var obj = Resolve(r.ObjectId);
            if (obj != null) r.Position = obj.transform.position;
            CloseAttempt(r, BlacksiteAttemptOutcome.Failed, cause);
            r.Phase = BlacksiteObjectivePhase.Unavailable; r.CarrierId = NoCarrier; r.ContainerCargo = false;
            Records[index] = r;
            _retryAt.Remove(r.ObjectId); _pickupAt.Remove(r.ObjectId);
            ReconcileDriveCarriers();
            if (ReArms(r.Kind)) NoteObjectiveLost(r, cause);
            BlacksiteDiagnostics.Log("Cargo", $"cargo abandoned: {r.Kind} object={r.ObjectId}, cause={cause}, reason={reason}; "
                + $"attempt closed Failed, record terminal at {r.Position}.");
        }
    }
}
