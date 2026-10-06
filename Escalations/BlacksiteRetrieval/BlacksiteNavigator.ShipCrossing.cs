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
            // #874 S3 (rules 2, 3): the ship deck is ordinary navmesh, and its two one-way links -
            // the drop (lip to landing or rope foot) and the ladder (mount or rope foot to deck
            // top) - are its only exits. Each is crossed as a scripted BlacksiteLinkCrossing on the
            // S2 jump's own shape: a fixed duration, a fixed end on the navmesh, the Jump hold, and
            // nothing but death, capture, dispose or a teleport ends one early (ReleaseJump). The
            // ship's ONE FIFO, ship.Ladder, is held by the member IN a crossing and released at its
            // end (#1283 S4: a ladder climb's at its top, LeaveShipLink, before the deck leg to his
            // seat); a member not yet admitted stays parked by Unity on the link start.
            private BlacksiteShipController _crossingShip;
            private bool _crossingBoards;

            internal BlacksiteShipCrossingKind? ShipCrossingKind => _jump?.Kind;
            /// <summary>A ship crossing owns the transform this frame.</summary>
            internal bool IsCrossingShipLink => _jump != null && _jump.Kind.HasValue;
            internal bool IsRappelling => ShipCrossingKind == BlacksiteShipCrossingKind.RopeDescend
                || ShipCrossingKind == BlacksiteShipCrossingKind.RopeAscend;
            internal bool IsAscendingRope => ShipCrossingKind == BlacksiteShipCrossingKind.RopeAscend;
            /// <summary>
            /// Parked by Unity on a ship link's start, or (#1219 C2) standing at the board link start
            /// asking <see cref="TryBoardShip"/>, and not yet admitted to the FIFO.
            /// </summary>
            internal bool IsQueuedAtShipLink
            {
                get
                {
                    if (_jump != null) return false;
                    if (WaitingToBoardDirectly) return true;
                    if (!AgentBound || !_agent.isOnOffMeshLink) return false;
                    try { return ShipOwningLink(_agent.currentOffMeshLinkData) != null; }
                    catch { return false; }
                }
            }

            // #874 S2: the transport's own links - the drop and the ladder BlacksiteShipNavigation
            // authors onto its deck and passage - or null for any other link.
            private BlacksiteShipController ShipOwningLink(OffMeshLinkData link)
            {
                if (!link.valid) return null;
                if (!_soldier.TryGetMission(out BlacksiteRetrievalMissionController mission))
                    mission = BlacksiteHardDriveObjectiveService.ActiveMission;
                BlacksiteShipController ship = mission != null ? mission.Objectives.Ship : null;
                return ship != null && (ship.OnDeck(link.startPos) || ship.OnDeck(link.endPos)
                    || ship.WithinPassage(link.startPos) || ship.WithinPassage(link.endPos)) ? ship : null;
            }

            /// <summary>
            /// #874 S3 (rules 2, 3): ask the FIFO for the crossing from <paramref name="start"/> to
            /// <paramref name="end"/> and begin it once admitted; true when it began. A refused
            /// member stays where he stands and asks again next frame (a parked member through
            /// Unity's link, which is also the lane's keep-alive; a direct boarder through TickJump
            /// while the executor keeps asking). #874 S4: it waits in a ship-queue dwell whose
            /// deadline is fixed at the first refusal: one fixed crossing duration per member ahead.
            /// #1293 R2: a direct waiter at the link start is the progress ledger's declared Queue
            /// (<see cref="IsQueuedAtShipLink"/>), never judged; what bounds the line is the holder's
            /// own crossing, whose duration is fixed (rule 2). The crossing (its polyline, and #1219
            /// C2's start where the body stands) is BlacksiteLinkCrossing's.
            /// </summary>
            private bool TryBeginShipCrossing(BlacksiteShipController ship, Vector3 start, Vector3 end)
            {
                ulong id = _soldier.NetworkObjectId;
                ship.Ladder.Bind(_soldier.ResourceBroker, BlacksiteResourceKey.ShipLadder());
                BlacksiteBrokerVerdict turn = ship.Ladder.RequestVerdict(id, Time.time);
                // The ladder (mount or rope foot to deck top) is the one link that climbs. A landed
                // hull's ladder top stands on its side catwalk, outside the OnDeck strip, so an
                // end-on-deck test read every ground ladder climb as a lip drop.
                bool board = end.y > start.y;
                bool rope = ship.Access.Hover;
                // Rule 2: the only durations a ship crossing has.
                float duration = rope
                    ? BlacksiteLandingGearPolicy.RopeSecondsPerTenMetres * ship.Access.DropHeight / 10f
                    : board ? Vector3.Distance(start, end) / BlacksiteLandingGearPolicy.ClimbSpeed
                    : BlacksiteLandingGearPolicy.LipDropSeconds;
                if (!turn.Admitted)
                {
                    if (_shipQueueSince <= 0f) _shipQueueSince = Time.time;
                    Dwell(_shipQueueSince + Mathf.Max(1, turn.Place) * duration - Time.time, ShipQueueDwell);
                    if (BlacksiteDiagnostics.Enabled)
                        BlacksiteDiagnostics.LogRateLimited($"shipQueue.{id}", 3f, "ShipPassage",
                            $"member={_soldier.MemberIndex}, queue=waiting, resource={ship.Ladder.Resource.Key.Token}, ahead={turn.Place}");
                    return false;
                }
                // #1283 S4: a ladder climb walks on from its top over the deck, through the door, onto the member's
                // reserved seat, where the hull's seat hold then finds him; refused, it ends on the top as before.
                Vector3[] deck = null;
                if (board && !rope && !ship.TryDeckRoute(_soldier, end, out deck, out string deckWhy) && BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("ShipPassage", $"deckLeg=refused reason={deckWhy}, member={_soldier.MemberIndex}");
                _jump = new BlacksiteLinkCrossing(ship.transform, ship.Access, start, end, _soldier.transform.position, Vector3.up * _agent.baseOffset, duration, deck);
                _crossingShip = ship; _crossingBoards = board; _crossingNavEnd = deck != null ? deck[deck.Length - 1] : end;
                BeginCrossing("ship");
                _shipQueueSince = 0f;
                _directBoardShip = null;
                if (_dwellReason == ShipQueueDwell) EndDwell();
                _agent.updatePosition = false; _agent.updateRotation = false;
                if (BlacksiteDiagnostics.Enabled)
                {
                    float deckMetres = 0f;
                    for (int i = 0; deck != null && i < deck.Length; i++) deckMetres += Vector3.Distance(i > 0 ? deck[i - 1] : end, deck[i]);
                    BlacksiteDiagnostics.Log("ShipPassage",
                        $"member={_soldier.MemberIndex}, begin={(board ? "board" : "exit")}, kind={_jump.Kind}, "
                        + $"duration={duration:0.00}, height={ship.Access.DropHeight:0.00}, "
                        + (deck != null ? $"deck={deck.Length}, deckMetres={deckMetres:0.00}, " : "") + "authority=host");
                }
                return true;
            }

            // #1219 C2: the crossing's end on the navmesh (unlifted), where the agent is bound on
            // completion when no parked link carries it there; #1283 S4: a ladder climb's seat, where
            // LeaveShipLink binds it already at the top.
            private Vector3 _crossingNavEnd;
            // #1219 C2: a direct boarder's ship, link ends and the brain decision that last asked.
            // While it asks in this decision or the previous one, TickJump asks the FIFO every frame
            // and the waiter keeps his queue start.
            private BlacksiteShipController _directBoardShip;
            private Vector3 _directBoardStart, _directBoardEnd;
            private int _directBoardAskedDecision;
            private bool WaitingToBoardDirectly
                => _directBoardShip != null && _soldier.Brain.Decisions - _directBoardAskedDecision <= 1;

            /// <summary>
            /// #1219 C2: ask for the board crossing from the ship's link start, crossed directly -
            /// Detour never has to route through the link. False when a crossing, seat or coupling
            /// owns the body, the agent is not bound, the member is parked on a link, or the ship
            /// gives no link ends. Otherwise the request stands and the motor ladder's TickJump asks
            /// the FIFO for it on every frame from the next one, so the crossing begins inside the
            /// motor tick that also moves the body (as a parked link's does) and lasts exactly its
            /// rule's duration; a refused turn waits in the ship-queue dwell where the member stands.
            /// The link ends are sampled once per request; a repeated ask only keeps it alive.
            /// The completed crossing boards him (CompleteBoardCrossingServer).
            /// </summary>
            internal bool TryBoardShip(BlacksiteShipController ship)
            {
                if (ship == null || _bodyWriter != BlacksiteBodyWriter.Agent || _jump != null || !AgentBound
                    || _agent.isOnOffMeshLink) return false;
                if (ship != _directBoardShip || !WaitingToBoardDirectly)
                {
                    if (!ship.TryBoardingLinkEnds(out Vector3 start, out Vector3 end)) return false;
                    _directBoardShip = ship;
                    _directBoardStart = start;
                    _directBoardEnd = end;
                }
                _directBoardAskedDecision = _soldier.Brain.Decisions;
                return true;
            }

            // The FIFO turn for the direct board crossing, asked by TickJump every frame while the
            // executor asks for it. True when the crossing began. Refused, and the request dropped,
            // once the hull is no longer grounded or the body is no longer at the stored link start
            // (the executor's own storey test), so a member re-planned away inside the window never
            // glides from wherever he stands.
            private bool TryBeginDirectBoard()
            {
                BlacksiteShipController ship = _directBoardShip;
                Vector3 body = _soldier.transform.position;
                Vector3 offset = body - _directBoardStart;
                if (ship == null || !ship.IsGrounded
                    || new Vector2(offset.x, offset.z).magnitude > ArrivalTolerance
                    || Mathf.Abs(offset.y) > VerticalReboundMetres)
                {
                    _directBoardShip = null;
                    return false;
                }
                return TryBeginShipCrossing(ship, _directBoardStart, _directBoardEnd);
            }

            private const string ShipQueueDwell = "ship-queue";
            // #874 S4: when this member's current ship-queue wait began (its first refusal), 0 when none.
            private float _shipQueueSince;

            /// <summary>
            /// #874 S3: the end of a ship crossing, completed (<paramref name="abortReason"/> null)
            /// or ended early. The FIFO turn goes back either way.
            /// #1283 S4: an early end of a board crossing - on the ladder or its deck leg, every caller of
            /// ReleaseJump - gives back the seat its begin reserved; a completed one keeps it, refused or not.
            /// </summary>
            private void EndShipCrossing(float elapsed, string abortReason)
            {
                BlacksiteShipController ship = _crossingShip;
                _crossingShip = null;
                if (ship != null) ship.Ladder.Release(_soldier.NetworkObjectId);
                if (abortReason != null)
                {
                    if (_crossingBoards && ship != null) ship.ReleaseBoardingSlot(_soldier);
                    if (BlacksiteDiagnostics.Enabled)
                        BlacksiteDiagnostics.Log("ShipPassage", $"member={_soldier.MemberIndex}, crossing=aborted, reason={abortReason}");
                    return;
                }
                _soldier.NoteTraversalMilestone(_crossingBoards ? "passage:boarded" : "passage:exited");
                if (BlacksiteDiagnostics.Enabled)
                    BlacksiteDiagnostics.Log("ShipPassage",
                        $"member={_soldier.MemberIndex}, completed={(_crossingBoards ? "board" : "exit")}, "
                        + $"point={BlacksiteDiagnosticFormat.V(_soldier.transform.position)}, seconds={elapsed:0.00}");
                // #1219 C2: a completed board crossing IS the boarding - and the custody event for
                // what this member carries up with him.
                if (_crossingBoards) _soldier.CompleteBoardCrossingServer();
            }

            /// <summary>
            /// #1283 S4: a ladder climb steps off its link at the top, onto its deck leg, once. The FIFO turn
            /// goes back (Release is idempotent; nothing re-asks, since a request after it queues him again),
            /// a parked member's link is consumed (Unity keeps the next member off it until then), and the
            /// agent - held since the begin where the climb started, on the link start the next member walks
            /// to - is bound at the crossing's end on the deck. A crossing that ended on its top had one such
            /// phantom at the mount; a walk to the seat left one per member walking, and the second kept the
            /// third out of arrival reach of the link start until the first had sat down (native run 20260930-
            /// 215046: m1 began 264 frames after the lane came free). From here to the seat the agent is not
            /// under the body, so an early end binds it back beneath him first (ReleaseJump).
            /// </summary>
            private void LeaveShipLink()
            {
                if (_crossingShip != null) _crossingShip.Ladder.Release(_soldier.NetworkObjectId);
                BindDetachedAgent(_crossingNavEnd);
            }

            // #1283 S4: bind the crossing's detached agent (updatePosition off) at nav, its link consumed, without
            // moving the body: Warp may carry the transform along, so it is put back as TryRestoreAtCurrentPosition does.
            private void BindDetachedAgent(Vector3 nav)
            {
                Vector3 body = _soldier.transform.position;
                try
                {
                    if (_agent.isOnNavMesh && _agent.isOnOffMeshLink) _agent.CompleteOffMeshLink();
                    _agent.Warp(nav);
                }
                catch (Exception e) { ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Blacksite] Ship link hand-off skipped: {e.Message}"); }
                _soldier.transform.position = body;
            }
        }
    }
}
