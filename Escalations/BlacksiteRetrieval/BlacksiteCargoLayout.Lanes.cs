using System.Collections.Generic;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal enum BlacksiteCargoLaneKind : byte { DropLanding, Passage, Egress, LadderFoot, LadderStaging, CartStaging, EntranceRoute }
    internal readonly struct BlacksiteCargoLane
    {
        internal readonly BlacksiteCargoLaneKind Kind;
        internal readonly string Label;
        // Traversable lane as a segment with a clearance radius; A == B is a standing disc.
        internal readonly Vector3 A, B;
        internal readonly float Radius;
        // Tier 0 is never sacrificed. The bounded fallback releases higher tiers in descending order.
        internal readonly int Tier;
        internal BlacksiteCargoLane(BlacksiteCargoLaneKind kind, string label, Vector3 a, Vector3 b, float radius, int tier)
        { Kind = kind; Label = label; A = a; B = b; Radius = radius; Tier = tier; }
    }
    // #874 Q5 (2026-09-25): the half of the cargo layout that is plain plan geometry - the lane set
    // both landing modes owe and the one footprint test the rope-foot deployment and the rope-foot
    // queue places read. Nothing here touches physics or navigation, so the headless
    // BlacksiteTierTwoThreeRegression compiles this file on its own and pins the rule the runtime
    // decides with.
    internal static partial class BlacksiteCargoLayout
    {
        // Soldier agent radius (ContractAssetLoader configures 0.45) plus a walking skin; the cart
        // is wider than a soldier and the loading approach is where a player opens the crate.
        internal const float AgentRadius = .45f, LaneClearance = .6f, CartClearance = .95f, LoadingReach = 1.25f;
        // #874 Q5: the shipped BlacksiteCargoCrate's measured half extents (1.200 x 0.566 x 0.788 m,
        // pinned against the bundle by CargoDeckChecks and CargoLayoutChecks). The headless rows
        // prove the footprint rule against this box; the ship reads the same rule with the
        // footprint it measures off the prefab at deployment.
        internal static readonly Vector3 ShippedCrateHalf = new Vector3(.600f, .283f, .394f);
        internal static float Diagonal(Vector3 half) => Mathf.Sqrt(half.x * half.x + half.z * half.z);
        // The box a candidate site reserves: the measured footprint widened by a soldier's clearance.
        internal static Vector3 Reserved(Vector3 half) => new Vector3(half.x + LaneClearance, half.y, half.z + LaneClearance);

        // The lanes a layout owes in either landing mode, in world space: the egress fan beyond the
        // exit, the cart staging, the facility door itself and the outgoing entrance route near the
        // rendezvous. `forward` is the ship's heading projected onto the ground plane.
        //
        // #874 Q5: the door is a lane of its own. 23 September round 1 put the rope foot 2 m from
        // the main door (log 6461); the route lanes start at the exit, 2 m ahead of the foot, so
        // they could pass a crate standing on a doorstep. BlacksiteEntrancePolicy.UseRange is where
        // a body stands to use the door, so a crate inside it blocks everyone who comes out.
        internal static void AddSharedLanes(List<BlacksiteCargoLane> lanes, Vector3 forward, Vector3 rendezvous, Vector3 exit,
            Vector3[] groundRoute, Vector3 cartStaging, Vector3 entrance)
        {
            // The egress fan TryEgressSlots lays out: one to 3.6 m beyond the exit, 0.85 m either side.
            lanes.Add(new BlacksiteCargoLane(BlacksiteCargoLaneKind.Egress, "egress staging", exit + forward, exit + forward * 3.6f, .85f + LaneClearance, 0));
            lanes.Add(new BlacksiteCargoLane(BlacksiteCargoLaneKind.CartStaging, "cart staging", cartStaging, cartStaging, CartClearance + .8f, 2));
            lanes.Add(new BlacksiteCargoLane(BlacksiteCargoLaneKind.EntranceRoute, "entrance door", entrance, entrance, BlacksiteEntrancePolicy.UseRange, 0));
            // The outgoing ground route the access was validated against, near the ship only: the
            // crate never reaches the far corridor and the audit stays bounded.
            if (groundRoute == null || groundRoute.Length < 2) return;
            lanes.Add(new BlacksiteCargoLane(BlacksiteCargoLaneKind.EntranceRoute, "route entry", exit, groundRoute[0], AgentRadius + LaneClearance, 0));
            for (int i = 1; i < groundRoute.Length && i <= 24; i++)
            {
                if ((groundRoute[i - 1] - rendezvous).sqrMagnitude > 400f && (groundRoute[i] - rendezvous).sqrMagnitude > 400f) continue;
                lanes.Add(new BlacksiteCargoLane(BlacksiteCargoLaneKind.EntranceRoute, "entrance route " + i, groundRoute[i - 1], groundRoute[i], AgentRadius + LaneClearance, 0));
            }
        }
        // #874 Q5: the rope-foot layout of a hover station. The drop landing and the passage are the
        // rope foot itself, which the squad and the crate share in time (squad down, crate down,
        // crate up, squad up), so they are not lanes the crate must stay out of.
        internal static List<BlacksiteCargoLane> CableLanes(Vector3 forward, Vector3 rendezvous, Vector3 exit,
            Vector3[] groundRoute, Vector3 cartStaging, Vector3 entrance)
        {
            var lanes = new List<BlacksiteCargoLane>();
            AddSharedLanes(lanes, forward, rendezvous, exit, groundRoute, cartStaging, entrance);
            return lanes;
        }

        // #874 Q5: the one footprint test. The crate is its plan circle (the measured half-diagonal
        // about its centre, so any yaw is covered); a lane is blocked when that circle comes inside
        // the lane's clearance radius of its centreline.
        internal static bool FootprintBlocks(in BlacksiteCargoLane lane, Vector3 centre, float diagonal)
            => PlanDistance(centre, lane.A, lane.B) < lane.Radius + diagonal;
        // A standing place (a rope-foot queue place, a rally slot) is inside the staged footprint
        // when it is within the crate circle plus one lane clearance: a soldier standing just
        // outside keeps LaneClearance - AgentRadius of air between its body and every crate corner.
        internal static bool FootprintContains(Vector3 centre, float diagonal, Vector3 point)
            => PlanDistance(centre, point, point) < diagonal + LaneClearance;
        // #874 Q5: whether a crate standing on this rope foot clears every lane of the station. The
        // ship refuses to lower the cache onto a foot that fails this, so a crate is never set down
        // on a site it then has to be hauled back up from. The landing does not ask it (#1305): the
        // crate stays on the deck instead. `blocked` is the first lane's label, for the reason.
        internal static bool CableSiteClear(Vector3 footprintCentre, float diagonal, List<BlacksiteCargoLane> lanes, out string blocked)
        {
            blocked = null;
            if (lanes == null) return true;
            foreach (var lane in lanes)
                if (FootprintBlocks(lane, footprintCentre, diagonal)) { blocked = lane.Label; return false; }
            return true;
        }
        // Horizontal distance from a point to a segment; heights are ignored because every lane
        // point is a floor point and a body stands taller than the crate.
        private static float PlanDistance(Vector3 point, Vector3 a, Vector3 b)
        {
            float abx = b.x - a.x, abz = b.z - a.z, apx = point.x - a.x, apz = point.z - a.z;
            float length = abx * abx + abz * abz;
            float t = length > 1e-8f ? Mathf.Clamp((apx * abx + apz * abz) / length, 0f, 1f) : 0f;
            float dx = apx - abx * t, dz = apz - abz * t;
            return Mathf.Sqrt(dx * dx + dz * dz);
        }
    }
}
