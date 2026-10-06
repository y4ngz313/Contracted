using UnityEngine;
using UnityEngine.AI;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed partial class BlacksiteShipController
    {
        // #874 S3 (plan §3 rule 3): the ship's ONE FIFO over its two deck links (the drop and the
        // ladder/rope). Capacity 1, no headway: the holder is whoever is IN a crossing, and the
        // crossing's end is the normal release. #1293 R2: the claim has no ceiling; a holder that
        // stops progressing is the progress ledger's miss, whose answer releases every lease he
        // holds. Built and bound by the one factory (BlacksitePassageQueue.ShipLadder,
        // BindShipLadder) the headless ShipLadder lane row drives.
        internal readonly BlacksitePassageQueue Ladder = BlacksitePassageQueue.ShipLadder();

        /// <summary>
        /// #1016 S2: put the ship FIFO under the mission broker. The queue is a field initialiser,
        /// so binding happens on the first server tick that has the mission in hand; every
        /// participant also binds lazily before its own first request. Idempotent.
        /// </summary>
        internal void BindAccessQueues()
        {
            if (_mission == null || !_mission.IsServer) return;
            Ladder.BindShipLadder(_mission.Blackboard.Broker);
        }
        internal Vector3 ExitPosition { get; private set; }
        private Vector3[] _egressPositions, _groundRoute;
        // Until the first passage proof, the exit is the only known ground anchor.
        internal Vector3 EgressPosition(int member) => _egressPositions != null && _egressPositions.Length > 0
            ? _egressPositions[Mathf.Clamp(member, 0, _egressPositions.Length - 1)] : ExitPosition;
        internal bool AccessValidated { get; private set; }
        internal bool PassageProven { get; private set; }
        // #874 (2026-09-20): BoardingWindow is DELETED. It was the transport's per-deployment
        // allowance for "how long the squad may take to board before I leave without it", scaled
        // by the rappel height and the member count. #1262 P2: no allowance answers it; the
        // departure's boarding facts do (every unboarded member stranded: no route, no session).
        // #872: bumped on every published access, so a member that gave up on the passage can
        // tell a re-proven route from the one it exhausted its attempts against.
        internal int AccessGeneration { get; private set; }
        private Vector3 _accessEntrance;
        private int _accessMembers;
        // #1305: the landing plan the search sealed before flight (server-only, from InitializeServer).
        private BlacksiteLandingPlan _plan;

        /// <summary>
        /// #1305: touchdown publishes the sealed plan and never re-judges it: the access, its links,
        /// the exit, the egress slots and the proven ground route. The search's deck proof is the only
        /// judge; one <c>[UAT][ShipAccess] touchdown</c> line (diagnostics only) measures the live
        /// hull's ladder leg with the search's own helper and changes nothing published.
        /// </summary>
        internal void SealAccessServer(Vector3 entrance, int members)
        {
            if (!IsServer) return;
            _accessEntrance = entrance; _accessMembers = members;
            WriteAccessServer(_plan.Access, FootToDeck); ConnectAccessLinks(true);
            ExitPosition = _plan.Exit; _egressPositions = _plan.Egress; _groundRoute = _plan.Access.GroundRoute;
            PassageProven = true; AccessValidated = true; AccessGeneration++;
            if (_rendezvous.Value == Vector3.zero) _rendezvous.Value = _plan.Exit;
            if (!BlacksiteDiagnostics.Enabled) return;
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            // #874 N1: the touchdown bake's lip-to-deck-navigation gap, beside the search's on [UAT][Landing].
            string lipNavGap = BlacksiteShipNavigation.TryLipEnds(transform, mask, out var lipNav, out var lipFloor, out _)
                ? BlacksiteShipNavigation.LipNavGap(lipNav, lipFloor).ToString("0.000") + "m" : "none";
            string liveLeg = _plan.Hover || !_plan.Access.HasLadder ? "none"
                : BlacksiteShipNavigation.TryLadderLeg(transform, _plan.Access, mask, out string legWhy) ? "complete" : "partial reason=" + legWhy;
            BlacksiteDiagnostics.Log("ShipAccess", $"touchdown plan=sealed mode={(_plan.Hover ? "hover" : "ground")} drop={_plan.Access.DropHeight:0.00}m ladder={_plan.Access.LadderSide} exit={_plan.Exit} lipNavGap={lipNavGap} liveLeg={liveLeg}");
        }

        /// <summary>
        /// #1219 C3: the re-landing's passage proof. The access for the current pose (hover: the
        /// rappel geometry; ground: the hull's support, then its boarding geometry) is written and
        /// linked, then the passage routes are proven; a refusal restores the previous published
        /// geometry before the network tick can observe a half-validated update. On success the
        /// egress and exit are updated.
        /// <paramref name="ignoredCargo"/> is the ship's own crate, ignored by the hull-volume and support checks.
        /// </summary>
        private bool TryProvePassageServer(int members, Transform ignoredCargo, int mask,
            out BlacksiteLandingAccess access, out Vector3 outside, out string reason)
        {
            const string prefix = "final deck/drop/ladder route unavailable: ";
            access = default; outside = default;
            if (IsHovering)
            {
                if (!BlacksiteRappelGeometry.TryAccess(transform.position, transform.rotation, mask, FootToDeck, members,
                    out access, out outside, out reason, transform, ignoredCargo)) { reason = "final hover access: " + reason; return false; }
            }
            else
            {
                if (!BlacksiteLandingGeometry.TrySupport(transform.position, transform.rotation, mask, FootToDeck, out var supported, out reason, transform, ignoredCargo)
                    || Mathf.Abs(supported.y - transform.position.y) > .1f) return false;
                if (!BlacksiteBoardingGeometry.TryAccess(transform.position, transform.rotation, mask, FootToDeck, out access, out outside, out reason, transform, members))
                { reason = prefix + reason; return false; }
            }
            access.GroundRoute = _groundRoute;
            // #1233 S7: the proven corridor altitude is not something the boarding geometry measures.
            access.Altitude = _landingAltitude;
            // The route query needs the candidate links. A refusal restores the previous
            // published geometry before the network tick can observe a half-validated update.
            BlacksiteLandingAccess previous = Access;
            WriteAccessServer(access, FootToDeck); ConnectAccessLinks(true);
            if (!BlacksiteShipNavigation.ValidatePassageRoutes(transform, access, outside, members, mask,
                out var egress, out _, out reason))
            {
                WriteAccessServer(previous, FootToDeck); ConnectAccessLinks(true);
                reason = prefix + reason; return false;
            }
            _egressPositions = egress; ExitPosition = outside;
            return true;
        }

        /// <summary>
        /// #1219 C3: a recalled hull that touched down again proves its passage again on the real
        /// hull (its crate, parked at the rope foot to be re-hooked, passed as the ignored cargo).
        /// A validated proof republishes the access, the egress points and ExitPosition as the
        /// touchdown seal does; a failed one restores the previous access and only logs, the
        /// ship's boarding facts (#1262 P2) and survivor detach staying the fallback. One
        /// <c>[UAT][ShipAccess] relanding</c> line (diagnostics only): the result, the link start,
        /// its distance to walkable moon navmesh, whether the ladder link is connected and the path
        /// status from the ground staging point to it. On a hover re-landing the parked crate stands
        /// on the rope foot until CargoLift, so the column/foot checks may report
        /// <c>failed ... blocked by &lt;crate&gt;</c>; linkStart, walkableNav and path are the diagnostic.
        /// </summary>
        private void ProveRelandingServer()
        {
            int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : ~0;
            var crate = CargoContainer;
            bool validated = TryProvePassageServer(_accessMembers, crate != null ? crate.transform : null, mask,
                out _, out _, out string reason);
            if (!BlacksiteDiagnostics.Enabled) return;
            Vector3 start = LadderMountPosition;
            // #1293 R2: walkable means bindable - moon navmesh within the one ground tolerance.
            string walkable = NavMesh.SamplePosition(start, out NavMeshHit hit, BlacksiteRecoveryLadder.GroundNavTolerance,
                    NavMesh.AllAreas & ~BlacksiteShipNavigation.DeckAreaMask)
                ? Vector3.Distance(start, hit.position).ToString("0.00") + "m" : "none";
            string status = BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(false, NavMesh.AllAreas), ExitPosition, start)
                .PathReason.Substring("path:".Length);
            BlacksiteDiagnostics.Log("ShipAccess",
                $"relanding result={(validated ? "validated" : "failed")}, reason={(validated ? BlacksiteDiagnosticFormat.None : reason)}"
                + $", linkStart={BlacksiteDiagnosticFormat.V(start)}, walkableNav={walkable}, ladderLink={_ladderLinked}"
                + $", path={status}, from={BlacksiteDiagnosticFormat.V(ExitPosition)}");
        }
        internal bool TryGroundRouteWaypoint(Vector3 current, Vector3 destination, bool outbound,
            float advanceRadius, out Vector3 waypoint, out bool isLast)
        {
            waypoint = default; isLast = false;
            if (!AccessValidated || _groundRoute == null) return false;
            return BlacksiteGroundRouteFollower.TryWaypoint(_groundRoute, current, destination,
                outbound, advanceRadius, out waypoint, out isLast);
        }

        /// <summary>#1305 G2 (R16): the point the corridor is followed toward. A goal the caller names in
        /// the other world is past the door, so the corridor is walked to its end and the route takes the
        /// doorway there; any other goal is itself.</summary>
        internal Vector3 CorridorGoal(Vector3 destination, bool goalAcross)
            => goalAcross && _groundRoute != null && _groundRoute.Length > 0 ? _groundRoute[_groundRoute.Length - 1] : destination;

        internal bool TryUpcomingWorldLadder(Vector3 current, bool outbound, int mask, out BlacksiteWorldLadder ladder, out Vector3[] points)
            => BlacksiteLandingApproach.TryUpcomingLadder(_groundRoute, current, outbound, mask, out ladder, out points);

        internal bool WithinPassage(Vector3 point) => BlacksiteBoardingGeometry.WithinPassage(transform.InverseTransformPoint(point), Access);
        internal bool OnDeck(Vector3 point) => BlacksiteBoardingGeometry.OnDeck(transform.InverseTransformPoint(point));
        /// #1233 S4 (rule 1): the one "aboard now" answer - the body is inside the hull volume or on the deck strip.
        internal bool Aboard(Vector3 point) => ContainsCargo(point) || OnDeck(point);
    }
}
