using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #948 S12 U3: what kind of thing a footprint describes. The wire never carries this; it is a
    /// local discriminator and its only job is to choose the containment test.
    /// </summary>
    internal enum BlacksiteHazardFootprintKind : byte
    {
        None = 0,
        Slime = 1,
        Egg = 2,
        Nest = 3,
        // #874 Q12 (F12): the flood plane. Not a disc: everything below the plane is inside it.
        Water = 4,
        // #1283 S5: a placed bee hive, and a Coil-Head a member is watching. Both are plain discs.
        Hive = 5,
        Coil = 6
    }

    /// <summary>
    /// #948 S12 U3: the ground a member must not walk on, as one value.
    ///
    /// A slime footprint asks <see cref="Near"/> and therefore measures a slime exactly as
    /// S7.4 shipped it (HazardMemoryChecks), while an egg, a nest, a placed hive or a watched
    /// Coil-Head (#1283 S5) is a plain disc. #1305 H (C3): a live footprint is a constraint on
    /// routes and fire, never a behaviour - the motion owner admits every requested route against
    /// it (<see cref="Crosses"/>) and detours or refuses, and a shot across one a squadmate stands
    /// in is withheld.
    ///
    /// A disc is horizontal distance plus one storey gate: unlike a slime, which is measured from
    /// its own ray line and carries its own height, a disc has no vertical extent of its own, and
    /// an outdoor nest must not reject a node on the floor below it.
    /// </summary>
    internal readonly struct BlacksiteHazardFootprint
    {
        internal BlacksiteHazardFootprint(Object source, EnemyAI owner, BlacksiteHazardFootprintKind kind,
            Vector3 centre, float radius)
        {
            Source = source;
            Owner = owner;
            Kind = kind;
            Centre = centre;
            Radius = radius;
        }

        /// <summary>The thing the record is about: a BlobAI, a KiwiBabyItem, a nest object, a hive, a SpringManAI, or TimeOfDay for flood water.</summary>
        internal Object Source { get; }

        /// <summary>The enemy whose footprint this is, which for a disc decides whether it is live.</summary>
        internal EnemyAI Owner { get; }

        internal BlacksiteHazardFootprintKind Kind { get; }
        internal Vector3 Centre { get; }
        internal float Radius { get; }

        internal bool HasValue => Kind != BlacksiteHazardFootprintKind.None && Source != null;

        /// <summary>The slime this footprint is, or null when it is a disc.</summary>
        internal BlobAI Slime => Source as BlobAI;

        /// <summary>
        /// Whether the record still describes something dangerous. A slime answers exactly as it
        /// did before: it is live while it is Dangerous, on no clock at all. A disc is live while
        /// its source still exists and its owner is alive and not a hostile-latched bird, because a
        /// member fighting for its life must not be held off ground by it. #1283 S5: a hive only
        /// while it is placed, and a Coil-Head only while it is natively frozen.
        /// </summary>
        internal bool Live
        {
            get
            {
                if (Kind == BlacksiteHazardFootprintKind.Slime)
                {
                    BlobAI blob = Slime;
                    return blob != null && blob.isActiveAndEnabled && !blob.isEnemyDead && Dangerous(blob);
                }
                // #874 Q12: flood water is live while the round's weather is Flooded; it has no
                // owning enemy and no hostile latch.
                if (Kind == BlacksiteHazardFootprintKind.Water)
                    return Source is TimeOfDay water && water != null
                        && water.currentLevelWeather == LevelWeatherType.Flooded;
                if (!HasValue || Owner == null || !Owner.isActiveAndEnabled || Owner.isEnemyDead
                    || BlacksiteKiwiThreatState.IsHostile(Owner)) return false;
                // #1283 S5: a hive anybody carries or has pocketed is not ground. #1293 R5: a Gaze row
                // (the Coil-Head) is ground only while its own native stop/go verdict holds it frozen -
                // somebody is looking at it; a moving coil is a monster to react to, not ground.
                if (Kind == BlacksiteHazardFootprintKind.Hive)
                    return Source is GrabbableObject hive && !hive.isHeld && !hive.isHeldByEnemy && !hive.isPocketed;
                if (Kind == BlacksiteHazardFootprintKind.Coil)
                    return Owner is SpringManAI coil && BlacksiteMonsterCapabilities.For(coil).Gaze
                        && BlacksiteVanillaMonsters.CoilStopping(coil);
                return true;
            }
        }

        /// <summary>
        /// Where the record's centre is NOW. A slime moves, so its own server position is authority;
        /// an egg can be carried, so its transform is. A destroyed source keeps the last centre.
        /// </summary>
        internal Vector3 CurrentCentre
        {
            get
            {
                if (Kind == BlacksiteHazardFootprintKind.Slime)
                    return Slime != null ? Slime.serverPosition : Centre;
                // #874 Q12: the plane rises through the round, so its height is read live, never
                // the value the footprint was built with.
                if (Kind == BlacksiteHazardFootprintKind.Water)
                    return Source is TimeOfDay water && water != null ? new Vector3(0f, FloodPlane(water), 0f) : Centre;
                return Source is Component component && component != null ? component.transform.position : Centre;
            }
        }

        /// <summary>
        /// Whether <paramref name="point"/> is inside the footprint with <paramref name="margin"/>
        /// metres of tolerance. The slime branch is <see cref="Near"/> verbatim, so an
        /// escape proof and a route test still measure a slime identically and none of the S7.4
        /// numbers move.
        /// </summary>
        internal bool Contains(Vector3 point, float margin)
        {
            if (Kind == BlacksiteHazardFootprintKind.Slime)
                return Near(Slime, point, margin);
            if (!HasValue) return false;
            // #874 Q12: an outdoor test only. The interior sits far below any flood plane, so the
            // caller gates on the member being outside rather than this answering "submerged".
            if (Kind == BlacksiteHazardFootprintKind.Water)
                return CurrentCentre.y - point.y > BlacksiteHazardPolicy.FloodWadeDepth - margin;
            Vector3 centre = CurrentCentre;
            if (Mathf.Abs(point.y - centre.y) > BlacksiteHazardPolicy.DiscFootprintHeight) return false;
            return Vector3.ProjectOnPlane(point - centre, Vector3.up).magnitude <= Radius + margin;
        }

        /// <summary>
        /// #1305 H (C3): whether the straight leg <paramref name="from"/> to <paramref name="to"/>
        /// enters the footprint: a <see cref="BlacksiteHazardPolicy.RouteSampleMetres"/> sample inside
        /// it with <paramref name="margin"/>. A leg whose closest horizontal approach to the centre is
        /// outside the radius plus margin is skipped unsampled, because Near costs a linecast per bone.
        /// </summary>
        internal bool Crosses(Vector3 from, Vector3 to, float margin)
        {
            if (!HasValue) return false;
            Vector3 centre = CurrentCentre;
            Vector3 a = Vector3.ProjectOnPlane(from - centre, Vector3.up), span = Vector3.ProjectOnPlane(to - from, Vector3.up);
            float t = span.sqrMagnitude < .0001f ? 0f : Mathf.Clamp01(-Vector3.Dot(a, span) / span.sqrMagnitude);
            if ((a + span * t).sqrMagnitude >= (Radius + margin) * (Radius + margin)) return false;
            int samples = Mathf.CeilToInt(Vector3.Distance(from, to) / BlacksiteHazardPolicy.RouteSampleMetres);
            for (int j = 0; j <= samples; j++)
                if (Contains(samples == 0 ? from : Vector3.Lerp(from, to, (float)j / samples), margin)) return true;
            return false;
        }

        internal static BlacksiteHazardFootprint ForSlime(BlobAI blob, float radius)
            => blob == null ? default : FromRow(blob, blob, blob.serverPosition, radius);

        /// <summary>
        /// #1283 S5: the disc around the hive a swarm tends, or none when it has no hive. Whether
        /// the hive is still placed is <see cref="Live"/>'s question, asked on every read, because a
        /// player can pick it up at any time. The swarm's own body is never a footprint.
        /// </summary>
        internal static BlacksiteHazardFootprint ForHive(RedLocustBees bees)
            => bees == null || bees.hive == null ? default : FromRow(bees, bees.hive, bees.hive.transform.position);

        /// <summary>#1283 S5: the disc around a Coil-Head, live only while it is natively frozen (<see cref="Live"/>).</summary>
        internal static BlacksiteHazardFootprint ForCoil(SpringManAI coil)
            => coil == null ? default : FromRow(coil, coil, coil.transform.position);

        /// <summary>#1293 R5: the footprint the monster's capability row gives it - the row's kind, and the
        /// row's radius unless the caller measured one (a slime's speed-scaled band) - or none.</summary>
        private static BlacksiteHazardFootprint FromRow(EnemyAI owner, Object source, Vector3 centre, float radius = -1f)
        {
            var row = BlacksiteMonsterCapabilities.For(owner);
            return row.Footprint == BlacksiteHazardFootprintKind.None
                ? default
                : new BlacksiteHazardFootprint(source, owner, row.Footprint, centre, radius < 0f ? row.FootprintRadius : radius);
        }

        private static readonly System.Reflection.FieldInfo Tamed = AccessTools.Field(typeof(BlobAI), "tamedTimer");
        private static readonly System.Reflection.FieldInfo Angered = AccessTools.Field(typeof(BlobAI), "angeredTimer");

        /// <summary>A slime that can hurt: alive, out of its vent, not stunned, and not safely tamed (an angered slime is dangerous whatever its tame timer).</summary>
        internal static bool Dangerous(BlobAI blob) => blob != null && blob.isActiveAndEnabled && !blob.isEnemyDead
            && blob.ventAnimationFinished && blob.stunNormalizedTimer < 0f
            && !((float)Tamed.GetValue(blob) > 0f && (float)Angered.GetValue(blob) <= 0f);

        /// <summary>
        /// Whether <paramref name="point"/> is within the ray radius plus <paramref name="margin"/> of one of a
        /// Dangerous slime's native deforming rays, on the same storey and with a clear line to it. Measured from
        /// the ray, never the root: a root-distance aura cannot describe this enemy.
        /// </summary>
        internal static bool Near(BlobAI blob, Vector3 point, float margin)
        {
            if (!Dangerous(blob) || blob.SlimeBones == null || blob.SlimeRaycastTargets == null || blob.centerPoint == null) return false;
            Vector3 origin = blob.serverPosition;
            for (int i = 0; i < blob.SlimeBones.Length && i < blob.SlimeRaycastTargets.Length; i++)
            {
                if (blob.SlimeBones[i] == null || blob.SlimeRaycastTargets[i] == null) continue;
                Vector3 direction = (blob.SlimeRaycastTargets[i].position - blob.centerPoint.position).normalized;
                float length = Mathf.Max(0f, Vector3.Distance(origin, blob.SlimeBones[i].position) - 1.55f);
                Vector3 closest = origin + direction * Mathf.Clamp(Vector3.Dot(point - origin, direction), 0f, length);
                if (Mathf.Abs(point.y - closest.y) > 1.2f || Vector3.Distance(point, closest) > BlacksiteHazardPolicy.RayRadius + margin) continue;
                int mask = StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : 2312;
                if (!Physics.Linecast(point + Vector3.up * .35f, closest + Vector3.up * .35f, mask, QueryTriggerInteraction.Ignore)) return true;
            }
            return false;
        }

        /// <summary>
        /// #874 Q12 (F12): the flood water as a footprint, or none when the round is not Flooded.
        /// Host-side like every footprint: only the server paths soldiers.
        /// </summary>
        internal static BlacksiteHazardFootprint ForFlood(TimeOfDay time)
            => time == null || time.currentLevelWeather != LevelWeatherType.Flooded
                ? default
                : new BlacksiteHazardFootprint(time, null, BlacksiteHazardFootprintKind.Water,
                    new Vector3(0f, FloodPlane(time), 0f), 0f);

        private static float FloodPlane(TimeOfDay time)
            => BlacksiteHazardPolicy.FloodPlaneHeight(time.currentWeatherVariable, time.currentWeatherVariable2, time.globalTime);
    }
}
