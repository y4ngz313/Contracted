using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // Actor targets never enter the game's PlayerControllerB arrays or player-victim RPCs.
    // Native movement/presentation continues; leases replace only player target selection.
    internal static partial class BlacksiteVanillaMonsters
    {
        // Wire format: the byte travels on SyncVanillaMonsterClientRpc, so values are APPENDED and
        // never reordered. #1293 R5 adds the last one (the Thumper's bite, folded in from its RPC).
        internal enum Event : byte { Acquire, Release, SpiderHit, ButlerHit, BarberHit, BrackenGrab, GiantGrab, DogGrab, FleaCling, EndCapture, Consumed, JesterHit, CrawlerHit }
        private sealed class Pursuit
        {
            internal BlacksiteSoldierAI Soldier;
            internal Vector3 LastSeen;
            internal float Expires, NextSync, SeenAt, Interest;
            internal ulong PreviousOwner;
            internal bool ClaimedOwner;
        }
        private sealed class Capture
        {
            internal BlacksiteSoldierAI Soldier;
            internal Event Kind;
            internal float Started, NextDamage;
            internal bool Killed;
            // #874 S13: the layer-0 animator state this captor was in when the capture pose was
            // triggered, recorded per peer by ApplyEvent and consumed by RestoreCapturePose on an
            // interrupted release. 0 when the animator could not be read. It lives and dies with
            // the capture record, so a rescue, a death, a disconnect or a round reset takes it.
            internal int PoseRestoreState;
            // #874 Plan F: the ONE baton member committed to ending this capture, and since when.
            // Host-only decision state: TryGetRescueAssignment is its only writer and every reader
            // runs on the host, so a client's copy of the record simply leaves it null. It lives
            // and dies with the record, so ClearCapture - the single release funnel - releases it
            // exactly once whatever ended the capture.
            internal BlacksiteSoldierAI Rescuer;
            internal float RescuerSince;
        }
        private static readonly Dictionary<EnemyAI, Pursuit> Pursuits = new Dictionary<EnemyAI, Pursuit>();
        private static readonly Dictionary<EnemyAI, Capture> Captures = new Dictionary<EnemyAI, Capture>();
        // #1305 E4 (D13): each Coil-Head's ONE watcher. Host-only, written by ElectWatchers alone; Reset clears it per round.
        private static readonly Dictionary<EnemyAI, BlacksiteSoldierAI> Watchers = new Dictionary<EnemyAI, BlacksiteSoldierAI>();
        private static readonly Dictionary<EnemyAI, float> NextHit = new Dictionary<EnemyAI, float>();
        // Host-only per-spider debounce for the every-frame web-trap acquisition (see WebContact).
        private const float WebReacquireSeconds = .5f;
        private static readonly Dictionary<EnemyAI, float> WebAcquire = new Dictionary<EnemyAI, float>();
        private static readonly Dictionary<(Type, string), FieldInfo> Fields = new Dictionary<(Type, string), FieldInfo>();
        private static readonly Dictionary<Type, FieldInfo[]> SearchFields = new Dictionary<Type, FieldInfo[]>();
        private static readonly List<BlacksiteSoldierAI> Soldiers = new List<BlacksiteSoldierAI>();
        private static readonly List<EnemyAI> Scratch = new List<EnemyAI>();
        // #1293 R5: NotifySoldierUnavailable's own lists. It runs inside a member's death (KillEnemy), which
        // TickCaptures' capture kill and the renew loop's dedicated Nutcracker shot reach while they walk
        // Scratch, so it never clears or refills Scratch or Soldiers. Main thread only.
        private static readonly List<EnemyAI> HandoffLeases = new List<EnemyAI>();
        private static readonly List<BlacksiteSoldierAI> HandoffCandidates = new List<BlacksiteSoldierAI>();
        private static readonly HashSet<BlacksiteSoldierAI> Participants = new HashSet<BlacksiteSoldierAI>();
        internal static void Register(BlacksiteSoldierAI soldier) => Participants.Add(soldier);
        internal static void Unregister(BlacksiteSoldierAI soldier)
        {
            Participants.Remove(soldier);
            Scratch.Clear(); foreach (var pair in Captures) if (pair.Value.Soldier == soldier) Scratch.Add(pair.Key);
            foreach (var enemy in Scratch) EndCapture(enemy, Authority());
        }
        private static float _nextScan;
        internal static T Read<T>(object instance, string name)
        {
            var key = (instance.GetType(), name);
            if (!Fields.TryGetValue(key, out var field)) Fields[key] = field = AccessTools.Field(key.Item1, name)
                ?? throw new MissingFieldException(key.Item1.FullName, name);
            return (T)field.GetValue(instance);
        }
        internal static void Write<T>(object instance, string name, T value)
        {
            Read<T>(instance, name);
            Fields[(instance.GetType(), name)].SetValue(instance, value);
        }
        internal static bool Authority() => RoundManager.Instance != null && RoundManager.Instance.IsServer;
        private static void CollectSoldiers()
        {
            Soldiers.Clear();
            Participants.RemoveWhere(soldier => soldier == null);
            foreach (var soldier in Participants) if (Living(soldier)) Soldiers.Add(soldier);
        }
        /// <summary>
        /// #1096 (WP5): does the squad know about a Jester that is winding up near it? One host-side
        /// bool, refreshed by the 0.2 s scan below inside the walk that already holds the live
        /// soldier list, and read by exactly two consumers - the urgent role speed
        /// (<c>BlacksiteSoldierAI.GetRoleMoveSpeed</c>) and the optional salvage fallback
        /// (<c>BlacksiteObjectiveManifestController.Raid</c>). Both ask the same fact through this
        /// one property rather than repeating the lookup.
        ///
        /// "Known" is distance and world only, deliberately without the reaction cone: Lawson's
        /// decision is that the squad knows a Jester exists, and a squad that is hurrying away from
        /// one is by definition no longer facing it - a sight test here would switch the hurry off
        /// in the first second of it. The cone still governs the popped Jester's evade, which is a
        /// per-member reaction to a contact the member is looking at.
        ///
        /// Quiet path: no Jester on the roster costs the type test the scan already pays.
        /// </summary>
        internal static bool CrankingJesterKnown => _crankingJesterKnown;
        private static bool _crankingJesterKnown;
        // #1283 S7a: an un-configured member is a rider stowed aboard the inbound aircraft, not a participant.
        internal static bool Living(BlacksiteSoldierAI soldier) => soldier != null && soldier.isActiveAndEnabled
            && soldier.HasReplicatedIdentity && !soldier.isEnemyDead && !soldier.ReplicatedDead && !soldier.HasBoardedShip;
        private static bool Active(EnemyAI enemy) => enemy != null && enemy.isActiveAndEnabled && !enemy.isEnemyDead && enemy.ventAnimationFinished;
        // #1219 B1: a thin name for BlacksiteWorld.Same, kept because the native fixtures call it.
        private static bool SameWorld(EnemyAI enemy, BlacksiteSoldierAI soldier) => BlacksiteWorld.Same(enemy, soldier);
        internal static int WorldMask => StartOfRound.Instance != null ? StartOfRound.Instance.collidersAndRoomMaskAndDefault : 268435712;
        internal static bool Clear(Vector3 from, Vector3 to) => !Physics.Linecast(from, to, WorldMask, QueryTriggerInteraction.Ignore);
        private static Vector3 Eyes(EnemyAI enemy) => enemy.eye != null ? enemy.eye.position : enemy.transform.position + Vector3.up;
        private static bool Sees(EnemyAI enemy, BlacksiteSoldierAI soldier, float range, float angle = 80f)
        {
            Vector3 delta = soldier.GetThreatLookTransform().position - Eyes(enemy);
            return SameWorld(enemy, soldier) && delta.sqrMagnitude <= range * range
                && (delta.sqrMagnitude < 4f || Vector3.Angle(enemy.eye != null ? enemy.eye.forward : enemy.transform.forward, delta) <= angle)
                && Clear(Eyes(enemy), soldier.GetThreatLookTransform().position);
        }
        internal static bool SoldierSees(BlacksiteSoldierAI soldier, EnemyAI enemy, float range = 30f, float angle = 68f)
        {
            if (!Living(soldier) || soldier.stunNormalizedTimer > 0f || soldier.MonsterVisionBlocked || !SameWorld(enemy, soldier)) return false;
            Vector3 origin = soldier.GetThreatLookTransform().position;
            Vector3 low = enemy.transform.position + Vector3.up * .6f;
            Vector3 high = enemy.transform.position + Vector3.up * (enemy is SpringManAI ? 3f : 1.6f);
            Vector3 delta = low - origin;
            return delta.sqrMagnitude > .09f && delta.sqrMagnitude <= range * range
                && Vector3.Angle(soldier.transform.forward, Vector3.ProjectOnPlane(delta, Vector3.up)) < angle
                && (Clear(origin, low) || Clear(origin, high));
        }
        internal static bool AddSoldierObservers(bool playersWatching, SpringManAI coil)
        {
            if (playersWatching) return true;
            // This executes on the native owner, including when that owner is a remote player.
            // Observer transforms and capture/stun state are replicated by the soldier itself.
            CollectSoldiers();
            foreach (var soldier in Soldiers) if (SoldierSees(soldier, coil)) return true;
            return false;
        }
        private static PlayerControllerB BetterPlayer(EnemyAI enemy, BlacksiteSoldierAI soldier)
        {
            if (StartOfRound.Instance == null) return null;
            float limit = Vector3.Distance(Eyes(enemy), soldier.GetThreatLookTransform().position);
            foreach (var player in StartOfRound.Instance.allPlayerScripts)
                if (player != null && player.isPlayerControlled && !player.isPlayerDead && !player.inAnimationWithEnemy
                    && enemy.PlayerIsTargetable(player) && BlacksiteWorld.Same(player, enemy)
                    && Vector3.Distance(Eyes(enemy), player.gameplayCamera.transform.position) <= limit
                    && Clear(Eyes(enemy), player.gameplayCamera.transform.position)) return player;
            return null;
        }
        internal static bool OwnsStrategy(EnemyAI enemy)
        {
            if (Captures.ContainsKey(enemy)) return true;
            if (!Pursuits.TryGetValue(enemy, out var state)) return false;
            if (!Active(enemy) || !Living(state.Soldier) || !SameWorld(enemy, state.Soldier) || Time.time > state.Expires)
            { Release(enemy); return false; }
            if (enemy is SpringManAI coil && (coil.onCooldownPhase > 0f || Read<bool>(coil, "setOnCooldown"))) return false;
            return !(enemy is MouthDogAI);
        }
        // #874 P-S4: this monster holds its pursuit lease on this member - it has chosen him. Host
        // state: only the authority writes Pursuits, so the answer is false on every client.
        private static bool AcquiredBy(EnemyAI enemy, BlacksiteSoldierAI soldier)
            => Pursuits.TryGetValue(enemy, out var state) && ReferenceEquals(state.Soldier, soldier);
        /// <summary>
        /// #1233 S6 (rule 5): the "it is hunting a squadmate" half of the engagement gate, from
        /// this bridge's own ledgers - a pursuit lease naming a living member, or a capture. The
        /// decision that writes either is the authority's; clients hold a presentation mirror
        /// through <c>SyncVanillaMonsterClientRpc</c>, and every caller of the gate is itself a
        /// host path, so no client decides an engagement from this answer.
        /// </summary>
        internal static bool Hunts(EnemyAI enemy)
            => !ReferenceEquals(enemy, null)
               && (Captures.ContainsKey(enemy) || (Pursuits.TryGetValue(enemy, out var state) && Living(state.Soldier)));
        // #1019 S5: widened from private to internal, unchanged in every other respect. It is the
        // one test for "a PLAYER is in this monster's hands", which is what separates the
        // AttachedToPlayer threat state from AttachedToAlly; the alternative was transcribing the
        // expression into BlacksiteThreatAdapters.cs, and a duplicated live rule is how S4's
        // deletion block went wrong.
        internal static bool HasNativePlayerCapture(EnemyAI enemy) => enemy.inSpecialAnimationWithPlayer != null
            || (enemy is CentipedeAI flea && flea.clingingToPlayer != null);
        internal static bool IsCapturing(EnemyAI enemy) => enemy != null && Captures.ContainsKey(enemy);
        internal static bool VictimCaptured(BlacksiteSoldierAI soldier)
        {
            foreach (var capture in Captures.Values) if (capture.Soldier == soldier) return true;
            return false;
        }
        // #1011 A3: the squadmate this monster is holding, or null. The capture registry has always
        // been able to answer "is this enemy capturing" and "is this soldier captured"; this is the
        // third question - WHO - and it is what lets the rescue decisions below name a victim
        // without a second scan or a monster type check.
        // #1262 P4: a capture outlives its kill until endAt; a corpse is not a squadmate to rescue or hold fire for.
        internal static BlacksiteSoldierAI CaptureVictim(EnemyAI enemy)
            => !ReferenceEquals(enemy, null) && Captures.TryGetValue(enemy, out var capture) && !capture.Killed ? capture.Soldier : null;
        /// <summary>
        /// #874 Plan F (after #1011 A3, observed failure O3): the captured squadmate THIS member is
        /// the committed rescuer of, and the monster holding it. Before Plan F this answered "the
        /// nearest captured squadmate within the radius" for every caller, so every baton member
        /// inside 12 m took the Rescue intent and only the lane claims kept them apart.
        ///
        /// Each live capture carries one <c>Capture.Rescuer</c>, elected lazily here on the host by
        /// whichever member asks (<c>ElectRescuer</c>): a still-eligible incumbent
        /// keeps it, otherwise the eligible member closest to the victim takes it. The answer is
        /// true only for the elected member, nearest victim first.
        ///
        /// Allocation-free on purpose: <c>Captures</c> and <c>Participants</c> are walked through
        /// their own struct enumerators and nothing is collected. Nothing here ends a capture, so
        /// the walks are safe.
        /// </summary>
        internal static bool TryGetRescueAssignment(BlacksiteSoldierAI self, float radius,
            out BlacksiteSoldierAI victim, out EnemyAI captor)
        {
            victim = null; captor = null;
            if (self == null || radius <= 0f) return false;
            float best = float.MaxValue;
            foreach (var pair in Captures)
            {
                var enemy = pair.Key;
                var capture = pair.Value;
                var soldier = capture.Soldier;
                if (!Active(enemy) || !Living(soldier)) continue;
                ElectRescuer(enemy, capture, radius);
                if (!ReferenceEquals(capture.Rescuer, self)) continue;
                float distance = Vector3.Distance(soldier.transform.position, self.transform.position);
                if (distance >= best) continue;
                best = distance; victim = soldier; captor = enemy;
            }
            return !ReferenceEquals(captor, null);
        }

        /// <summary>
        /// #874 Plan F: this monster is holding a squadmate and ANOTHER member is its committed
        /// rescuer, so this member leaves it alone. Read-only: the callers have already run the
        /// election on this think through <see cref="TryGetRescueAssignment"/>. A capture with no
        /// rescuer answers false on purpose, so a member outside the radius may still close on
        /// the captor and become eligible.
        /// </summary>
        internal static bool RescuedByAnother(EnemyAI captor, BlacksiteSoldierAI self)
            => !ReferenceEquals(captor, null) && Captures.TryGetValue(captor, out var capture)
               && !capture.Killed && !ReferenceEquals(capture.Rescuer, null) && !ReferenceEquals(capture.Rescuer, self) && Living(capture.Rescuer);

        // #874 Plan F: keep the incumbent while it is still eligible, otherwise hand the capture to
        // the eligible member closest to the victim (or to nobody). Logged on change only, never
        // per query. #1233 S6: the blocked-approach COUNTER that used to hand a rescue on is
        // deleted; what hands it on now is the melee executor's own ApproachBlocked verdict on this
        // captor, fresh from the rescuer's last think (BlacksiteSoldierAI.RescueApproachBlocked).
        private static void ElectRescuer(EnemyAI captor, Capture capture, float radius)
        {
            var incumbent = capture.Rescuer;
            bool hadIncumbent = !ReferenceEquals(incumbent, null);
            string cause = hadIncumbent ? RescueRefusal(incumbent, captor, capture, radius) : null;
            if (hadIncumbent && cause == null) return;
            var victim = capture.Soldier;
            BlacksiteSoldierAI best = null;
            float bestDistance = float.MaxValue;
            foreach (var candidate in Participants)
            {
                if (ReferenceEquals(candidate, incumbent) || RescueRefusal(candidate, captor, capture, radius) != null
                    || RescuesAnother(candidate, captor)) continue;
                float distance = Vector3.Distance(candidate.transform.position, victim.transform.position);
                if (distance >= bestDistance) continue;
                best = candidate; bestDistance = distance;
            }
            if (best == null && !hadIncumbent) return;
            capture.Rescuer = best;
            capture.RescuerSince = best != null ? Time.time : 0f;
            if (!BlacksiteDiagnostics.Enabled) return;
            if (!hadIncumbent)
                BlacksiteDiagnostics.Log("Rescue", $"assign victim={victim.MemberIndex}, rescuer={best.MemberIndex}, distance={bestDistance:0.00}, captor={captor.GetType().Name}.");
            else
                BlacksiteDiagnostics.Log("Rescue", $"handoff victim={victim.MemberIndex}, from={incumbent.MemberIndex}, to="
                    + (best != null ? best.MemberIndex.ToString() : "none") + $", cause={cause}.");
        }

        // #874 Plan F: why this member may not be (or stay) the rescuer of this capture, or null
        // when it may. The answer is the cause= printed on a [UAT][Rescue] handoff line.
        private static string RescueRefusal(BlacksiteSoldierAI soldier, EnemyAI captor, Capture capture, float radius)
        {
            if (!Living(soldier) || !Participants.Contains(soldier)) return "dead";
            if (VictimCaptured(soldier)) return "captured";
            // #1293 R2: a stranded member cannot walk to the captor, so he never holds a rescue.
            if (soldier.Navigator.IsStranded) return "stranded";
            if (!soldier.UsesBaton) return "ineligible";
            if (!BlacksiteWorld.Same(soldier, capture.Soldier)
                || !BlacksiteCombatPolicy.RescueTakesPriority(
                    Vector3.Distance(soldier.transform.position, capture.Soldier.transform.position), radius))
                return "out-of-range";
            if (soldier.RescueApproachBlocked(captor)) return "blocked";
            if (!soldier.CanConsiderRescueCaptor(captor)) return "ineligible";
            return null;
        }

        // #874 Plan F: one capture per rescuer, so a member between two held squadmates is never
        // named for a victim it is not walking to.
        private static bool RescuesAnother(BlacksiteSoldierAI soldier, EnemyAI captor)
        {
            foreach (var pair in Captures)
                if (!ReferenceEquals(pair.Key, captor) && ReferenceEquals(pair.Value.Rescuer, soldier) && Active(pair.Key) && !pair.Value.Killed)
                    return true;
            return false;
        }
        // #1305 E4 (D13, R14): the perception pass's watcher election. A Coil-Head the squad knows (a table row: the acquire's
        // Told row or the frozen disc's Live row; a lease; a member's own sight; the watch held) keeps a watcher that CanWatch;
        // else goes to the member it acquired if idle, else the nearest idle, else the nearest eligible one. One per watcher.
        internal static void ElectWatchers(BlacksiteWorldModel world, List<BlacksiteSoldierAI> members)
        {
            if (RoundManager.Instance == null || RoundManager.Instance.SpawnedEnemies == null) return;
            foreach (var coil in RoundManager.Instance.SpawnedEnemies)
            {
                var row = BlacksiteMonsterCapabilities.For(coil); if (!row.Gaze) continue;
                bool had = Watchers.TryGetValue(coil, out var incumbent), active = Active(coil);
                if (had && active && incumbent.CanWatch(coil, row.ReactionRange)) continue;
                bool known = active && (had || world.TryGet(coil.NetworkObjectId, out _) || Hunts(coil));
                BlacksiteSoldierAI best = null; float bestScore = float.MaxValue;
                if (active) foreach (var member in members)
                {
                    known = known || SoldierSees(member, coil, row.ReactionRange, row.ReactionConeDegrees);
                    if (Watchers.ContainsValue(member) || !member.CanWatch(coil, row.ReactionRange)) continue;
                    // A rank outweighs any distance in range: the idle reporter 0, idle (not travelling) 1, travelling 2.
                    float score = (member.Navigator.AgentVelocity >= BlacksiteMotionPolicy.MovingSpeed ? 2000f : AcquiredBy(coil, member) ? 0f : 1000f) + Vector3.Distance(member.transform.position, coil.transform.position);
                    if (score < bestScore) { best = member; bestScore = score; }
                }
                if (had && Watchers.Remove(coil)) LogWatch(incumbent, coil, "released");
                if (known && best != null) { Watchers[coil] = best; LogWatch(best, coil, "elected"); }
            }
        }
        private static bool ElectedWatcher(EnemyAI coil, BlacksiteSoldierAI soldier) => Watchers.TryGetValue(coil, out var watcher) && ReferenceEquals(watcher, soldier);
        private static void LogWatch(BlacksiteSoldierAI watcher, EnemyAI coil, string reason)
        { if (BlacksiteDiagnostics.Enabled) BlacksiteDiagnostics.Log("Monster", $"watch kind=Coil watcher={watcher.MemberIndex} reason={reason} entity={coil.NetworkObjectId}"); }

        // #874 Plan F: the cause printed on the [UAT][Rescue] release line, read from the state
        // ClearCapture was reached in. "ended" is every other release: the flea's hurt reaction,
        // a capture timeout, a native player grab and the round reset.
        private static string RescueReleaseCause(EnemyAI enemy, Capture capture)
        {
            var victim = capture.Soldier;
            if (!Active(enemy)) return "captor-lost";
            if (enemy.stunNormalizedTimer > 0f) return "hit";
            if (victim == null || victim.isEnemyDead || victim.ReplicatedDead || capture.Killed) return "victim-dead";
            if (victim.HasBoardedShip || !SameWorld(enemy, victim)) return "victim-left";
            return "ended";
        }
        internal static void Reset()
        {
            Scratch.Clear(); Scratch.AddRange(Captures.Keys);
            foreach (var enemy in Scratch) EndCapture(enemy, broadcast: Authority());
            Scratch.Clear(); Scratch.AddRange(Pursuits.Keys);
            foreach (var enemy in Scratch) Release(enemy);
            Captures.Clear(); Pursuits.Clear(); Watchers.Clear(); NextHit.Clear(); WebAcquire.Clear(); Guns.Clear(); SlimeHits.Clear(); Soldiers.Clear(); Participants.Clear(); _nextScan = 0f;
            // #1096: no new dictionary, so nothing new to sweep - but the derived flag must not
            // survive a round, or the next squad starts the mission at the urgent speed.
            _crankingJesterKnown = false;
            // #1219 F1: the once-per-type contact lines (fallback=default, not-hittable) are once per
            // type per ROUND, so every round's log names what it met.
            GenericContactLogged.Clear();
            BlacksiteHitResolver.Reset();
        }
        internal static void UpdateRuntime()
        {
            if (!Authority()) return;
            TickCaptures();
            if (Time.time < _nextScan) return;
            _nextScan = Time.time + .2f;
            CollectSoldiers();
            Scratch.Clear(); Scratch.AddRange(Pursuits.Keys);
            foreach (var enemy in Scratch)
            {
                if (!OwnsStrategy(enemy) && !Pursuits.ContainsKey(enemy)) continue;
                if (!Pursuits.TryGetValue(enemy, out var state)) continue;
                if (Captures.ContainsKey(enemy)) continue;
                if (BetterPlayer(enemy, state.Soldier) != null) { Release(enemy); continue; }
                // #1096 (WP5): the lease follows the machine shutting. Nothing else here would drop
                // it - Active() is still true for a closed Jester, Living()/SameWorld() are about
                // the soldier, and Expires is refreshed by this very loop - so a Jester that
                // vanilla stood back down to state 0 would otherwise keep a lease that suppresses
                // its own player targeting. Released within one 0.2 s scan.
                if (enemy is JesterAI closing
                    && closing.currentBehaviourStateIndex != BlacksiteMonsterCapabilities.JesterPoppedState) { Release(enemy); continue; }
                bool visible = Sees(enemy, state.Soldier, enemy is ForestGiantAI ? 50f : 30f, 100f);
                if (visible) { state.LastSeen = state.Soldier.transform.position; state.SeenAt = Time.time; }
                if (Time.time - state.SeenAt > 8f) { Release(enemy); continue; }
                state.Expires = Time.time + 3f;
                if (state.ClaimedOwner && enemy.NetworkObject != null && enemy.NetworkObject.IsSpawned
                    && enemy.NetworkObject.OwnerClientId != NetworkManager.ServerClientId) enemy.ChangeOwnershipOfEnemy(NetworkManager.ServerClientId);
                if (Time.time >= state.NextSync)
                {
                    state.NextSync = Time.time + 1f; Publish(enemy, state.Soldier, Event.Acquire);
                    // #874 P-S4: the engagement the acquisition started is renewed on the lease's own
                    // sync clock, so a member the monster keeps hunting keeps answering it for as
                    // long as the lease lives rather than for one immediate-attacker window.
                    ReactToAcquisition(enemy, state.Soldier, out _);
                }
                TickPursuit(enemy, state, .2f);
            }
            _crankingJesterKnown = false;
            var enemies = RoundManager.Instance.SpawnedEnemies;
            if (enemies == null) return;
            foreach (var enemy in enemies)
            {
                // #1293 R5: the leased classes are the capability table's LeasedMotor rows.
                if (!BlacksiteMonsterCapabilities.For(enemy).LeasedMotor || !Active(enemy) || enemy.stunNormalizedTimer >= 0f || Captures.ContainsKey(enemy)) continue;
                if (enemy is JesterAI jester)
                {
                    // #1096 (WP5): the two Jester facts this walk answers, in the order they matter.
                    // Cranking (behaviour state 1) within the reaction radius of any living member
                    // is the squad's hurry; the Soldiers list is the one CollectSoldiers refreshed
                    // above, so this is a squared distance per member and no ray. A stunned Jester
                    // never reaches here, which is correct: vanilla pauses its pop timer too.
                    if (jester.currentBehaviourStateIndex == 1 && !_crankingJesterKnown)
                        foreach (var soldier in Soldiers)
                            if (SameWorld(jester, soldier) && (jester.transform.position - soldier.transform.position).sqrMagnitude
                                    <= BlacksiteCombatPolicy.MonsterReactionRange * BlacksiteCombatPolicy.MonsterReactionRange)
                            {
                                _crankingJesterKnown = true;
                                if (BlacksiteDiagnostics.Enabled)
                                    BlacksiteDiagnostics.LogRateLimited($"monster.jester.aware.{jester.GetInstanceID()}", 5f, "Monster",
                                        $"jester aware: id={jester.GetInstanceID()}, state=cranking, member={soldier.MemberIndex}, "
                                        + "squad=hurry, salvageFallback=skipped.");
                                break;
                            }
                    // Only a machine that is OPEN hunts a soldier, and that is the LIVE index, not
                    // the sticky latch. The latch stays where #1086 put it - the member's flee arm,
                    // which must keep running from a Jester that has merely lost its target - but a
                    // lease taken on it would suppress the closed machine's own player targeting
                    // for the life of the lease while Contact refused to kill, because Contact also
                    // reads the live index.
                    if (jester.currentBehaviourStateIndex != BlacksiteMonsterCapabilities.JesterPoppedState) continue;
                }
                if (enemy is MouthDogAI) continue; // Native hearing strategy stays intact.
                if (enemy.inSpecialAnimationWithPlayer != null || (enemy.inSpecialAnimation && !(enemy is CentipedeAI))) continue;
                if (Pursuits.ContainsKey(enemy)) continue;
                if (enemy is CentipedeAI hidingFlea && !hidingFlea.clingingToCeiling && hidingFlea.currentBehaviourStateIndex != 2) continue;
                if (enemy is ButlerEnemyAI passiveButler && passiveButler.currentBehaviourStateIndex != 2
                    && Read<float>(passiveButler, "berserkModeTimer") <= 0f && NearbyPeople(enemy) != 1) continue;
                BlacksiteSoldierAI best = null; float distance = float.MaxValue;
                foreach (var soldier in Soldiers)
                {
                    if (!Living(soldier) || VictimCaptured(soldier) || !SameWorld(enemy, soldier)) continue;
                    bool sees = enemy is CentipedeAI flea && flea.clingingToCeiling
                        ? UnderFlea(flea, soldier) : Sees(enemy, soldier, enemy is ForestGiantAI ? 40f : 20f);
                    float candidate = (enemy.transform.position - soldier.transform.position).sqrMagnitude;
                    if (sees && candidate < distance && BetterPlayer(enemy, soldier) == null) { best = soldier; distance = candidate; }
                }
                if (best != null) Acquire(enemy, best);
            }
            // Bounded cleanup also covers despawned actors that never received another collision.
            Scratch.Clear(); foreach (var enemy in NextHit.Keys) if (enemy == null || !enemy.isActiveAndEnabled || enemy.isEnemyDead) Scratch.Add(enemy);
            foreach (var enemy in Scratch) NextHit.Remove(enemy);
            Scratch.Clear(); foreach (var enemy in WebAcquire.Keys) if (enemy == null || !enemy.isActiveAndEnabled || enemy.isEnemyDead) Scratch.Add(enemy);
            foreach (var enemy in Scratch) WebAcquire.Remove(enemy);
            Scratch.Clear(); foreach (var gunner in Guns.Keys) if (gunner == null || !gunner.isActiveAndEnabled || gunner.isEnemyDead) Scratch.Add(gunner);
            foreach (var enemy in Scratch) Guns.Remove((NutcrackerEnemyAI)enemy);
        }
        internal static void ForceAggro(EnemyAI enemy, BlacksiteSoldierAI soldier)
        {
            if (!Authority() || !BlacksiteMonsterCapabilities.For(enemy).LeasedMotor || !Active(enemy) || !Living(soldier) || !SameWorld(enemy, soldier)) return;
            if (enemy is MouthDogAI dog) { dog.DetectNoise(soldier.transform.position, 1f, 0, 0); return; }
            // #1096 (WP5): only an OPEN Jester may take a lease, on the LIVE index exactly as in
            // UpdateRuntime. Nothing can hurt a closed one into chasing, and forcing it to would put a
            // member in front of a machine whose own crank has not finished.
            if ((enemy is CentipedeAI hiding && !hiding.clingingToCeiling && hiding.currentBehaviourStateIndex != 2)
                || (enemy is JesterAI && enemy.currentBehaviourStateIndex != BlacksiteMonsterCapabilities.JesterPoppedState)
                // #1293 R5: a Thumper is drawn only along a clear line, as its deleted adapter's ForceAggro was.
                || (enemy is CrawlerAI && !Clear(Eyes(enemy), soldier.GetThreatLookTransform().position))
                || Captures.ContainsKey(enemy) || BetterPlayer(enemy, soldier) != null) return;
            if (Pursuits.TryGetValue(enemy, out var current) && Living(current.Soldier))
            { if (enemy is ButlerEnemyAI) Write(enemy, "berserkModeTimer", 8f); return; }
            Acquire(enemy, soldier);
            if (enemy is ButlerEnemyAI) { Write(enemy, "berserkModeTimer", 8f); SetState(enemy, 2); }
            if (enemy is FlowermanAI bracken) { bracken.angerMeter = 8f; SetState(enemy, 2); }
        }
        private static void Acquire(EnemyAI enemy, BlacksiteSoldierAI soldier)
        {
            if (Captures.ContainsKey(enemy)) return;
            var state = new Pursuit { Soldier = soldier, LastSeen = soldier.transform.position, SeenAt = Time.time,
                Expires = Time.time + 3f };
            Pursuits[enemy] = state;
            if (enemy.NetworkObject != null && enemy.NetworkObject.IsSpawned)
            {
                state.PreviousOwner = enemy.NetworkObject.OwnerClientId; state.ClaimedOwner = true;
                if (enemy.NetworkObject.OwnerClientId != NetworkManager.ServerClientId) enemy.ChangeOwnershipOfEnemy(NetworkManager.ServerClientId);
            }
            BeginLease(enemy);
            if (enemy is CentipedeAI flea && flea.clingingToCeiling)
                flea.TriggerCentipedeFallServerRpc(NetworkManager.ServerClientId);
            else if (enemy is SandSpiderAI) SetState(enemy, 2);
            else if (enemy is SpringManAI coil && coil.onCooldownPhase <= 0f && !Read<bool>(coil, "setOnCooldown")) SetState(enemy, 1);
            else if (enemy is ForestGiantAI) SetState(enemy, 1);
            // #1097: the bloom's state 0 is the one state whose native Update does NOT write a
            // destination - it only holds agent.speed at 5 and clears the chest plant - while its
            // state 1 jitter-chases a (possibly stale) targetPlayer every frame. The strategy
            // transpiler gates the interval that would switch it back to 1, so parking it here is
            // what leaves the lease's destination standing. Release() already restores state 0.
            else if (enemy is CadaverBloomAI) SetState(enemy, 0);
            Publish(enemy, soldier, Event.Acquire);
            soldier.NoteAdapterAcquireServer(enemy, Vector3.Distance(enemy.transform.position, soldier.transform.position)); // #1262 P4: every acquisition faces the member at its hunter, registry or lease alike.
            var intent = ReactToAcquisition(enemy, soldier, out string reason);
            if (BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.LogRateLimited($"monster.acquire.{enemy.GetInstanceID()}.{soldier.MemberIndex}", 2f,
                    "Monster", $"acquire type={enemy.GetType().Name}, member={soldier.MemberIndex}, position={state.LastSeen}, "
                    + $"intent={intent}, reason={reason}.");
        }
        /// <summary>
        /// #874 P-S4: the acquisition's intent, from the threat table
        /// (<c>BlacksiteThreatResponsePolicy.DecideAcquired</c>), delivered before the member's next
        /// think. In the 2026-09-23 run a Forest Keeper acquired rifle guard member 5 (t=1859.70) and
        /// grabbed him 5.3 s later with no intent in between: the table was only ever asked about a
        /// contact the member had SEEN, and he never saw this one.
        ///
        /// Evade and Watch need no delivery here: <see cref="SoldierReaction"/> asks the same question
        /// of every lease that names the member once per squad perception pass (#1305 H); TickMonsterReaction then takes
        /// the body from the squad order for an evade and only turns it for a watch (#1271 S3a). An
        /// engagement goes through the member's existing
        /// "a monster is on me" entry, <c>NotifyAttackedByEnemy</c> - remembered target, exact track,
        /// brain re-decision and the weapon's immediate-attacker priority, which is the path a
        /// monster's first hit already takes. Ignore is the table's answer and is only reported.
        /// Host-only, like every caller.
        /// </summary>
        private static BlacksiteThreatIntent ReactToAcquisition(EnemyAI enemy, BlacksiteSoldierAI soldier, out string reason)
        {
            if (!BlacksiteThreatAdapterRegistry.DescribeClass(enemy, soldier, out var threatClass, out var state))
            {
                reason = "gone";
                return BlacksiteThreatIntent.Ignore;
            }
            var context = new BlacksiteSquadContext(
                allyCaptured: false,
                contactIsCaptor: false,
                // #1305 E4: SoldierReaction's watch rule - the Gaze row's squad-elected watcher.
                isObserverOfContact: ElectedWatcher(enemy, soldier),
                contactInReactionRange: true,
                withdrawing: false,
                warningFireOpen: BlacksiteThreatAdapterRegistry.WarningFireOpen(enemy, soldier));
            var response = BlacksiteThreatResponsePolicy.DecideAcquired(in threatClass, state,
                soldier.UsesBaton ? BlacksiteEngagementRole.Melee : BlacksiteEngagementRole.Rifle,
                BlacksiteThreatProximity.None, in context, out reason);
            if (response.Engages) soldier.NotifyAttackedByEnemy(enemy);
            return response.Intent;
        }
        private static void Release(EnemyAI enemy)
        {
            if (ReferenceEquals(enemy, null) || !Pursuits.TryGetValue(enemy, out var state)) return;
            Pursuits.Remove(enemy);
            if (enemy == null) return;
            // #1293 R5: a released Nutcracker's gun clocks go with the lease, as its adapter's reset took them.
            if (enemy is NutcrackerEnemyAI gunner) Guns.Remove(gunner);
            if (Authority())
            {
                Publish(enemy, state.Soldier, Event.Release);
                // #1293 R5: Publish skips a gone (null/destroyed) member, ApplyEvent's gun-down included, so the
                // host lowers a Nutcracker's gun itself (the deleted adapter's release lowered it unconditionally).
                if (state.Soldier == null && enemy is NutcrackerEnemyAI unleased) LowerGun(unleased);
                if (HasNativePlayerCapture(enemy)) return;
                var player = Living(state.Soldier) ? BetterPlayer(enemy, state.Soldier) : null;
                if (player != null) { enemy.targetPlayer = player; if (enemy is ForestGiantAI giant) giant.chasingPlayer = player; }
                if (state.ClaimedOwner && enemy.NetworkObject != null && enemy.NetworkObject.IsSpawned && NetworkManager.Singleton != null)
                {
                    ulong owner = player != null ? player.actualClientId : state.PreviousOwner;
                    if (NetworkManager.Singleton.ConnectedClients.ContainsKey(owner) && enemy.NetworkObject.OwnerClientId != owner) enemy.ChangeOwnershipOfEnemy(owner);
                }
                // #1096 (WP5): the Jester joins the Coil-Head and the dog in keeping its own state
                // on release. Forcing an open Jester back to state 0 would close the machine, run
                // SetJesterInitialValues and hand a nearby player a fresh 35 to 40 second timer
                // purely because the soldier it was chasing died. The stand-down is vanilla's own
                // state-2 arm, made reachable for soldiers by JesterInterval: while a living
                // soldier is in the Jester's world the postfix writes noPlayersToChaseTimer = 5f
                // every interval, so once the last player AND the last soldier are out of the
                // facility, vanilla's own countdown closes the machine 5 s later.
                if (player == null && !enemy.isEnemyDead && !(enemy is SpringManAI) && !(enemy is MouthDogAI)
                    && !(enemy is JesterAI) && !Captures.ContainsKey(enemy)) SetState(enemy, 0);
                // #1293 R5 (the deleted crawler adapter's release): a Thumper chases the player it was
                // handed straight away, else nothing; it never keeps walking to the soldier's last point.
                if (enemy is CrawlerAI) { enemy.moveTowardsDestination = false; enemy.movingTowardsTargetPlayer = player != null; return; }
            }
            enemy.movingTowardsTargetPlayer = false;
        }
        private static void SetState(EnemyAI enemy, int state)
        {
            if (enemy.currentBehaviourStateIndex == state) return;
            enemy.SwitchToBehaviourStateOnLocalClient(state);
            if (enemy.NetworkObject != null && enemy.NetworkObject.IsSpawned) enemy.SwitchToBehaviourClientRpc(state);
        }
        /// <summary>
        /// #1293 R5 (the deleted registry TryHandoff): a member died (KillEnemy, host). A squad hunter
        /// (the capability table's SquadHunter rows) he was leasing passes to the nearest living teammate
        /// it can see within 20 m with no better player, keeping its state and its clocks; a Thumper's
        /// bite clock restarts for the new victim (#988 B5). Clients re-point on the Acquire event. Any
        /// other lease, and a hunter with nobody to pass to, is released by OwnsStrategy as before.
        /// </summary>
        internal static void NotifySoldierUnavailable(BlacksiteSoldierAI fallen)
        {
            if (!Authority() || fallen == null) return;
            HandoffLeases.Clear(); foreach (var pair in Pursuits) if (pair.Value.Soldier == fallen) HandoffLeases.Add(pair.Key);
            if (HandoffLeases.Count == 0) return;
            HandoffCandidates.Clear(); foreach (var soldier in Participants) if (Living(soldier)) HandoffCandidates.Add(soldier);
            foreach (var enemy in HandoffLeases)
            {
                if (!Active(enemy) || !Pursuits.TryGetValue(enemy, out var state) || !BlacksiteMonsterCapabilities.For(enemy).SquadHunter) continue;
                BlacksiteSoldierAI next = null; float best = 20f * 20f;
                foreach (var candidate in HandoffCandidates)
                {
                    float gap = (enemy.transform.position - candidate.transform.position).sqrMagnitude;
                    if (gap >= best || VictimCaptured(candidate) || !Sees(enemy, candidate, 20f, 180f) || BetterPlayer(enemy, candidate) != null) continue;
                    next = candidate; best = gap;
                }
                if (next == null) continue;
                state.Soldier = next; state.LastSeen = next.transform.position; state.SeenAt = Time.time; state.Expires = Time.time + 3f;
                bool bite = enemy is CrawlerAI;
                if (bite) Write(enemy, "timeSinceHittingPlayer", 0f);
                Publish(enemy, next, Event.Acquire);
                BlacksiteDiagnostics.Log("Monster", $"handoff type={enemy.GetType().Name}, previous={fallen.MemberIndex}, member={next.MemberIndex}; "
                    + (bite ? "bite clock reset for the new victim." : "native combat clocks retained."));
            }
            HandoffLeases.Clear(); HandoffCandidates.Clear();
        }
        // The first acquire's housekeeping, on every peer (the host's Acquire, a client's first Acquire
        // event): native searches stop, and a Thumper enters the chase its own state-1 entry opens.
        private static void BeginLease(EnemyAI enemy)
        {
            StopSearches(enemy);
            if (!(enemy is CrawlerAI crawler)) return;
            Write(crawler, "hasEnteredChaseMode", true); Write(crawler, "lostPlayerInChase", false);
            crawler.noticePlayerTimer = 0f; crawler.useSecondaryAudiosOnAnimatedObjects = true; crawler.openDoorSpeedMultiplier = 1.5f;
            if (crawler.agent != null) crawler.agent.stoppingDistance = .5f;
        }
        private static void StopSearches(EnemyAI enemy)
        {
            // Clear=false preserves native search nodes for the player handoff. The declared-field
            // scan is cached per concrete type (same scope as GetDeclaredFields, no inheritance).
            var type = enemy.GetType();
            if (!SearchFields.TryGetValue(type, out var fields))
            {
                var found = new List<FieldInfo>();
                foreach (var field in AccessTools.GetDeclaredFields(type)) if (field.FieldType == typeof(AISearchRoutine)) found.Add(field);
                SearchFields[type] = fields = found.ToArray();
            }
            foreach (var field in fields)
                if (field.GetValue(enemy) is AISearchRoutine search && search.inProgress) enemy.StopSearch(search, clear: false);
        }
        private static void Publish(EnemyAI enemy, BlacksiteSoldierAI soldier, Event action)
        {
            if (enemy == null || soldier == null) return;
            ApplyEvent(enemy, soldier, action);
            if (Authority() && soldier.NetworkObject != null && soldier.NetworkObject.IsSpawned && enemy.NetworkObject != null && enemy.NetworkObject.IsSpawned)
                soldier.SyncVanillaMonsterClientRpc(new NetworkObjectReference(enemy.NetworkObject), (byte)action);
        }
    }
}
