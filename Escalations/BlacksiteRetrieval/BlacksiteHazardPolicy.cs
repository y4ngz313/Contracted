using System;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S7.4: the geometry a contact hazard is kept at, as numbers.
    //
    // The 2026-09-09 Offense run killed member 3 with 0.3 s of warning, then re-issued the
    // withdrawal goal (-16.12, -219.55, 75.15) straight back through the same slime, dead at 296.3.
    // #1305 H (C3): a live disc - a dangerous slime included - is now a constraint on routes and on
    // fire, admitted where a goal is requested (BlacksiteNavigator.RefuseHazardRoute), so there is
    // no warning band, no hold and no lifetime left to budget here.
    //
    // Every number here is a budget, not a measurement, so it belongs outside UnityEngine where
    // tests/BlacksiteReactiveRegression can assert it without an editor.
    internal static class BlacksiteHazardPolicy
    {
        /// <summary>
        /// The slime ray radius. BlobAI.RaycastCollisionWithPlayers sweeps its bone rays with a
        /// 0.7 m sphere, and both halves of the slime contact - BlacksiteVanillaMonsters.SlimeRay's SphereCast
        /// and OverlapCapsule, and BlacksiteHazardFootprint.Near's "within 0.7 + margin of the ray" test - mirror it exactly.
        /// It is declared once here and referenced from both, so a warning band and the
        /// damage envelope it is measured against can never drift apart.
        /// </summary>
        internal const float RayRadius = .7f;

        /// <summary>
        /// The band a standing member kept around a slime in the shipped build: the 2.5 m clamp in
        /// BlacksiteHazardAwareness.Nearby plus the 0.7 m ray radius Near measures from.
        /// </summary>
        internal const float MinimumWarningRadius = 2.5f + RayRadius;

        /// <summary>
        /// #1305 H (C3): the disc radius a dangerous slime is admitted as. Containment stays the ray
        /// test (BlacksiteHazardFootprint.Near); this bounds which route segments are sampled and
        /// where a detour waypoint stands: the standing band plus one <see cref="FootprintBand"/>,
        /// the record S7.4 shipped at rest.
        /// </summary>
        internal const float SlimeFootprintRadius = MinimumWarningRadius + FootprintBand;

        /// <summary>
        /// #1305 H (C3): how far outside a disc's refused ring (radius plus
        /// <see cref="FootprintMargin"/>) a detour or escape waypoint's legs pass: the clearance the
        /// S7.4 escape search proved an escape point by.
        /// </summary>
        internal const float DetourClearance = .75f;

        /// <summary>
        /// The tolerance the navigator refuses a route at, and the number acceptance G is asserted
        /// with: half the ray radius. A corner or a 0.5 m route sample inside
        /// BlacksiteHazardFootprint.Near(blob, point, 0.35f) is inside the footprint.
        /// </summary>
        internal const float FootprintMargin = RayRadius / 2f;

        /// <summary>
        /// How far a 0.5 m route sample walks. The admission samples every route and every detour
        /// leg it proves at this step, so the route that is
        /// refused and the route that is proven are measured identically.
        /// </summary>
        internal const float RouteSampleMetres = .5f;

        /// <summary>
        /// One MinDestinationChange (1.45 m), the smallest goal move the navigator treats as a
        /// different destination.
        /// </summary>
        internal const float FootprintBand = BlacksiteArrivalPolicy.MinDestinationChange;

        /// <summary>
        /// #951 S11H U3 (g): the absolute ceiling on any hazard proposal. Nothing beyond this is a
        /// hazard to anybody, whatever radius a caller asks about. Test 3 asked about 449 m off one
        /// warp frame and was answered about a bee 306.8 m away; 40 m clears the shipped 7 m nest
        /// with room to spare, measured from a disc's anchor (BlacksiteHazardAwareness.InRange).
        /// </summary>
        internal const float MaxProposalRange = 40f;

        /// <summary>
        /// #948 S12 U3: how far a soldier keeps from a sapsucker egg.
        ///
        /// Derivation, from vanilla and from this mod's own goal quantum, with nothing invented:
        /// GiantKiwiAI.cs:574 marks a threat as an egg thief the moment it is within 3 m of an egg,
        /// and that mark is permanent for the round (the bird hunts an egg thief through
        /// GiantKiwiAI.cs:1028 and nothing in vanilla ever prunes the list for a non-player). So
        /// the avoid radius has to EXCEED 3 m, not equal it: a member standing exactly on the edge
        /// is already marked. The margin is one <see cref="BlacksiteArrivalPolicy.MinDestinationChange"/>,
        /// the smallest goal move the navigator treats as a different destination, so a record
        /// taken at this radius cannot flicker off and on inside one goal quantum - the same
        /// reasoning <see cref="FootprintBand"/> already uses for a slime.
        /// </summary>
        internal const float EggFootprintRadius = 3f + BlacksiteArrivalPolicy.MinDestinationChange;

        /// <summary>
        /// #948 S12 U3: how tall a footprint disc is. Unlike a slime, which is measured from its
        /// own ray line and so carries its own height, a disc is a horizontal radius and needs an
        /// explicit storey gate or an outdoor nest would reject an indoor node beneath it. One
        /// soldier agent height (the NavMeshAgent height the runtime binds): a point more than one
        /// body above or below the disc centre is on other ground.
        /// </summary>
        internal const float DiscFootprintHeight = 2.65f;

        /// <summary>
        /// #948 S12 U3: how long a sapsucker that has committed to a soldier stays a threat to
        /// fight rather than a hazard to walk around.
        ///
        /// Derivation: vanilla gives up on a threat it has not seen for 4 s
        /// (GiantKiwiAI.cs:1970, timeSinceSeeingThreat > 4f leaves state 2), and its state-2 chase
        /// re-acquires inside that window, so one give-up window is not evidence of anything. Three
        /// consecutive windows is: a bird that is genuinely disengaging has had three chances to
        /// re-acquire and taken none. Refreshed by any latch source, and held outright while the
        /// bird is in state 2, so the clock only ever runs on a bird that has already stopped
        /// chasing. Longer than the member's 14 s target memory is deliberately NOT required: a
        /// remembered target that is no longer a valid one is exactly what the avoidance is for.
        /// </summary>
        internal const float HostileLatchSeconds = 12f;

        /// <summary>
        /// #948 S12 U3: the radius of the disc around a nest. The vanilla
        /// GiantKiwiAI.protectNestRadius (7 m as authored) is what the bird itself defends, and the
        /// egg radius is the floor beneath it, because a nest holds eggs and an egg alone already
        /// earns <see cref="EggFootprintRadius"/>. Taking the larger of the two means a mod that
        /// shrinks protectNestRadius cannot shrink the disc below what one egg earns, and a mod
        /// that widens it is respected.
        /// </summary>
        internal static float NestFootprintRadius(float protectNestRadius)
            => Math.Max(Math.Max(0f, protectNestRadius), EggFootprintRadius);

        /// <summary>
        /// #1283 S5: the radius of the disc around a placed bee hive. It means "do not walk through
        /// the hive", not "keep out of the swarm's ground": vanilla bees target players only and
        /// never harm a soldier, so the swarm's defended extent is not a soldier's business. Pass-6
        /// R3 put a hive 3.9 m beside the straight route to a main entrance 16 m away, and the squad
        /// re-tripped a four-second swarm hold every 4-5 s for 92 s (106 [UAT][Hazard] lines) without
        /// ever entering. Three metres keeps a body off the hive itself, and even with the 0.35 m
        /// <see cref="FootprintMargin"/> the route test adds (3.35 m) it leaves that 3.9 m route uncrossed.
        /// </summary>
        internal const float HiveFootprintRadius = 3f;

        /// <summary>
        /// #1283 S5: the radius of the disc around a watched Coil-Head. The watch freezes it, and a
        /// member that walks into a frozen coil shoves it (pass-5 S3a watch); the disc keeps bodies
        /// off it for exactly as long as it stays natively frozen (BlacksiteHazardFootprint.Live),
        /// after which the coil is a monster to react to rather than ground to walk around.
        /// </summary>
        internal const float CoilFootprintRadius = 2.5f;

        /// <summary>
        /// #874 Q12 (F12): how far below the flood plane a route point may lie before the route is
        /// wading rather than walking. One <see cref="FootprintMargin"/>: a baked navmesh vertex sits
        /// a few centimetres off the ground and vanilla's water surface eases toward its target at
        /// 0.5 m/s, so a route grazing the shoreline must not flicker between accepted and refused.
        /// </summary>
        internal const float FloodWadeDepth = FootprintMargin;

        /// <summary>
        /// #874 Q12: the height vanilla's flood water is rising toward. Transcribed from
        /// <c>FloodWeather</c> in Assembly-CSharp (v81): <c>OnGlobalTimeSync</c> sets
        /// <c>floodLevelOffset = Clamp(globalTime / 1080, 0, 100) * currentWeatherVariable2</c> and
        /// <c>Update</c> moves the water toward <c>currentWeatherVariable + floodLevelOffset</c>. The
        /// water object's own transform is NOT read: <c>OnDisable</c> parks it at y = -50 whenever
        /// the host player walks inside and the weather effect is switched off, and soldiers
        /// outside must still see the flood then. The target is never below the visible surface,
        /// so the test errs toward refusing.
        /// </summary>
        internal static float FloodPlaneHeight(float baseLevel, float riseRate, float globalTime)
            => baseLevel + Math.Min(100f, Math.Max(0f, globalTime / 1080f)) * riseRate;

        /// <summary>
        /// #874 Q12: whether a route is refused by the flood plane. Heights only, because a navmesh
        /// route is straight segments between corners, so its deepest point is always a corner or
        /// the destination: <paramref name="lowestY"/> is the lowest of them.
        ///
        /// A member on dry ground never enters water deeper than <see cref="FloodWadeDepth"/>. A
        /// member the rising plane has already caught may walk anywhere no deeper than where it
        /// stands and to a destination shallower than its own feet, so the route OUT of the water
        /// is never refused and a member is not frozen where the flood found it.
        /// </summary>
        internal static bool FloodRefusesRoute(float plane, float startY, float destinationY, float lowestY)
        {
            if (FloodRefusesDestination(plane, startY, destinationY)) return true;
            return plane - Math.Min(lowestY, destinationY) > Math.Max(FloodWadeDepth, plane - startY);
        }

        /// <summary>
        /// #874 Q12: the destination half of <see cref="FloodRefusesRoute"/>. The goal itself is
        /// deeper than the wade depth and no shallower than where the member stands, so no route
        /// reaches it while the plane stays: the refusal is "unreachable", not "a corner on the way
        /// is submerged", and the motion owner may close the attempt instead of holding.
        /// </summary>
        internal static bool FloodRefusesDestination(float plane, float startY, float destinationY)
        {
            float destination = plane - destinationY;
            return destination > FloodWadeDepth && destination >= plane - startY;
        }
    }
}
