using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Runtime instances are renamed and may be parented by network scene management.
    // Floor identity belongs to the vessel, not its display name or scene root.
    internal sealed class BlacksiteShipNavigationRoot : MonoBehaviour { }

    internal static class BlacksiteShipNavigation
    {
        // The deck navmesh is built into its own area. Moon navigation often survives beneath the
        // landed hull within centimetres of the deck, and height alone cannot tell the two apart;
        // sampling by area keeps deck endpoints and the ground links on the ship surface.
        internal const int DeckArea = 3;
        internal const int DeckAreaMask = 1 << DeckArea;
        internal static bool IsShipFloor(Collider floor)
            => floor != null && floor.GetComponentInParent<BlacksiteShipNavigationRoot>() != null;
        internal static bool TryBindDeploymentAgent(NavMeshAgent agent, Vector3 validatedSlot, int mask)
        {
            if (agent == null || !BlacksiteLandingGeometry.Ground(validatedSlot, .6f, mask, null, out var floor)
                || !IsShipFloor(floor.collider)
                || !NavMesh.SamplePosition(floor.point, out var deck, .4f, DeckAreaMask)
                || Mathf.Abs(deck.position.y - floor.point.y) > .15f) return false;
            agent.autoTraverseOffMeshLink = false;
            int previousMask = agent.areaMask;
            try
            {
                agent.enabled = false;
                agent.transform.position = deck.position;
                agent.areaMask = DeckAreaMask;
                agent.enabled = true;
                return agent.Warp(deck.position) && agent.isOnNavMesh
                    && Mathf.Abs(agent.transform.position.y - floor.point.y) <= .15f;
            }
            finally { agent.areaMask = previousMask | DeckAreaMask; }
        }
        // Returning bodies need a real column and deck route, not another trip to the facility.
        internal static bool ValidatePassageRoutes(Transform ship, in BlacksiteLandingAccess access,
            Vector3 outside, int members, int mask, out Vector3[] egress, out string ladderWhy, out string reason)
        {
            egress = null;
            if (!TryShipPassage(ship, access, ship.TransformPoint(access.DropLandingLocal), outside, mask, out ladderWhy, out reason)) return false;
            if (!TryEgressSlots(ship, outside, members, mask, out egress, out reason))
            { reason = "ground staging: " + reason; return false; }
            reason = null; return true;
        }
        // The deck legs, the drop link, drop to staging and the ladder top to the door.
        private static bool TryShipPassage(Transform ship, in BlacksiteLandingAccess access, Vector3 drop, Vector3 outside, int mask,
            out string ladderWhy, out string reason)
        {
            ladderWhy = null;
            var probe = BlacksitePhysicalStep.ProbeTransform;
            Vector3 approach = ship.Find("DoorApproach").position;
            if (!TryAisleToDoor(ship, mask, out reason)) return false;
            // #874 N1: the leg ends at the drop link's own deck navigation and walks the rest onto
            // LipStep physically (TryLipTail); the drop link is then asked from that same point.
            if (!TryLipTail(ship, probe, mask, true, out var lipNav, out _, out reason)
                || !BlacksiteLandingGeometry.WalkRoute(probe, approach, lipNav, mask, out _, out reason))
            { reason = "door to lip: " + reason; return false; }
            if (!BlacksiteReachability.Walk(BlacksiteTraveller.Soldier(false, NavMesh.AllAreas), lipNav, drop).Reachable)
            { reason = "deck lip does not link to the drop landing"; return false; }
            if (!TryDropToStaging(drop, outside, mask, out reason)) return false;
            // Preserve the server's existing optional-link policy; physical return still validates
            // its complete passage and fails bounded if the top-to-door leg cannot be walked. A
            // rappel tops out on LipStep, so its deck walk starts along the lip tail.
            if (access.HasLadder)
            {
                if (!BlacksiteLandingGeometry.Ground(ship.TransformPoint(access.LadderTopLocal), .6f, mask, null, out var floor) || !IsShipFloor(floor.collider))
                    ladderWhy = "ladder top is not on the ship floor";
                else if (!access.Hover) ladderWhy = TryLadderLeg(ship, access, mask, out var legWhy) ? null : legWhy;
                else if (!TrySampleLadderTop(ship, access.LadderTopLocal, out _)) ladderWhy = "ladder top has no deck navigation";
                else if (!TryLipTail(ship, probe, mask, false, out var topNav, out _, out var walkWhy)
                    || !BlacksiteLandingGeometry.WalkRoute(probe, topNav, approach, mask, out _, out walkWhy))
                    ladderWhy = "ladder top to the door: " + walkWhy;
            }
            if (ladderWhy != null) { reason = ladderWhy; return false; }
            reason = null; return true;
        }
        // #1305: the one ground ladder leg, ladder top to the door, behind the landing search's own
        // gate: deck navigation within 0.4 m of the authored top and 0.15 m of its height. The
        // search's deck proof advances it in slices on its probe deck; the hull passage's ground leg
        // (the re-landing) and touchdown's liveLeg diagnostic run it whole.
        internal static BlacksiteLandingGeometry.RouteCheck LadderLeg(Transform ship, in BlacksiteLandingAccess access, int mask, out string reason)
        {
            Vector3 top = ship.TransformPoint(access.LadderTopLocal);
            reason = NavMesh.SamplePosition(top, out var hit, .4f, DeckAreaMask) && Mathf.Abs(hit.position.y - top.y) <= .15f
                ? null : "ladder top has no matching deck navigation";
            return reason != null ? null
                : new BlacksiteLandingGeometry.RouteCheck(BlacksitePhysicalStep.ProbeTransform, top, ship.Find("DoorApproach").position, mask);
        }
        internal static bool TryLadderLeg(Transform ship, in BlacksiteLandingAccess access, int mask, out string reason)
        {
            var leg = LadderLeg(ship, access, mask, out reason);
            if (leg == null) return false;
            while (!leg.Complete) leg.Advance();
            reason = leg.Success ? null : "ladder top to the door: " + leg.Reason; return leg.Success;
        }
        // #1305: one owner per passage leg. The re-landing's ValidatePassageRoutes walks both legs
        // (TryShipPassage); the landing search proves the aisle and ladder legs on its probe deck and
        // the drop leg at every candidate pose (ground access and hover step-off). Touchdown publishes
        // the plan it seals and never re-judges it.
        internal static bool TryAisleToDoor(Transform ship, int mask, out string reason)
        {
            if (BlacksiteLandingGeometry.WalkRoute(BlacksitePhysicalStep.ProbeTransform, ship.Find("BoardingPoint").position,
                ship.Find("DoorApproach").position, mask, out _, out reason)) return true;
            reason = "aisle to door: " + reason; return false;
        }
        internal static bool TryDropToStaging(Vector3 drop, Vector3 exit, int mask, out string reason)
        {
            if (BlacksiteLandingGeometry.WalkRoute(BlacksitePhysicalStep.ProbeTransform, drop, exit, mask, out _, out reason)) return true;
            reason = "drop to staging: " + reason; return false;
        }
        internal static bool TryDeploymentSlot(Transform ship, Transform actor, Vector3 desired, Vector3 approach,
            IReadOnlyList<Vector3> occupied, int mask, out Vector3 position)
        {
            position = Vector3.zero;
            for (int i = -1; i < 18; i++)
            {
                Vector3 candidate = i < 0 ? desired : ship.TransformPoint(new Vector3((i % 2 == 0 ? -.65f : .65f), .1f, 5.3f - (i / 2) * 1.3f));
                if (!BlacksiteLandingGeometry.TryFloorNav(candidate, mask, out var surface)
                    || Mathf.Abs(ship.InverseTransformPoint(surface).y) > .15f) continue;
                var hit = new NavMeshHit { position = surface };
                bool separated = true;
                // A later fallback must not spawn ahead of a member already waiting to exit.
                for(int j=0;j<occupied.Count;j++)if(ship.InverseTransformPoint(hit.position).z>ship.InverseTransformPoint(occupied[j]).z+.1f){separated=false;break;}
                for (int j = 0; j < occupied.Count; j++) if (Vector3.ProjectOnPlane(occupied[j] - hit.position, Vector3.up).magnitude < 1.12f) { separated = false; break; }
                if (!separated || !BlacksitePhysicalStep.CanOccupy(actor, hit.position, mask)) continue;
                Vector3 aisle=ship.TransformPoint(new Vector3(0,0,5.3f));
                if (!BlacksiteLandingGeometry.WalkRoute(actor, hit.position, aisle, mask, out _, out _) || !BlacksiteLandingGeometry.WalkRoute(actor, aisle, approach, mask, out _, out _)) continue;
                position = hit.position + Vector3.up * .02f; return true;
            }
            return false;
        }
        // Aisle rows used for boarding seats (ship-local z). Members fill the deepest row first and
        // approach along their own column, so a later boarder never squeezes past an earlier one.
        private static readonly float[] BoardingRows = { 6.6f, 5.3f, 4f, 2.7f };
        private static readonly float[] BankedBoardingRows = { 6.6f, 5.3f, 4f, 2.7f, 1.4f, .1f, -1.2f, -2.5f, -3.8f, -5.1f };
        private static bool Banked(Transform ship) => Vector3.Angle(ship.up, Vector3.up) > 1f;
        private const float SeatColumnX = .65f, SeatApproachAhead = 1.4f;
        internal static Vector3 SeatApproach(Transform ship, Vector3 seat)
        {
            Vector3 local = ship.InverseTransformPoint(seat);
            return ship.TransformPoint(new Vector3(Mathf.Clamp(local.x, -SeatColumnX, SeatColumnX), 0, Mathf.Clamp(local.z + SeatApproachAhead, Banked(ship) ? -3.7f : 2.2f, 7.6f)));
        }
        internal static bool InBoardingArea(Transform ship, Vector3 position)
        {
            Vector3 local = ship.InverseTransformPoint(position);
            return Mathf.Abs(local.x) < 1.7f && local.z > (Banked(ship) ? -5.5f : 1.9f) && local.z < 7.6f && Mathf.Abs(local.y) < .6f;
        }
        internal static bool TryBoardingSlot(Transform ship, Transform actor, Vector3 approach, IReadOnlyList<Vector3> occupied, int mask, out Vector3 position)
        {
            // A bank can erode one side of the native deck mesh near the wall. Use the
            // deeper aisle rows, each with the same floor/body/path checks, rather than
            // pushing seats into the wall or widening the deck sampling radius.
            var rows = Banked(ship) ? BankedBoardingRows : BoardingRows;
            position = ship.TransformPoint(new Vector3(0, .1f, rows[0]));
            for (int row = rows.Length - 1; row >= 0; row--)
                for (int column = 0; column < 2; column++)
                {
                    Vector3 candidate = ship.TransformPoint(new Vector3(column == 0 ? -.65f : .65f, .1f, rows[row]));
                    if (!BlacksiteLandingGeometry.TryFloorNav(candidate, mask, out var surface) || Mathf.Abs(ship.InverseTransformPoint(surface).y) > .15f) continue;
                    bool separated = true;
                    for (int j = 0; j < occupied.Count && separated; j++)
                    {
                        // Never seat behind an occupied row: the newcomer would have to pass a standing member.
                        if (ship.InverseTransformPoint(occupied[j]).z > rows[row] + .4f) separated = false;
                        if (Vector3.ProjectOnPlane(occupied[j] - surface, Vector3.up).magnitude < 1.12f) separated = false;
                    }
                    if (!separated || !BlacksitePhysicalStep.CanOccupy(actor, surface, mask)) continue;
                    Vector3 columnPoint = SeatApproach(ship, surface);
                    if (!BlacksiteLandingGeometry.WalkRoute(actor, approach, columnPoint, mask, out _, out _)
                        || !BlacksiteLandingGeometry.WalkRoute(actor, columnPoint, surface, mask, out _, out _)) continue;
                    position = surface + Vector3.up * .02f; return true;
                }
            return false;
        }
        // #1233 S8: the four clearances the ladder ground route is tried at. The landing search
        // steps one variant per Step through TryLadderGroundRouteVariant; the synchronous form below
        // (touchdown access) keeps walking all four and taking the shortest.
        internal static readonly float[] LadderRouteClearances = { 0f, .8f, 1.8f, 3f };
        internal static bool TryLadderGroundRoute(Vector3 origin, Quaternion rotation, Transform actor, Vector3 from,
            in BlacksiteLandingAccess access, int mask, out Vector3[] ground, out string reason)
        {
            ground = null; reason = "no ground approach to ladder"; float bestMetres = float.PositiveInfinity;
            if (access.Hover) return BlacksiteLandingGeometry.WalkRoute(actor, from, origin + rotation * access.LadderMountLocal, mask, out ground, out reason);
            for (int variant = 0; variant < LadderRouteClearances.Length; variant++)
            {
                if (!TryLadderGroundRouteVariant(origin, rotation, actor, from, access, mask, variant, out var samples, out float metres, out string variantReason))
                { if (variantReason != null) reason = variantReason; continue; }
                if (metres < bestMetres) { ground = samples; bestMetres = metres; }
            }
            if (ground == null) return false;
            reason = null; return true;
        }
        /// <summary>
        /// One clearance variant of the ladder ground route: a native route that stays outside the
        /// future hull at clearance 0, or an explicit approach from beyond the bow and outboard at the
        /// larger clearances. <paramref name="reason"/> is null when the variant was simply
        /// unsupported (no ground for its waypoints) rather than refused.
        /// </summary>
        internal static bool TryLadderGroundRouteVariant(Vector3 origin, Quaternion rotation, Transform actor, Vector3 from,
            in BlacksiteLandingAccess access, int mask, int variant, out Vector3[] ground, out float metres, out string reason)
        {
            ground = null; metres = 0f; reason = null;
            float clearance = LadderRouteClearances[variant];
            Vector3 mount = origin + rotation * access.LadderMountLocal;
            var inverse = Quaternion.Inverse(rotation);
            bool Forbidden(Vector3 p) => BlacksiteHullBlockers.InsideFootprint(inverse * (p - origin), .3f);
            var points = new List<Vector3> { from };
            if (clearance > 0)
            {
                float x = access.LadderMountLocal.x + access.LadderOutward * clearance;
                foreach (float z in new[] { 15f + clearance, access.LadderMountLocal.z })
                {
                    Vector3 point = origin + rotation * new Vector3(x, access.LadderMountLocal.y, z);
                    if (!BlacksiteLandingGeometry.Ground(point, 5f, mask, null, out var floor)) return false;
                    points.Add(floor.point);
                }
            }
            points.Add(mount);
            var samples = new List<Vector3>();
            for (int i = 1; i < points.Count; i++)
            {
                var check = new BlacksiteLandingGeometry.RouteCheck(actor, points[i-1], points[i], mask,
                    forbidden: Forbidden, physicalCorners: clearance > 0 ? new[] { points[i-1], points[i] } : null,
                    areaMask: ~DeckAreaMask, groundNavTolerance: .6f);
                while (!check.Complete) check.Advance();
                if (!check.Success) { reason = check.Reason; return false; }
                metres += check.WalkedMetres;
                foreach (var sample in check.Corners)
                    if (samples.Count == 0 || Vector3.Distance(samples[samples.Count - 1], sample) > .02f) samples.Add(sample);
            }
            ground = samples.ToArray();
            return true;
        }
        internal static bool TryEgressSlots(Transform ship, Vector3 exit, int count, int mask, out Vector3[] positions, out string reason)
        {
            return TryEgressSlots(ship.rotation,exit,count,mask,out positions,out reason);
        }
        internal static bool TryEgressSlots(Quaternion rotation,Vector3 exit,int count,int mask,out Vector3[] positions,out string reason)
        {
            positions = new Vector3[count]; reason = null;
            for (int member = 0; member < count; member++)
            {
                Vector3 desired = exit + (rotation*Vector3.right) * (member % 2 == 0 ? -.85f : .85f) + (rotation*Vector3.forward) * (1f + ((count - 1) / 2 - member / 2) * 1.3f);
                if (!TryStagingSlot(exit, desired, mask, out positions[member], out reason)) return false;
            }
            return true;
        }
        // #1305: one staging slot - physical floor with navigation at it, walked to from the exit.
        internal static bool TryStagingSlot(Vector3 exit, Vector3 slot, int mask, out Vector3 position, out string reason)
        {
            if (BlacksiteLandingGeometry.TryFloorNav(slot, mask, out position, .6f))
                return BlacksiteLandingGeometry.WalkRoute(BlacksitePhysicalStep.ProbeTransform, exit, position, mask, out _, out reason);
            reason = "ground staging slot has no matching physical navigation"; return false;
        }
        // #873: one-way links. The drop link leaves the catwalk just behind the lip for the drop
        // landing; the ladder link climbs from the mount to the catwalk top. Ground navigation can
        // therefore reach the deck only at the ladder and leave it only at the lip. The drop link
        // is a point link: a link with width only connects where its whole segment lies on
        // navmesh, and at the eroded deck edge that is never the case (the fixture's width survey
        // completes only at 0). #874 S3: both are crossed by the navigator's scripted ship crossing.
        internal const float DropLinkWidth = 0f, LadderLinkWidth = .6f;
        // Ship-local start of the drop link, sampled onto the deck area.
        internal static readonly Vector3 DropLinkStart = BlacksiteBoardingGeometry.Lip + new Vector3(0, 0, -.35f);
        // #874 N1: the lip end of every deck walk. The eroded deck navigation stops short of LipStep
        // by whatever the bake's voxel phase and the landed tilt leave (0.426 m on Icebound
        // 2026-09-23, past the 0.40 m ship-floor bind, and the door-to-lip leg aborted the
        // touchdown), so no deck leg binds LipStep itself. A walk to the lip ends at the deck
        // navigation the drop link leaves from and walks the rest physically, as the entrance
        // approach walks its doorway tail; a walk from the lip (the rappel top) starts the same way
        // in reverse. The 0.40 m bind stays as it is for seats and slots.
        internal static bool TryLipEnds(Transform ship, int mask, out Vector3 nav, out Vector3 lip, out string reason)
        {
            nav = lip = default;
            if (!NavMesh.SamplePosition(ship.TransformPoint(DropLinkStart), out var deck, 1.2f, DeckAreaMask))
            { reason = "no deck navigation within 1.2m of the lip"; return false; }
            if (!BlacksiteLandingGeometry.Ground(ship.TransformPoint(BlacksiteBoardingGeometry.LipStep), .6f, mask, null, out var floor)
                || !IsShipFloor(floor.collider))
            { reason = "the lip step is not on the ship floor"; return false; }
            nav = deck.position; lip = floor.point; reason = null; return true;
        }
        internal static bool TryLipTail(Transform ship, Transform actor, int mask, bool outbound, out Vector3 nav, out Vector3 lip, out string reason)
        {
            if (!TryLipEnds(ship, mask, out nav, out lip, out reason)) return false;
            if (BlacksiteLandingGeometry.WalkCorners(actor, outbound ? new[] { nav, lip } : new[] { lip, nav }, mask, out reason)) return true;
            reason = $"physical lip tail of {LipNavGap(nav, lip):0.00}m: {reason}"; return false;
        }
        // The horizontal gap from LipStep's floor to that navigation: what the old endpoint had to
        // bind inside 0.40 m, printed at acceptance ([UAT][Landing]) and at touchdown ([UAT][ShipAccess]).
        internal static float LipNavGap(Vector3 nav, Vector3 lip) => Vector3.ProjectOnPlane(lip - nav, Vector3.up).magnitude;
        internal static List<NavMeshLinkInstance> ConnectAccess(Transform ship, Vector3 dropLanding, Vector3? ladderMount, Vector3? ladderTopLocal)
        {
            var links=new List<NavMeshLinkInstance>();
            if(NavMesh.SamplePosition(ship.TransformPoint(DropLinkStart),out var deck,1.2f,DeckAreaMask))
                links.Add(AddOneWayLink(deck.position,dropLanding,DropLinkWidth));
            if(ladderMount.HasValue && ladderTopLocal.HasValue && TrySampleLadderTop(ship,ladderTopLocal.Value,out var top))
                links.Add(AddOneWayLink(ladderMount.Value,top,LadderLinkWidth));
            return links;
        }
        // #1219 C2: the ladder link's end - the ship-local ladder top sampled onto the deck area.
        // The one sample the link, the direct board crossing (TryBoardingLinkEnds) and the hover hull passage's ladder top use.
        internal static bool TrySampleLadderTop(Transform ship, Vector3 ladderTopLocal, out Vector3 top)
        {
            bool sampled = NavMesh.SamplePosition(ship.TransformPoint(ladderTopLocal), out var hit, .8f, DeckAreaMask);
            top = hit.position;
            return sampled;
        }
        internal static NavMeshLinkInstance AddOneWayLink(Vector3 start,Vector3 end,float width)
        {
            var settings=NavMesh.GetSettingsByIndex(0);
            return NavMesh.AddLink(new NavMeshLinkData{startPosition=start,endPosition=end,width=width,bidirectional=false,agentTypeID=settings.agentTypeID,costModifier=1});
        }
        private static Transform _searchDeck, _searchSource;
        private static readonly Queue<(Transform Source, Transform Parent)> SearchCopies = new Queue<(Transform, Transform)>();
        private static readonly Dictionary<string, (string Reason, float LipNavGap)> DeckProofs = new Dictionary<string, (string, float)>();
        internal static double LastDeckProofMilliseconds;
        // Only called after a candidate passes its moon route. Reuse a collider-only shell;
        // never instantiate network behaviours or a second visible ship during the search.
        private static AsyncOperation _deckBuild;
        private static NavMeshData _proofData;
        private static NavMeshDataInstance _proofInstance;
        private static BlacksiteLandingGeometry.RouteCheck _proofRoute;
        private static int _proofStage;
        private static string _proofKey;
        private static float _proofLipNavGap;
        // Build the candidate deck asynchronously, then audit its walking legs in small batches.
        // The caller yields while this proof is pending; no 40ms synchronous nav bake at spawn.
        internal static bool AdvanceCandidateDeck(Quaternion rotation, in BlacksiteLandingAccess access, out bool complete, out string reason, out float lipNavGap)
        {
            complete = true; reason = "ship geometry not configured"; lipNavGap = float.NaN;
            var source = BlacksiteLandingGeometry.ShipGeometry;
            if (source == null) return false;
            var timer = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                if (_searchDeck == null || _searchSource != source)
                {
                    if (_searchDeck != null) { _searchDeck.gameObject.SetActive(false); Object.Destroy(_searchDeck.gameObject); }
                    _searchSource = source; DeckProofs.Clear();
                    SearchCopies.Clear();
                    _searchDeck = CopyGeometryNode(source, null); _searchDeck.name = "BlacksiteDeckSearchProbe";
                    _searchDeck.position = new Vector3(10000, 10000, 10000);
                    _searchDeck.gameObject.SetActive(false);
                    _searchDeck.gameObject.AddComponent<BlacksiteShipNavigationRoot>();
                    complete = false; reason = null; return false;
                }
                if (SearchCopies.Count > 0)
                {
                    // The source has hundreds of transforms. Copy a bounded batch without
                    // enabling its colliders until the entire isolated shell is ready.
                    for (int copied = 0; copied < 12 && SearchCopies.Count > 0; copied++)
                    { var pair = SearchCopies.Dequeue(); CopyGeometryNode(pair.Source, pair.Parent); }
                    complete = false; reason = null; return false;
                }
                string key = rotation.eulerAngles.ToString("F2") + ":" + access.LadderIndex;
                if (DeckProofs.TryGetValue(key, out var proof)) { reason = proof.Reason; lipNavGap = proof.LipNavGap; return reason == null; }
                complete = false; reason = null;
                if (_deckBuild == null)
                {
                    _proofKey = key; _proofStage = 0; _proofLipNavGap = float.NaN;
                    _searchDeck.gameObject.SetActive(true);
                    _searchDeck.SetPositionAndRotation(new Vector3(10000, 10000, 10000), rotation);
                    Physics.SyncTransforms();
                    _deckBuild = BeginBuild(_searchDeck, null, out _proofData); // #874 N1: the touchdown's own bake, on the probe shell
                    return false;
                }
                if (!_deckBuild.isDone) return false;
                Vector3 approach = _searchDeck.Find("DoorApproach").position;
                if (_proofStage == 0)
                {
                    _proofInstance = NavMesh.AddNavMeshData(_proofData);
                    // #1305: the ladder leg's gate (LadderLeg), then the aisle-to-door leg, then the
                    // ladder leg's route on later calls so the Deck step stays bounded.
                    _proofRoute = LadderLeg(_searchDeck, access, ~0, out var topWhy);
                    if (_proofRoute == null) { reason = "search " + topWhy; complete = true; }
                    else if (!TryAisleToDoor(_searchDeck, ~0, out var aisleWhy))
                    { reason = "search deck passage: " + aisleWhy; complete = true; }
                    else { _proofStage = 1; return false; }
                }
                if (!complete)
                {
                    _proofRoute.Advance(12);
                    if (!_proofRoute.Complete) return false;
                    if (!_proofRoute.Success) { reason = "search deck passage: " + _proofRoute.Reason; complete = true; }
                    else if (_proofStage == 1)
                    {
                        // #874 N1: door to lip exactly as the hull passage walks it: to the drop link's
                        // deck navigation, then the physical tail onto LipStep.
                        if (!TryLipTail(_searchDeck, BlacksitePhysicalStep.ProbeTransform, ~0, true, out var lipNav, out var lip, out var tailWhy))
                        { reason = "search deck passage: door to lip: " + tailWhy; complete = true; }
                        else
                        {
                            _proofLipNavGap = LipNavGap(lipNav, lip);
                            _proofRoute = new BlacksiteLandingGeometry.RouteCheck(BlacksitePhysicalStep.ProbeTransform, approach, lipNav, ~0);
                            _proofStage = 2; return false;
                        }
                    }
                    else complete = true;
                }
                if (DeckProofs.Count < 128) DeckProofs[_proofKey] = (reason, _proofLipNavGap);
                lipNavGap = _proofLipNavGap;
                _proofInstance.Remove(); Object.Destroy(_proofData); _proofData = null; _deckBuild = null; _proofRoute = null;
                _searchDeck.gameObject.SetActive(false); Physics.SyncTransforms();
                return reason == null;
            }
            finally { LastDeckProofMilliseconds = System.Math.Max(LastDeckProofMilliseconds, timer.Elapsed.TotalMilliseconds); }
        }
        private static Transform CopyGeometryNode(Transform source, Transform parent)
        {
            var copy = new GameObject(source.name).transform;
            copy.SetParent(parent, false); copy.localPosition = source.localPosition;
            copy.localRotation = source.localRotation; copy.localScale = source.localScale;
            if (source.name == "AnimatedShipDoor") return copy; // runtime builds with the open door excluded
            // #874 N1: every solid collider shape the touchdown bake collects, convex flag included.
            foreach (var collider in source.GetComponents<Collider>())
            {
                if (!collider.enabled || collider.isTrigger) continue;
                if (collider is MeshCollider mesh && mesh.sharedMesh != null)
                { var m = copy.gameObject.AddComponent<MeshCollider>(); m.convex = mesh.convex; m.sharedMesh = mesh.sharedMesh; }
                else if (collider is BoxCollider box)
                { var b = copy.gameObject.AddComponent<BoxCollider>(); b.center = box.center; b.size = box.size; }
                else if (collider is SphereCollider sphere)
                { var s = copy.gameObject.AddComponent<SphereCollider>(); s.center = sphere.center; s.radius = sphere.radius; }
                else if (collider is CapsuleCollider capsule)
                { var c = copy.gameObject.AddComponent<CapsuleCollider>(); c.center = capsule.center; c.radius = capsule.radius; c.height = capsule.height; c.direction = capsule.direction; }
            }
            foreach (Transform child in source) if (child.gameObject.activeSelf) SearchCopies.Enqueue((child, copy));
            return copy;
        }
        // #874 N1: the one deck bake. The landing search's probe shell and the landed ship both bake
        // through here, so a leg the search proves stands on the navigation the landed hull
        // walks. Unity voxelises in the NavMeshData's own frame, and both used to bake in the
        // world frame, so the eroded lip edge fell at one voxel phase for the probe at 10000 m and
        // another for the ship at Icebound's (-151.23, 2.49, -45.47). The frame is now the hull's
        // origin and yaw: the phase belongs to the hull, not to where it stands. Tilt stays in the
        // sources, because walkable slope is judged against world up. Only enabled, solid colliders
        // count, however they were gathered (the probe copies nothing else).
        private static readonly Bounds DeckBounds = new Bounds(Vector3.up * 2, new Vector3(50, 18, 50));
        private static Quaternion DeckFrame(Quaternion rotation) => Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
        private static NavMeshBuildSettings DeckSettings()
        {
            var settings = NavMesh.GetSettingsByIndex(0); settings.agentHeight = 2.8f; settings.agentRadius = .45f;
            // Preserve physical floor heights between interior obstacles instead of interpolating
            // a coarse polygon above the deck and invalidating otherwise walkable seat approaches.
            settings.buildHeightMesh = true;
            return settings;
        }
        private static List<NavMeshBuildSource> DeckSources(Transform root, Transform openDoor)
        {
            var sources = new List<NavMeshBuildSource>(); var markups = new List<NavMeshBuildMarkup>();
            if (openDoor != null) markups.Add(new NavMeshBuildMarkup { root = openDoor, ignoreFromBuild = true });
            NavMeshBuilder.CollectSources(root, ~0, NavMeshCollectGeometry.PhysicsColliders, DeckArea, markups, sources);
            for (int i = sources.Count - 1; i >= 0; i--)
            {
                var source = sources[i];
                if (source.component is Collider collider && (!collider.enabled || collider.isTrigger)) { sources.RemoveAt(i); continue; }
                source.area = DeckArea; sources[i] = source;
            }
            return sources;
        }
        internal static NavMeshData Build(Transform ship, Transform openDoor)
            => NavMeshBuilder.BuildNavMeshData(DeckSettings(), DeckSources(ship, openDoor), DeckBounds, ship.position, DeckFrame(ship.rotation));
        // #874 Q11 (F11): the landed ship's bake, scheduled over frames exactly as the search proof
        // above schedules its own (the 23 September touchdown frame measured 113 ms with the bake
        // run synchronously inside it). Same settings, sources, bounds and frame as Build, which the
        // native fixtures still call synchronously as their setup, so the deck the ship lands on is
        // the deck they prove. The caller holds the hull un-grounded until the operation is done.
        internal static AsyncOperation BeginBuild(Transform ship, Transform openDoor, out NavMeshData data)
        {
            var settings = DeckSettings();
            data = new NavMeshData(settings.agentTypeID) { position = ship.position, rotation = DeckFrame(ship.rotation) };
            return NavMeshBuilder.UpdateNavMeshDataAsync(data, settings, DeckSources(ship, openDoor), DeckBounds);
        }
    }
}
