using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.AI;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Physical evidence, not terrain names or distant navigation samples, accepts a landing.
    internal static class BlacksiteLandingGeometry
    {
        private static readonly Collider[] Overlaps = new Collider[128];
        private static readonly RaycastHit[] GroundHits = new RaycastHit[64];
        // #873: the ship lands on its four thruster pods like the crew ship. The deck sits a measured
        // height (footToDeck) above the highest foot; the hull below the deck may sink into terrain
        // the way the crew ship's does, but nothing may reach the catwalk underside or the belly.
        internal const float MaximumRelief = 3.5f;
        // #1233 S7: the hull top (the cabin volume's ceiling) is the flight envelope's too.
        internal const float CabinHalfWidth = 3.4f, HullTop = 5.96f;
        // #1293 P2: the cabin box (deck-relative floor, centre, half length) ClearUsableVolume and CabinBlockedAtEveryDeck share.
        internal const float CabinFloor = .04f, CabinZ = .675f, CabinHalfLength = 11.175f;
        // The feet of the last TrySupport call, for the landing search's near-miss log.
        internal static string LastSupportDetail = string.Empty;
        internal static Vector3[] LowerHullSamples;
        internal static Transform ShipGeometry { get; private set; }
        internal static bool ConfigureShip(Transform ship, out string reason)
        {
            reason = "requires the BlacksiteGeometryV2 ship bundle with measured feet and extended player ladders";
            LowerHullSamples = null; ShipGeometry = null;
            if (ship == null || ship.Find("BlacksiteGeometryV2") == null) return false;
            for (int i = 0; i < BlacksiteLandingGearPolicy.Feet.Length; i++)
            {
                var anchor = ship.Find("LandingFoot" + i);
                var foot = BlacksiteLandingGearPolicy.Feet[i];
                if (anchor == null || Vector3.Distance(anchor.localPosition, new Vector3(foot.X, -BlacksiteLandingGearPolicy.FootDepths[i], foot.Z)) > .01f) return false;
            }
            for (int i = 0; i < 2; i++)
                foreach (string name in new[] { "LadderRungBottom", "LadderBottom", "LadderTop", "LadderNode", "LadderHorizontal", "LadderTrigger" })
                    if (ship.Find(name + i) == null) return false;
            var samples = ship.Find("LandingHullSamples");
            if (samples == null || samples.childCount == 0 || samples.childCount > 192) return false;
            LowerHullSamples = new Vector3[samples.childCount];
            for (int i = 0; i < samples.childCount; i++) LowerHullSamples[i] = samples.GetChild(i).localPosition;
            ShipGeometry = ship; reason = null; return true;
        }
        internal static int LastSupportRays, LastTotalSupportRays, LastHullQueries, LastPoseCount;
        // #1293 P2: the deck heights the last cleared TryCornerSupport leaves TrySupport able to return.
        internal static float LastCornerDeckLow, LastCornerDeckHigh;
        private static readonly float[] SupportHeights = new float[4];
        private static readonly Vector3[] PatchOffsets = { Vector3.zero, new Vector3(.025f,0,.16f), new Vector3(-.025f,0,-.16f) };
        internal static bool TrySupport(Vector3 candidate, Quaternion rotation, int mask, float footToDeck, out Vector3 origin, out string reason, Transform ignoreRoot = null, Transform attachedCargo = null)
        {
            origin=candidate;reason=null;LastSupportRays=LastHullQueries=0;
            if(Vector3.Angle(Vector3.up,rotation*Vector3.up)>BlacksiteLandingGearPolicy.MaximumTilt+.01f)
            {reason="gear tilt exceeds 8 degrees";return false;}
            var feet=BlacksiteLandingGearPolicy.Feet;var levels=new float[feet.Length];
            float highest=float.NegativeInfinity;string detail="feet=[";
            for(int i=0;i<feet.Length;i++)
            {
                var foot=feet[i];float high=float.NegativeInfinity,low=float.PositiveInfinity;int supported=0;
                Vector3 local=new Vector3(foot.X,-BlacksiteLandingGearPolicy.FootDepths[i],foot.Z);
                foreach(var offset in PatchOffsets)
                {
                    // Ground candidate, not the raised deck, anchors the downward-only window.
                    Vector3 expected = candidate + rotation * (local + offset);
                    // Search supplies a ground candidate; revalidation supplies the settled deck.
                    expected.y = candidate.y - (ignoreRoot != null ? footToDeck : 0f);
                    LastSupportRays++; LastTotalSupportRays++;
                    if (!ProbeSupportGround(expected, footToDeck, mask, ignoreRoot, out var hit)) continue;
                    if(hit.normal.y<BlacksiteLandingGearPolicy.FootMinNormalY)continue;
                    supported++;high=Mathf.Max(high,hit.point.y);low=Mathf.Min(low,hit.point.y);
                }
                if(supported<2 || high-low>.3f) {reason="gear patch "+foot.Name+" lacks continuous support";return false;}
                SupportHeights[i] = high; levels[i]=high-(rotation*local).y;highest=Mathf.Max(highest,levels[i]);
                detail+=(i==0?"":" ")+foot.Name+" "+high.ToString("0.00");
            }
            origin.y=highest;
            var contacts=new List<int>();
            for(int i=0;i<feet.Length;i++)if(highest-levels[i]<=BlacksiteLandingGearPolicy.ContactTolerance)contacts.Add(i);
            Vector2 centroid=Vector2.zero;foreach(var foot in feet)centroid+=new Vector2(foot.X,foot.Z);centroid/=feet.Length;
            bool stable=contacts.Count>=3;
            if(contacts.Count==2)
            {
                Vector2 a=new Vector2(feet[contacts[0]].X,feet[contacts[0]].Z)-centroid;
                Vector2 c=new Vector2(feet[contacts[1]].X,feet[contacts[1]].Z)-centroid;
                float length=(c-a).magnitude;
                stable=Vector2.Dot(a,c)<0 && length>4f && Mathf.Abs(a.x*c.y-a.y*c.x)/length<=1.5f;
            }
            LastSupportDetail=detail+"] contacts="+contacts.Count+" deck="+origin.y.ToString("0.00");
            if(!stable){reason="gear feet spread leaves unstable contacts";return false;}
            return ClearUsableVolume(origin,rotation,mask,ignoreRoot,footToDeck,out reason,attachedCargo);
        }
        // #873 S12 U5 (2): the candidate pre-filter, asked at the corners of the same four-foot
        // footprint TrySupport uses, at the candidate's yaw and through the same downward window,
        // BEFORE TrySupport. It exists so a moon with no ground under the hull stops paying twelve
        // support rays, a stability solve and the whole ClearUsableVolume hull pass per heading: the
        // 2026-09-10 Assurance search spent 973,161 support rays and 676,825 hull queries on 1,198
        // sites and accepted none of them.
        //
        // It refuses only what TrySupport provably refuses. TrySupport calls a foot supported when
        // at least two of its three patch probes find ground, so a foot can only fail when at least
        // two of the three MISS, and that is the one thing this asks. It stops probing a foot the
        // moment the answer is settled - two misses refuse the candidate, two hits clear the foot -
        // so a refusal costs two rays at the first bad foot and a candidate that goes on to the full
        // solve costs two to three rays a foot for the certainty that the full solve is worth doing.
        //
        // Existence only, deliberately: the surface-normal and patch-spread tests stay in TrySupport,
        // where the fitted pose is known, so nothing here can refuse a site the real check accepts.
        // #1293 P2: it also publishes the deck heights TrySupport can still return: a supported foot has two valid
        // hits of its three patch rays, so one hit found here is valid and the spread rule caps the other at +.3 m.
        internal static bool TryCornerSupport(Vector3 candidate, Quaternion rotation, int mask, float footToDeck, out string reason)
        {
            var feet = BlacksiteLandingGearPolicy.Feet;
            float deckLow = float.NegativeInfinity, deckHigh = float.NegativeInfinity;
            for (int i = 0; i < feet.Length; i++)
            {
                Vector3 local = new Vector3(feet[i].X, -BlacksiteLandingGearPolicy.FootDepths[i], feet[i].Z);
                int found = 0, missing = 0;
                float minHit = float.PositiveInfinity, maxHit = float.NegativeInfinity;
                for (int patch = 0; patch < PatchOffsets.Length && missing < 2 && found < 2; patch++)
                {
                    // The search supplies a ground candidate, so the window is anchored on the
                    // candidate exactly as TrySupport anchors it when ignoreRoot is null.
                    Vector3 expected = candidate + rotation * (local + PatchOffsets[patch]);
                    expected.y = candidate.y;
                    LastSupportRays++; LastTotalSupportRays++;
                    if (!ProbeSupportGround(expected, footToDeck, mask, null, out var hit)) { missing++; continue; }
                    found++; minHit = Mathf.Min(minHit, hit.point.y); maxHit = Mathf.Max(maxHit, hit.point.y);
                }
                if (missing >= 2) { reason = "corner rays find no ground"; return false; }
                float dy = -(rotation * local).y;
                deckLow = Mathf.Max(deckLow, minHit + dy); deckHigh = Mathf.Max(deckHigh, maxHit + .3f + dy);
            }
            LastCornerDeckLow = deckLow; LastCornerDeckHigh = deckHigh;
            reason = null; return true;
        }
        // #1293 P2: the ClearUsableVolume cabin box cut to the span every deck in that window shares: a hit here is a
        // level pose whose cabin test refuses at any deck the support rays return. Yaw-only rotation.
        internal static bool CabinBlockedAtEveryDeck(Vector3 candidate, Quaternion rotation, int mask, out string reason)
        {
            reason = null;
            float bottom = LastCornerDeckHigh + CabinFloor, top = LastCornerDeckLow + HullTop;
            if (top <= bottom) return false;
            int count = Physics.OverlapBoxNonAlloc(new Vector3(candidate.x, (bottom + top) * .5f, candidate.z) + rotation * new Vector3(0, 0, CabinZ),
                new Vector3(CabinHalfWidth, (top - bottom) * .5f, CabinHalfLength), Overlaps, rotation, mask, QueryTriggerInteraction.Ignore);
            return !ClearOverlaps(count, null, null, "cabin volume", out reason);
        }
        // #1305 C7: enabled carving obstacles off any Blacksite hull, as world bounds of centre +/- size/2 through the transform (size: a capsule's height reads back halved).
        internal static (Bounds bounds, string name)[] WorldCarvers()
        {
            var carvers = new List<(Bounds, string)>();
            foreach (var o in UnityEngine.Object.FindObjectsOfType<NavMeshObstacle>())
            {
                if (!o.carving || !o.isActiveAndEnabled || o.GetComponentInParent<BlacksiteShipNavigationRoot>() != null) continue;
                var bounds = new Bounds(o.transform.TransformPoint(o.center), Vector3.zero);
                for (int i = 0; i < 8; i++) bounds.Encapsulate(o.transform.TransformPoint(o.center + Vector3.Scale(o.size * .5f, new Vector3(i & 1, i >> 1 & 1, i >> 2 & 1) * 2f - Vector3.one)));
                carvers.Add((bounds, (o.transform.parent != null ? o.transform.parent.name + "/" : "") + o.name));
            }
            return carvers.ToArray();
        }
        // #1305 C7: a carver reaching the hull band (catwalk underside to hull top, in the hull frame) with its centre inside the hull footprint.
        internal static bool DeckCarved(Vector3 origin, Quaternion rotation, (Bounds bounds, string name)[] carvers, out string reason)
        {
            reason = null; var inverse = Quaternion.Inverse(rotation);
            foreach (var (bounds, name) in carvers)
                if (inverse * (bounds.center - origin) is var local && local.y + bounds.extents.y > BlacksiteLandingGearPolicy.CatwalkUnderside
                    && local.y - bounds.extents.y < HullTop && BlacksiteHullBlockers.InsideFootprint(local, Mathf.Max(bounds.extents.x, bounds.extents.z)))
                { reason = "world carver on the deck at " + name; return true; }
            return false;
        }
        // A level hull imposes a near-flat terrain rule on a ten-metre wheelbase. A gentle
        // fitted bank puts the actual feet on sloping terrain instead of increasing their air gap.
        // #1233 S8: two poses, each a TrySupport (twelve support rays and the stability solve; the
        // hull-volume pass only on a pose the spread rule passed). The landing search steps them
        // separately through TrySupport and TryBankedRotation, because the pair was the 5.5-6.0 ms
        // step behind its longest slice on every cached moon; this synchronous form is the fixtures'.
        internal static bool TryGroundPlacement(Vector3 candidate, Quaternion yaw, int mask, float footToDeck,
            out Vector3 origin, out Quaternion rotation, out string reason, Transform ignoreRoot = null)
        {
            LastPoseCount=1; LastTotalSupportRays=0;
            rotation = yaw;
            if (TrySupport(candidate, rotation, mask, footToDeck, out origin, out reason, ignoreRoot)) return true;
            if (!TryBankedRotation(yaw, reason, out rotation)) return false;
            LastPoseCount=2;
            if (!TrySupport(candidate, rotation, mask, footToDeck, out origin, out reason, ignoreRoot)) return false;
            LastSupportDetail += " tilt=" + Vector3.Angle(Vector3.up, rotation * Vector3.up).ToString("0.0");
            return true;
        }
        /// <summary>
        /// The banked second pose after a level <see cref="TrySupport"/> refused for gear spread:
        /// the plane fitted through the four measured foot heights, tilted at most MaximumTilt.
        /// False when the refusal was anything else or the feet are degenerate. Reads the heights the
        /// last TrySupport measured, so it is asked immediately after that call.
        /// </summary>
        internal static bool TryBankedRotation(Quaternion yaw, string levelReason, out Quaternion rotation)
        {
            rotation = yaw;
            if (levelReason == null || !levelReason.StartsWith("gear feet spread")) return false;
            var feet = BlacksiteLandingGearPolicy.Feet;
            float mx = 0, mz = 0, my = 0;
            for (int i = 0; i < feet.Length; i++)
            { mx += feet[i].X; mz += feet[i].Z; my += SupportHeights[i] + BlacksiteLandingGearPolicy.FootDepths[i]; }
            mx /= feet.Length; mz /= feet.Length; my /= feet.Length;
            float xx = 0, zz = 0, xz = 0, xy = 0, zy = 0;
            for (int i = 0; i < feet.Length; i++)
            {
                float x = feet[i].X - mx, z = feet[i].Z - mz, y = SupportHeights[i] + BlacksiteLandingGearPolicy.FootDepths[i] - my;
                xx += x*x; zz += z*z; xz += x*z; xy += x*y; zy += z*y;
            }
            float determinant = xx*zz-xz*xz;
            if (Mathf.Abs(determinant) < .001f) return false;
            float a = (xy*zz-zy*xz)/determinant, b = (zy*xx-xy*xz)/determinant;
            var normal = new Vector3(-a, 1, -b).normalized;
            rotation = yaw * Quaternion.RotateTowards(Quaternion.identity, Quaternion.FromToRotation(Vector3.up, normal), BlacksiteLandingGearPolicy.MaximumTilt);
            return true;
        }
        private static bool ProbeSupportGround(Vector3 expected, float depth, int mask, Transform ignoreRoot, out RaycastHit ground)
            => NearestDown(expected + Vector3.up, depth + 2f, mask, ignoreRoot, out ground) > 0;
        internal static bool ClearUsableVolume(Vector3 origin, Quaternion rotation, int mask, Transform ignoreRoot, float footToDeck, out string reason, Transform attachedCargo = null)
        {
            // Reject external intrusions, not the ship or its independently networked aboard cache.
            // Only hull clearance excludes attached cargo; personnel passage still tests its solids.
            int count = Physics.OverlapBoxNonAlloc(origin + rotation * new Vector3(0, (CabinFloor + HullTop) * .5f, CabinZ),
                new Vector3(CabinHalfWidth, (HullTop - CabinFloor) * .5f, CabinHalfLength), Overlaps, rotation, mask, QueryTriggerInteraction.Ignore);
            if (!ClearOverlaps(count, ignoreRoot, attachedCargo, "cabin volume", out reason)) return false;
            // Outer hull beside the cabin, from the catwalk underside up: the pods below it sit in
            // the terrain as the crew ship's do, but nothing may stand on the side catwalks.
            float lift = BlacksiteLandingGearPolicy.CatwalkUnderside, half = (HullTop - lift) * .5f;
            for (int side = -1; side <= 1; side += 2)
            {
                count = Physics.OverlapBoxNonAlloc(origin + rotation * new Vector3(side * (CabinHalfWidth + .8f), lift + half, .675f),
                    new Vector3(.8f, half, 11.175f), Overlaps, rotation, mask, QueryTriggerInteraction.Ignore);
                if (!ClearOverlaps(count, ignoreRoot, attachedCargo, "hull volume", out reason)) return false;
            }
            // Signed clearance at the measured lower surface of each hull/leg column.
            // This leaves empty terrain between the legs available instead of filling it with a slab.
            if(LowerHullSamples!=null)
                foreach(var local in LowerHullSamples)
                {
                    Vector3 surface=origin+rotation*local;
                    Vector3 start = new Vector3(surface.x, Mathf.Max(surface.y + .1f, origin.y + 2f), surface.z);
                    LastHullQueries++;
                    int hits=Physics.RaycastNonAlloc(start,Vector3.down,GroundHits,Mathf.Max(.2f,start.y-surface.y+1f),mask,QueryTriggerInteraction.Ignore);
                    if(hits==GroundHits.Length){reason="hull clearance query saturated";return false;}
                    for(int i=0;i<hits;i++)
                        if((ignoreRoot==null || !GroundHits[i].transform.IsChildOf(ignoreRoot))
                            && (attachedCargo==null || !GroundHits[i].transform.IsChildOf(attachedCargo))
                            && GroundHits[i].point.y>surface.y+.05f)
                        {reason="terrain penetrates measured lower hull at "+local;return false;}
                }
            reason = null; return true;
        }
        private static bool ClearOverlaps(int count, Transform ignoreRoot, Transform attachedCargo, string volume, out string reason)
        {
            if (count == Overlaps.Length) { reason = volume + " clearance query saturated"; return false; }
            for (int i = 0; i < count; i++)
            {
                var c = Overlaps[i];
                if (c == null || (ignoreRoot != null && c.transform.IsChildOf(ignoreRoot))
                    || (attachedCargo != null && c.transform.IsChildOf(attachedCargo))) continue;
                reason = volume + " intersects " + c.name; return false;
            }
            reason = null; return true;
        }
        internal static bool ProbeGround(Vector3 expected, float tolerance, int mask, Transform ignoreRoot, out RaycastHit ground, out string reason)
        {
            int found = NearestDown(expected + Vector3.up * tolerance, tolerance * 2f, mask, ignoreRoot, out ground);
            reason = found > 0 ? null : found < 0 ? "ground query saturated" : "no ground within vertical probe window";
            return found > 0;
        }
        // The nearest downward hit outside ignoreRoot: 1, 0 when none, -1 when the query saturated.
        private static int NearestDown(Vector3 top, float length, int mask, Transform ignoreRoot, out RaycastHit ground)
        {
            ground = default; float nearest = float.PositiveInfinity;
            int count = Physics.RaycastNonAlloc(top, Vector3.down, GroundHits, length, mask, QueryTriggerInteraction.Ignore);
            if (count == GroundHits.Length) return -1;
            for (int i = 0; i < count; i++)
                if ((ignoreRoot == null || !GroundHits[i].transform.IsChildOf(ignoreRoot)) && GroundHits[i].distance < nearest) { nearest = GroundHits[i].distance; ground = GroundHits[i]; }
            return nearest < float.PositiveInfinity ? 1 : 0;
        }
        internal static bool Ground(Vector3 expected, float tolerance, int mask, Transform ignoreRoot, out RaycastHit ground)
        {
            if(BlacksitePhysicalStep.TrySupportingGround(expected + Vector3.up * tolerance, expected.y, mask, out ground, out _,
                tolerance * 2f, tolerance, ignoreRoot)) return true;
            return Physics.SphereCast(expected+Vector3.up*tolerance,.08f,Vector3.down,out ground,tolerance*2f,mask,QueryTriggerInteraction.Ignore)
                && ground.normal.y>BlacksitePhysicalStep.WalkableNormalY && (ignoreRoot==null || !ground.transform.IsChildOf(ignoreRoot));
        }
        // The physical floor point under a probe, accepted only where navigation exists beside it.
        // Callers receive the physical point (routes are audited physically); path endpoints use
        // TryNavPoint for the navigation sample itself.
        internal static bool TryFloorNav(Vector3 point, int mask, out Vector3 nav, float tolerance = .3f)
        {
            nav = point;
            if (!Ground(point, tolerance, mask, null, out var floor) || !TryNavSample(floor, out _)) return false;
            nav = floor.point; return true;
        }
        internal static bool TryNavPoint(Vector3 point, int mask, out Vector3 nav, float tolerance = .3f, float groundNavTolerance = BlacksiteRecoveryLadder.GroundNavTolerance)
        {
            nav = point;
            if (!Ground(point, tolerance, mask, null, out var floor) || !TryNavSample(floor, out var sample, groundNavTolerance)) return false;
            nav = sample; return true;
        }
        private static bool TryNavSample(RaycastHit floor, out Vector3 sample, float groundNavTolerance = BlacksiteRecoveryLadder.GroundNavTolerance)
        {
            sample = floor.point;
            // A ship floor requires its deck area; moon floors take the nearest surface.
            if (BlacksiteShipNavigation.IsShipFloor(floor.collider))
            {
                if (!NavMesh.SamplePosition(floor.point, out var deck, .4f, BlacksiteShipNavigation.DeckAreaMask)
                    || Mathf.Abs(deck.position.y - floor.point.y) > .15f) return false;
                sample = deck.position; return true;
            }
            // Moon navigation approximates terrain. Vow's original mesh moves about 15.3 cm
            // above its physical floor after carving; a second 15 cm vertical cutoff rejects
            // that valid surface. Keep the existing 25 cm three-dimensional sample bound and
            // retain the actual floor point for physical movement. Deck matching stays tighter.
            // #978: the 25 cm is BlacksiteRecoveryLadder.GroundNavTolerance, the same constant the
            // physical-recovery rebind gate reads, so a point this publishes can always be rebound
            // to. It was a literal here and a tighter 0.15 m literal there, and the tighter one won:
            // every exit point whose nearest mesh point sat straight above or below it was published
            // and then permanently refused.
            return TryMoonNav(floor.point, groundNavTolerance, out sample);
        }
        // #1305: a carved moon tile is re-tessellated and a query box only the tolerance wide can
        // miss navigation still inside it (Rend: 0.15 m away after the hull carve), so the query
        // is 1 m wide and the tolerance judges the hit's distance.
        private static bool TryMoonNav(Vector3 floor, float tolerance, out Vector3 sample)
        {
            sample = floor;
            if (!NavMesh.SamplePosition(floor, out var hit, 1f, NavMesh.AllAreas) || hit.distance > tolerance) return false;
            sample = hit.position; return true;
        }
        // #874 plan B: the one entrance approach. Every navigation route to a door - the landing
        // search's ground and hover approaches and MoonRoute's re-target gate - ends at the navigation
        // this binds and walks the rest physically, so a door one of them accepts is a door all of them do.
        //
        // The first two answers are the previous entrance bind's exact rules: the anchor's own floor with
        // navigation inside the 25 cm moon bound, then navigation within 1.5 m whose own floor lies
        // under it. An authored anchor may sit in a door recess the moon navigation stops short of
        // (Vertigo: 1.57 m), so when both refuse, navigation within ApproachSearchRadius of the door
        // is asked nearest first, and a point is bound only when (a) physical floor supports it, (b)
        // the doorway tail from that floor to the door is walked with RouteCheck's own sample rule and
        // arrives where RouteCheck's entrance arrival does, and (c) the caller's reachability evidence
        // (a complete path from its own navigation) accepts it. The tail proof is what restricts the
        // search to the outward side: the building shell refuses every tail from inside it. The
        // evidence is a factory, built at most once and only for the first candidate that reaches (c),
        // so a door the fast paths or the physical checks settle never pays for it.
        // Rings a quarter of the bound apart at twelve headings, each sampled a ring step wide, mirror
        // the Bundy entrance ring search (BundyAIBase.Navigation.cs).
        private const int ApproachRings = 4, ApproachHeadings = 12;
        internal static BlacksiteEntranceApproach ResolveEntranceApproach(Vector3 entrance, int mask, Func<Predicate<Vector3>> reachability = null)
        {
            bool floored = Ground(entrance, 1.5f, mask, null, out var under);
            Vector3 door = floored ? under.point : entrance;
            string floor = floored ? DescribeCollider(under.collider) : "none";
            if (TryNavPoint(entrance, mask, out var nav, 1.5f))
                return new BlacksiteEntranceApproach(entrance, door, nav, floor, true, false, "physical floor matched");
            if (NavMesh.SamplePosition(entrance, out var hit, 1.5f, NavMesh.AllAreas) && Ground(hit.position, .6f, mask, null, out var hitFloor)
                && Mathf.Abs(hitFloor.point.y - hit.position.y) < .35f)
                return new BlacksiteEntranceApproach(entrance, door, hit.position, floor, true, false,
                    $"navigation sampled {Vector3.Distance(entrance, hit.position):0.00}m from the entrance point");

            float radius = BlacksiteEntrancePolicy.ApproachSearchRadius, ring = radius / ApproachRings;
            var candidates = new List<Vector3>();
            bool anyNav = NavMesh.SamplePosition(door, out var nearest, radius, NavMesh.AllAreas);
            if (anyNav) AddApproachCandidate(candidates, nearest.position, door, radius);
            for (int r = 1; r <= ApproachRings; r++)
                for (int h = 0; h < ApproachHeadings; h++)
                {
                    float angle = h * 2f * Mathf.PI / ApproachHeadings;
                    Vector3 around = door + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * (r * ring);
                    if (NavMesh.SamplePosition(around, out var sample, ring, NavMesh.AllAreas)) AddApproachCandidate(candidates, sample.position, door, radius);
                }
            string context = "; door floor=" + floor + ", nearestNavDelta="
                + (anyNav ? (nearest.position - door).ToString("F2") : "none within " + radius.ToString("0.0") + "m");
            if (candidates.Count == 0)
                return new BlacksiteEntranceApproach(entrance, door, door, floor, false, false,
                    "no nav in bound: no navigation within " + radius.ToString("0.0") + "m of the door" + context);
            candidates.Sort((a, b) => Flat(a - door).CompareTo(Flat(b - door)));
            var probe = BlacksitePhysicalStep.ProbeTransform;
            string first = null; int unsupported = 0, tails = 0, disconnected = 0;
            Predicate<Vector3> reachable = null; bool evidenceBuilt = false;
            foreach (var candidate in candidates)
            {
                string refusal; float away = Flat(candidate - door);
                if (!Ground(candidate, .6f, mask, null, out var support) || Mathf.Abs(support.point.y - candidate.y) >= .35f)
                {
                    unsupported++;
                    refusal = $"floor without support: navigation {away:0.00}m from the door at {candidate:F2} has no physical floor within 0.35m";
                    first = first ?? refusal; continue;
                }
                if (!TryDoorwayTail(probe, support.point, entrance, door, mask, out float tail, out refusal))
                { tails++; first = first ?? refusal; continue; }
                if (!evidenceBuilt) { reachable = reachability?.Invoke(); evidenceBuilt = true; }
                if (reachable != null && !reachable(candidate))
                {
                    disconnected++;
                    first = first ?? $"disconnected component: navigation {away:0.00}m from the door at {candidate:F2} has no complete path from the caller's navigation";
                    continue;
                }
                return new BlacksiteEntranceApproach(entrance, door, candidate, floor, true, true,
                    $"bound navigation {away:0.00}m from the door (search bound {radius:0.0}m); " + (tail > 0f
                        ? $"physical tail of {tail:0.00}m proven to within {BlacksiteEntrancePolicy.RouteArrivalRange:0.0}m of the door"
                        : $"already inside the {BlacksiteEntrancePolicy.RouteArrivalRange:0.0}m arrival range, no tail to walk"));
            }
            return new BlacksiteEntranceApproach(entrance, door, door, floor, false, false,
                first + $" (nearest of {candidates.Count} candidates within {radius:0.0}m: {unsupported} floor without support, {tails} physical tail refused, {disconnected} disconnected)" + context);
        }
        private static void AddApproachCandidate(List<Vector3> candidates, Vector3 nav, Vector3 door, float radius)
        {
            if (Flat(nav - door) > radius) return;
            foreach (var known in candidates) if ((known - nav).sqrMagnitude < .25f) return;
            candidates.Add(nav);
        }
        private static float Flat(Vector3 delta) => new Vector2(delta.x, delta.z).magnitude;
        private static bool AtEntrance(Vector3 point, Vector3 entrance)
            => Flat(point - entrance) <= BlacksiteEntrancePolicy.RouteArrivalRange
            && Mathf.Abs(point.y - entrance.y) <= BlacksiteEntrancePolicy.HeightTolerance;
        // The doorway tail, walked from supported floor at the bound navigation toward the door with
        // RouteCheck's own sample rule, and accepted where RouteCheck's entrance arrival is.
        private static bool TryDoorwayTail(Transform actor, Vector3 from, Vector3 entrance, Vector3 door, int mask, out float walked, out string refusal)
        {
            walked = 0f; refusal = null;
            if (!BlacksitePhysicalStep.CanOccupy(actor, from, mask, out var occupant))
            { refusal = "physical tail blocked by " + DescribeCollider(occupant) + " at its start " + from.ToString("F2"); return false; }
            Vector3 previous = from;
            int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(from, door) / .15f));
            for (int step = 1; !AtEntrance(previous, entrance); step++)
            {
                if (step > steps) { refusal = $"physical tail ends {Flat(previous - entrance):0.00}m from the entrance at {previous:F2}"; return false; }
                Vector3 expected = Vector3.Lerp(from, door, step / (float)steps);
                expected.y = previous.y;
                var result = WalkSample(actor, previous, expected, mask, null, out var floor, out var blocker);
                if (result != WalkRefusal.None) { refusal = TailRefusal(result, expected, previous, floor, blocker); return false; }
                walked += Vector3.Distance(previous, floor.point); previous = floor.point;
            }
            return true;
        }
        private enum WalkRefusal { None, Unsupported, StepTooHigh, Footprint, Blocked }
        // One 0.15 m sample of a physically walked route: supported floor within the 0.32 m step,
        // outside the forbidden footprint, and a clear swept body segment. RouteCheck and the doorway
        // tail walk with this one rule. The caller sets expected.y to the previous foot, because
        // navigation corners omit slope breaks and the walk follows physical height.
        private static WalkRefusal WalkSample(Transform actor, Vector3 previous, Vector3 expected, int mask, Predicate<Vector3> forbidden, out RaycastHit floor, out Collider blocker)
        {
            blocker = null;
            if (!Ground(expected, .6f, mask, null, out floor)) return WalkRefusal.Unsupported;
            if (Mathf.Abs(floor.point.y - previous.y) > .32f) return WalkRefusal.StepTooHigh;
            if (forbidden != null && forbidden(floor.point)) return WalkRefusal.Footprint;
            return BlacksitePhysicalStep.ClearSegment(actor, previous, floor.point, mask, null, out blocker) ? WalkRefusal.None : WalkRefusal.Blocked;
        }
        private static string TailRefusal(WalkRefusal refusal, Vector3 expected, Vector3 previous, RaycastHit floor, Collider blocker)
        {
            switch (refusal)
            {
                case WalkRefusal.StepTooHigh:
                    return $"physical tail blocked by {DescribeCollider(floor.collider)}: a {floor.point.y - previous.y:0.00}m step, over the 0.32m limit, at {floor.point:F2}";
                case WalkRefusal.Footprint: return "physical tail enters planned ship footprint at " + floor.point.ToString("F2");
                case WalkRefusal.Blocked: return $"physical tail blocked by {DescribeCollider(blocker)} at {floor.point:F2}";
                default: return "physical tail unsupported: no walkable floor at " + expected.ToString("F2");
            }
        }
        // A collider named well enough to find it in the scene: up to three parents, the layer, and
        // whether it is terrain. "Shut up your name is plart" alone did not say it was Vertigo's Terrain.
        internal static string DescribeCollider(Collider collider)
        {
            if (collider == null) return "saturated query";
            string path = collider.name; Transform parent = collider.transform.parent;
            for (int depth = 0; parent != null && depth < 3; depth++, parent = parent.parent) path = parent.name + "/" + path;
            if (parent != null) path = ".../" + path;
            int layer = collider.gameObject.layer; string layerName = LayerMask.LayerToName(layer);
            return path + " (layer " + layer + (string.IsNullOrEmpty(layerName) ? "" : " " + layerName)
                + (collider is TerrainCollider ? ", terrain" : "") + ")";
        }
        // #874 plan B: the reachability evidence a caller standing on navigation hands the resolver:
        // a complete path from its own floor, with RouteCheck's upper-surface retry. Null when the
        // caller's own point has no navigation, which leaves the component to the route itself.
        internal static Predicate<Vector3> ReachableFrom(Vector3 from, int mask, int areaMask = NavMesh.AllAreas)
        {
            if (!TryNavPoint(from, mask, out var start, .6f, .6f)) return null;
            var path = new NavMeshPath();
            return nav => TryConnectedPath(from, start, nav, nav, mask, path, areaMask);
        }
        // #1305: the landing job's reachability evidence: a complete path from any outside AI node
        // with supported navigation (ground within 4 m), nearest the entrance first. Null when no
        // node vets, which leaves the component to the route legs themselves.
        internal static Predicate<Vector3> OutsideReach(Vector3[] nodes, Vector3 entrance, int mask)
        {
            if (nodes == null || nodes.Length == 0) return null;
            var vetted = new List<KeyValuePair<float, Vector3>>();
            foreach (var node in nodes)
                if (ProbeGround(node, 4f, mask, null, out var under, out _) && TryNavPoint(under.point, mask, out var nav, .6f))
                    vetted.Add(new KeyValuePair<float, Vector3>(Vector3.ProjectOnPlane(nav - entrance, Vector3.up).sqrMagnitude, nav));
            if (vetted.Count == 0) return null;
            vetted.Sort((a, b) => a.Key.CompareTo(b.Key));
            var traveller = BlacksiteTraveller.Soldier(false, NavMesh.AllAreas);
            return point =>
            {
                foreach (var anchor in vetted)
                    if (BlacksiteReachability.Walk(traveller, anchor.Value, point).Reachable) return true;
                return false;
            };
        }
        // #874 plan B: the nearest navigation is asked at the entrance approach's own bound, so a
        // refused endpoint says how far the navmesh really is, not only that it is not within 1.5 m.
        private static string DescribeEndpoint(Vector3 point, int mask, float tolerance)
        {
            if (!Ground(point, tolerance, mask, null, out var floor)) return "point=" + point.ToString("F3") + ", no physical ground";
            bool ship = BlacksiteShipNavigation.IsShipFloor(floor.collider);
            int area = ship ? BlacksiteShipNavigation.DeckAreaMask : NavMesh.AllAreas;
            float bound = BlacksiteEntrancePolicy.ApproachSearchRadius;
            bool sampled = NavMesh.SamplePosition(floor.point, out var nearby, bound, area);
            return "point=" + point.ToString("F3") + ", floor=" + DescribeCollider(floor.collider) + ", ship=" + ship
                + ", nearestNavDelta=" + (sampled ? (nearby.position - floor.point).ToString("F3") : "none within " + bound.ToString("0.0") + "m");
        }
        internal sealed class RouteCheck
        {
            internal bool Complete, Success;
            internal string Reason;
            internal Vector3[] Corners;
            internal readonly List<Vector3> Samples = new List<Vector3>();
            private Vector3 _previous, _begin;
            private int _corner = 1, _step, _steps, _samples;
            private readonly Transform _actor;
            private readonly int _mask;
            private readonly float _physicalDistance;
            private readonly bool _allowWorldLadders, _followed;
            // #1305: a followed route (the landing approach's legs, sealed as the plan's GroundRoute)
            // holds every moon sample to the navigation its endpoints are bound with.
            private readonly float _navTolerance;
            private readonly BlacksiteEntranceApproach _entrance;
            // The doorway tail a searched entrance approach adds after the navigation route. Its
            // samples stay out of Samples, so a corridor built from them ends on navigation and its
            // reverse leg starts on navigation.
            private Vector3 _tailBegin;
            private int _tailStep, _tailSteps;
            private readonly Predicate<Vector3> _forbidden;
            internal Vector3 EndPosition => _previous;
            internal float WalkedMetres => _walked;
            private float _walked;
            internal RouteCheck(Transform actor, Vector3 from, Vector3 to, int mask, float physicalDistance = float.PositiveInfinity, Predicate<Vector3> forbidden = null, Vector3[] physicalCorners = null, int areaMask = NavMesh.AllAreas, float groundNavTolerance = BlacksiteRecoveryLadder.GroundNavTolerance, bool allowWorldLadders = false, bool followed = false)
                : this(actor, from, to, null, mask, physicalDistance, forbidden, physicalCorners, areaMask, groundNavTolerance, allowWorldLadders, followed) { }
            // #874 plan B: a route to a door. It ends at the entrance approach's bound navigation,
            // arrives where it always has (within RouteArrivalRange of the door on navigation), and
            // for an approach the bounded search bound it then walks the doorway tail to the door.
            internal RouteCheck(Transform actor, Vector3 from, BlacksiteEntranceApproach entrance, int mask, Predicate<Vector3> forbidden = null, Vector3[] physicalCorners = null, int areaMask = NavMesh.AllAreas, float groundNavTolerance = BlacksiteRecoveryLadder.GroundNavTolerance, bool allowWorldLadders = false, bool followed = false)
                : this(actor, from, entrance.Nav, entrance, mask, float.PositiveInfinity, forbidden, physicalCorners, areaMask, groundNavTolerance, allowWorldLadders, followed) { }
            private RouteCheck(Transform actor, Vector3 from, Vector3 to, BlacksiteEntranceApproach entrance, int mask, float physicalDistance, Predicate<Vector3> forbidden, Vector3[] physicalCorners, int areaMask, float groundNavTolerance, bool allowWorldLadders, bool followed)
            {
                _actor=actor;_mask=mask;_physicalDistance=physicalDistance;_entrance=entrance;_forbidden=forbidden;_allowWorldLadders=allowWorldLadders;
                _followed=followed;_navTolerance=groundNavTolerance;
                if (!TryNavPoint(from,mask,out var start,.6f,groundNavTolerance))
                { Fail("route start does not match physical floor: " + DescribeEndpoint(from, mask, .6f));return; }
                Vector3 end;
                if (entrance != null)
                {
                    if (!entrance.Resolved) { Fail("route endpoint has no entrance approach: " + entrance.Verdict); return; }
                    end = entrance.Nav;
                }
                else if (!TryNavPoint(to,mask,out end,1.5f,groundNavTolerance))
                { Fail("route endpoint does not match physical floor: " + DescribeEndpoint(to, mask, 1.5f));return; }
                var path=new NavMeshPath();
                if(TryConnectedPath(from,start,to,end,mask,path,areaMask)) Corners=physicalCorners ?? path.corners;
                // A short leg inside authored geometry (aisle seat, door approach) is proven by the
                // physical audit below; navigation surfaces eroded near walls must not veto it.
                else if(Vector3.Distance(start,end)<=ShortLegMetres) Corners=physicalCorners ?? new[]{start,end};
                else { Fail("no complete walking route");return; }
                if (_allowWorldLadders) BlacksiteWorldLadder.ExpandRoute(Corners, mask);
                if(Corners.Length==0 || !BlacksitePhysicalStep.CanOccupy(actor,start,mask))
                { Fail("blocked or empty route start");return; }
                if(Ground(Corners[0],.6f,mask,null,out var floor))Corners[0]=floor.point;
                _previous=Corners[0]; Samples.Add(_previous);
                if (_forbidden != null && _forbidden(_previous)) Fail("route enters planned ship footprint");
            }
            private void Fail(string reason) { Complete=true;Success=false;Reason=reason; }
            // On navigation within the door's arrival range, or anywhere inside it once on the tail.
            private bool ArrivedAtEntrance() => _entrance != null && AtEntrance(_previous, _entrance.Entrance)
                && (_tailSteps > 0 || TryNavPoint(_previous, _mask, out _, .6f));
            internal void Advance(int budget = 24)
            {
                while(!Complete && budget-- > 0)
                {
                    if (ArrivedAtEntrance()) { Complete=true;Success=true;return; }
                    if (_tailSteps > 0) { AdvanceTail(); continue; }
                    if(_corner>=Corners.Length || _walked>=_physicalDistance)
                    {
                        if (_entrance != null && _entrance.PhysicalTail)
                        {
                            _tailBegin = _previous; _tailStep = 0;
                            _tailSteps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(_previous, _entrance.Door) / .15f));
                            continue;
                        }
                        Complete=true;Success=true;return;
                    }
                    if (_step == 0 && _allowWorldLadders && BlacksiteWorldLadder.TryMatch(_previous, Corners[_corner], _mask, out _, out var climb))
                    {
                        foreach (var point in climb) if (_forbidden != null && _forbidden(point)) { Fail("ladder enters planned ship footprint"); return; }
                        _walked += Vector3.Distance(_previous, climb[climb.Length-1]);
                        _previous = climb[climb.Length-1]; Samples.Add(_previous); _corner++; continue;
                    }
                    if(_step==0){_begin=_previous;_steps=Mathf.Max(1,Mathf.CeilToInt(Vector3.Distance(_begin,Corners[_corner])/.15f));}
                    if(++_samples>2700){Fail("walking route exceeds 400m validation budget");return;}
                    Vector3 expected=Vector3.Lerp(_begin,Corners[_corner],++_step/(float)_steps);
                    // NavMesh corners omit slope breaks on straight paths; follow physical height.
                    expected.y=_previous.y;
                    switch (WalkSample(_actor, _previous, expected, _mask, _forbidden, out var floor, out var blocker))
                    {
                        case WalkRefusal.Unsupported:
                        case WalkRefusal.StepTooHigh: Fail($"unsupported route or step above 0.32m at {expected} from {_previous}"); return;
                        case WalkRefusal.Footprint: Fail("route enters planned ship footprint"); return;
                        case WalkRefusal.Blocked: Fail($"physical route blocked by {(blocker!=null?blocker.name:"saturated query")} at {floor.point}"); return;
                    }
                    // #1305: a member follows this route on navigation, so a sample the walk reached under or
                    // over it (Landslide: nav 0.61 m, then 2.12 m above the floor) is ground no member reaches.
                    if (_followed && !BlacksiteShipNavigation.IsShipFloor(floor.collider) && !TryMoonNav(floor.point, _navTolerance, out _))
                    { Fail("route has no matching navigation at " + floor.point.ToString("F2") + ": " + NavGap(floor.point)); return; }
                    _walked+=Vector3.Distance(_previous,floor.point);
                    _previous=floor.point; Samples.Add(_previous);
                    if(_step==_steps){Corners[_corner]=floor.point;_corner++;_step=0;}
                }
            }
            // The distance TryMoonNav judged, with its vertical and sideways parts: beside a wall the
            // navigation is eroded sideways, under or over a surface it is not at the floor's height.
            private static string NavGap(Vector3 floor)
            {
                if (!NavMesh.SamplePosition(floor, out var near, 8f, NavMesh.AllAreas)) return "no navigation within 8m";
                float above = near.position.y - floor.y;
                return $"nav {near.distance:0.00}m away ({Mathf.Abs(above):0.00}m {(above >= 0 ? "above" : "below")} the floor, {Flat(near.position - floor):0.00}m aside)";
            }
            // The doorway tail, re-walked on every route rather than trusted from the resolver: the
            // planned hull footprint and anything that moved since are asked of it here.
            private void AdvanceTail()
            {
                if (_tailStep >= _tailSteps)
                { Fail($"physical tail ends {Flat(_previous - _entrance.Entrance):0.00}m from the entrance at {_previous:F2}"); return; }
                if (++_samples > 2700) { Fail("walking route exceeds 400m validation budget"); return; }
                Vector3 expected = Vector3.Lerp(_tailBegin, _entrance.Door, ++_tailStep / (float)_tailSteps);
                expected.y = _previous.y;
                var result = WalkSample(_actor, _previous, expected, _mask, _forbidden, out var floor, out var blocker);
                if (result != WalkRefusal.None) { Fail(TailRefusal(result, expected, _previous, floor, blocker)); return; }
                _walked += Vector3.Distance(_previous, floor.point);
                _previous = floor.point;
            }
        }
        // Two navigation surfaces can lie within a few centimetres of one physical floor (the
        // ship deck over uncarved moon navigation, a landing pad over terrain). Endpoints sampled
        // onto different unlinked surfaces make a walkable route look partial, so the check also
        // tries the upper surface at each end before giving up.
        private static bool TryUpperNav(Vector3 point, int mask, out Vector3 nav)
        {
            nav = point;
            if (!Ground(point, 1.5f, mask, null, out var floor)) return false;
            int areas = BlacksiteShipNavigation.IsShipFloor(floor.collider) ? BlacksiteShipNavigation.DeckAreaMask : NavMesh.AllAreas;
            if (!NavMesh.SamplePosition(floor.point + Vector3.up * .3f, out var hit, .6f, areas)
                || Mathf.Abs(hit.position.y - floor.point.y) > .35f) return false;
            nav = hit.position; return true;
        }
        internal const float ShortLegMetres = 6f;
        internal static bool TryConnectedPath(Vector3 from, Vector3 start, Vector3 to, Vector3 end, int mask, NavMeshPath path, int areaMask = NavMesh.AllAreas)
        {
            var traveller = BlacksiteTraveller.Soldier(false, areaMask);
            if (BlacksiteReachability.Walk(traveller, start, end, path).Reachable) return true;
            bool altStart = TryUpperNav(from, mask, out var upperStart) && (upperStart - start).sqrMagnitude > .0001f;
            bool altEnd = TryUpperNav(to, mask, out var upperEnd) && (upperEnd - end).sqrMagnitude > .0001f;
            if (altStart && BlacksiteReachability.Walk(traveller, upperStart, end, path).Reachable) return true;
            if (altEnd && BlacksiteReachability.Walk(traveller, start, upperEnd, path).Reachable) return true;
            if (altStart && altEnd && BlacksiteReachability.Walk(traveller, upperStart, upperEnd, path).Reachable) return true;
            return false;
        }
        // Native moon navigation includes authored stairs/ladder links and terrain proxies.
        // Prove connectivity and physically audit the entire route to the usable entrance threshold.
        // Ship passage itself always audits every metre using WalkRoute. #874 plan B: the door is the
        // one entrance approach, bound on the component the walker's own navigation reaches.
        internal static bool MoonRoute(Vector3 from, Vector3 entrance, int mask, out string reason)
        {
            var approach=ResolveEntranceApproach(entrance,mask,()=>ReachableFrom(from,mask));
            var check=new RouteCheck(BlacksitePhysicalStep.ProbeTransform,from,approach,mask);
            while(!check.Complete)check.Advance();
            reason=check.Reason;return check.Success;
        }
        internal static bool WalkRoute(Transform actor, Vector3 from, Vector3 to, int mask, out Vector3[] corners, out string reason, bool groundOnly = false)
        {
            var check=new RouteCheck(actor,from,to,mask,areaMask:groundOnly ? NavMesh.AllAreas & ~BlacksiteShipNavigation.DeckAreaMask : NavMesh.AllAreas);
            while(!check.Complete)check.Advance();
            corners=check.Corners;reason=check.Reason;return check.Success;
        }
        internal static bool WalkCorners(Transform actor, Vector3[] corners, int mask, out string reason)
        {
            reason = "empty walking route";
            if (corners == null || corners.Length == 0) return false;
            // A native corner may sit above its physical terrain. Resolve the starting foot
            // before measuring steps, as RouteCheck already does; never count mesh error as a step.
            if (!Ground(corners[0], .6f, mask, null, out var startFloor)) { reason = "unsupported route start"; return false; }
            Vector3 previous = startFloor.point; int samples = 0;
            if (!BlacksitePhysicalStep.CanOccupy(actor, previous, mask)) { reason = "blocked route start"; return false; }
            for (int i = 1; i < corners.Length; i++)
            {
                Vector3 begin = previous; int steps = Mathf.Max(1, Mathf.CeilToInt(Vector3.Distance(begin, corners[i]) / .15f));
                for (int step = 1; step <= steps; step++)
                {
                    if (++samples > 2700) { reason = "walking route exceeds 400m validation budget"; return false; }
                    Vector3 expected = Vector3.Lerp(begin, corners[i], step / (float)steps);
                    expected.y=previous.y;
                    var result = WalkSample(actor, previous, expected, mask, null, out var floor, out var blocker);
                    if (result != WalkRefusal.None)
                    { reason = result == WalkRefusal.Blocked ? "physical route blocked by " + (blocker != null ? blocker.name : "saturated query") : "unsupported route or step above 0.32m"; return false; }
                    previous = floor.point;
                }
            }
            reason = null; return true;
        }
    }

    /// <summary>
    /// #874 plan B: the resolved approach to one door, made once by
    /// <see cref="BlacksiteLandingGeometry.ResolveEntranceApproach"/> and handed to every route that
    /// ends there. <see cref="Nav"/> is where the navigation part of such a route ends; a route to an
    /// approach with <see cref="PhysicalTail"/> then walks from there to <see cref="Door"/>. An
    /// unresolved approach carries the one refusal, naming the segment that failed.
    /// </summary>
    internal sealed class BlacksiteEntranceApproach
    {
        internal BlacksiteEntranceApproach(Vector3 entrance, Vector3 door, Vector3 nav, string floor, bool resolved, bool physicalTail, string verdict)
        { Entrance = entrance; Door = door; Nav = nav; Floor = floor; Resolved = resolved; PhysicalTail = physicalTail; Verdict = verdict; }
        /// <summary>The raw authored anchor every arrival test measures against.</summary>
        internal readonly Vector3 Entrance;
        /// <summary>The physical floor under the anchor, or the anchor when it has none.</summary>
        internal readonly Vector3 Door;
        internal readonly Vector3 Nav;
        /// <summary>The door floor's collider, with its parents and layer.</summary>
        internal readonly string Floor;
        internal readonly bool Resolved;
        /// <summary>Bound by the bounded search rather than by the two exact fast paths.</summary>
        internal readonly bool PhysicalTail;
        internal readonly string Verdict;
        /// <summary>How far from the anchor a proven route to this door can end: the arrival range,
        /// or the bound navigation when that lies further out.</summary>
        internal float ArrivalMetres => Mathf.Max(BlacksiteEntrancePolicy.RouteArrivalRange, Vector3.Distance(Nav, Entrance));
        internal string Refusal => "entrance approach unresolved: " + Verdict;
        public override string ToString() => Resolved
            ? "nav=" + Nav.ToString("F2") + ", navDelta=" + (Nav - Door).ToString("F2") + ", floor=" + Floor + ", verdict=" + Verdict
            : "unresolved, verdict=" + Verdict;
    }
}
