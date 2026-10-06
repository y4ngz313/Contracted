using System.Collections.Generic;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteHazardAwareness
    {
        // #948 S12 U3: a sapsucker that has not committed to a soldier joins the bees and the slime.
        // BlacksiteSoldierAI.OnCollideWithEnemy reads this, so bumping into a cold bird is "back
        // off", not "start a fight"; the moment the latch is hot the bird stops being a hazard and
        // becomes the visible threat it shipped as. #1293 R5: the bees and the slime are the table's
        // Hive and Slime footprint rows (a Coil-Head's Coil disc is not a contact hazard); the
        // sapsucker stays the live latch, not a table cell.
        internal static bool IsHazard(EnemyAI enemy)
            => BlacksiteMonsterCapabilities.For(enemy).Footprint is BlacksiteHazardFootprintKind.Hive or BlacksiteHazardFootprintKind.Slime
                || BlacksiteKiwiThreatState.IsAvoided(enemy);

        /// <summary>
        /// #874 Q12 (F12): the flood water, when the round's weather is Flooded. Hazards were
        /// enemies only, so a squad on a flooded moon walked the navmesh under the water plane.
        /// Read from TimeOfDay on every call, never cached: the plane rises through the round.
        /// </summary>
        internal static bool TryGetFlood(out BlacksiteHazardFootprint water)
        {
            water = BlacksiteHazardFootprint.ForFlood(TimeOfDay.Instance);
            return water.HasValue;
        }

        /// <summary>
        /// #948 S12 U3: the one identity test for "this is a sapsucker egg". Vanilla makes a HELD
        /// egg an instant, permanent death sentence (GiantKiwiAI.cs:562-583, :1028), so an egg is
        /// never salvage, never a pickup and never an objective. Today eggs are excluded from the
        /// manifest only incidentally, because an outdoor nest makes them !isInFactory.
        /// </summary>
        internal static bool IsProtectedEgg(Component item)
            => item != null && item.GetComponent<KiwiBabyItem>() != null;
        internal static bool ExcludeFromRifleTargets(EnemyAI enemy) => BlacksiteThreatAdapterRegistry.WeaponsExcludedFor(enemy);
        /// <summary>#1283 S5: where a swarm's hive is (vanilla's own public field), or the swarm itself when it has none.</summary>
        internal static Vector3 HivePosition(EnemyAI enemy)
            => enemy is RedLocustBees bees && bees.hive != null ? bees.hive.transform.position : enemy.transform.position;

        /// <summary>
        /// #1283 S5: a swarm's hive disc or a Coil-Head's disc, live or not; default for anything
        /// else. A hazard's live discs are this one while it is Live - a placed hive, a Coil-Head
        /// while it is natively frozen (#1293 R5: stoppingMovement, vanilla's stop/go check plus
        /// BlacksiteVanillaMonsters.AddSoldierObservers) - or, for a cold sapsucker, every egg and its nest
        /// (BlacksiteKiwiThreatState.ColdDiscCount / ColdDisc). A slime is not a disc, and the
        /// swarm's own body is never a footprint: vanilla bees never harm a soldier. #1293 R1: none
        /// of them is enumerated, so asking allocates nothing.
        /// </summary>
        internal static BlacksiteHazardFootprint OwnDisc(EnemyAI hazard)
            => hazard is RedLocustBees bees ? BlacksiteHazardFootprint.ForHive(bees)
                : hazard is SpringManAI coil ? BlacksiteHazardFootprint.ForCoil(coil) : default;

        // Whether any live disc of this hazard is within radius of point. Geometry first, so a
        // disc's liveness is read only for a disc actually in range.
        private static bool Covers(EnemyAI hazard, Vector3 point, float radius)
        {
            BlacksiteHazardFootprint own = OwnDisc(hazard);
            if (own.HasValue) return own.Contains(point, radius) && own.Live;
            for (int i = 0, count = BlacksiteKiwiThreatState.ColdDiscCount(hazard); i < count; i++)
                if (BlacksiteKiwiThreatState.ColdDisc(hazard, i).Contains(point, radius)) return true;
            return false;
        }

        /// <summary>
        /// A hazard within <paramref name="radius"/> metres of <paramref name="point"/>, measured
        /// from the thing that actually hurts: a slime's ray line, or a disc's rim.
        ///
        /// #874 S7.4: the slime branch used to clamp every caller to Mathf.Min(radius, 2.5f), so a
        /// caller asking about 10 or 12 m was answered about 3.2 m (2.5 plus the 0.7 ray radius
        /// BlacksiteHazardFootprint.Near measures from). That clamp is what gave member 3 of the
        /// 2026-09-09 run 0.3 s of warning at 4.5 m/s. The question every caller was already
        /// asking - "is a hazard within this many metres of this point" - is now answered honestly
        /// for a slime too. The radius is a distance from the ray, so the margin handed to Near,
        /// which measures from 0.7 + margin, has the ray radius taken back off it.
        ///
        /// #1283 S5: <paramref name="accept"/> null is the node filters' question - ground hazards
        /// only: a slime, a placed hive, a sapsucker's eggs and nest - answered by the first match,
        /// because those callers only ask whether there is one. With a filter the answer is the
        /// first slime it accepts, else the first disc it accepts. #1293 R1: a member's own hazard
        /// read no longer comes here - it reads the squad's hazard rows (<see cref="PublishRows"/>) -
        /// so no caller in the build passes a filter any more.
        /// </summary>
        internal static EnemyAI Nearby(Vector3 point, float radius, System.Func<EnemyAI, bool> accept = null)
        {
            var enemies = RoundManager.Instance?.SpawnedEnemies;
            if (enemies == null) return null;
            // #951 S11H U3 (g): no question wider than the widest legitimate one is answered. Test 3
            // asked about 449 m off one warp frame and was told about a bee 306.8 m away, which put
            // the Hazard branch on the table at every think for the rest of the mission. The clamp
            // is here as well as on the policy's own radii because Nearby is called with literals
            // too, and because a caller's arithmetic is not this file's to trust.
            radius = Mathf.Min(radius, BlacksiteHazardPolicy.MaxProposalRange);
            float slimeMargin = Mathf.Max(0f, radius - BlacksiteHazardPolicy.RayRadius);
            EnemyAI disc = null;
            foreach (var enemy in enemies)
            {
                if (enemy == null || enemy.isEnemyDead || !enemy.isActiveAndEnabled) continue;
                if (enemy is BlobAI blob)
                {
                    if (BlacksiteHazardFootprint.Near(blob, point, slimeMargin) && (accept == null || accept(blob))) return blob;
                }
                // #1283 S5: a swarm is answered through its hive's disc, never its body, and a
                // sapsucker (#948 S12 U3) through every egg and its nest, so the five manifest node
                // filters stop placing posts and nodes on a nest with no per-call-site edit.
                else if (disc == null && (accept != null || !(enemy is SpringManAI)) && Covers(enemy, point, radius))
                {
                    if (accept == null) return enemy;
                    if (accept(enemy)) disc = enemy;
                }
            }
            return disc;
        }

        /// <summary>
        /// #1293 R1: the squad's hazard rows, replacing every member's own 0.5 s / 2 s probe. Each
        /// hazard <see cref="InRange"/> of a living member is a Live Hazard row at
        /// <see cref="HivePosition"/>: a slime and a cold sapsucker (<see cref="IsHazard"/>), and a
        /// swarm or a Coil-Head while its <see cref="OwnDisc"/> is live (#1283 S5: a placed hive, a
        /// natively frozen coil). That liveness - for a coil its native stoppingMovement (#1293 R5:
        /// vanilla's stop/go check plus AddSoldierObservers; no observer linecasts) - is read here once
        /// per pass, so a row's presence is the answer every member's read takes. The pass's eyes
        /// never see a coil (its weapons are excluded by class, which blinds BlacksiteSightPolicy's
        /// sight gate too), so this is a coil's only row. A Hazard row this call did not refresh is
        /// forgotten. Server only, once per perception pass before any member perceives; allocates
        /// nothing.
        /// </summary>
        internal static void PublishRows(BlacksiteWorldModel world, IReadOnlyList<BlacksiteSoldierAI> members, float now)
        {
            var enemies = RoundManager.Instance?.SpawnedEnemies;
            if (enemies != null)
                for (int i = 0; i < enemies.Count; i++)
                {
                    EnemyAI enemy = enemies[i];
                    if (enemy == null || enemy.isEnemyDead || !enemy.isActiveAndEnabled) continue;
                    bool ownDisc = enemy is RedLocustBees || enemy is SpringManAI;
                    if ((ownDisc || IsHazard(enemy)) && NearSquad(enemy, members) && (!ownDisc || OwnDisc(enemy).Live))
                        world.Live(enemy.NetworkObjectId, enemy, BlacksiteWorldClass.Hazard, HivePosition(enemy), enemy.isOutside, now);
                }
            // Live stamps SeenAt with this call's own clock, so a Hazard row it did not touch is stale.
            // Backwards, so a Forget never shifts a row this loop has still to visit.
            var rows = world.Rows;
            for (int i = rows.Count - 1; i >= 0; i--)
                if (rows[i].Class == BlacksiteWorldClass.Hazard && rows[i].SeenAt != now) world.Forget(rows[i].Key);
        }

        private static readonly List<BlacksiteHazardFootprint> Discs = new List<BlacksiteHazardFootprint>();

        /// <summary>
        /// #1305 H (C3): every live disc behind the table's Hazard rows - a dangerous slime as a
        /// <see cref="BlacksiteHazardPolicy.SlimeFootprintRadius"/> footprint, a placed hive, a
        /// natively frozen Coil-Head, a cold sapsucker's eggs and nest - the one set the route
        /// admission, the awareness pass and the fire lane read. One shared buffer the next call
        /// refills: server main thread, consumed before anything that may ask again. Allocates nothing.
        /// </summary>
        internal static List<BlacksiteHazardFootprint> LiveDiscs(BlacksiteWorldModel world)
        {
            Discs.Clear();
            if (world == null) return Discs;
            var rows = world.Rows;
            for (int r = 0; r < rows.Count; r++)
            {
                if (rows[r].Class != BlacksiteWorldClass.Hazard || !(rows[r].Subject is EnemyAI hazard) || hazard == null) continue;
                int count = hazard is GiantKiwiAI ? BlacksiteKiwiThreatState.ColdDiscCount(hazard) : 1;
                for (int i = 0; i < count; i++)
                {
                    BlacksiteHazardFootprint disc = hazard is BlobAI slime
                        ? BlacksiteHazardFootprint.ForSlime(slime, BlacksiteHazardPolicy.SlimeFootprintRadius)
                        : hazard is GiantKiwiAI ? BlacksiteKiwiThreatState.ColdDisc(hazard, i) : OwnDisc(hazard);
                    if (disc.HasValue && disc.Live) Discs.Add(disc);
                }
            }
            return Discs;
        }

        /// <summary>
        /// #1305 H (C3, coordinator Q3): whether a shot from <paramref name="origin"/> to
        /// <paramref name="end"/> crosses a live disc a squadmate stands in. An empty disc shields
        /// nobody, so it never withholds a shot.
        /// </summary>
        internal static bool LaneCrossesOccupiedDisc(BlacksiteWorldModel world, IReadOnlyList<BlacksiteSoldierAI> members,
            BlacksiteSoldierAI shooter, Vector3 origin, Vector3 end)
        {
            var discs = LiveDiscs(world);
            for (int d = 0; d < discs.Count; d++)
            {
                BlacksiteHazardFootprint disc = discs[d];
                if (!disc.Crosses(origin, end, BlacksiteHazardPolicy.FootprintMargin)) continue;
                for (int m = 0; m < members.Count; m++)
                    if (members[m] != null && members[m] != shooter && !members[m].isEnemyDead
                        && disc.Contains(members[m].transform.position, BlacksiteHazardPolicy.FootprintMargin)) return true;
            }
            return false;
        }

        private static bool NearSquad(EnemyAI hazard, IReadOnlyList<BlacksiteSoldierAI> members)
        {
            for (int m = 0; m < members.Count; m++)
                if (members[m] != null && InRange(hazard, members[m].transform.position, BlacksiteHazardPolicy.MaxProposalRange)) return true;
            return false;
        }

        /// <summary>
        /// #1293 R1: whether <paramref name="point"/> is within <paramref name="range"/> of what this
        /// hazard can put a ray or a disc on - a body, a hive, a nest or any egg (a dropped egg is
        /// wherever it was dropped). Anchors only, so it allocates nothing.
        /// </summary>
        internal static bool InRange(EnemyAI hazard, Vector3 point, float range)
        {
            float squared = range * range;
            if (!(hazard is GiantKiwiAI bird)) return (HivePosition(hazard) - point).sqrMagnitude <= squared;
            if (bird.birdNest != null && (bird.birdNest.transform.position - point).sqrMagnitude <= squared) return true;
            if (bird.eggs != null)
                for (int i = 0; i < bird.eggs.Count; i++)
                    if (bird.eggs[i] != null && (bird.eggs[i].transform.position - point).sqrMagnitude <= squared) return true;
            return false;
        }

        /// <summary>
        /// #948 S12 U3 A5: how far a ground landing site must stay from a sapsucker's nest. The nest
        /// footprint (BlacksiteHazardPolicy.NestFootprintRadius: the bird's own protectNestRadius or
        /// one egg disc, whichever is larger) plus BlacksiteDeploymentPolicy.GuardLeash - the
        /// EXISTING radius a ship guard may occupy, not a new one - so a guard posted at the ship is
        /// never standing on ground the raid is being routed around.
        /// </summary>
        internal static float NestAvoidMetres(EnemyAI enemy)
        {
            var bird = enemy as GiantKiwiAI;
            return bird == null ? 0f
                : BlacksiteHazardPolicy.NestFootprintRadius(bird.protectNestRadius)
                    + BlacksiteDeploymentPolicy.GuardLeash;
        }

        /// <summary>
        /// #948 S12 U3 A5: whether a GROUND landing site stands inside a sapsucker's nest ring. Run 2
        /// put the ship 4.3 m from a nest (line 53272) and the squad deployed into the one piece of
        /// ground it was being routed around. The reason is written for BlacksiteLandingSearch's own
        /// Reject vocabulary, which categorises on the text before " at ".
        ///
        /// The hover fallback does not ask: a rappel site is chosen for its drop, the ship never
        /// touches the ground, and hover is the search's last resort.
        /// </summary>
        internal static bool NestBlocksLanding(Vector3 origin, out string reason)
        {
            reason = null;
            var enemies = RoundManager.Instance?.SpawnedEnemies;
            if (enemies == null) return false;
            foreach (var enemy in enemies)
            {
                var bird = enemy as GiantKiwiAI;
                if (bird == null || bird.isEnemyDead || bird.birdNest == null) continue;
                float ring = NestAvoidMetres(bird);
                float distance = Vector3.Distance(origin, bird.birdNest.transform.position);
                if (distance >= ring) continue;
                reason = $"sapsucker nest guard ring at {distance:0.0}m of {ring:0.0}m";
                return true;
            }
            return false;
        }
    }
}
