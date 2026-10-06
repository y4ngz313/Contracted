using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AI;
using Y4NGZCompany.Escalations._Shared;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static partial class BlacksiteVanillaMonsters
    {
        /// <summary>
        /// #1219 F1.2: a monster touching a soldier, decided by one table instead of one hand-written
        /// branch per monster. Every row is the monster's own vanilla contact rule: its native gate
        /// (the state and private clocks its <c>OnCollideWithPlayer</c> reads), the raw damage it
        /// deals a player, how long this lease table holds the contact off afterwards, the native
        /// clock it writes back and the event it presents. Capture rows have no damage and act
        /// instead. The shared gates in front of every row are unchanged: host, live, same world,
        /// no stun, no player in its hands, no capture in progress, in reach, a clear line, no better
        /// player, and this monster's own NextHit.
        ///
        /// A type with no row is not ignored any more. <see cref="GenericContact"/> applies the
        /// documented default through the same soldier entry and names the type once, and the
        /// caller's own reaction path still runs for it (the return is false).
        /// </summary>
        internal static bool Contact(EnemyAI enemy, BlacksiteSoldierAI soldier)
        {
            var row = ContactRowFor(enemy);
            if (row == null) return GenericContact(enemy, soldier);
            if (!ContactGatesOpen(enemy, soldier, row.Reach, row.Origin != null ? row.Origin(enemy) : enemy.transform.position)
                || !row.Gate(enemy, soldier)) return true;
            // #1293 R5: the table's LethalOnContactWhenMoving (the Coil-Head) makes the row's touch a kill;
            // the row's own gate is the "while moving".
            int damage = BlacksiteMonsterCapabilities.For(enemy).LethalOnContactWhenMoving ? BlacksiteCombatPolicy.CaptureKillRawDamage : row.Damage;
            if (damage <= 0) { row.Act(enemy, soldier); return true; }
            row.Clock?.Invoke(enemy);
            NextHit[enemy] = Time.time + row.Cooldown;
            if (row.Hit.HasValue) Publish(enemy, soldier, row.Hit.Value);
            soldier.ApplyVanillaMonsterDamage(enemy, damage);
            return true;
        }

        // The gates every contact passes before its own row is asked, verbatim from the branch
        // chain they used to sit in front of.
        private static bool ContactGatesOpen(EnemyAI enemy, BlacksiteSoldierAI soldier, float reach, Vector3 origin)
        {
            if (!Authority() || !Active(enemy) || !Living(soldier) || !SameWorld(enemy, soldier)
                || enemy.stunNormalizedTimer >= 0f || enemy.inSpecialAnimationWithPlayer != null
                || Captures.ContainsKey(enemy) || VictimCaptured(soldier)) return false;
            return InReach(enemy, soldier, reach, origin)
                && BetterPlayer(enemy, soldier) == null
                && !(NextHit.TryGetValue(enemy, out float next) && Time.time < next);
        }

        // The reach half of the gates above: inside the row's reach with a clear line between the
        // two bodies. One owner, read by the contact that lands and by the gate that decides.
        private static bool InReach(EnemyAI enemy, BlacksiteSoldierAI soldier, float reach, Vector3 origin)
            => (origin - soldier.transform.position).sqrMagnitude <= reach * reach
               && Clear(origin + Vector3.up * .5f, soldier.transform.position + Vector3.up * .7f);

        /// <summary>
        /// #1233 S6 (rule 5): this monster's native attack can reach this member NOW - its contact
        /// row's reach and origin (the generic <see cref="DefaultContactReach"/> for a type with no
        /// row) with a clear line. Geometry only: the shared gates above decide whether a touch
        /// LANDS on this frame, not whether the thing standing there is a threat. A class the
        /// squad's weapons refuse is still refused downstream by the threat table's row 6.
        /// </summary>
        internal static bool NativeAttackReaches(EnemyAI enemy, BlacksiteSoldierAI soldier)
        {
            if (enemy == null || soldier == null) return false;
            var row = ContactRowFor(enemy);
            return InReach(enemy, soldier, row != null ? row.Reach : DefaultContactReach,
                row != null && row.Origin != null ? row.Origin(enemy) : enemy.transform.position);
        }

        private delegate bool ContactGate(EnemyAI enemy, BlacksiteSoldierAI soldier);
        private delegate void ContactAct(EnemyAI enemy, BlacksiteSoldierAI soldier);

        /// <summary>
        /// One monster's contact rule. <see cref="Gate"/> is the native gate; <see cref="Damage"/>
        /// the vanilla player damage (raw, converted by ApplyVanillaMonsterDamage); <see cref="Cooldown"/>
        /// this table's NextHit hold; <see cref="Clock"/> writes the native cooldown fields back so
        /// the monster's own clock sees the hit; <see cref="Hit"/> is the presentation event.
        /// A row with no damage is a capture or a reaction and runs <see cref="Act"/> instead.
        /// </summary>
        private sealed class ContactRow
        {
            internal ContactGate Gate;
            internal int Damage;
            internal float Cooldown, Reach = DefaultContactReach;
            internal Event? Hit;
            internal System.Action<EnemyAI> Clock;
            internal ContactAct Act;
            internal System.Func<EnemyAI, Vector3> Origin;
        }

        // Keyed by the vanilla type; a subclass resolves to its nearest listed ancestor, exactly as
        // the `is` chain it replaces did, and the answer is cached per concrete type.
        private static readonly Dictionary<System.Type, ContactRow> ContactRows = new Dictionary<System.Type, ContactRow>
        {
            [typeof(SandSpiderAI)] = new ContactRow
            {
                Gate = (e, s) => !((SandSpiderAI)e).onWall && !Read<bool>(e, "spoolingPlayerBody") && Read<float>(e, "timeSinceHittingPlayer") > 1f,
                Damage = 90, Cooldown = 1f, Hit = Event.SpiderHit,
                Origin = e => ((SandSpiderAI)e).meshContainer != null ? ((SandSpiderAI)e).meshContainer.position : e.transform.position,
            },
            [typeof(ButlerEnemyAI)] = new ContactRow
            {
                Gate = (e, s) => e.currentBehaviourStateIndex == 2 && Read<float>(e, "timeSinceChangingItem") >= 1.7f
                    && Read<float>(e, "timeSinceHittingPlayer") >= .25f,
                Damage = 10, Cooldown = .25f, Hit = Event.ButlerHit,
            },
            [typeof(ClaySurgeonAI)] = new ContactRow
            {
                Gate = (e, s) => Read<float>(e, "timeSinceSnip") >= 1f,
                Damage = 1000, Cooldown = 1f, Hit = Event.BarberHit,
            },
            // #1096 (WP5): a popped Jester kills outright - vanilla's own kill on a player is instant
            // and unsurvivable - and nothing at all happens before it has popped, which is also
            // vanilla (OnCollideWithPlayer only kills from behaviour state 2). No capture record
            // and no pose: there is no NPC equivalent of killPlayerAnimation's dead-body attach.
            [typeof(JesterAI)] = new ContactRow
            {
                Gate = (e, s) => e.currentBehaviourStateIndex == BlacksiteMonsterCapabilities.JesterPoppedState && !Read<bool>(e, "inKillAnimation"),
                Damage = 1000, Cooldown = 1f, Hit = Event.JesterHit,
            },
            // #1293 R5 (D3): vanilla's own gate on the native fields (CoilMayStrike) - it strikes only
            // while its own stop/go verdict lets it move - and the table's LethalOnContactWhenMoving makes
            // the touch a kill. No NextHit hold: the native hitPlayerTimer written back here is its one
            // contact clock.
            [typeof(SpringManAI)] = new ContactRow
            {
                Gate = (e, s) => CoilMayStrike((SpringManAI)e, s),
                Clock = e => { Write(e, "hitPlayerTimer", .2f); Write(e, "timeSinceHittingPlayer", Time.realtimeSinceStartup); },
            },
            // #1293 R5 (the deleted crawler adapter): vanilla's bite gate - its chase state and its own
            // 0.65 s timeSinceHittingPlayer - landing on the touch; the adapter's 0.12 s windup is gone.
            // 50 raw is the adapter's force 2 (vanilla bites a player for 40) and 0.77 s its re-arm.
            [typeof(CrawlerAI)] = new ContactRow
            {
                Gate = (e, s) => e.currentBehaviourStateIndex == 1 && !e.inSpecialAnimation && Read<float>(e, "timeSinceHittingPlayer") >= .65f,
                Damage = 50, Cooldown = .77f, Reach = 2.4f, Hit = Event.CrawlerHit,
            },
            // 2026-09-16 playtest (WP6, #1097): v81 CadaverBloomAI.OnCollideWithPlayer is a PLAIN
            // collision hit - 30, CauseOfDeath.Mauling, on the private timeSinceHittingPlayer the
            // vanilla collision uses. Sharing that field dedupes a soldier bite against the HOST's
            // own player only (vanilla writes it on the bitten peer and nothing replicates it), so a
            // remote player and a soldier may each take one bite inside the same 0.65 s: one extra
            // native hit, no runaway clock and no desync. The bite audio is vanilla's own
            // SyncBitePlayerRpc rather than a new Event value.
            [typeof(CadaverBloomAI)] = new ContactRow
            {
                Gate = (e, s) => Time.realtimeSinceStartup - Read<float>(e, "timeSinceHittingPlayer") >= .65f,
                Damage = 30, Cooldown = .65f,
                Clock = e =>
                {
                    var bloom = (CadaverBloomAI)e;
                    Write(bloom, "timeSinceHittingPlayer", Time.realtimeSinceStartup);
                    Play(bloom.burstSource, bloom.bitePlayer);
                    if (bloom.NetworkObject != null && bloom.NetworkObject.IsSpawned) bloom.SyncBitePlayerRpc();
                },
            },
            [typeof(ButlerBeesEnemyAI)] = new ContactRow
            {
                Gate = (e, s) => Time.realtimeSinceStartup - Read<float>(e, "timeAtSpawning") >= 1.7f
                    && Time.realtimeSinceStartup - Read<float>(e, "timeAtLastHurtingPlayer") >= .5f,
                Damage = 10, Cooldown = .5f,
                Clock = e => Write(e, "timeAtLastHurtingPlayer", Time.realtimeSinceStartup),
            },
            [typeof(FlowermanAI)] = new ContactRow
            {
                Gate = (e, s) => !((FlowermanAI)e).inKillAnimation && !((FlowermanAI)e).carryingPlayerBody,
                Act = (e, s) => BeginCapture(e, s, Event.BrackenGrab),
            },
            [typeof(ForestGiantAI)] = new ContactRow
            {
                Reach = 5f,
                Gate = (e, s) => e.currentBehaviourStateIndex != 2 && !Read<bool>(e, "inEatingPlayerAnimation"),
                Act = (e, s) =>
                {
                    if (e.currentBehaviourStateIndex == 0) Acquire(e, s);
                    else BeginCapture(e, s, Event.GiantGrab);
                },
            },
            [typeof(MouthDogAI)] = new ContactRow
            {
                Gate = (e, s) => !Read<bool>(e, "inKillAnimation"),
                Act = (e, s) =>
                {
                    var dog = (MouthDogAI)e;
                    if (dog.currentBehaviourStateIndex == 3) BeginCapture(e, s, Event.DogGrab);
                    else if (dog.currentBehaviourStateIndex == 2 && !dog.inLunge)
                    {
                        Vector3 forward = Vector3.ProjectOnPlane(s.transform.position - dog.transform.position, Vector3.up);
                        if (forward.sqrMagnitude > .01f) dog.transform.rotation = Quaternion.LookRotation(forward);
                        dog.inLunge = true;
                        AccessTools.Method(typeof(MouthDogAI), "EnterLunge").Invoke(dog, null);
                    }
                    else if (dog.currentBehaviourStateIndex < 2) dog.DetectNoise(s.transform.position, 1f, 0, 0);
                },
            },
            [typeof(CentipedeAI)] = new ContactRow
            {
                Gate = (e, s) => e.currentBehaviourStateIndex == 2 && !((CentipedeAI)e).clingingToCeiling
                    && !((CentipedeAI)e).inSpecialAnimation && ((CentipedeAI)e).clingingToPlayer == null,
                Act = (e, s) => BeginCapture(e, s, Event.FleaCling),
            },
        };
        private static readonly Dictionary<System.Type, ContactRow> ContactRowCache = new Dictionary<System.Type, ContactRow>();

        private static ContactRow ContactRowFor(EnemyAI enemy)
        {
            if (enemy == null) return null;
            var type = enemy.GetType();
            if (ContactRowCache.TryGetValue(type, out var row)) return row;
            for (var probe = type; probe != null && probe != typeof(EnemyAI); probe = probe.BaseType)
                if (ContactRows.TryGetValue(probe, out row)) break;
            ContactRowCache[type] = row;
            return row;
        }

        /// <summary>
        /// #1219 F1.2: the contact rule for a type with no row - <see cref="DefaultContactDamage"/>
        /// raw every <see cref="DefaultContactCooldown"/> seconds, through the soldier's own
        /// <c>ApplyVanillaMonsterDamage</c>, behind the same shared gates every row has. It replaces
        /// the registry's "no simulated damage fallback", under which such a monster could chase a
        /// soldier forever and never hurt him.
        ///
        /// 25 raw is exactly one point of soldier health through ApplyVanillaMonsterDamage's
        /// 25-raw-to-1-force conversion - the smallest value that lands on EVERY touch, where
        /// vanilla's lightest bite (20: the Baboon Hawk's and the Spore Lizard's, v81 decompiled)
        /// would land on every second one - and one second is vanilla's commonest contact clock,
        /// so a full-health soldier (<see cref="BlacksiteCombatPolicy.SoldierHealth"/>) survives
        /// many touches from an unknown monster. Vanilla v81 exposes no enemy's player damage as a
        /// field - every <c>DamagePlayer</c> call takes a literal - so the native value is read only
        /// from a type that declares one (<see cref="NativeContactDamage"/>), which is how a modded
        /// enemy gets its own number.
        ///
        /// Left out: a hazard (its own contact path follows), a type whose own native attack is the
        /// damage (its capability row's attack window), a type whose class the weapon rules exclude, and a type that
        /// overrides <c>OnCollideWithEnemy</c>. The override owns native enemy contact (v81: the
        /// Bush Wolf's HitEnemy(1) and the Earth Leviathan's kill), so a default on top would hit
        /// twice. The contract's witness is excluded by its own collision override, not by the
        /// class-level weapon exclusion.
        /// </summary>
        internal const int DefaultContactDamage = 25;
        internal const float DefaultContactCooldown = 1f;
        internal const float DefaultContactReach = 3f;
        private static readonly HashSet<System.Type> GenericContactLogged = new HashSet<System.Type>();

        /// <summary>#1219 F1.2: the generic contact rule applies to this monster (see <see cref="GenericContact"/>):
        /// no contact row names it, its capability row's attack window is a plain contact, and its type does not
        /// own native enemy collision.</summary>
        internal static bool UsesGenericContact(EnemyAI enemy)
            => enemy != null && ContactRowFor(enemy) == null
               && BlacksiteMonsterCapabilities.MotorFor(enemy).AttackWindow == BlacksiteAttackWindow.Contact
               && !OwnsEnemyCollision(enemy.GetType());

        private static readonly Dictionary<System.Type, bool> NativeEnemyCollision = new Dictionary<System.Type, bool>();
        private static bool OwnsEnemyCollision(System.Type type)
        {
            if (!NativeEnemyCollision.TryGetValue(type, out bool owns))
            {
                var method = AccessTools.Method(type, nameof(EnemyAI.OnCollideWithEnemy), new[] { typeof(Collider), typeof(EnemyAI) });
                NativeEnemyCollision[type] = owns = method != null && method.DeclaringType != typeof(EnemyAI);
            }
            return owns;
        }

        private static bool GenericContact(EnemyAI enemy, BlacksiteSoldierAI soldier)
        {
            if (!UsesGenericContact(enemy) || !ContactGatesOpen(enemy, soldier, DefaultContactReach, enemy.transform.position)) return false;
            int damage = NativeContactDamage(enemy, out bool native);
            NextHit[enemy] = Time.time + DefaultContactCooldown;
            if (GenericContactLogged.Add(enemy.GetType()))
                BlacksiteDiagnostics.Log("Monster", $"contact enemy={enemy.GetType().Name}, member={soldier.MemberIndex}, damage={damage}, "
                    + $"cooldown={DefaultContactCooldown:0.00}, source={(native ? "native-field" : "default")}, fallback=default");
            soldier.ApplyVanillaMonsterDamage(enemy, damage);
            return false;
        }

        // The field names a contact-damage value is declared under, in the order they are tried.
        private static readonly string[] NativeDamageFields = { "playerDamage", "damageToPlayer", "attackDamage", "hitDamage", "damage" };
        private static readonly Dictionary<System.Type, AccessTools.FieldRef<object, int>> NativeDamageReader
            = new Dictionary<System.Type, AccessTools.FieldRef<object, int>>();

        private static int NativeContactDamage(EnemyAI enemy, out bool native)
        {
            var type = enemy.GetType();
            if (!NativeDamageReader.TryGetValue(type, out var readDamage))
            {
                foreach (var name in NativeDamageFields)
                {
                    var candidate = AccessTools.Field(type, name);
                    if (candidate != null && !candidate.IsStatic && candidate.FieldType == typeof(int))
                    {
                        readDamage = AccessTools.FieldRefAccess<int>(candidate.DeclaringType, candidate.Name);
                        break;
                    }
                }
                NativeDamageReader[type] = readDamage;
            }
            int value = readDamage != null ? readDamage(enemy) : 0;
            native = value > 0;
            return native ? value : DefaultContactDamage;
        }
        private static void BeginCapture(EnemyAI enemy, BlacksiteSoldierAI soldier, Event kind)
        {
            if (Captures.ContainsKey(enemy) || VictimCaptured(soldier)) return;
            // Snapshot the victim before broadcasting; repeated capsule callbacks cannot redirect it.
            NextHit[enemy] = Time.time + 1f;
            if (!Pursuits.ContainsKey(enemy)) Acquire(enemy, soldier);
            Publish(enemy, soldier, kind);
            // #1065 WP4: DogGrab used to set Killed and apply the 1000 raw HERE, on the contact
            // frame. That is why all six 2026-09-15 dog kills logged Captured and Dead at the same
            // timestamp: the capture existed for zero frames in which anything could act on it. The
            // dog now takes the same path as every other capture - TickCaptures holds it for
            // BlacksiteCombatPolicy.CaptureKillGraceSeconds, during which a baton stun, a killing
            // burst or the victim leaving the world all release the member alive - and its 5.01 s
            // end-to-end window, the native EndLungeKill animation, is unchanged.
            BlacksiteDiagnostics.Log("Monster", $"capture={kind}, member={soldier.MemberIndex}, enemy={enemy.GetType().Name}, grace={BlacksiteCombatPolicy.CaptureKillGraceSecondsFor(kind == Event.GiantGrab)}.");
        }
        private static void TickCaptures()
        {
            Scratch.Clear(); Scratch.AddRange(Captures.Keys);
            foreach (var enemy in Scratch)
            {
                if (!Captures.TryGetValue(enemy, out var capture)) continue;
                var soldier = capture.Soldier;
                if (!Active(enemy) || soldier == null || !soldier.isActiveAndEnabled
                    || !SameWorld(enemy, soldier) || enemy.stunNormalizedTimer > 0f
                    || (!capture.Killed && !Living(soldier))) { EndCapture(enemy, true); continue; }
                float elapsed = Time.time - capture.Started;
                if (capture.Kind == Event.FleaCling)
                {
                    if (soldier.isOutside) { EndCapture(enemy, true); enemy.KillEnemyOnOwnerClient(); continue; }
                    if (Time.time >= capture.NextDamage)
                    {
                        capture.NextDamage = Time.time + 2f;
                        soldier.ApplyVanillaMonsterDamage(enemy, 10);
                        if (!Living(soldier)) EndCapture(enemy, true);
                    }
                    continue;
                }
                // #1065 WP4: same two values, read from BlacksiteCombatPolicy so the one branch that
                // remains - a Forest Keeper eats on its own animation clock - is the only branch,
                // and so the tier-one suite can state that DogGrab is not one.
                float killAt = BlacksiteCombatPolicy.CaptureKillGraceSecondsFor(capture.Kind == Event.GiantGrab);
                if (!capture.Killed && elapsed >= killAt)
                {
                    capture.Killed = true;
                    soldier.ApplyVanillaMonsterDamage(enemy, BlacksiteCombatPolicy.CaptureKillRawDamage);
                    if (capture.Kind == Event.GiantGrab) Publish(enemy, soldier, Event.Consumed);
                }
                // #1262 P4: a killed victim is released at endAt, hidden if consumed; the kill no longer ends the capture a tick early.
                float endAt = capture.Kind == Event.GiantGrab ? 7.7f : capture.Kind == Event.DogGrab ? 5.01f : 1f;
                if (elapsed >= endAt) EndCapture(enemy, true);
            }
        }
        internal static void EndCapture(EnemyAI enemy, bool broadcast)
        {
            if (ReferenceEquals(enemy, null) || !Captures.TryGetValue(enemy, out var capture)) return;
            if (broadcast && enemy != null && capture.Soldier != null) Publish(enemy, capture.Soldier, Event.EndCapture);
            else ClearCapture(enemy, capture);
            if (Authority()) Release(enemy);
        }
        private static void ClearCapture(EnemyAI enemy, Capture capture)
        {
            Captures.Remove(enemy);
            // #1065 WP4: the captor's re-arm cooldown, keyed on the captor and therefore shared by
            // the whole squad - one member's capture is what buys the next member the time. It is
            // the same NextHit clock Contact() already consults before any branch runs, so it also
            // holds the dog's lunge and the spider's bite, which is the point: the 2026-09-15 run
            // lost six members in 21.5 s to a cadence that was travel time and a 1 s re-arm.
            // Written on every client because ClearCapture is the one funnel both the broadcast and
            // the direct path reach; only the host ever reads it back, in the authority-gated
            // Contact().
            if (enemy != null) NextHit[enemy] = Time.time + BlacksiteCombatPolicy.MonsterCaptureRearmSeconds;
            BlacksiteDiagnostics.Log("Monster", $"release capture={capture.Kind}, killed={capture.Killed}, member={capture.Soldier?.MemberIndex}, rearm={BlacksiteCombatPolicy.MonsterCaptureRearmSeconds}.");
            // #874 Plan F: the committed rescuer is released with the record it lives on, so this
            // line is printed once per rescue whatever ended the capture. Host-only: a client's
            // record never has a rescuer.
            if (!ReferenceEquals(capture.Rescuer, null) && BlacksiteDiagnostics.Enabled)
                BlacksiteDiagnostics.Log("Rescue", $"release victim={capture.Soldier?.MemberIndex}, rescuer={capture.Rescuer.MemberIndex}, "
                    + $"cause={RescueReleaseCause(enemy, capture)}, capture={capture.Kind}, held={Time.time - capture.RescuerSince:0.00}s.");
            capture.Soldier?.EndMonsterCapture();
            if (enemy == null || HasNativePlayerCapture(enemy)) return;
            enemy.inSpecialAnimation = false;
            if (enemy is FlowermanAI bracken)
            {
                bracken.inKillAnimation = false;
                bracken.creatureAnimator?.SetBool("killing", false);
                bracken.creatureAnimator?.SetBool("carryingBody", false);
                bracken.crackNeckAudio?.Stop();
            }
            else if (enemy is ForestGiantAI giant)
            {
                Write(giant, "inEatingPlayerAnimation", false);
                giant.creatureVoice?.Stop();
                // #874 S13: the trigger our own capture set. Resetting it only matters while it is
                // still queued - a release inside the same frame, or an animator that was disabled
                // when the capture opened - and it is a no-op once the controller has consumed it,
                // which is what RestoreCapturePose below is for.
                giant.creatureAnimator?.ResetTrigger("EatPlayer");
            }
            else if (enemy is MouthDogAI dog)
            {
                Write(dog, "inKillAnimation", false);
                // Capture skips state 3 Update, including EndLungeClientRpc's reset. Leaving
                // inLunge set prevents later sounds from redirecting the old destination.
                Write(dog, "inLunge", false);
                Write(dog, "lungeCooldown", .25f);
                dog.creatureVoice?.Stop(); dog.endingLunge = true; dog.suspicionLevel = 2;
                if (!dog.isEnemyDead) dog.SwitchToBehaviourStateOnLocalClient(2);
                dog.creatureAnimator?.ResetTrigger("EndLungeKill");
            }
            else if (enemy is CentipedeAI flea)
            {
                flea.creatureAnimator?.SetBool("clingingToPlayer", false);
                flea.creatureSFX?.Stop();
                if (flea.modelContainer != null) flea.modelContainer.localPosition = Vector3.zero;
                if (Physics.Raycast(flea.transform.position, Vector3.down, out var floor, 5f, WorldMask, QueryTriggerInteraction.Ignore))
                    flea.transform.position = floor.point;
                if (!flea.isEnemyDead && NavMesh.SamplePosition(flea.transform.position, out var nav, 1.5f, NavMesh.AllAreas)
                    && Mathf.Abs(nav.position.y - flea.transform.position.y) < 1.5f)
                {
                    flea.agent.enabled = true; flea.agent.Warp(nav.position);
                }
                if (!flea.isEnemyDead) flea.SwitchToBehaviourStateOnLocalClient(0);
            }
            RestoreCapturePose(enemy, capture);
        }
        // #874 S13: cancelling an interrupted capture POSE, which no field restore can do.
        //
        // Evidence. Decompiled ForestGiantAI (v81 Assembly-CSharp, Mono.Cecil): the eat pose is one
        // animator write, creatureAnimator.SetTrigger("EatPlayer") in
        // <EatPlayerAnimation>d__78.MoveNext IL_003e, and the type's ENTIRE animator surface is that
        // trigger plus SetBool("staring"), SetBool("stunned") and the VelocityX/VelocityY floats
        // CalculateAnimationDirection writes. There is no cancel parameter to set and vanilla never
        // resets the trigger: its own escape branch (IL_027b, the player left the animation) stops
        // creatureVoice and writes inEatingPlayerAnimation = false, nothing more, because a vanilla
        // escape is rare and the authored clip is allowed to run itself out. A soldier capture is
        // interrupted CONSTANTLY - every baton stun rescue is one; run 1 of 2026-09-20 produced
        // three in fifteen seconds (t=170.21, 178.85, 184.65, all killed=False) - so the clip
        // outliving the capture is this mod's problem to solve, not vanilla's. MouthDogAI's
        // EndLungeKill trigger has the same shape and is covered by the same restore.
        //
        // The cancel invents nothing: no guessed state name, no authored parameter and no new
        // replication. ApplyEvent records the layer-0 state the captor's OWN animator was in on the
        // frame before the capture pose was triggered - locomotion, by construction, because
        // Contact() refuses a stunned, dead, already-capturing or already-eating captor - and this
        // fades back to exactly that state. It runs on every peer because ApplyEvent and
        // ClearCapture are the one funnel the host path and SyncVanillaMonsterClientRpc both reach,
        // and each peer restores the state ITS own animator recorded.
        //
        // Narrow on purpose. A completed kill (capture.Killed) keeps its consumption presentation,
        // a dead captor keeps its death animation, a captor that has already opened another capture
        // keeps the new pose, and an animator that cannot be read is left alone. The recorded state
        // lives in the capture record, so it is released with the capture on rescue, death,
        // disconnect, despawn and round reset, and nothing outlives it.
        private const float CapturePoseBlendSeconds = .15f;
        private static int CapturePoseState(EnemyAI enemy)
        {
            var animator = enemy.creatureAnimator;
            return animator == null || !animator.isActiveAndEnabled || !animator.isInitialized
                || animator.runtimeAnimatorController == null || animator.layerCount == 0
                ? 0
                : animator.GetCurrentAnimatorStateInfo(0).fullPathHash;
        }
        private static void RestoreCapturePose(EnemyAI enemy, Capture capture)
        {
            if (capture.Killed || capture.PoseRestoreState == 0 || enemy.isEnemyDead
                || Captures.ContainsKey(enemy)) return;
            var animator = enemy.creatureAnimator;
            if (animator == null || !animator.isActiveAndEnabled || !animator.isInitialized
                || animator.runtimeAnimatorController == null || animator.layerCount == 0) return;
            animator.CrossFadeInFixedTime(capture.PoseRestoreState, CapturePoseBlendSeconds, 0);
            BlacksiteDiagnostics.Log("Monster",
                $"capture pose cancelled kind={capture.Kind}, enemy={enemy.GetType().Name}, state={capture.PoseRestoreState}.");
        }
        internal static void ApplyEvent(EnemyAI enemy, BlacksiteSoldierAI soldier, Event action)
        {
            if (enemy == null || soldier == null) return;
            if (action == Event.Acquire)
            {
                if (!Pursuits.TryGetValue(enemy, out var state))
                {
                    Pursuits[enemy] = state = new Pursuit { Soldier = soldier, SeenAt = Time.time };
                    BeginLease(enemy);
                }
                // #1293 R5: the authority's latest member is the lease's, so a handoff (NotifySoldierUnavailable)
                // re-points every peer without a release in between; the host's own entry already names him.
                state.Soldier = soldier;
                state.Expires = Time.time + 3f;
                enemy.movingTowardsTargetPlayer = false;
                return;
            }
            if (action == Event.Release)
            {
                if (Pursuits.TryGetValue(enemy, out var state) && state.Soldier == soldier) Pursuits.Remove(enemy);
                // #1293 R5: a Nutcracker no longer leased lowers its gun on every peer, as its adapter's release did.
                if (enemy is NutcrackerEnemyAI gunner && !Pursuits.ContainsKey(enemy)) LowerGun(gunner);
                return;
            }
            if (action == Event.EndCapture)
            {
                if (Captures.TryGetValue(enemy, out var capture) && capture.Soldier == soldier) ClearCapture(enemy, capture);
                return;
            }
            if (action == Event.Consumed)
            {
                soldier.HideConsumedSoldier();
                if (enemy is ForestGiantAI giant && giant.bloodOnFaceDecal != null) giant.bloodOnFaceDecal.enabled = true;
                return;
            }
            if (action == Event.SpiderHit && enemy is SandSpiderAI spider)
            {
                Write(spider, "timeSinceHittingPlayer", 0f); spider.overrideAnimation = .8f;
                spider.creatureAnimator?.SetTrigger("attack"); Play(spider.creatureSFX, spider.attackSFX); return;
            }
            if (action == Event.ButlerHit && enemy is ButlerEnemyAI butler)
            {
                Write(butler, "timeSinceHittingPlayer", 0f); Write(butler, "timeSinceStealthStab", Time.realtimeSinceStartup);
                butler.creatureAnimator?.SetTrigger("Stab"); butler.stabBloodParticle?.Play();
                if (butler.enemyType != null && butler.enemyType.audioClips.Length > 0) Play(butler.creatureSFX, butler.enemyType.audioClips[0]);
                return;
            }
            if (action == Event.BarberHit && enemy is ClaySurgeonAI barber)
            {
                if (Authority()) { if (barber.agent != null) barber.agent.speed = 0f; Write(barber, "beatTimer", Read<float>(barber, "beatTimer") + 2f); }
                Write(barber, "snareIntervalTimer", Mathf.Min(Read<float>(barber, "snareIntervalTimer") + 2f, 4f));
                Write(barber, "timeSinceSnip", 0f); barber.creatureAnimator?.SetTrigger("snip"); Play(barber.creatureSFX, barber.snipScissors); return;
            }
            if (action == Event.JesterHit && enemy is JesterAI jester)
            {
                // The native clip on the native source: killPlayerAnimation itself does
                // creatureSFX.PlayOneShot(killPlayerSFX). No animator trigger - vanilla's
                // "KillPlayer" trigger drives the dead-body grab, which has no NPC counterpart.
                Play(jester.creatureSFX, jester.killPlayerSFX); return;
            }
            if (action == Event.CrawlerHit && enemy is CrawlerAI crawler)
            {
                // The native bite's own presentation (HitPlayerClientRpc) and clocks: the bite clock and the recoil.
                Write(crawler, "timeSinceHittingPlayer", 0f); Write(crawler, "agentSpeedWithNegative", -.5f);
                crawler.creatureAnimator?.SetTrigger("HitPlayer"); Play(crawler.creatureVoice, crawler.bitePlayerSFX); return;
            }
            if (Captures.ContainsKey(enemy) || VictimCaptured(soldier)) return;
            // #874 S13: the pose the captor is in BEFORE anything below triggers a capture clip,
            // read from this peer's own animator. RestoreCapturePose fades back to it when the
            // capture is interrupted; see the derivation above it.
            Captures[enemy] = new Capture { Soldier = soldier, Kind = action, Started = Time.time,
                NextDamage = Time.time + 2f, PoseRestoreState = CapturePoseState(enemy) };
            soldier.BeginMonsterCapture();
            enemy.movingTowardsTargetPlayer = false;
            if (enemy.agent != null && enemy.agent.enabled && enemy.agent.isOnNavMesh) enemy.agent.speed = 0f;
            enemy.inSpecialAnimation = true;
            if (action == Event.BrackenGrab && enemy is FlowermanAI flower)
            {
                flower.inKillAnimation = true;
                flower.creatureAnimator?.SetBool("killing", true);
                Play(flower.crackNeckAudio, flower.crackNeckSFX);
            }
            else if (action == Event.GiantGrab && enemy is ForestGiantAI giantGrab)
            {
                Write(giantGrab, "inEatingPlayerAnimation", true); Write(giantGrab, "lookingAtTarget", false);
                giantGrab.creatureAnimator?.SetTrigger("EatPlayer");
            }
            else if (action == Event.DogGrab && enemy is MouthDogAI dog)
            {
                Write(dog, "inKillAnimation", true);
                dog.creatureAnimator?.SetTrigger("EndLungeKill"); Play(dog.creatureVoice, dog.killPlayerSFX);
            }
            else if (action == Event.FleaCling && enemy is CentipedeAI flea)
            {
                var coroutine = Read<Coroutine>(flea, "ceilingAnimationCoroutine");
                if (coroutine != null) flea.StopCoroutine(coroutine);
                Write<Coroutine>(flea, "ceilingAnimationCoroutine", null);
                Write(flea, "startedCeilingAnimationCoroutine", false);
                flea.clingingToCeiling = false; flea.agent.enabled = false;
                flea.creatureAnimator?.SetBool("clingingToPlayer", true);
                if (flea.creatureSFX != null) { flea.creatureSFX.clip = flea.clingToPlayer3D; flea.creatureSFX.Play(); }
                flea.SwitchToBehaviourStateOnLocalClient(3);
            }
        }
        private static void Play(AudioSource source, AudioClip clip)
        {
            if (source == null || clip == null) return;
            source.PlayOneShot(clip); WalkieTalkie.TransmitOneShotAudio(source, clip);
        }
        /// <summary>
        /// #1011 A3: whether this capture parks the CAPTOR on the victim rather than the victim on
        /// the captor. Derived from the capture kind, not from the monster type: it is exactly the
        /// branch <see cref="PoseCapture"/> answers by moving <c>enemy.transform</c> onto the
        /// victim's <c>GetThreatLookTransform()</c>, against the Giant/Dog/Bracken branches which
        /// move the SOLDIER onto a socket on the monster and leave the monster's own body where a
        /// rescuer can already see it.
        /// </summary>
        private static bool RidesVictim(Event kind) => kind == Event.FleaCling;
        /// <summary>
        /// #1011 A3: the point a captor is riding, for a captor that rides one. This is where a
        /// rescue swing has to arrive, and it is derived from the capture record - the victim it
        /// names and the kind it was opened with - so nothing downstream has to ask what species
        /// the captor is. False for a captor that is not riding anybody, whose own body is already
        /// the right thing to aim at.
        /// </summary>
        internal static bool TryGetCaptureAnchor(EnemyAI captor, out Vector3 anchor)
        {
            anchor = Vector3.zero;
            if (ReferenceEquals(captor, null) || !Captures.TryGetValue(captor, out var capture)) return false;
            if (!RidesVictim(capture.Kind) || capture.Soldier == null) return false;
            Transform head = capture.Soldier.GetThreatLookTransform();
            if (head == null) return false;
            anchor = head.position;
            return true;
        }
        internal static void PoseCapture(BlacksiteSoldierAI soldier)
        {
            EnemyAI release = null;
            foreach (var pair in Captures)
            {
                var enemy = pair.Key; var capture = pair.Value;
                if (capture.Soldier != soldier) continue;
                // #1262 P4: a killed victim is released by TickCaptures at endAt, never by its own death here.
                if (!Active(enemy) || enemy.stunNormalizedTimer > 0f
                    || !SameWorld(enemy, soldier) || Time.time - capture.Started > (capture.Kind == Event.FleaCling ? 300f : 10f))
                { release = enemy; break; }
                if (enemy is FlowermanAI && Time.time - capture.Started > .1f)
                { enemy.creatureAnimator?.SetBool("killing", false); enemy.creatureAnimator?.SetBool("carryingBody", true); }
                if (enemy is CentipedeAI flea)
                {
                    Transform head = soldier.GetThreatLookTransform();
                    flea.transform.SetPositionAndRotation(head.position, soldier.transform.rotation);
                    if (flea.modelContainer != null) flea.modelContainer.localPosition = Vector3.zero;
                }
                else if (enemy is ForestGiantAI giant && giant.holdPlayerPoint != null)
                    soldier.Navigator.PoseMonsterCapture(giant.holdPlayerPoint.position, giant.holdPlayerPoint.rotation, false);
                else if (enemy is MouthDogAI dog && dog.mouthGrip != null)
                    soldier.Navigator.PoseMonsterCapture(dog.mouthGrip.position, dog.mouthGrip.rotation, true);
                else if (enemy is FlowermanAI)
                    soldier.Navigator.PoseMonsterCapture(enemy.transform.position + enemy.transform.forward * .65f, enemy.transform.rotation, false);
                break;
            }
            if (!ReferenceEquals(release, null)) EndCapture(release, Authority());
        }
        internal static void WebContact(SandSpiderWebTrap web, Collider collider)
        {
            if (!Authority() || web == null || web.mainScript == null || Read<bool>(web, "webHasBeenBroken")) return;
            var soldier = collider.GetComponentInParent<BlacksiteSoldierAI>();
            if (!Living(soldier) || !SameWorld(web.mainScript, soldier)) return;
            soldier.ApplySpiderWebSlow();
            // The slow above still applies to a dead spider's webs; only the acquisition below is
            // gated on the spider being alive. OnTriggerStay runs every frame, and OwnsStrategy
            // releases a pursuit owned by a dead spider on the same frame it is taken, so an
            // ungated Acquire here re-published Event.Acquire every frame (8,980 acquires / 6 min
            // on 2026-09-16). The debounce lives here rather than in Acquire because Acquire is
            // also the ForceAggro, giant and contact path, none of which may be delayed.
            var spider = web.mainScript;
            if (!Active(spider) || Pursuits.ContainsKey(spider)) return;
            if (WebAcquire.TryGetValue(spider, out float next) && Time.time < next) return;
            WebAcquire[spider] = Time.time + WebReacquireSeconds;
            if (BetterPlayer(spider, soldier) == null) Acquire(spider, soldier);
        }

        // #1293 R5 (the deleted slime contact class): the slime hurts along its own native deforming
        // rays - a root-distance aura cannot describe this enemy - sampled from
        // BlobAI.RaycastCollisionWithPlayers (BlacksiteSlimeRayPatch), and on a body collision
        // (BlacksiteSoldierAI.OnCollideWithEnemy). Once per slime and member per 0.25 s.
        private static readonly BlacksiteEventCooldown SlimeHits = new BlacksiteEventCooldown();
        private static readonly Collider[] SlimeTouches = new Collider[128];

        // The slime's host gate, its own method so the native fixtures can replace the roster read.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool SlimeAuthority() => Authority() && MoonEscalationState.HasLivingBlacksiteSoldiers;

        internal static void SlimeRay(BlobAI blob, Ray ray, float boneDistance)
        {
            if (!SlimeAuthority() || !BlacksiteHazardFootprint.Dangerous(blob)) return;
            float length = Mathf.Max(0f, boneDistance - 1.55f);
            // Same wall/player mask and radius as BlobAI.RaycastCollisionWithPlayers. #874 S7.4: the
            // 0.7 m radius is BlacksiteHazardPolicy.RayRadius, so the warning band and this damage
            // envelope cannot drift apart.
            if (Physics.SphereCast(ray, BlacksiteHazardPolicy.RayRadius, out var obstruction, length, 2312, QueryTriggerInteraction.Ignore))
                length = obstruction.distance;
            int count = Physics.OverlapCapsuleNonAlloc(ray.origin, ray.GetPoint(length), BlacksiteHazardPolicy.RayRadius, SlimeTouches, ~0, QueryTriggerInteraction.Collide);
            for (int i = 0; i < count; i++)
            {
                var collider = SlimeTouches[i]; SlimeTouches[i] = null;
                SlimeContact(blob, collider != null ? collider.GetComponentInParent<BlacksiteSoldierAI>() : null);
            }
        }

        internal static void SlimeContact(BlobAI blob, BlacksiteSoldierAI soldier)
        {
            if (!SlimeAuthority() || !BlacksiteHazardFootprint.Dangerous(blob) || soldier == null || !soldier.isActiveAndEnabled
                || soldier.isEnemyDead || soldier.ReplicatedDead || soldier.HasBoardedShip || !SameWorld(blob, soldier)) return;
            if (!SlimeHits.TryAccept(blob.GetInstanceID(), soldier.GetInstanceID(), Time.time, .25f)) return;
            // The member's slime entry observes it and deals its 35 raw per touch through ApplyVanillaMonsterDamage.
            soldier.ApplySlimeContactServer(blob);
        }
    }
}
