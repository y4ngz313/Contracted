using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #948 S12 U3: whether a Giant Sapsucker is presently hostile to this squad, and where its
    /// eggs and nest are.
    ///
    /// Lawson's verdict after run 2 (2026-09-10, Deluge): soldiers should avoid the sapsucker and
    /// its eggs unless it is already on them. One bird killed 7 of 12 soldiers across two
    /// deployments because the squad classed it as an ordinary visible threat and opened fire on a
    /// bird that was minding its nest, and vanilla's own egg-thief mark (GiantKiwiAI.cs:562-583)
    /// then made that permanent.
    ///
    /// This is the one place that decides "hot or cold". BlacksiteThreatCompatibility.GetSupportLevel
    /// and ExcludesWeapons, BlacksiteHazardAwareness.IsHazard, BlacksiteVanillaMonsters
    /// .SoldierReaction and BlacksiteMonsterCombatAdapterRegistry.ResolveAdapter all consult it, so
    /// there is no second opinion anywhere: while the latch is cold the bird is
    /// BlacksiteMonsterSupportLevel.AvoidUnlessHostile and every acquisition path already refuses
    /// it; while it is hot the bird is exactly the NativeVisibleThreat it shipped as.
    ///
    /// Server-authoritative for the one thing it WRITES (the vanilla egg-thief prune). The latch
    /// itself is read wherever it is asked, because every field it reads is either replicated by
    /// vanilla or host-only state that a client never acts on.
    /// </summary>
    internal static class BlacksiteKiwiThreatState
    {
        // Reflective handles, resolved once. GiantKiwiAI.eggs, .birdNest and .protectNestRadius are
        // public and are read directly; these three are not.
        private static readonly FieldInfo WatchingThreatField = AccessTools.Field(typeof(GiantKiwiAI), "watchingThreat");
        private static readonly FieldInfo AttackingThreatField = AccessTools.Field(typeof(GiantKiwiAI), "attackingThreat");
        private static readonly FieldInfo SeenThreatsHoldingEggField = AccessTools.Field(typeof(GiantKiwiAI), "seenThreatsHoldingEgg");

        /// <summary>
        /// How often the bird's own fields are re-read, so the latch never costs a per-frame
        /// reflective read. 0.32 s is the cadence the deleted adapter-registry acquire loop ran at.
        /// </summary>
        private const float PollSeconds = .32f;

        /// <summary>The chase state. GiantKiwiAI state 2 is "attacking a threat".</summary>
        private const int ChaseState = 2;

        private sealed class Latch
        {
            internal bool Hot;
            internal string Cause = BlacksiteDiagnosticFormat.None;
            internal float HotUntil;
            internal float NextPollAt;
        }

        private static readonly Dictionary<GiantKiwiAI, Latch> Latches = new Dictionary<GiantKiwiAI, Latch>();
        private static readonly BlacksiteChangeGate LatchGate = new BlacksiteChangeGate();

        /// <summary>Round end, like every other Blacksite static. Called from the registry reset.</summary>
        internal static void Reset()
        {
            Latches.Clear();
            LatchGate.Reset();
        }

        /// <summary>True while this bird is a threat the squad may fight.</summary>
        internal static bool IsHostile(EnemyAI enemy)
        {
            var bird = enemy as GiantKiwiAI;
            if (bird == null) return false;
            Latch latch = Resolve(bird);
            if (latch == null) return false;
            Evaluate(bird, latch);
            return latch.Hot;
        }

        /// <summary>
        /// True while this bird is something to walk around rather than something to shoot. This is
        /// the predicate every compatibility and hazard question asks.
        /// </summary>
        internal static bool IsAvoided(EnemyAI enemy) => enemy is GiantKiwiAI && !IsHostile(enemy);

        /// <summary>
        /// Latch source 2: the bird has hit one of ours. Called from
        /// BlacksiteSoldierAI.NotifyAttackedByEnemy, which already runs for every monster hit, so a
        /// member that is being killed is never route-refused away from the fight.
        ///
        /// Deliberately NOT called when a soldier shoots the bird: one stray round must not start a
        /// permanent war, and vanilla's own HitEnemy handler already answers a stray round.
        /// </summary>
        internal static void NotifyAttacked(EnemyAI enemy)
        {
            var bird = enemy as GiantKiwiAI;
            if (bird == null) return;
            Latch latch = Resolve(bird);
            if (latch == null) return;
            Refresh(bird, latch, "damaged");
        }

        /// <summary>
        /// #1283 S5: how many discs one cold sapsucker owns - one per egg, then its nest - or 0 for
        /// anything else, a dead bird or a hostile one. Read each through <see cref="ColdDisc"/>.
        /// #1293 R1: indexed rather than enumerated, so a member's hazard read on every perception
        /// pass and the node filters walk a bird's discs without allocating.
        /// </summary>
        internal static int ColdDiscCount(EnemyAI enemy)
        {
            var bird = enemy as GiantKiwiAI;
            return bird == null || bird.isEnemyDead || !bird.isActiveAndEnabled || IsHostile(bird) ? 0 : EggCount(bird) + 1;
        }

        /// <summary>
        /// Disc <paramref name="index"/> (below <see cref="ColdDiscCount"/>) of a bird: its eggs, then
        /// its nest. GiantKiwiAI.eggs is the bird's own complete egg list - vanilla adds each egg it
        /// spawns there (SpawnEggsClientRpc) and never removes one, LostEggs is drawn from it, and the
        /// egg-thief mark (GiantKiwiAI.cs:565-576) is measured against it alone - so an abandoned egg
        /// is still in it and no scene scan is needed (#1293 R1). Default for a held egg - the member
        /// holding it is already in the worst possible trouble and refusing its routes would only
        /// trap it - and for a deactivated egg or a missing nest.
        /// </summary>
        internal static BlacksiteHazardFootprint ColdDisc(EnemyAI enemy, int index)
        {
            var bird = (GiantKiwiAI)enemy;
            if (index < EggCount(bird)) return EggFootprint(bird, bird.eggs[index]);
            return bird.birdNest == null ? default
                : new BlacksiteHazardFootprint(bird.birdNest, bird, BlacksiteHazardFootprintKind.Nest,
                    bird.birdNest.transform.position, BlacksiteHazardPolicy.NestFootprintRadius(bird.protectNestRadius));
        }

        private static int EggCount(GiantKiwiAI bird) => bird.eggs != null ? bird.eggs.Count : 0;

        private static BlacksiteHazardFootprint EggFootprint(GiantKiwiAI bird, KiwiBabyItem egg)
            => egg == null || egg.isHeld || egg.isHeldByEnemy || egg.deactivated
                ? default
                : new BlacksiteHazardFootprint(egg, bird, BlacksiteHazardFootprintKind.Egg,
                    egg.transform.position, BlacksiteHazardPolicy.EggFootprintRadius);

        private static Latch Resolve(GiantKiwiAI bird)
        {
            PruneDeadBirds();
            if (!Latches.TryGetValue(bird, out Latch latch))
            {
                latch = new Latch();
                Latches[bird] = latch;
            }
            return latch;
        }

        private static void PruneDeadBirds()
        {
            if (Latches.Count == 0) return;
            List<GiantKiwiAI> gone = null;
            foreach (var pair in Latches)
                if (pair.Key == null) (gone ?? (gone = new List<GiantKiwiAI>())).Add(pair.Key);
            if (gone == null) return;
            for (int i = 0; i < gone.Count; i++) Latches.Remove(gone[i]);
        }

        /// <summary>
        /// Re-read the bird and decide. Sources 1, 3 and 4 are polled on
        /// <see cref="PollSeconds"/>; source 2 arrives as an event. The latch is HELD outright
        /// while the bird is in its state-2 chase, so the clock only ever runs on a bird that has
        /// already stopped chasing.
        /// </summary>
        private static void Evaluate(GiantKiwiAI bird, Latch latch)
        {
            if (Time.time >= latch.NextPollAt)
            {
                latch.NextPollAt = Time.time + PollSeconds;
                string cause = PolledCause(bird);
                if (cause != null) Refresh(bird, latch, cause);
            }
            if (!latch.Hot) return;
            if (bird.currentBehaviourStateIndex == ChaseState) return;   // held while it is chasing
            if (Time.time < latch.HotUntil) return;
            latch.Hot = false;
            latch.Cause = "cooled";
            Report(bird, latch);
            PruneEggThieves(bird);
        }

        /// <summary>
        /// Latch sources 1, 3 and 4, in the order the evidence is strongest.
        /// 1. The bird is watching or attacking a live soldier of ours.
        /// 3. It is in its state-2 chase with one of ours on its permanent egg-thief list.
        /// 4. One of ours is inside baton reach of a state-2 bird, and being cornered means fighting.
        /// </summary>
        private static string PolledCause(GiantKiwiAI bird)
        {
            if (IsOurs(ThreatTransform(AttackingThreatField, bird)) || IsOurs(ThreatTransform(WatchingThreatField, bird))
                || IsOurs(bird.watchingThreatTransform))
                return "attacking";
            if (bird.currentBehaviourStateIndex != ChaseState) return null;
            if (SeenThreatsHoldingEggField?.GetValue(bird) is IList seen)
                for (int i = 0; i < seen.Count; i++)
                    if (IsOurs(seen[i] as Transform)) return "eggThief";
            var soldiers = BlacksiteVanillaMonsters.LivingSoldiers();
            for (int i = 0; i < soldiers.Count; i++)
                if (Vector3.Distance(soldiers[i].transform.position, bird.transform.position)
                    <= BlacksiteCombatPolicy.BatonAttackRange)
                    return "cornered";
            return null;
        }

        private static Transform ThreatTransform(FieldInfo field, GiantKiwiAI bird)
        {
            var threat = field?.GetValue(bird) as IVisibleThreat;
            return threat == null ? null : threat.GetThreatTransform();
        }

        private static bool IsOurs(Transform candidate)
        {
            if (candidate == null) return false;
            var soldier = candidate.GetComponentInParent<BlacksiteSoldierAI>();
            return BlacksiteVanillaMonsters.Living(soldier);
        }

        private static void Refresh(GiantKiwiAI bird, Latch latch, string cause)
        {
            latch.HotUntil = Time.time + BlacksiteHazardPolicy.HostileLatchSeconds;
            bool changed = !latch.Hot || !string.Equals(latch.Cause, cause, System.StringComparison.Ordinal);
            latch.Hot = true;
            latch.Cause = cause;
            if (changed) Report(bird, latch);
        }

        /// <summary>
        /// #948 S12 U3: the sample shape is
        /// <c>[UAT][KiwiThreat] bird=1234, latch=hot, cause=attacking, hostileFor=12.0</c>.
        /// Emitted on a transition only, through the S11 U6 change gate, so a bird that stays hot
        /// for a whole engagement costs one line and not one per poll.
        /// </summary>
        private static void Report(GiantKiwiAI bird, Latch latch)
        {
            if (!BlacksiteDiagnostics.Enabled) return;
            int key = bird.GetInstanceID();
            string state = (latch.Hot ? "hot|" : "cold|") + latch.Cause;
            if (!LatchGate.ShouldReport(key, state, out _)) return;
            float remaining = latch.Hot ? Mathf.Max(0f, latch.HotUntil - Time.time) : 0f;
            BlacksiteDiagnostics.Log("KiwiThreat",
                $"bird={key}, latch={(latch.Hot ? "hot" : "cold")}, cause={latch.Cause}, hostileFor={remaining:0.0}");
        }

        /// <summary>
        /// #948 S12 U3 A6: when the latch cools, release every LIVE soldier the bird has marked as
        /// an egg thief but which is no longer standing inside an egg footprint.
        ///
        /// Vanilla never prunes seenThreatsHoldingEgg for a non-player threat (GiantKiwiAI.cs:562-583
        /// adds, :1028 hunts, nothing removes), so without this a single member that once walked
        /// within 3 m of an egg is hunted for the rest of the round and "avoid the bird" is
        /// unachievable after one mistake. A member that is still on an egg keeps its mark: this is
        /// a release for somebody who has walked away, not an amnesty.
        ///
        /// #948 S12 U3: the removal itself is U1's BlacksiteKiwiThreatPatches.PruneLiveSoldier
        /// (#947 S12 U1), the one entry that edits seenThreatsHoldingEgg, so the release and the
        /// cooling prune share one server-gated path.
        /// </summary>
        private static void PruneEggThieves(GiantKiwiAI bird)
        {
            if (!BlacksiteVanillaMonsters.Authority()) return;
            if (!(SeenThreatsHoldingEggField?.GetValue(bird) is IList seen) || seen.Count == 0) return;
            for (int i = seen.Count - 1; i >= 0; i--)
            {
                var candidate = seen[i] as Transform;
                if (candidate == null) { seen.RemoveAt(i); continue; }
                var soldier = candidate.GetComponentInParent<BlacksiteSoldierAI>();
                if (soldier == null) continue;                       // vanilla owns player marks
                if (BlacksiteVanillaMonsters.Living(soldier) && StandsOnAnEgg(bird, soldier.transform.position)) continue;
                // #948 S12 U3 / #947 S12 U1: the release entry U1 owns (server-gated there).
                BlacksiteKiwiThreatPatches.PruneLiveSoldier(bird, candidate);
            }
        }


        private static bool StandsOnAnEgg(GiantKiwiAI bird, Vector3 point)
        {
            for (int i = 0; i < EggCount(bird); i++)
                if (EggFootprint(bird, bird.eggs[i]).Contains(point, 0f)) return true;
            return false;
        }
    }
}
