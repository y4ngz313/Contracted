using System;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        internal sealed partial class BlacksiteNavigator
        {
            // #991, #874 S2/S3 (rule 2): the off-mesh link, crossed the way vanilla crosses one.
            // ConfigureAgent turns Unity's own traversal off (autoTraverseOffMeshLink) only so the
            // elevator's links can be backed off and the ship's links queued before anything moves
            // the body; every other link the navmesh holds is crossed here, always, at agent speed
            // from where the body stands to the link's end (#1293 R2: an escort's prisoner is
            // attached to him, so the crossing carries nobody else). Nothing refuses, waits, parks
            // or re-plans it.
            private BlacksiteLinkCrossing _jump;
            private bool _areaMaskLogged;
            // #874 N5: the furthest a link back-off may move the body. The #991 fixture's own bound.
            private const float BackOffMaxMetres = 1f;

            internal bool IsJumping => _jump != null;
            /// <summary>#874 S2: presentation only, and no gait predicate picks the running clip.</summary>
            internal bool IsRunningJump => false;

            /// <summary>#874 S5: the crossing's duration, which the presentation's clip rate is derived from.</summary>
            internal float CrossingSeconds => _jump?.PlannedAirtime ?? 0f;

            // #989 C5 and #991 E1, reconciled at integration: ONE line per member, at the first
            // agent configuration, carrying the union of both units' keys. A link the moon authored
            // in an area this agent's mask excludes is then visible as a mask rather than as a
            // mystery, and the off-mesh-link mode this unit turns off is recorded beside it.
            //
            // PathfindingLib patches every vanilla enemy prefab's mask at load to admit non-smart
            // off-mesh links (Crawler 0x0000267F to 0x0200267F) and writes no such line for the
            // Blacksite soldier. If area 25 is missing here, the agent's own paths are partial
            // across exactly the links an all-areas NavMesh path query reports
            // complete, which is the systematic disagreement between the manifest's route checks
            // and the agent's verdict that the 2026-09-12 run showed and could not explain.
            // #1305 G1: ConfigureAgent clears area 25 itself (BlacksiteReachability.SoldierAreaMask),
            // the mask every route the service answers for a soldier uses; this line records it.
            private void LogAgentAreaMaskOnce()
            {
                if (_areaMaskLogged || _agent == null || !BlacksiteDiagnostics.Enabled) return;
                _areaMaskLogged = true;
                BlacksiteDiagnostics.Log("Navigation",
                    $"member={_soldier.MemberIndex}, event=agentMask, areaMask=0x{_agent.areaMask:X8}"
                    + $", agentTypeID={_agent.agentTypeID}, radius={_agent.radius:0.00}, height={_agent.height:0.00}"
                    + $", autoTraverseOffMeshLink={_agent.autoTraverseOffMeshLink}.");
            }

            /// <summary>
            /// #874 S2/S3: a link the navmesh holds, taken by one of three arms. The elevator's own
            /// link is the elevator task's board, ride and exit: the member is backed off it. The
            /// ship's own links are the ship crossing (TryBeginShipCrossing), taken in the ship's FIFO
            /// order. Every other link is crossed with Detour's off-mesh animation: from where the
            /// body stands onto the link start, then across to the link end, both lifted by the
            /// agent's base offset. The world ladder's mount shape - a crossing, the agent detached
            /// from the transform - is kept so the hand-back is the one every motor in this
            /// navigator makes.
            /// </summary>
            private void BeginLinkCrossing(OffMeshLinkData link)
            {
                if (BlacksiteElevatorInteraction.OwnsOffMeshLink(link, _agent.navMeshOwner)) { SkipLink(link, "elevator"); return; }
                BlacksiteShipController ship = ShipOwningLink(link);
                if (ship != null) { TryBeginShipCrossing(ship, link.startPos, link.endPos); return; }
                Vector3 initPos = _soldier.transform.position;
                Vector3 start = link.startPos + Vector3.up * _agent.baseOffset;
                Vector3 end = link.endPos + Vector3.up * _agent.baseOffset;
                // #1262 P6: the arc is the one a body crossing at its role speed flies; Detour's tmax stretches
                // with a slowed agent, and gravity over a 5 s crawl-crossing would put the apex through the ceiling.
                _jump = new BlacksiteLinkCrossing(initPos, start, end, _agent.speed, Mathf.Max(_agent.speed, _soldier.GetRoleMoveSpeed()));
                BeginCrossing("jump");
                _agent.updatePosition = false; _agent.updateRotation = false;
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("Jump",
                        $"event=begin, member={_soldier.MemberIndex}, "
                        + $"start={BlacksiteDiagnosticFormat.V(start)}, end={BlacksiteDiagnosticFormat.V(end)}, "
                        + $"distance={Vector3.ProjectOnPlane(end - start, Vector3.up).magnitude:0.00}, rise={end.y - start.y:0.00}, "
                        + $"duration={_jump.PlannedAirtime:0.00}, speed={_agent.speed:0.00}, apex={_jump.Apex:0.00}, "
                        + $"animator={DescribeAnimatorState()}, authority=host");
            }

            // #874 N5: an elevator's own link - the mineshaft car's or a PathfindingLib elevator's -
            // is crossed by the elevator task, never by this mover; the Vow run hopped member 2 into
            // the cabin on one (log 13859). Back off and leave it. #1305 G1 (Q1, M5): a body the back-off
            // cannot re-bind (recovery=none) is the route service's refused link (BlacksiteReachability.Walk,
            // RefusedLink), so no route starts there; this stays the elevator's last physical refusal.
            private void SkipLink(OffMeshLinkData link, string owner)
            {
                bool backedOff = BackOffLink(link);
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.LogRateLimited($"{owner}Link.{_soldier.NetworkObjectId}", 3f, "Jump",
                        $"event=skipped, member={_soldier.MemberIndex}, owner={owner}, "
                        + $"start={BlacksiteDiagnosticFormat.V(link.startPos)}, end={BlacksiteDiagnosticFormat.V(link.endPos)}, "
                        + $"recovery={(backedOff ? "backOff" : "none")}, authority=host");
            }

            /// <summary>
            /// #991 round 2 (blocking 1): un-park the agent from a link it is not going to cross,
            /// WITHOUT completing it. CompleteOffMeshLink would put the body on the far side in one
            /// frame. The link start is where the agent already stands, so the warp is a re-binding
            /// and not a move.
            ///
            /// #874 N5: the link start is only where the BODY stands when the agent was carried with
            /// it; the Vow run's agent had been left on the top floor, and this warp moved member 1
            /// 37.22 m back up the shaft (log 13745). A near side further than
            /// <see cref="BackOffMaxMetres"/> from the body is refused, which hands the member to the
            /// ordinary recovery instead of across a floor.
            /// </summary>
            private bool BackOffLink(OffMeshLinkData link)
            {
                if (_agent == null || !_agent.enabled || !_agent.isOnNavMesh) return false;
                return TryBackOffPoint(link, _soldier.transform.position, out Vector3 nav) && WarpOffLink(nav);
            }

            /// <summary>
            /// Where a back-off re-binds the agent - the link's near side on the navmesh beside it - and
            /// whether that is within <see cref="BackOffMaxMetres"/> of <paramref name="body"/>. #1305 G1
            /// (M5): the route service's refused-link rule reads this same test.
            /// </summary>
            internal static bool TryBackOffPoint(OffMeshLinkData link, Vector3 body, out Vector3 nav)
            {
                Vector3 nearSide = link.valid ? link.startPos : body;
                if (!BlacksiteLandingGeometry.TryNavPoint(nearSide, PhysicalMask, out nav, .6f, .6f))
                    nav = nearSide;
                return (nav - body).sqrMagnitude <= BackOffMaxMetres * BackOffMaxMetres;
            }

            // The re-binding the back-off makes: the agent is warped to a point beside the body,
            // never the body to the agent, and the destination is re-planned.
            private bool WarpOffLink(Vector3 nav)
            {
                bool backedOff = false;
                try
                {
                    if (_agent.hasPath) _motion.ClearPath();
                    backedOff = _agent.Warp(nav);
                }
                catch (Exception e)
                { ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Jump link back-off skipped: {e.Message}"); }
                if (!backedOff) return false;
                _soldier.serverPosition = _soldier.transform.position;
                _soldier.SyncPositionToClients();
                RequestImmediateDestinationRefresh();
                return true;
            }

            /// <summary>
            /// #991, #874 S2/S3: the per-frame half, ticked beside TickWorldLadder before any request.
            /// It detects the link the agent is parked on and crosses it; true while a crossing owns
            /// the transform. The crossing is atomic: a goal change meets the crossing (OwnedByMotor),
            /// a stun does not stop it, and only death, capture or a lost agent ends it early
            /// (ReleaseJump). #1219 C2: boarding is the board crossing's completion, never an early end.
            /// </summary>
            internal bool TickJump()
            {
                if (_jump == null)
                {
                    if (!AgentBound || IsWorldClimbing
                        || _soldier._boardedShip || _soldier._monsterCaptured || _soldier.isEnemyDead
                        // Another crossing or a seat moving the body crosses nothing this frame.
                        || _bodyWriter != BlacksiteBodyWriter.Agent) return false;
                    if (_agent.isOnOffMeshLink)
                    {
                        OffMeshLinkData link;
                        try { link = _agent.currentOffMeshLinkData; }
                        catch { return false; }
                        if (!link.valid) return false;
                        BeginLinkCrossing(link);
                    }
                    // #1219 C2: a member waiting at the board link start directly is not on a link;
                    // he re-asks the FIFO every frame, as a parked member does, and keeps his queue
                    // start while the executor keeps asking.
                    else if (WaitingToBoardDirectly) TryBeginDirectBoard();
                    // #874 S4: a ship-queue wait is measured from the member's first refusal at the
                    // link start; a member that leaves the link start un-admitted (a re-plan, a
                    // combat move) forgets it, so a later wait is not measured from a stale start.
                    else { _shipQueueSince = 0f; return false; }
                    if (_jump == null) return false;
                }
                if (_soldier.isEnemyDead || _soldier._monsterCaptured || !AgentBound)
                {
                    ReleaseJump(_soldier.isEnemyDead ? "dead" : _soldier._monsterCaptured ? "captured" : "noAgent");
                    return false;
                }
                bool wasOnLadder = _jump.OnLadder;
                bool landed = _jump.Tick(_soldier.transform, Time.deltaTime);
                Vector3? facing = _jump.FacingHint;
                if (facing.HasValue)
                    FaceCombatPoint(_soldier.transform.position + facing.Value, FacingTurnDegreesPerSecond);
                bool shipCrossing = IsCrossingShipLink;
                // #874 S3: the FIFO turn is re-asked every frame (the lane's stale sweep would otherwise hand
                // it on mid-crossing); `!= null`, not `?.`: a despawned ship still runs to its fixed End.
                // #1283 S4: only while on the link; a ladder climb steps off it at the top (LeaveShipLink).
                if (shipCrossing && _jump.OnLadder)
                {
                    if (_crossingShip != null) _crossingShip.Ladder.Request(_soldier.NetworkObjectId, Time.time);
                }
                else if (shipCrossing && wasOnLadder) LeaveShipLink();
                _soldier.SyncPositionToClients();
                if (!landed) return true;

                float elapsed = _jump.Elapsed;
                _jump = null;
                // The link is consumed, the agent's own position is the link end, and the transform
                // is on it: the hand-back is the rebind every motor here makes. A body left off the
                // navmesh anyway is EnsureOnNavMesh's, as every off-mesh body is. #1219 C2: a direct
                // board crossing has no link to consume, so its agent is bound at the crossing's end
                // before it drives the transform again - or the transform snaps back to the mount.
                // #1283 S4: a ladder climb's deck leg consumed a parked member's link at the top.
                try
                {
                    if (_agent.isOnOffMeshLink) _agent.CompleteOffMeshLink();
                    else if (shipCrossing) _agent.Warp(_crossingNavEnd);
                }
                catch (Exception e) { ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Jump link completion skipped: {e.Message}"); }
                _agent.updatePosition = true; _agent.updateRotation = true;
                Y4NGZCompany.Contracts.Payload.PayloadCartController.ReleaseForWarpServer(_soldier);
                EndCrossing(_soldier.transform.position);
                _soldier.NoteTraversalMilestone("jump:landed");
                if (shipCrossing) EndShipCrossing(elapsed, abortReason: null);
                else if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("Jump",
                        $"event=land, member={_soldier.MemberIndex}, at={BlacksiteDiagnosticFormat.V(_soldier.transform.position)}, "
                        + $"elapsed={elapsed:0.00}, authority=host");
                return true;
            }

            /// <summary>
            /// #991 round 2 (should-fix 4), #874 S2/S3: the early end of a crossing - death, monster
            /// capture, boarding, a lost agent, a teleport or teardown; <paramref name="reason"/> is
            /// the caller's. It drops the leg, consumes the link so the agent is never stranded on
            /// one, restores the agent flags the begin wrote, ends the crossing, sets the carried
            /// witness down and, for a ship crossing, gives the FIFO turn back. #1283 S4: a ladder climb
            /// ended on its deck leg has its agent on the seat (LeaveShipLink); it is bound back under
            /// the body first, or restoring updatePosition would carry the body there in one frame.
            /// </summary>
            internal void ReleaseJump(string reason)
            {
                if (_jump == null) return;
                bool shipCrossing = IsCrossingShipLink, walkedOn = !_jump.OnLadder;
                _jump = null;
                if (_agent != null && _agent.enabled)
                {
                    try { if (_agent.isOnNavMesh && _agent.isOnOffMeshLink) _agent.CompleteOffMeshLink(); }
                    catch (Exception e) { ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Jump link completion skipped: {e.Message}"); }
                    if (walkedOn) BindDetachedAgent(BodyFloor);
                    _agent.updateRotation = true;
                    _agent.updatePosition = true;
                }
                EndCrossing(_soldier.transform.position);
                if (shipCrossing) EndShipCrossing(0f, reason);
                else if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("Jump",
                        $"event=abort, member={_soldier.MemberIndex}, reason={reason}, authority=host");
            }
        }
    }
}
