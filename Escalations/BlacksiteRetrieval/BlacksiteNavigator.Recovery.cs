using System;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    public sealed partial class BlacksiteSoldierAI
    {
        // Per-member movement owner. Nesting keeps the soldier's mission and animation details
        // private while navigation is extracted without changing their decisions.
        internal sealed partial class BlacksiteNavigator : IDisposable
        {
            // #874 S1 (plan rule 1): the vanilla navmesh is the only truth about where a soldier may
            // stand. There is no per-frame collision sweep, no rewind, no carve, no escape walk and
            // no hold here any more. #1293 R2 (rule 3): what is left is binding the agent where the
            // body stands, the stranded fact when it cannot, and the per-frame record other layers
            // read: the member's last navmesh position.
            private Vector3 _lastGoodNavPosition;
            private bool _hasLastGoodNavPosition;
            /// <summary>
            /// #874 S9.4: how far above or below the member a navmesh sample may sit. The swept body
            /// column, <see cref="BlacksiteWorldLadderColumn.BodyColumnMetres"/>: a sample within one
            /// body height is the same storey by construction, because a second storey cannot hide
            /// inside one body. The payload coupling's waypoint sampling reads the same bound.
            /// </summary>
            internal const float VerticalReboundMetres = BlacksitePhysicalStep.CapsuleHeight;
            // One agent diameter (2 x 0.45 m): the smallest disc that can describe a place a body
            // cannot stand. The cart corridor's clearance.
            internal const float MinObstacleRadius = BlacksiteArrivalPolicy.AgentRadius * 2f;
            // The cart corridor's lead: how far past a refused pose the coupled pair plans a leg.
            internal const float RouteLeadMetres = 2f;
            // The collision mask every hand-driven step and probe in this navigator sweeps against.
            internal static int PhysicalMask => StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            // #1293 R2: the body cannot bind where it stands. Logged once on entry and once on exit.
            private bool _stranded;

            /// <summary>
            /// #1293 R2 (rule 3): the agent is off the navmesh and no navmesh lies inside the body's
            /// own footprint (<see cref="BindAt"/>); nothing moves a body to find navmesh. The planner
            /// gives a stranded member no travel (it holds), the progress ledger does not judge him
            /// and the boarding fold reports him <see cref="BlacksiteBoardingFact.Stranded"/>. It
            /// clears on the maintenance tick that finds the agent on the navmesh again (the body
            /// moved by an elevator, the ship or physics).
            /// </summary>
            internal bool IsStranded => _stranded;

            /// <summary>
            /// #1293 R2 (rule 3): the one off-mesh answer, for the per-tick guard and every caller
            /// that finds the agent off the navmesh: bind the agent where the body stands
            /// (<see cref="BindAt"/>), or the member is stranded.
            /// </summary>
            internal bool BindInPlace(string reason) => BindAt(_soldier.transform.position, reason);

            /// <summary>
            /// #1293 R2 (rule 3): binds the agent when the nearest navmesh point lies inside the body's
            /// own footprint - horizontally within the agent's radius (<c>_agent.radius</c>: the mesh is
            /// eroded by that radius, so a body pressed against a wall stands up to one radius off it)
            /// and vertically within <see cref="BlacksiteRecoveryLadder.GroundNavTolerance"/>, the ground
            /// tolerance every bind uses. A member whose bind fails is stranded only while his agent
            /// is off the navmesh - the inverse of <see cref="EnsureOnNavMesh"/>'s clear - so a failed
            /// bind with the agent still on the mesh (a ladder dismount) is not stranded. Refused
            /// while a crossing or a seat owns the body (its own end binds it).
            /// </summary>
            private bool BindAt(Vector3 body, string reason)
            {
                if (_agent == null || !_agent.enabled) return false;
                if (AgentBoundAt(body)) { NoteStranded(false, reason, body); return true; }
                if (_bodyWriter != BlacksiteBodyWriter.Agent || (_interactionCoordinator?.IsCommittedTraversal ?? false))
                    return false;
                float footprint = _agent.radius;
                bool bound = NavMesh.SamplePosition(body, out var nav, footprint + BlacksiteRecoveryLadder.GroundNavTolerance, RecoveryAreaMask())
                    && Flat(nav.position, body) <= footprint
                    && Mathf.Abs(nav.position.y - body.y) < BlacksiteRecoveryLadder.GroundNavTolerance
                    && _agent.Warp(nav.position);
                NoteStranded(!bound && !_agent.isOnNavMesh, reason, body);
                if (!bound) return false;
                RequestImmediateDestinationRefresh();
                // #1018 S4: the no-planner path. A survivor and the native fixtures have no mission
                // and no assignment at all, so the action is entered with `default` and every read
                // of it is guarded by IsValid - it behaves as the rally hold.
                if (!_soldier.TryGetSquadDirector(out _))
                    _soldier.ExecuteIdleAction(default, "recovery", out _, out _, out _, out _);
                return true;
            }

            // #1293 R2: one [UAT][Navigation] line per change of the fact. #1293 R3: and each change
            // is an Outcome decision event - a body bound again is what ends the planner's stranded hold.
            private void NoteStranded(bool stranded, string reason, Vector3 at)
            {
                if (_stranded == stranded) return;
                _stranded = stranded;
                BlacksiteDiagnostics.Log("Navigation", $"member={_soldier.MemberIndex}, stranded={(stranded ? "true" : "false")}"
                    + $", reason={reason}, at={BlacksiteDiagnosticFormat.V(at)}");
                _soldier.RequestDecision(BlacksiteDecisionCause.Outcome);
            }

            // A member standing on the deck strip must bind to the deck area, never to the moon
            // mesh beneath the landed hull. Only the deck: a member at the drop landing or the
            // ladder mount is on moon ground and must not be bound up onto the catwalk.
            // #990 round 2: the bind and the payload coupling's waypoint sampling read this one
            // mask; NavMesh.AllAreas let a member standing on the landed ship's deck bind to the
            // moon mesh under the hull.
            private int RecoveryAreaMask()
                => _soldier.TryGetMission(out var mission) && mission.Objectives.Ship != null && mission.Objectives.Ship.IsGrounded
                    && mission.Objectives.Ship.OnDeck(_soldier.transform.position) ? BlacksiteShipNavigation.DeckAreaMask : NavMesh.AllAreas;

            /// <summary>
            /// #978: "already bound to the navmesh, HERE". The agent's own simulated position
            /// (<c>nextPosition</c>) is what a warp would replace, and it stops following the
            /// transform the moment a motor sets <c>updatePosition = false</c>, so
            /// <c>isOnNavMesh</c> alone cannot tell a member standing on the mesh at its own feet
            /// from a member whose body a motor carried off a lip while its binding stayed on the
            /// deck above. Within one ground tolerance of the body is the same place by the same
            /// definition every published ground point uses.
            /// </summary>
            private bool AgentBoundAt(Vector3 position)
                => _agent != null && _agent.enabled && _agent.isOnNavMesh
                    && Vector3.Distance(_agent.nextPosition, position) <= BlacksiteRecoveryLadder.GroundNavTolerance;

            internal void LateTick() => LateTick(Time.deltaTime);
            // The frame length stays explicit so the native fixture can drive the production
            // method at 30, 60 and 120 Hz without a live player loop.
            internal void LateTick(float deltaSeconds)
            {
                if (!IsServerAuthority() || !_soldier._initialized || _soldier.isEnemyDead || _soldier._boardedShip) return;
                // #874 S1: the navmesh is the truth (rule 1), so the last good position is simply
                // the last place the agent was walking the body on the navmesh.
                if (_bodyWriter == BlacksiteBodyWriter.Agent && _agent != null && _agent.enabled && _agent.updatePosition
                    && _agent.isOnNavMesh && !(_interactionCoordinator?.IsCommittedTraversal ?? false))
                {
                    Vector3 now = _soldier.transform.position;
                    _lastGoodNavPosition = now; _hasLastGoodNavPosition = true;
                }
            }
        }
    }
}
