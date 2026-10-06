using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static partial class BlacksiteVanillaMonsters
    {
        private static readonly MethodInfo BrackenAnimation = AccessTools.Method(typeof(FlowermanAI), "CalculateAnimationDirection");
        // #1293 R5: a leased Nutcracker's gun clocks, from its deleted registry adapter - when it may
        // aim again and, on a dedicated server (where vanilla's client-side AimGun coroutine never runs),
        // when its shot and its reload land. Host state, per enemy and outside the Pursuit so a handoff
        // keeps them; Release, Reset and the despawn sweep in UpdateRuntime clear them.
        private sealed class GunClock { internal float NextAim, FireAt = float.MaxValue, ReloadAt = float.MaxValue; }
        private static readonly Dictionary<NutcrackerEnemyAI, GunClock> Guns = new Dictionary<NutcrackerEnemyAI, GunClock>();
        private static readonly AccessTools.FieldRef<NutcrackerEnemyAI, bool> Aiming = AccessTools.FieldRefAccess<NutcrackerEnemyAI, bool>("aimingGun");
        private static readonly AccessTools.FieldRef<NutcrackerEnemyAI, bool> Reloading = AccessTools.FieldRefAccess<NutcrackerEnemyAI, bool>("reloadingGun");
        internal static readonly AccessTools.FieldRef<SpringManAI, bool> CoilStopping = AccessTools.FieldRefAccess<SpringManAI, bool>("stoppingMovement");
        private static readonly AccessTools.FieldRef<NutcrackerEnemyAI, Vector3> SeenPosition = AccessTools.FieldRefAccess<NutcrackerEnemyAI, Vector3>("lastSeenPlayerPos");
        private static readonly System.Action<NutcrackerEnemyAI, Vector3> FaceNutcracker = AccessTools.MethodDelegate<System.Action<NutcrackerEnemyAI, Vector3>>(
            AccessTools.Method(typeof(NutcrackerEnemyAI), "SetTargetDegreesToPosition"));
        private static readonly System.Action<NutcrackerEnemyAI> LowerGun = AccessTools.MethodDelegate<System.Action<NutcrackerEnemyAI>>(
            AccessTools.Method(typeof(NutcrackerEnemyAI), "StopAimingGun"));
        private static bool GunBusy(NutcrackerEnemyAI gunner) => Aiming(gunner) || Reloading(gunner);
        // The deleted adapter's motor, on the lease's 0.2 s tick (host): its native combat state 2, the
        // dedicated server's own fire and reload, then an aim at the member on the 3.1 s clock once the
        // muzzle bears within 20 degrees.
        private static void TickGun(NutcrackerEnemyAI gunner, BlacksiteSoldierAI soldier)
        {
            if (gunner.currentBehaviourStateIndex != 2) { gunner.StopInspection(); SetState(gunner, 2); }
            if (!Guns.TryGetValue(gunner, out var clock)) Guns[gunner] = clock = new GunClock();
            var gun = gunner.gun;
            if (!gunner.IsClient && gun != null)
            {
                if (Time.time >= clock.ReloadAt) { clock.ReloadAt = float.MaxValue; gun.shellsLoaded = 2; Reloading(gunner) = false; }
                if (Time.time >= clock.FireAt)
                {
                    clock.FireAt = float.MaxValue; gunner.FireGunClientRpc();
                    BlacksiteNativeShotgun.ApplyDedicated(gunner);
                    gun.shellsLoaded = Mathf.Max(0, gun.shellsLoaded - 1);
                    Aiming(gunner) = false; return;
                }
            }
            Vector3 aim = soldier.GetThreatLookTransform().position;
            float distance = Vector3.Distance(gunner.transform.position, soldier.transform.position);
            if (distance > 24f || distance < 2f || GunBusy(gunner) || Time.time < clock.NextAim || !Clear(Eyes(gunner), aim)) return;
            var muzzle = gun != null ? gun.shotgunRayPoint : null;
            if (muzzle == null || Vector3.Angle(muzzle.forward, aim - muzzle.position) > 20f) return;
            clock.NextAim = Time.time + 3.1f;
            // The argument is the shooter's own position, NOT the target: the native server entry owns
            // ammunition, reload eligibility and the authentic aim/fire timing.
            bool reload = gun.shellsLoaded <= 0;
            gunner.AimGunServerRpc(gunner.transform.position);
            if (gunner.IsClient) return;
            if (reload) { Reloading(gunner) = true; clock.ReloadAt = Time.time + 1.74f; }
            else clock.FireAt = Time.time + (gunner.enemyHP <= 1 ? .5f : gun.shellsLoaded == 1 ? 1.3f : 1.75f);
        }
        private static bool UnderFlea(CentipedeAI flea, BlacksiteSoldierAI soldier)
        {
            Vector3 delta = soldier.GetThreatLookTransform().position - flea.transform.position;
            return delta.y < 0f && delta.y > -20f && Vector3.ProjectOnPlane(delta, Vector3.up).sqrMagnitude < 2.15f * 2.15f
                && Clear(flea.transform.position, soldier.GetThreatLookTransform().position);
        }
        private static bool AnyoneObserves(EnemyAI enemy)
        {
            foreach (var soldier in Soldiers) if (SoldierSees(soldier, enemy)) return true;
            if (StartOfRound.Instance != null)
                foreach (var player in StartOfRound.Instance.allPlayerScripts)
                    if (player != null && player.isPlayerControlled && !player.isPlayerDead
                        && BlacksiteWorld.Same(player, enemy)
                        && player.HasLineOfSightToPosition(enemy.transform.position + Vector3.up * 1.6f, 68f, 30)) return true;
            return false;
        }
        // #1293 R5 (D3): vanilla SpringManAI.OnCollideWithPlayer's gate on the native fields, the frozen
        // test being the coil's own stop/go verdict (vanilla's player check plus the soldiers
        // BlacksiteCoilObservationPatch adds). A refusal builds its one [UAT][Monster] line only with
        // diagnostics on, once per cause per 2 s.
        private static bool CoilMayStrike(SpringManAI coil, BlacksiteSoldierAI touched)
        {
            int cause = coil.currentBehaviourStateIndex != 1 ? 1
                : Read<bool>(coil, "setOnCooldown") ? 2
                : Read<float>(coil, "hitPlayerTimer") >= 0f ? 3
                : Time.realtimeSinceStartup - coil.timeAtLastCooldown < .45f ? 4
                : CoilStopping(coil) ? 5 : 0;
            if (cause == 0) return true;
            if (BlacksiteDiagnostics.Enabled && BlacksiteDiagnostics.AllowRateLimited("monster.coil.refused." + coil.GetInstanceID() + "." + cause, 2f))
                BlacksiteDiagnostics.Log("Monster", $"coil contact refused: id={coil.GetInstanceID()}, member={touched.MemberIndex}, cause="
                    + (cause == 1 ? "state=" + coil.currentBehaviourStateIndex : cause == 2 ? "cooldown" : cause == 3 ? "hit-timer"
                        : cause == 4 ? "post-cooldown" : "frozen") + ".");
            return false;
        }
        private static int NearbyPeople(EnemyAI enemy)
        {
            int count = 0;
            foreach (var soldier in Soldiers) if (Living(soldier) && Sees(enemy, soldier, 8f, 180f)) count++;
            if (StartOfRound.Instance != null)
                foreach (var player in StartOfRound.Instance.allPlayerScripts)
                    if (player != null && player.isPlayerControlled && !player.isPlayerDead && BlacksiteWorld.Same(player, enemy)
                        && Vector3.Distance(player.transform.position, enemy.transform.position) < 8f
                        && Clear(Eyes(enemy), player.gameplayCamera.transform.position)) count++;
            return count;
        }
        private static void Destination(EnemyAI enemy, Vector3 position)
        {
            enemy.movingTowardsTargetPlayer = false;
            if (enemy.agent == null || !enemy.agent.enabled || !enemy.agent.isOnNavMesh || enemy.stunNormalizedTimer >= 0f) return;
            if (!NavMesh.SamplePosition(position, out var nav, 1.5f, enemy.agent.areaMask) || Mathf.Abs(nav.position.y - position.y) > 1.5f) return;
            enemy.moveTowardsDestination = true;
            enemy.SetDestinationToPosition(nav.position, checkForPath: true);
        }
        private static void TickPursuit(EnemyAI enemy, Pursuit state, float delta)
        {
            if (enemy.stunNormalizedTimer >= 0f || enemy is ClaySurgeonAI) return;
            Vector3 goal = state.LastSeen;
            if (enemy is FlowermanAI bracken)
            {
                bool seen = AnyoneObserves(enemy);
                if (bracken.currentBehaviourStateIndex == 2)
                {
                    bracken.angerMeter -= delta;
                    if (bracken.angerMeter <= 0f) { SetState(bracken, 1); state.Interest = 0f; }
                }
                else if (seen)
                {
                    state.Interest += delta;
                    SetState(bracken, 1);
                    if (state.Interest > 2f) { bracken.angerMeter = 8f; SetState(bracken, 2); }
                }
                else { state.Interest = Mathf.Max(0f, state.Interest - delta); SetState(bracken, 0); }
                if (bracken.currentBehaviourStateIndex != 2)
                {
                    if (seen)
                        goal = enemy.transform.position + (enemy.transform.position - state.LastSeen).normalized * 6f;
                    else goal -= state.Soldier.transform.forward * 2f;
                }
            }
            if (enemy is ButlerEnemyAI butler)
            {
                bool berserk = Read<float>(butler, "berserkModeTimer") > 0f;
                int count = NearbyPeople(enemy);
                if (count > 1 && !berserk) { Release(enemy); return; }
                if (count == 1) state.Interest += delta; else state.Interest = 0f;
                if (berserk || state.Interest >= 8f) SetState(enemy, 2);
                if (enemy.currentBehaviourStateIndex != 2 && Vector3.Distance(goal, enemy.transform.position) < 4f) goal = enemy.transform.position;
            }
            if (enemy is CentipedeAI flea && (flea.clingingToCeiling || flea.inSpecialAnimation)) return;
            if (enemy is SpringManAI coil)
            {
                // #874 F09: the lease has to re-enter behaviour state 1 after vanilla's own
                // movement budget expires, because the gate this package puts on
                // SpringManAI.DoAIInterval is what removes the ONLY native writer that ever does.
                //
                // Installed v81 SpringManAI (Assembly-CSharp sha256 5f7db553...): Update case 1
                // accumulates timeSpentMoving while the Coil-Head is actually moving and, past 9 s,
                // writes onCooldownPhase = 11, setOnCooldown = true and
                // SwitchToBehaviourStateOnLocalClient(0). DoAIInterval case 0 then counts the phase
                // down, clears setOnCooldown, and - this is the half that is lost - re-enters state
                // 1 from its own player line-of-sight scan. OwnsStrategy answers false while the
                // Coil-Head is on cooldown, so the native interval runs and the countdown completes
                // normally; the moment it clears, the gate closes again and that scan never runs.
                // It only ever looked at StartOfRound.allPlayerScripts in any case, so a squad the
                // lease is chasing could not have re-armed it even ungated.
                //
                // Without this the Coil-Head spends the rest of the round in state 0 holding a live
                // lease: UpdateRuntime keeps refreshing the lease while it can still see the member,
                // so nothing releases it, Acquire (the other caller of this state write) is never
                // reached again, and Contact's state-1 gate - which is native OnCollideWithPlayer's
                // own gate, currentBehaviourStateIndex == 1 - refuses every contact from then on.
                // It follows the member forever and can never hurt it. That is F09.
                //
                // Nothing here is a new budget or a shortened cooldown: the 9 s of movement and the
                // 11 s phase are still vanilla's, still counted by vanilla, and a watched Coil-Head
                // put back into state 1 is still held still by AddSoldierObservers' stoppingMovement.
                // StopSearches is the first thing vanilla's own case 1 does (DoAIInterval case 1
                // stops searchForPlayers), and case 0 starts that search while the cooldown clears.
                if (coil.currentBehaviourStateIndex == 0 && coil.onCooldownPhase <= 0f && !Read<bool>(coil, "setOnCooldown"))
                {
                    StopSearches(coil);
                    SetState(coil, 1);
                    if (BlacksiteDiagnostics.Enabled)
                        BlacksiteDiagnostics.Log("Monster",
                            $"coil resumed after native cooldown: id={coil.GetInstanceID()}, member={state.Soldier.MemberIndex}.");
                }
                if (coil.currentBehaviourStateIndex != 1 || CoilStopping(coil)) return;
            }
            // #1293 R5: the Nutcracker's motor is its gun and its walk (AfterUpdate), never the shared
            // destination; a Thumper chases in its own state 1 unless an animation holds it.
            if (enemy is NutcrackerEnemyAI gunner) { TickGun(gunner, state.Soldier); return; }
            if (enemy is CrawlerAI) { if (enemy.inSpecialAnimation) return; SetState(enemy, 1); }
            Destination(enemy, goal);
        }
        // Called after native Update. Native clocks, mesh interpolation, sounds, and base network sync still run.
        internal static void AfterUpdate(EnemyAI enemy)
        {
            if (!OwnsStrategy(enemy)) return;
            if (Captures.ContainsKey(enemy))
            {
                if (enemy.agent != null && enemy.agent.enabled && enemy.agent.isOnNavMesh) enemy.agent.speed = 0f;
                return;
            }
            if (!Pursuits.TryGetValue(enemy, out var state)) return;
            enemy.movingTowardsTargetPlayer = false;
            if (enemy is FlowermanAI bracken)
            {
                bool anger = enemy.currentBehaviourStateIndex == 2;
                bracken.creatureAnimator?.SetBool("anger", anger);
                bracken.creatureAnimator?.SetBool("sneak", !anger);
                if (anger && !bracken.isInAngerMode) bracken.creatureAngerVoice?.Play();
                else if (!anger && bracken.isInAngerMode) bracken.creatureAngerVoice?.Stop();
                bracken.isInAngerMode = anger;
                BrackenAnimation.Invoke(bracken, new object[] { anger ? 3f : 1f });
                if (bracken.agent != null) bracken.agent.speed = bracken.stunNormalizedTimer >= 0f ? 0f : anger ? 12f : 6f;
            }
            if (enemy is NutcrackerEnemyAI gunner)
            {
                // #1293 R5 (the deleted adapter's per-frame half, on every peer): track and face the
                // member and drive the animator's combat state, aim and aim-down; its Update switch is gated.
                Vector3 point = state.Soldier.GetThreatLookTransform().position;
                SeenPosition(gunner) = point; FaceNutcracker(gunner, point);
                gunner.creatureAnimator?.SetInteger("State", 2);
                gunner.creatureAnimator?.SetBool("Aiming", Aiming(gunner));
                gunner.creatureAnimator?.SetBool("AimDown", Vector3.Distance(point, gunner.transform.position) < 2f);
            }
            if (!Authority() || enemy.agent == null || !enemy.agent.enabled) return;
            if (enemy.stunNormalizedTimer >= 0f) { enemy.agent.speed = 0f; return; }
            if (enemy is SandSpiderAI spider)
            {
                spider.lookingForWallPosition = false;
                spider.overrideSpiderLookRotation = false;
                Write(spider, "watchFromDistance", false);
                enemy.agent.speed = Read<float>(spider, "timeSinceHittingPlayer") < .5f ? .7f
                    : spider.onWall ? 4.25f : spider.enemyHP <= 1 ? 5.04f : spider.enemyHP == 2 ? 4.58f : 4.4f;
                spider.spiderSpeed = Read<float>(spider, "timeSinceHittingPlayer") < .5f ? .4f : enemy.agent.speed;
            }
            else if (enemy is ForestGiantAI giant)
            {
                enemy.agent.speed = Mathf.Min(enemy.agent.speed + Time.deltaTime * 1.3f, 7f);
                giant.lookTarget.position = state.LastSeen;
                Write(giant, "lookingAtTarget", true);
            }
            else if (enemy is ButlerEnemyAI butler && enemy.currentBehaviourStateIndex == 2)
                enemy.agent.speed = Read<float>(butler, "timeSinceChangingItem") < 1.7f ? 0f : 8f;
            // #1293 R5: a Nutcracker stands to aim or reload, and walks to the member only beyond 12 m.
            else if (enemy is NutcrackerEnemyAI nutcracker && enemy.agent.isOnNavMesh)
            {
                bool busy = GunBusy(nutcracker);
                enemy.agent.speed = busy ? 0f : 2.5f;
                if (!busy && Vector3.Distance(enemy.transform.position, state.Soldier.GetThreatLookTransform().position) > 12f)
                    enemy.SetDestinationToPosition(state.Soldier.transform.position);
                else if (enemy.agent.hasPath) enemy.agent.ResetPath();
            }
            // Native Spring/Flea Update normally sets movingTowardsTargetPlayer. Reapply the
            // observed NPC destination without touching their spring/drop motor or cooldowns.
            if (enemy is SpringManAI coil && !CoilStopping(coil) && !Read<bool>(coil, "setOnCooldown")) Destination(enemy, state.LastSeen);
            else if (enemy is CentipedeAI flea && !flea.clingingToCeiling && !flea.inSpecialAnimation) Destination(enemy, state.LastSeen);
        }
        internal static void BarberBeat(ClaySurgeonAI barber)
        {
            if (!Authority() || !OwnsStrategy(barber) || barber.stunNormalizedTimer > 0f || !Pursuits.TryGetValue(barber, out var state)) return;
            // Only the native dance beat grants a destination; native jumpTimer/speed/snipping remain intact.
            Vector3 origin = state.LastSeen;
            if (Physics.Raycast(origin + Vector3.up * .5f, Vector3.down, out var floor, 7f, WorldMask, QueryTriggerInteraction.Ignore)) origin = floor.point;
            Vector3 direction = Random.onUnitSphere; direction.y = 0f;
            var ray = new Ray(origin, direction);
            float distance = Vector3.Distance(barber.transform.position, origin);
            Vector3 goal = Physics.Raycast(ray, out var hit, distance, WorldMask, QueryTriggerInteraction.Ignore) ? hit.point : ray.GetPoint(distance);
            Destination(barber, goal);
        }
        /// <summary>
        /// #948 S12 U3: the squad's own living members, for a caller outside this file. The list is
        /// the existing participant registry refreshed by the existing CollectSoldiers; nothing new
        /// is scanned, and the returned list is the shared scratch every other caller here uses.
        /// </summary>
        internal static List<BlacksiteSoldierAI> LivingSoldiers()
        {
            CollectSoldiers();
            return Soldiers;
        }

        /// <summary>The contact this member must react to rather than fight, and whether the reaction is a
        /// WATCH (the Gaze row the squad elected it to watch, #1305 E4) or an evade. No clock of its own (KiwiAvoidanceChecks
        /// drives it by name); each contact is described once (DescribeClass) and
        /// BlacksiteThreatResponsePolicy.Decide answers.</summary>
        internal static EnemyAI SoldierReaction(BlacksiteSoldierAI soldier, out bool watch)
        {
            watch = false;
            var enemies = RoundManager.Instance?.SpawnedEnemies;
            if (enemies == null || !Living(soldier) || soldier.MonsterVisionBlocked) return null;
            var role = soldier.UsesBaton ? BlacksiteEngagementRole.Melee : BlacksiteEngagementRole.Rifle;
            foreach (var enemy in enemies)
            {
                if (!Active(enemy) || !SameWorld(enemy, soldier)) continue;
                // #1096 (WP5): the ONE chokepoint where popped and cranking part. The threat table
                // is untouched - a Jester still describes to the Evade row, which is what the
                // reaction range and the matrix say - but a Jester that has not popped never
                // becomes this member's reaction contact, so it never displaces a Coil-Head watch,
                // never selects the Evade behaviour and never stalls the member's objective. It
                // cannot touch anybody until it pops: vanilla's kill is state 2 only, against a
                // colliding PLAYER. What the squad does about a cranking one is hurry, and that is
                // a squad-wide fact read in GetRoleMoveSpeed, not a per-member reaction. This also
                // retires #1086's one-contact deferral: the contact it deferred no longer answers.
                if (IsCrankingJester(enemy)) continue;
                if (enemy is BlobAI) continue; // H.check ruling 1: the slime is a hazard footprint (#1305 H: a route and fire constraint), never a reaction.
                if (enemy is ButlerEnemyAI && enemy.currentBehaviourStateIndex != 2) continue; // a passive butler turns hostile on an ISOLATED member (NearbyPeople); backing off is what isolates one - the squad stays put and fights it once it turns.
                if (!BlacksiteThreatAdapterRegistry.DescribeClass(enemy, soldier,
                        out var threatClass, out var state)) continue;
                // #1262 P4: vision runs only for a known contact this member cannot stop (or that has acquired it,
                // or that is watched); a class it fights or no authority names never evades, so it pays no linecast.
                bool acquired = AcquiredBy(enemy, soldier);
                if (!acquired && !threatClass.ReactionIsWatch
                    && (!threatClass.IsKnown || BlacksiteThreatResponsePolicy.WeaponStops(in threatClass, role))) continue;
                bool seen = SoldierSees(soldier, enemy, threatClass.ReactionRange, threatClass.ReactionConeDegrees), elected = ElectedWatcher(enemy, soldier);
                var context = new BlacksiteSquadContext(
                    allyCaptured: false,
                    contactIsCaptor: false,
                    // #1305 E4 (D13): only the Gaze row's squad-elected watcher watches it, turning to it even before it sees it.
                    isObserverOfContact: elected,
                    contactInReactionRange: seen || elected,
                    withdrawing: false,
                    // #874 N8: asked only of a seen contact, so the unseen majority pays nothing. A
                    // rifle with warning fire left at an Old Bird is not sent away by the row that
                    // evades it; the ordinary acquisition takes it instead.
                    warningFireOpen: seen && BlacksiteThreatAdapterRegistry.WarningFireOpen(enemy, soldier));
                // An Engage answer for an acquired contact is not this poll's to act on: Acquire
                // delivered it to the combat path (ReactToAcquisition), and it falls through here.
                var response = acquired
                    ? BlacksiteThreatResponsePolicy.DecideAcquired(
                        in threatClass, state, role, BlacksiteThreatProximity.None, in context, out _)
                    : BlacksiteThreatResponsePolicy.Decide(
                        in threatClass, state, role, BlacksiteThreatProximity.None, in context);
                // Watch: the squad-elected watcher; every other member is answered with nothing.
                if (response.Intent == BlacksiteThreatIntent.Watch) { watch = true; return enemy; }
                if (response.Intent == BlacksiteThreatIntent.Evade) return enemy;
            }
            return null;
        }

        /// <summary>
        /// #1096 (WP5): a Jester that has not opened yet - the only contact on the roster that the
        /// squad is aware of and still walks past. The popped LATCH is read beside the live index,
        /// exactly as #1086's deferral read it, so a Jester that has dropped back out of state 2
        /// after losing a target is still treated as popped for the rest of the round.
        /// </summary>
        private static bool IsCrankingJester(EnemyAI enemy)
            => enemy is JesterAI
               && enemy.currentBehaviourStateIndex < BlacksiteMonsterCapabilities.JesterPoppedState
               && !BlacksiteMonsterCapabilities.JesterPopped(enemy);

        /// <summary>
        /// #1096 (WP5) step 6: the crank a SOLDIER starts. Vanilla leaves behaviour state 0 through
        /// two steps, and this postfix follows both rather than inventing a shortcut.
        /// <c>JesterAI.DoAIInterval</c> case 0 (v81, Assembly-CSharp 81.0.5) calls
        /// <c>TargetClosestPlayer(3f, requireLineOfSight: true, 70f, doGroundCast: true)</c>, whose
        /// sight test is <c>CheckLineOfSightForPosition(camera, width: 70, range: 40)</c> - so the
        /// distance is 40 m, the arc is 70 degrees off the eye's forward and the line must be clear
        /// both ways. Setting <c>targetPlayer</c> is all case 0 does; the TRANSITION is in
        /// <c>JesterAI.Update</c> case 0, which counts <c>beginCrankingTimer</c> down while a target
        /// is held and calls <c>SwitchToBehaviourState(1)</c> at zero. This runs the same countdown
        /// on the same field with the same call, so a soldier buys the same 13 to 21 seconds of
        /// warning a player does and the crank cannot start instantly.
        ///
        /// Host-only, and it stands down the moment vanilla has a player of its own: with
        /// <c>targetPlayer</c> set, vanilla's own Update is already counting the same field down and
        /// a second decrement here would halve a player's warning.
        ///
        /// The SECOND half of this postfix is what makes a soldier-started pop last longer than one
        /// frame. Vanilla <c>JesterAI.Update</c> case 2 stands the machine back down through
        /// <c>noPlayersToChaseTimer</c>, and in v81 that arm is broken for anything but a player:
        /// <c>targetingPlayer</c> is declared and NEVER assigned, so the <c>else</c> branch holding
        /// the only <c>noPlayersToChaseTimer = 5f</c> write is unreachable, and
        /// <c>SetJesterInitialValues</c> seeds the field at 0. With the crew outside the facility
        /// the "any player isPlayerControlled &amp;&amp; isInsideFactory" flag is false, the timer
        /// goes negative on the first frame of state 2 and <c>SwitchToBehaviourState(0)</c> fires
        /// immediately - the pop would last one frame, the lease would never hold, and the crank
        /// would loop forever. Writing the 5 f that vanilla's own dead branch intended, while a
        /// living soldier is in the Jester's world, pre-seeds the timer before the pop and refreshes
        /// it every interval after it; when the last soldier leaves or dies, vanilla stands the
        /// machine down 5 s later exactly as it does for a player. One field write, no new state,
        /// and <c>Update</c> is not gated.
        /// </summary>
        internal static void JesterInterval(JesterAI jester)
        {
            if (!Authority() || !Active(jester)) return;
            int state = jester.currentBehaviourStateIndex;
            if (state == 1 || state == BlacksiteMonsterCapabilities.JesterPoppedState)
            {
                CollectSoldiers();
                foreach (var soldier in Soldiers)
                    if (SameWorld(jester, soldier)) { Write(jester, "noPlayersToChaseTimer", 5f); break; }
                return;
            }
            if (state != 0 || jester.targetPlayer != null || jester.stunNormalizedTimer > 0f) return;
            CollectSoldiers();
            bool detected = false;
            foreach (var soldier in Soldiers) if (Sees(jester, soldier, 40f, 70f)) { detected = true; break; }
            if (!detected) return;
            jester.beginCrankingTimer -= jester.AIIntervalTime;
            if (jester.beginCrankingTimer > 0f) return;
            jester.SwitchToBehaviourState(1);
            if (BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.Log("Monster",
                    $"jester cranking: id={jester.GetInstanceID()}, trigger=soldier, detect=40m/70deg.");
        }
    }
}
