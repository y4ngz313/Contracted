// #1018 S4: the two order PRODUCERS, moved off the manifest verbatim (design note F.2).
//
// PlanDutyOrder came off …Controller.cs:615-702 and SupportOrSearchOrder off …Raid.cs:609-743, with
// MakeOrder, which both of them and every re-site use. They are the planner's because the planner is
// the one writer of a member's assignment and these are the rules that decide what it says; leaving
// them on the manifest kept a second entry point into planning, which is design note A.1's whole
// finding.
//
// Nothing here changes a rule while moving it. Every candidate, radius, clock and rejection test is
// the one that was already there; the only edits are the _manifest. qualifiers on the manifest
// members these rules still ask (the ship, the records, the approach resolvers, SupportPoint, the
// squad focus, the committed route) and the three places the board's own Duty state was read, which
// are now the planner's own.
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Contracts.Payload;
using BlacksiteFollow = Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteSoldierAI.BlacksiteNavigator.BlacksiteFollow;
using BlacksiteMotionOwner = Y4NGZCompany.Escalations.BlacksiteRetrieval.BlacksiteSoldierAI.BlacksiteNavigator.BlacksiteMotionOwner;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteObjectiveManifestController
    {
        internal sealed partial class BlacksiteSquadPlanner
        {
            // #874 P-S2: `follow` is the Follow the order is the goal of, or default when the order is
            // a plain destination. Only SupportOrSearchOrder produces one.
            private BlacksiteMemberOrder PlanDutyOrder(BlacksiteSoldierAI soldier, out BlacksiteFollowOrder follow)
            {
                follow = default;
                if(soldier.isOutside && _manifest.Ship.ContainsCargo(soldier.transform.position))_manifest.Ship.EnsureAccessServer();
                // #1305 G2 (R16): a crossing order's goal is the far side of the squad's door (CrossingFor); the route crosses.
                Vector3 point = CrossingFor(soldier); var kind = BlacksiteSquadOrderKind.Infiltrate; bool op = false;
                // #874 S1.2: an order that ends in an interaction carries that action's reach contract.
                BlacksiteApproachProfile approach = default;
                if (_manifest.TryCargoOrder(soldier, out var cargoOrder)) return cargoOrder; // #1305 H4 (R24): its record's order
                if (_manifest.Returning || soldier.IsRaidWithdrawing) { point = _manifest.BoardingApproach(); kind = BlacksiteSquadOrderKind.Board; }
                else if (soldier.IsShipGuard)
                {
                    point = soldier.isOutside ? _manifest.GuardPatrolPoint(soldier) : CrossingFor(soldier);
                    kind = soldier.isOutside ? BlacksiteSquadOrderKind.Hold : BlacksiteSquadOrderKind.Extract;
                }
                else
                {
                    bool assigned = false;
                    foreach (var r in _manifest.Records) if (r.CarrierId == soldier.NetworkObjectId)
                    {
                        assigned = true; op = true;
                        bool objectiveOutside = _manifest.ObjectiveWorld(r);
                        if (r.Phase == BlacksiteObjectivePhase.Carried && r.Kind == BlacksiteObjectiveKind.Witness)
                        {
                            // #964 S14 U2: the escort walks the witness INSIDE the ship. There is no
                            // staging release for him and nothing to load him into, so the goal is the
                            // deck's boarding point - reached over the ship's board link, whose completed
                            // crossing boards the escort and loads the witness (#1219 C2) - and the order
                            // carries the custody reach contract rather than the cargo one.
                            // #1233 S3 (rule 2): the goal is the deck point in every world; from inside
                            // the route crosses on the way (#1305 G2).
                            point = _manifest.Ship.BoardingPosition;
                            kind = BlacksiteSquadOrderKind.Extract;
                            approach = CustodyProfile(point);
                        }
                        else if (r.Phase == BlacksiteObjectivePhase.Carried)
                        {
                            // #874 (2026-09-22, Rend): the delivery POINT is the manifest's, not
                            // the ship's, because a crate that never reached the ground is loaded
                            // on the deck and a carrier walked to the rendezvous point instead
                            // would put the round's objective on the moon. CargoDeliveryPoint
                            // answers CargoLoadingPosition for every other case. #1233 S3: in every
                            // world; from inside the route crosses on the way (#1305 G2).
                            point = _manifest.CargoDeliveryPoint(r);
                            kind = BlacksiteSquadOrderKind.Extract;
                            // TryStageOrLoad releases the cargo within CargoLoadReachDistance of this
                            // point, and only for a carrier that is outside.
                            approach = new BlacksiteApproachProfile(CargoLoadStopDistance, CargoLoadReachDistance, point, requiresFacing: false);
                        }
                        else if (soldier.isOutside != objectiveOutside)
                        { point = CrossingFor(soldier); kind = soldier.isOutside ? BlacksiteSquadOrderKind.Infiltrate : BlacksiteSquadOrderKind.Extract; }
                        else
                        {
                            point = _manifest.PickupApproach(r, soldier); kind = r.Kind == BlacksiteObjectiveKind.ShadowDrive ? BlacksiteSquadOrderKind.ApproachObjective : BlacksiteSquadOrderKind.RecoverDrive;
                            // #1283 S6: an operator whose stand is refused from afar has no point to walk to; it takes
                            // the squad's order (the route search), and the next published stand (Refresh, every server
                            // tick) brings it back. Asked after PickupApproach, so the first measurement already diverts;
                            // its own-feet answer and cause are dropped. A local refusal keeps both and the probe close.
                            if (_manifest.StandRefusedFromAfar(r, soldier))
                            { _manifest._planningBlocked.Remove(soldier.NetworkObjectId); return SupportOrSearchOrder(soldier, out follow); }
                            var cart = r.Kind == BlacksiteObjectiveKind.Payload ? Resolve(r.ObjectId)?.GetComponent<PayloadCartController>() : null;
                            if (cart != null)
                            {
                                // #874 (2026-09-20): the operator faces the cart to TAKE THE HANDLE,
                                // and for nothing else. The old reason - "UpdateNpcHaul drives it
                                // from his facing" - is gone with the follow-point drive: the cart's
                                // coupled step now takes its heading from the route and carries the
                                // operator with it, so his facing no longer steers anything. What
                                // still needs it is the acquisition gate, which asks IsFacing(cart,
                                // 45) before the attach, so the approach must deliver him pointed at
                                // the thing he is about to grab. An unvalidated handle keeps the
                                // default stand-off instead of being driven at under a reach contract
                                // that cannot be satisfied.
                                // #1078: a point the ring had to relocate off the handle carries the
                                // relocated profile, whose tighter stop is what keeps
                                // point-to-handle + arrival floor inside the attach gate. See
                                // BlacksiteObjectiveManifestController.HandleProfile.
                                if (_manifest.TryHandleApproach(r, cart, soldier, out point) == HandleApproach.Pose) // #1305 G4: never the cart's row
                                    approach = HandleProfile(cart.transform.position, IsHandleApproachRelocated(cart, point));
                            }
                            // #874 S4.2: the pickup gate is 0.8 m and the movement default is 1.1 m, so
                            // this order must always carry its reach contract - not only inside six
                            // metres. Under the old window a carrier further out was ordered to a goal
                            // whose gate nothing in movement had been told about, and the duty board
                            // then declared it arrived at 1.8 m; that is member 1's 31.5 s park at
                            // remaining=1.08 in the 2026-09-09 run. The six-metre test is retained for
                            // the operator lane alone, which is a crowding decision about the last few
                            // metres and not part of the reach contract.
                            // #874 S6.2: while the drive is still sealed the order does not end in a
                            // pickup at all, it ends in the container override, so it carries THAT
                            // action's contract and faces the lock rather than the object behind it.
                            // The operator lane test is unchanged: it is a crowding decision about the
                            // last few metres, not part of the reach contract.
                            else if (soldier.isOutside == objectiveOutside
                                && BlacksiteObjectiveApproach.TryResolveBarrier(_manifest._mission, r.Kind, out var lockAnchor, out _))
                                approach = new BlacksiteApproachProfile(ContainerLockStopDistance, ContainerLockReachDistance, lockAnchor,
                                    operatorLane: Vector3.Distance(soldier.transform.position, lockAnchor) <= 6f);
                            // #964 S14 U2: the reserved leg of a witness ends in a custody take, not in
                            // a pickup, so it carries the take's own gate (1.20 m body to body) rather
                            // than the item reach (0.80 m). The facing point is the witness himself and
                            // the profile does not require facing: an escort may take a man from any
                            // side, and _manifest.PickupApproach has already published the witness's live
                            // position as the goal, so the stand-off is measured to where he is now.
                            else if (r.Kind == BlacksiteObjectiveKind.Witness && soldier.isOutside == objectiveOutside)
                                approach = CustodyProfile(point);
                            else if (soldier.isOutside == objectiveOutside)
                                approach = new BlacksiteApproachProfile(PickupStopDistance, PickupReachDistance, r.Position,
                                    operatorLane: Vector3.Distance(soldier.transform.position, r.Position) <= 6f);
                        }
                        break;
                    }
                    // #1233 S2 (rule 1): outside and intending the interior by RequiresInterior - the
                    // entrance gate's own predicate - is the Infiltrate below; every other mission
                    // member works where SupportOrSearchOrder sends it, whose crossings go only toward
                    // that same world.
                    if (!assigned && (!soldier.isOutside || !_manifest.RequiresInterior(soldier)))
                        return SupportOrSearchOrder(soldier, out follow);
                }
                if ((kind == BlacksiteSquadOrderKind.Board || kind == BlacksiteSquadOrderKind.Extract) && soldier.isOutside) _manifest.Ship.EnsureAccessServer();
                var order = new BlacksiteSquadOrder(0, kind, BlacksiteFormationKind.SpreadSecurity, point, point - soldier.transform.position);
                // Arrival tolerance follows the action's own gate rather than a generic 45 cm radius.
                // #1219 E2: a withdrawal's Board is the one order here that declares Urgent.
                var memberOrder = new BlacksiteMemberOrder(order, point, approach.HasValue ? approach.FacingPoint : point,
                    1.2f, approach.HasValue ? approach.ReachRadius : .45f, op, approach);
                return kind == BlacksiteSquadOrderKind.Board && soldier.IsRaidWithdrawing
                    ? memberOrder.WithPace(BlacksiteOrderPace.Urgent) : memberOrder;
            }

            private BlacksiteMemberOrder SupportOrSearchOrder(BlacksiteSoldierAI soldier, out BlacksiteFollowOrder follow)
            {
                follow = default;
                // #1293 R1: the alert is the squad's newest threat row this member does not see itself.
                if (soldier.TryGetWorld(out var squadWorld) && squadWorld.TryGetAlert(soldier.PerceptionSlot, out var alert)
                    && Mathf.Abs(alert.Position.y - soldier.transform.position.y) < 3f
                    && Vector3.Distance(alert.Position, soldier.transform.position) < 18f
                    && _manifest._members.Any(m => m != soldier && m.isOutside == soldier.isOutside && m.IsObjectiveCombatActive
                        && Vector3.Distance(m.transform.position, soldier.transform.position) < 18f))
                {
                    Vector3 contact = alert.Position;
                    Vector3 near = contact + Vector3.ProjectOnPlane(soldier.transform.position-contact, Vector3.up).normalized * 4f;
                    if (NavMesh.SamplePosition(near, out var nav, 1f, NavMesh.AllAreas)
                        && BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(soldier), soldier.transform.position, nav.position).Reachable)
                        return MakeOrder(BlacksiteSquadOrderKind.SearchLastKnown, nav.position, contact);
                }
                // #874 S6.4b: the anchor is a POINT with an optional owner, not the member holding a
                // live reservation. While a non-Loot record is attainable the squad stages on it (on
                // ITS storey) through every backoff and bench window; the frontier is only what runs
                // for what is genuinely unknown.
                var focus = _manifest.ResolveSquadFocus(soldier, out var record, out bool hasRecord, out var holder);
                ulong focusObject = hasRecord ? record.ObjectId : NoCarrier;
                var kind = focus.Kind;
                // #950 A5 (O1): the second leader election gains the same storey term as RouteLead. A
                // candidate more than BlacksiteResponsePolicy.OffStoreyMetres above or below this member
                // is on another floor of the mineshaft and cannot be anchored on; the run spent 34 s
                // with m1 at the bottom taking its formation from m0 at the top.
                // #1283 S6: a Carrier focus reads the holder only for the Follow; a holder without a route leads nobody (#1271 S2).
                var lead = LocalLead(soldier, kind == BlacksiteSquadFocusKind.Carrier ? holder : null);
                if (lead == null)
                {
                    _manifest.NoteSquadFocus(soldier, new BlacksiteSquadFocus(BlacksiteSquadFocusKind.None, "no member remains to anchor on"), focusObject);
                    Vector3 boarding = _manifest.BoardingApproach();
                    return MakeOrder(BlacksiteSquadOrderKind.Board, boarding, boarding);
                }
                _manifest.NoteSquadFocus(soldier, focus, focusObject);
                // #1233 S2 (rule 1): the objective crossing below takes this member into the other
                // world, so it is produced only while that world is its own by RequiresInterior - the
                // mission world, which is the objective's - the predicate the entrance gate admits
                // by. An order never walks a member to a door the gate refuses.
                bool crossable = soldier.isOutside == _manifest.RequiresInterior(soldier);
                if (kind == BlacksiteSquadFocusKind.Objective || kind == BlacksiteSquadFocusKind.Carrier)
                {
                    // A helper on the wrong side of the boundary takes the crossing, the same goal
                    // PlanDutyOrder gives an operator in that position (CrossingFor, #1305 G2).
                    // For the OBJECTIVE only: a carrier focus follows its holder, whose world is the
                    // objective's own.
                    if (kind == BlacksiteSquadFocusKind.Objective && crossable)
                    {
                        Vector3 crossing = CrossingFor(soldier);
                        return MakeOrder(soldier.isOutside ? BlacksiteSquadOrderKind.Infiltrate : BlacksiteSquadOrderKind.Extract, crossing, crossing);
                    }
                    // #1233 S3 (rule 2): a Carrier focus IS a Follow of its live holder, in any world
                    // and on any storey - the goal is the anchor, re-derived every tick, and a door or
                    // a car the holder took is only this member's leg to it (the route's own
                    // transition, #1305 G2). Round 3 of
                    // 2026-09-27 (1308-1311) demoted a helper "closer" than the operator to a local
                    // search, and an off-storey holder to a frozen SearchLastKnown point. #1271 S2: the
                    // follow lasts while the holder's attempt is open and it has a route; a holder
                    // without one leads nobody, and this member stages on the record instead.
                    bool carried = record.Phase == BlacksiteObjectivePhase.Carried;
                    int holderAttempt = 0;
                    bool follows = kind == BlacksiteSquadFocusKind.Carrier
                        && FollowTargetFor(soldier, holder, carried, out holderAttempt) == BlacksiteFollowTarget.Anchor;
                    // #1271 S2: the slot is the trail point on the anchor-to-member route, which may lie on the member's side of the anchor.
                    if (follows) return FollowOrder(soldier, holder, carried ? "carrier" : "operator", holderAttempt, out follow);
                    Vector3 anchor = record.Position;
                    if (record.Kind == BlacksiteObjectiveKind.Payload)
                    {
                        var cart = Resolve(record.ObjectId)?.GetComponent<Y4NGZCompany.Contracts.Payload.PayloadCartController>();
                        if (cart != null) anchor = cart.NpcHandlePosition;
                    }
                    // floorY is the ANCHOR's height, so a candidate is judged against the objective's
                    // storey - off it, the interaction coordinator's elevator task is the route.
                    // Ordinary order destinations, so the navigator's ClaimDestination (S4.3),
                    // _manifest.SupportPoint's own separation reservation and the operator lane (S1.2) all apply.
                    // #874 Q2: an escort is never sent to the object itself; round 1 sent all three
                    // escorts onto the apparatus (log 11720, 11754, 11892), where their claims displaced
                    // the holder's reach point. #1283 S6: only a ring slot (each on a COMPLETE route)
                    // stages an escort. No ring slot - or one TryOffObject cannot clear - is the squad
                    // route below: never the member's own feet and never a Partial walk to the anchor,
                    // the rule pass 5 gave the operator.
                    Vector3 staged = _manifest.SupportPoint(soldier, anchor, anchor - soldier.transform.position, anchor.y, out var support);
                    if (support == BlacksiteSupportFallback.Ring && TryOffObject(staged, record.Position, soldier.transform.position, out staged))
                        return MakeOrder(BlacksiteSquadOrderKind.SearchLastKnown, staged, anchor);
                    // #1283 S6: the refused ring is no refusal of the route order this member takes instead.
                    _manifest._planningBlocked.Remove(soldier.NetworkObjectId);
                }
                // #955 S12A U2: the lead walks the held route goal (#1305 S: HoldLeadGoal), SearchGoal while
                // none is held. The stall clock is the duty budget in either case, untouched here.
                bool routed = _manifest.TickSquadRoute(lead);
                Vector3 transit = routed ? _manifest._routeLeadGoal : SearchGoal(lead);
                if (lead != soldier && transit != Vector3.zero
                    && Mathf.Abs(transit.y - soldier.transform.position.y) > 4f)
                    return MakeOrder(BlacksiteSquadOrderKind.SearchLastKnown, transit, transit);
                Vector3 destination;
                if (lead == soldier)
                {
                    destination = transit;
                    // Let the nearby rear pair catch up before entering another room. Do not wait
                    // indefinitely for separated, fighting or incapacitated members.
                    bool separated = _manifest._members.Any(s => !s.IsShipGuard && !s.IsRaidWithdrawing && s.isOutside == soldier.isOutside
                        && !s.ReplicatedStunned && s != lead && Vector3.Distance(s.transform.position, lead.transform.position) > 14f);
                    int world = soldier.isOutside ? 1 : 0;
                    // #957 S12A U4, IN #955's REGION: this hunk is the orchestrator's to accept, move
                    // or refuse; it is the only edit U4 makes outside its own ownership table and it is
                    // what R2's five-second bound needs. The regroup wait exists to let the
                    // rear pair catch up BEFORE entering another room. It must not pin a lead standing
                    // in a room the committed route has already left, because there the rear guard is
                    // holding its place in the entrance column of the very doorway the squad is about
                    // to leave by: it is far away BECAUSE it is doing what it was told, and the lead
                    // waits out the whole six seconds for a man who is waiting for it. A tile with one
                    // doorway is not "another room to enter"; leaving it is the only move there is.
                    bool leavingSpentRoom = routed && _manifest.RoutePlanner != null
                        && _manifest.RoutePlanner.CurrentTile >= 0 && _manifest.RoutePlanner.NextRoom >= 0
                        && _manifest.RoutePlanner.NextRoom != _manifest.RoutePlanner.CurrentTile
                        && _manifest.RoutePlanner.DoorwayTargets(_manifest.RoutePlanner.CurrentTile).Count <= 1;
                    if (!separated || leavingSpentRoom) _manifest._regroupUntil[world] = 0f;
                    else if (_manifest._regroupUntil[world] == 0f) _manifest._regroupUntil[world] = Time.time + 6f;
                    if (separated && !leavingSpentRoom && Time.time < _manifest._regroupUntil[world])
                    {
                        destination = lead.transform.position;
                        // #1293 R3: the hold's own end revises the lead's goal.
                        if (_duties.TryGetValue(soldier.NetworkObjectId, out var holding))
                            holding.ReviseAt = _manifest._regroupUntil[world];
                    }
                }
                else if (_manifest.TryRouteSlotOrder(soldier, lead, transit, out var slotOrder))
                    return slotOrder;
                else if (FollowTargetFor(soldier, lead, false, out int leadAttempt) == BlacksiteFollowTarget.Anchor)
                {
                    // #874 P-S2: formation on the lead is a Follow of the lead, re-derived from where
                    // the lead is on every path refresh instead of a support slot re-sampled around it.
                    return FollowOrder(soldier, lead, "member", leadAttempt, out follow);
                }
                else
                {
                    // #1271 S2 (rule 2): a lead with no open attempt or no route leads nobody, so a standing member searches on
                    // its own (2026-09-29 round 2, Player.log 38553-38618); #1305 S: a moving one keeps his goal, else the lead's.
                    destination = BlacksiteSquadPlannerRules.MovingFollowerGoal(soldier.GetThreatVelocity(), WalkingGoal(soldier),
                        TryGetTaskGoal(lead, out Vector3 leadGoal) ? leadGoal : transit) ?? SearchGoal(soldier);
                }
                return MakeOrder(BlacksiteSquadOrderKind.SearchLastKnown, destination, destination + lead.transform.forward * 5f);
            }

            /// <summary>
            /// The storey-local election: the lowest-index member in this member's world and on its
            /// storey that is not guarding, withdrawing, stunned, boarded or stranded (possibly this member),
            /// other than <paramref name="except"/>. #1305 G5 (D17): never the holder or escort for another member.
            /// #950 A5 (O1): a candidate more than BlacksiteResponsePolicy.OffStoreyMetres above or
            /// below is on another floor of the mineshaft and cannot be anchored on.
            /// </summary>
            private BlacksiteSoldierAI LocalLead(BlacksiteSoldierAI soldier, BlacksiteSoldierAI except)
                => _manifest._members.Where(s => s != except && !s.IsShipGuard && !s.IsRaidWithdrawing && !s.ReplicatedStunned && !s.HasBoardedShip && s.isOutside == soldier.isOutside
                        && !s.Navigator.IsStranded && !BlacksiteResponsePolicy.OffStorey(s.transform.position.y, soldier.transform.position.y)
                        && (s == soldier || !_manifest.HasOwnJob(s)))
                    .OrderBy(s => s.MemberIndex).FirstOrDefault();

            // ---------------------------------------------------------------------------------
            // #874 P-S2: the Follow producers.
            // ---------------------------------------------------------------------------------

            /// <summary>
            /// #874 P-S2: what a Follow assignment is, beside the order it projects to. The navigator
            /// walks <see cref="Follow"/>; the order's destination is the anchor's live slot
            /// (<see cref="FollowGoal"/>), re-derived on every refresh. Stamped with the assignment's
            /// sequence at issue, so it is read only while that assignment stands, and (#1271 S2) with
            /// the anchor's duty attempt, so it expires with that attempt.
            /// </summary>
            internal readonly struct BlacksiteFollowOrder
            {
                internal readonly BlacksiteFollowTarget Target;
                internal readonly BlacksiteFollow Follow;
                /// <summary>Who is followed. Held by reference so the per-tick liveness read costs no lookup.</summary>
                internal readonly BlacksiteSoldierAI Anchor;
                internal readonly ulong AnchorId;
                /// <summary>The anchor carries the objective this follow escorts.</summary>
                internal readonly bool CarriesObjective;
                /// <summary>#1271 S2: the anchor's duty attempt (its Budget.Generation) this follow was issued under.</summary>
                internal readonly int AnchorAttempt;
                internal readonly int Sequence;

                internal BlacksiteFollowOrder(BlacksiteFollowTarget target, BlacksiteFollow follow, BlacksiteSoldierAI anchor,
                    bool carriesObjective, int anchorAttempt, int sequence = 0)
                {
                    Target = target; Follow = follow; Anchor = anchor; AnchorId = anchor != null ? anchor.NetworkObjectId : 0UL;
                    CarriesObjective = carriesObjective; AnchorAttempt = anchorAttempt; Sequence = sequence;
                }

                internal bool IsFollow => Target != BlacksiteFollowTarget.None;

                internal BlacksiteFollowOrder WithSequence(int sequence)
                    => new BlacksiteFollowOrder(Target, Follow, Anchor, CarriesObjective, AnchorAttempt, sequence);

                /// <summary>The same anchor carrying the same thing, under the same attempt.</summary>
                internal bool SameAs(in BlacksiteFollowOrder other)
                    => IsFollow && Target == other.Target && AnchorId == other.AnchorId
                       && CarriesObjective == other.CarriesObjective && AnchorAttempt == other.AnchorAttempt;
            }

            /// <summary>
            /// #874 P-S2 / #1271 S2: <see cref="BlacksiteSquadPlannerRules.FollowTarget"/> with the world
            /// read in. #874 Q2: an anchor is live by the motion owner's own rule
            /// (<see cref="BlacksiteMotionPolicy.FollowEndReason"/>, the world left out: another world
            /// is the executor's link leg, #1233 S3), so the follow the owner ends - the anchor dead,
            /// seated, boarding, or no longer carrying what it was escorted for - is re-planned in the
            /// same think. Its attempt is its duty's open one, answered as <paramref name="anchorAttempt"/>
            /// (0 when none), and its route is its navigator's (BlacksiteNavigator.HasRoute) while that duty is not blocked.
            /// </summary>
            private BlacksiteFollowTarget FollowTargetFor(BlacksiteSoldierAI soldier, BlacksiteSoldierAI anchor,
                bool carriesObjective, out int anchorAttempt)
            {
                bool live = anchor != null && anchor != soldier && !anchor.HasLostTransport
                    && BlacksiteMotionPolicy.FollowEndReason(true, anchor.isEnemyDead, true,
                        BlacksiteMotionOwner.AnchorBoarding(anchor), anchor.HasBoardedShip, carriesObjective,
                        !carriesObjective || anchor.IsObjectiveCarrier) == null;
                Duty anchorDuty = null;
                bool attemptOpen = live && _duties.TryGetValue(anchor.NetworkObjectId, out anchorDuty) && anchorDuty.Budget.AttemptOpen;
                anchorAttempt = attemptOpen ? anchorDuty.Budget.Generation : 0;
                return BlacksiteSquadPlannerRules.FollowTarget(live, attemptOpen, live && anchor.Navigator != null
                    && BlacksiteSquadPlannerRules.AnchorRouted(anchor.Navigator.HasRoute, anchorDuty?.Blocked ?? BlacksiteBlockedCause.None),
                    Leashed(soldier));
            }

            /// <summary>
            /// #874 P-S2: the Guard leash, as the follow rule reads it - the same test
            /// <see cref="ShapeDutyOrder"/> applies (<see cref="BlacksiteDeploymentPolicy.LeashedGoal"/>):
            /// an outside ship guard on Guard duty. The executor walks a follow without the leash, so a
            /// leashed member is never given one; it is what keeps that guard from following a lead or
            /// a carrier inside.
            /// </summary>
            private bool Leashed(BlacksiteSoldierAI soldier)
                => soldier.IsShipGuard && soldier.isOutside
                   && BlacksiteDeploymentPolicy.LeashesDuty(ResolveIntent(soldier).Kind);

            /// <summary>
            /// #874 P-S2: a Follow of <paramref name="anchor"/>. A follow already held on that anchor
            /// under the same attempt (#1271 S2) stands as it is - its refresh re-derives the goal.
            /// #874 Q2: a new one names its rank among the anchor's followers and nothing else; the
            /// motion owner derives the slot on the route behind the anchor.
            /// </summary>
            private BlacksiteMemberOrder FollowOrder(BlacksiteSoldierAI soldier, BlacksiteSoldierAI anchor,
                string role, int anchorAttempt, out BlacksiteFollowOrder follow)
            {
                ulong id = soldier.NetworkObjectId;
                bool carries = role == "carrier";
                if (_duties.TryGetValue(id, out var duty) && FollowHeld(duty) && !duty.Retry
                    && duty.Follow.AnchorId == anchor.NetworkObjectId && duty.Follow.CarriesObjective == carries
                    && duty.Follow.AnchorAttempt == anchorAttempt)
                {
                    follow = duty.Follow;
                    return duty.Order;
                }
                // The follow IS the answer for a live anchor, so no support refusal is carried into
                // the assignment it becomes.
                _manifest._planningBlocked.Remove(id);
                int rank = FollowRank(soldier, anchor);
                var motion = new BlacksiteFollow(anchor, rank, BlacksiteMotionPolicy.FollowBandMetres,
                    role + ":" + anchor.MemberIndex, carries);
                follow = new BlacksiteFollowOrder(BlacksiteFollowTarget.Anchor, motion, anchor, carries, anchorAttempt);
                return FollowGoal(soldier, anchor, rank);
            }

            /// <summary>
            /// #1233 S3 (rule 2): where the follower of <paramref name="anchor"/> at
            /// <paramref name="rank"/> stands NOW. #1271 S2: its slot on the route behind the anchor,
            /// the motion owner's one derivation (BlacksiteMotionOwner.FollowGoalFor). Asked on issue,
            /// on every refresh and by <see cref="TryGetTaskGoal"/>, so the goal the planner reports is
            /// where the anchor is, never where it stood (round 3, 1047.63-1052.75: an unchanged goal
            /// re-reported on every expiry while the anchor rode down).
            /// </summary>
            private static BlacksiteMemberOrder FollowGoal(BlacksiteSoldierAI soldier, BlacksiteSoldierAI anchor, int rank)
            {
                Vector3 goal = soldier.Navigator.Motion.FollowGoalFor(anchor, rank);
                return MakeOrder(BlacksiteSquadOrderKind.SearchLastKnown, goal,
                    goal + BlacksiteFollow.Heading(anchor.transform.forward) * 5f);
            }

            /// <summary>
            /// #874 Q2: a follower's place among the members that can follow this anchor - live, not
            /// guarding the ship, in its world - by member index, so no two followers of one anchor
            /// share a trail distance whichever of them plans first.
            /// </summary>
            private int FollowRank(BlacksiteSoldierAI soldier, BlacksiteSoldierAI anchor)
            {
                int rank = 0;
                foreach (var member in _manifest._members)
                    if (member != null && member != soldier && member != anchor && !member.isEnemyDead
                        && !member.IsShipGuard && !member.HasBoardedShip && member.isOutside == soldier.isOutside
                        && member.MemberIndex < soldier.MemberIndex)
                        rank++;
                return rank;
            }

            /// <summary>
            /// #874 Q2: <paramref name="staged"/>, moved out to
            /// <see cref="BlacksiteMotionPolicy.EscortApproachClearMetres"/> from the object when it
            /// lies inside that - along its own bearing from the object first, then this member's,
            /// then the rest of a six-spoke ring from it - onto standable floor. False when no spoke
            /// stands: the escort is never handed a point on or beside the object instead.
            /// </summary>
            private static bool TryOffObject(Vector3 staged, Vector3 objectPosition, Vector3 member, out Vector3 point)
            {
                const float Clear = BlacksiteMotionPolicy.EscortApproachClearMetres;
                point = staged;
                Vector3 away = Vector3.ProjectOnPlane(staged - objectPosition, Vector3.up);
                if (away.magnitude >= Clear) return true;
                Vector3 mine = Vector3.ProjectOnPlane(member - objectPosition, Vector3.up);
                if (mine.sqrMagnitude < .01f) mine = away.sqrMagnitude >= .01f ? away : Vector3.forward;
                for (int spoke = -1; spoke < 6; spoke++)
                {
                    Vector3 bearing = spoke < 0 ? away : Quaternion.Euler(0f, 60f * spoke, 0f) * mine;
                    if (bearing.sqrMagnitude < .01f) continue;
                    Vector3 candidate = objectPosition + bearing.normalized * (Clear + BlacksiteArrivalPolicy.AgentRadius);
                    if (NavMesh.SamplePosition(candidate, out var hit, BlacksiteArrivalPolicy.AgentRadius, NavMesh.AllAreas)
                        && Vector3.ProjectOnPlane(hit.position - objectPosition, Vector3.up).magnitude >= Clear)
                    {
                        point = hit.position;
                        return true;
                    }
                }
                return false;
            }

            /// <summary>
            /// #1233 S3 (rule 2): the ONE link resolver - where a member whose goal is in the other
            /// world stands in its own (<see cref="TryGetTaskGoal"/>). For a held Follow whose anchor's last crossing left
            /// from this member's world, that crossing (<see cref="NoteMemberCrossing"/>); otherwise
            /// the director's chosen door in this member's world. A read of existing state; #1305 G2:
            /// the route, not this, picks the doorway a member crosses by.
            /// </summary>
            internal bool TryGetCrossingLink(BlacksiteSoldierAI member, out Vector3 nearSide, out int entranceId)
            {
                nearSide = Vector3.zero; entranceId = -1;
                var director = _manifest._mission != null ? _manifest._mission.SquadDirector : null;
                if (member == null || director == null) return false;
                if (_duties.TryGetValue(member.NetworkObjectId, out var duty) && FollowHeld(duty)
                    && _duties.TryGetValue(duty.Follow.AnchorId, out var anchorDuty)
                    && BlacksiteSquadPlannerRules.UsesAnchorCrossing(anchorDuty.HasCrossing,
                        anchorDuty.CrossedFromOutside == member.isOutside))
                {
                    nearSide = anchorDuty.CrossedFrom;
                    entranceId = anchorDuty.CrossedEntranceId;
                    return true;
                }
                nearSide = member.isOutside ? director.OutsideMainEntrance : director.InsideMainEntrance;
                entranceId = director.ChosenEntranceId;
                return true;
            }

            /// <summary>#874 P-S2: the Follow this duty holds is the one its live assignment was issued with.</summary>
            private static bool FollowHeld(Duty duty)
                => duty.Follow.IsFollow && duty.Assignment.IsValid && duty.Follow.Sequence == duty.Assignment.Sequence;

            /// <summary>
            /// #874 P-S2: the one read of a member's Follow, for the order executor. An expired or
            /// replaced assignment answers false, exactly as <see cref="TryGetAssignment"/> does.
            /// </summary>
            internal bool TryGetFollowOrder(ulong member, out BlacksiteFollowOrder follow)
            {
                follow = default;
                if (!_duties.TryGetValue(member, out var duty) || !FollowHeld(duty)) return false;
                if (!TryGetAssignment(member, out var assignment) || assignment.Sequence != duty.Follow.Sequence) return false;
                follow = duty.Follow;
                return true;
            }

            /// <summary>
            /// #874 P-S2 / #1219 B2: THE crossing callback - this member cleared a paired entrance
            /// into the other world (<c>TransferThroughEntranceServer</c> with a real world change;
            /// an internal teleport never reaches here). It bumps the member's crossing count
            /// (<see cref="CrossingCount"/>) and records the door on the member's own plan state, the
            /// link <see cref="TryGetCrossingLink"/> reads for its followers. Only the latest door is kept, and it
            /// goes with the duty on release. #1233 S2: nothing else is counted here - who may cross
            /// next is read from the objective's live world, not from past crossings. #1293 R3: the
            /// crossing marks the member.
            /// </summary>
            internal void NoteMemberCrossing(BlacksiteSoldierAI soldier, Vector3 nearSide, int entranceId, bool fromOutside)
            {
                if (soldier == null) return;
                ulong id = soldier.NetworkObjectId;
                _dirty.Mark(id);
                _crossings[id] = CrossingCount(id) + 1;
                if (_duties.TryGetValue(id, out var duty))
                {
                    duty.HasCrossing = true;
                    duty.CrossedFrom = nearSide;
                    duty.CrossedEntranceId = entranceId;
                    duty.CrossedFromOutside = fromOutside;
                }
            }

            // #1305 H4 (R24): the one lever puller for an owed cart: a free, outside member able to load (a ship guard too;
            // he ends on the deck), not a holder or the escort. The incumbent keeps it, else the nearest to the lever.
            internal BlacksiteSoldierAI LeverPuller()
            {
                var ship = _manifest.Ship; BlacksiteSoldierAI nearest = null;
                float best = float.PositiveInfinity; Vector3? lever = null;
                foreach (var s in _manifest._members)
                {
                    if (!CouldEverLoad(s) || !s.isOutside || s.ReplicatedStunned || _manifest.GuardCalledIn(s.NetworkObjectId)
                        || _manifest.HasPlayerResponse(s) || s.IsObjectiveCombatActive || _manifest.HoldsAnyRecord(s.NetworkObjectId)
                        || _manifest.HasOwnJob(s) || (!_manifest.Returning && s.IsRaidWithdrawing)) continue;
                    if (_duties.TryGetValue(s.NetworkObjectId, out var duty) && duty.Intent.Kind == BlacksiteJobKind.Objective
                        && duty.Intent.Object == ship.NetworkObjectId) return s;
                    lever = lever ?? ship.MagnetLeverPose.position;
                    float distance = (s.transform.position - lever.Value).sqrMagnitude;
                    if (distance < best) { best = distance; nearest = s; }
                }
                return nearest;
            }

            internal static BlacksiteMemberOrder MakeOrder(BlacksiteSquadOrderKind kind, Vector3 point, Vector3 facing,
                BlacksiteApproachProfile approach = default)
            {
                var order = new BlacksiteSquadOrder(0, kind, BlacksiteFormationKind.SpreadSecurity, point, facing-point);
                return new BlacksiteMemberOrder(order, point, facing, 1.1f, approach.HasValue ? approach.ReachRadius : .65f,
                    approach.HasValue, approach);
            }
        }
    }
}
