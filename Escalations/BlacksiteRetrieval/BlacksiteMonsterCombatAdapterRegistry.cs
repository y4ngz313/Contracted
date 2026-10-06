using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations._Shared;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1293 R5: the passive-acquire ledger of the native IVisibleThreat consumers - the capability
    /// table's NativeVisibleThreat rows (the Baboon Hawk, the Old Bird, a hostile Giant Sapsucker). Their
    /// own scans choose a soldier; this ledger names the member each is associated with (Hunts, the Old
    /// Bird stomp) and lets go of a fallen one. Every monster MOTOR is the lease's (BlacksiteVanillaMonsters).
    /// </summary>
    internal static class BlacksiteMonsterCombatAdapterRegistry
    {
        private const float PassiveAcquireRange = 20f;
        private const float ForcedAcquireRange = 36f;
        private const float CloseAcquireRange = 6f;

        private static readonly Dictionary<EnemyAI, TargetState> Targets = new Dictionary<EnemyAI, TargetState>();
        private static readonly List<EnemyAI> EnemyScratch = new List<EnemyAI>();
        private static readonly List<BlacksiteSoldierAI> SoldierScratch = new List<BlacksiteSoldierAI>();
        private static readonly BlacksiteCombatLifetime Lifetime = new BlacksiteCombatLifetime();

        internal static void Reset()
        {
            ReleaseAllTargets("runtime reset");
            // #947 S12 U1: the one-line-per-soldier-death-per-bird gate, cleared with the round.
            BlacksiteKiwiThreatPatches.Reset();
            // #948 S12 U3: the per-bird hostile latch and the egg scan are round state too.
            BlacksiteKiwiThreatState.Reset();
            // #1083 (WP I): so is the give-up clock. It is keyed by NetworkObjectId, which a new
            // round re-issues, so an unclear memo would be both a leak and a lie about a body that
            // no longer exists. Cleared here, beside the other per-round latch, because this is the
            // reset BlacksiteMonsterTargeting.Reset() already drives from every round transition.
            BlacksiteThreatAdapterRegistry.ResetFutileDamage();
            // #874 N8: and the Old Bird warning budget, keyed the same way for the same reason.
            BlacksiteThreatAdapterRegistry.ResetWarningFire();
            // #1086: and the per-Jester popped latch (the capability table's), which is the same kind
            // of round state and is cleared by the same reset so a new round cannot inherit a flight.
            BlacksiteMonsterCapabilities.Reset();
            Lifetime.Clear();
            EnemyScratch.Clear();
            SoldierScratch.Clear();
            BlacksiteMonsterCapabilities.LogTableOnce();
        }

        /// <summary>
        /// #1233 S6 (rule 5): the "it is hunting a squadmate" half of the engagement gate, from
        /// this registry's own target ledger - a native visible threat is associated with a living
        /// member. Host state, like the ledger.
        /// </summary>
        internal static bool Hunts(EnemyAI enemy)
            => !ReferenceEquals(enemy, null) && Targets.TryGetValue(enemy, out TargetState state) && CanUseSoldier(state.Soldier);

        // The ledger's one table read: a native IVisibleThreat consumer the squad fights (a cold sapsucker
        // is AvoidUnlessHostile and walked around, so it never enters the ledger).
        private static bool PassiveAcquires(EnemyAI enemy)
            => BlacksiteMonsterCapabilities.GetSupportLevel(enemy) == BlacksiteMonsterSupportLevel.NativeVisibleThreat;

        internal static void ForceAggro(EnemyAI enemy, BlacksiteSoldierAI soldier, float seconds)
        {
            if (!IsServerAuthority()
                || !CanUseEnemy(enemy)
                || !PassiveAcquires(enemy)
                || !CanUseSoldier(soldier)
                || !CanShareCombatSpace(enemy, soldier)
                || HasBetterPlayerTarget(enemy, Vector3.Distance(enemy.transform.position, soldier.transform.position)))
            {
                return;
            }

            if (!Targets.TryGetValue(enemy, out TargetState state))
            {
                state = new TargetState();
                Targets.Add(enemy, state);
            }
            state.Soldier = soldier;
            state.ForcedUntil = Mathf.Max(state.ForcedUntil, Time.time + Mathf.Max(0.5f, seconds));
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.LogRateLimited(
                    $"adapter.force.{enemy.GetInstanceID()}.{soldier.MemberIndex}",
                    2f,
                    "MonsterAdapter",
                    $"forced acquire enemy={DescribeEnemy(enemy)}, soldier={soldier.MemberIndex}, duration={seconds:0.0}s.");
            }
        }

        internal static void SignalSoldierThreat(BlacksiteSoldierAI soldier, float radius, float seconds)
        {
            if (!IsServerAuthority() || !CanUseSoldier(soldier))
                return;

            List<EnemyAI> enemies = RoundManager.Instance?.SpawnedEnemies;
            if (enemies == null)
                return;

            float range = Mathf.Max(CloseAcquireRange, radius);
            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI enemy = enemies[i];
                if (!CanUseEnemy(enemy)
                    || Vector3.Distance(enemy.transform.position, soldier.transform.position) > range)
                {
                    continue;
                }
                // #1293 R5: gunfire draws exactly what it drew before the fold - the passive ledger's
                // types and the leased squad hunters (Thumper, Nutcracker), each on its own guard.
                if (!BlacksiteMonsterCapabilities.For(enemy).LeasedMotor) ForceAggro(enemy, soldier, seconds);
                else if (BlacksiteMonsterCapabilities.For(enemy).SquadHunter) BlacksiteVanillaMonsters.ForceAggro(enemy, soldier);
            }
        }

        // Per frame: only the lifetime, so the ledger is reset the frame the Blacksite and its last
        // soldier are gone. The ledger's own work runs in the squad's perception pass.
        internal static void UpdateRuntime()
        {
            if (Lifetime.Advance(IsServerAuthority(), MoonEscalationState.IsBlacksiteActive, MoonEscalationState.HasLivingBlacksiteSoldiers)
                == BlacksiteCombatLifetimeAction.Reset)
                Reset();
        }

        /// <summary>#1293 R3: the ledger's pass, once per squad perception pass (host only).</summary>
        internal static void OnPerceptionPass(float now)
        {
            if (!IsServerAuthority())
                return;

            // WP-A5: measured at 3.7-4.9 ms on the landed frame, so a pass that breaches the budget
            // names itself in one Warning, as the Seraphim summon does (#705).
            long activeStart = System.Diagnostics.Stopwatch.GetTimestamp();
            UpdateActiveTargets(now);
            double activeMilliseconds = ElapsedMilliseconds(activeStart);
            long acquireStart = System.Diagnostics.Stopwatch.GetTimestamp();
            AcquirePassiveTargets();
            double acquireMilliseconds = ElapsedMilliseconds(acquireStart);

            if (activeMilliseconds + acquireMilliseconds >= SlowTargetingPassMilliseconds)
            {
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Timing] Slow BlacksiteMonsterCombatAdapterRegistry.OnPerceptionPass " +
                    $"{activeMilliseconds + acquireMilliseconds:0.00}ms: UpdateActiveTargets={activeMilliseconds:0.00}ms " +
                    $"AcquirePassiveTargets={acquireMilliseconds:0.00}ms.");
            }
        }

        /// <summary>One frame at 60fps is a third of this; 3ms is the point where this state is worth
        /// naming in the log rather than the point where it is a problem.</summary>
        private const double SlowTargetingPassMilliseconds = 3.0;

        private static double ElapsedMilliseconds(long startTicks)
        {
            return (System.Diagnostics.Stopwatch.GetTimestamp() - startTicks) * 1000.0
                   / System.Diagnostics.Stopwatch.Frequency;
        }

        // The Old Bird stomp's reach on a soldier, shared by the damage and the warning latch.
        private const float RadMechStompReach = 4.75f;

        internal static void NotifyRadMechStomp(RadMechAI mech)
        {
            if (!IsServerAuthority() || !CanUseEnemy(mech))
                return;

            // #874 N8 (decision 7): a stomp is an ATTACK on every squad with a member inside its
            // reach, whoever the damage below picks and whether or not it lands (cooldown, a blocked
            // line, a victim association elsewhere). Latched before the damage, so a member the
            // stomp kills has already closed its squad's warning fire.
            CollectSoldiers();
            BlacksiteSoldierAI nearest = null;
            float nearestDistance = RadMechStompReach;
            for (int i = 0; i < SoldierScratch.Count; i++)
            {
                BlacksiteSoldierAI inReach = SoldierScratch[i];
                if (!CanUseSoldier(inReach) || !CanShareCombatSpace(mech, inReach)) continue;
                float distance = Vector3.Distance(mech.transform.position, inReach.transform.position);
                if (distance > RadMechStompReach) continue;
                BlacksiteThreatAdapterRegistry.NoteRefusedContactAttack(mech, inReach);
                if (distance <= nearestDistance) { nearest = inReach; nearestDistance = distance; }
            }

            // The associated soldier when it is under the foot, else the nearest one that is. Gunfire
            // (SignalSoldierThreat, and #874 N8's warning rounds through ForceAggro) associates the
            // Old Bird with a SHOOTER that can stand 16 m away; the stomp used to pick that shooter,
            // find it out of reach and damage nobody while a member stood under the foot.
            BlacksiteSoldierAI soldier = Targets.TryGetValue(mech, out TargetState associated)
                && CanUseSoldier(associated.Soldier)
                && Vector3.Distance(mech.transform.position, associated.Soldier.transform.position) <= RadMechStompReach
                ? associated.Soldier : nearest;
            if (soldier == null || !PassiveAcquires(mech))
                return;
            if (!Targets.TryGetValue(mech, out TargetState state))
                Targets.Add(mech, state = new TargetState { Soldier = soldier });
            if (Time.time < state.NextAttackAllowedAt || !HasLineOfEffect(mech, soldier))
                return;

            state.NextAttackAllowedAt = Time.time + 0.7f;
            // #1293 R5: the one monster damage path. The stomp took force 2; 2 x 25 raw is exactly force 2
            // through ApplyVanillaMonsterDamage's 25-raw-to-1-force conversion, whatever remainder it holds.
            soldier.ApplyVanillaMonsterDamage(mech, RadMechStompForce * 25);
        }

        private const int RadMechStompForce = 2;

        internal static void NotifySoldierUnavailable(BlacksiteSoldierAI soldier)
        {
            if (!IsServerAuthority()) return;
            // Snapshot: changing a target can synchronously run native callbacks.
            var enemies = new List<EnemyAI>(Targets.Keys);
            foreach (var enemy in enemies)
                if (Targets.TryGetValue(enemy, out var state) && state.Soldier == soldier)
                {
                    // #947 S12 U1: the native release runs in the death frame and names the member.
                    BlacksiteKiwiThreatPatches.ReleaseDeadThreat(enemy, soldier);
                    ReleaseTarget(enemy, "soldier unavailable");
                }
        }

        private static void UpdateActiveTargets(float now)
        {
            if (Targets.Count == 0)
                return;

            EnemyScratch.Clear();
            foreach (EnemyAI enemy in Targets.Keys)
                EnemyScratch.Add(enemy);

            for (int i = 0; i < EnemyScratch.Count; i++)
            {
                EnemyAI enemy = EnemyScratch[i];
                if (!Targets.TryGetValue(enemy, out TargetState state))
                    continue;

                if (!CanUseEnemy(enemy)
                    || !CanUseSoldier(state.Soldier)
                    || !CanShareCombatSpace(enemy, state.Soldier))
                {
                    ReleaseTarget(enemy, "enemy/soldier invalid or combat spaces separated");
                    continue;
                }

                bool forced = now <= state.ForcedUntil;
                float distance = Vector3.Distance(enemy.transform.position, state.Soldier.transform.position);
                float range = forced ? ForcedAcquireRange : PassiveAcquireRange;
                if (distance > range
                    || HasBetterPlayerTarget(enemy, distance)
                    || (!forced && distance > CloseAcquireRange && !HasLineOfEffect(enemy, state.Soldier)))
                    ReleaseTarget(enemy, "range, line-of-effect, or native player priority");
            }
        }

        private static void AcquirePassiveTargets()
        {
            CollectSoldiers();
            if (SoldierScratch.Count == 0)
            {
                ReleaseAllTargets("no living Blacksite soldiers");
                return;
            }

            List<EnemyAI> enemies = RoundManager.Instance?.SpawnedEnemies;
            if (enemies == null)
                return;

            for (int i = 0; i < enemies.Count; i++)
            {
                EnemyAI enemy = enemies[i];
                if (!CanUseEnemy(enemy) || Targets.ContainsKey(enemy))
                    continue;

                if (!PassiveAcquires(enemy))
                    continue;

                BlacksiteSoldierAI soldier = FindBestSoldier(enemy, out float distance);
                if (soldier == null || HasBetterPlayerTarget(enemy, distance))
                    continue;

                Targets.Add(enemy, new TargetState { Soldier = soldier });
                // #874 S7.5: the acquire is the soldier's own first warning about a monster it
                // cannot see yet. Outside the diagnostics branch on purpose: this is behaviour, not
                // a log line. No range or selection above is touched.
                soldier.NoteAdapterAcquireServer(enemy, distance);
                if (BlacksiteDiagnostics.Enabled)
                {
                    BlacksiteDiagnostics.Log(
                        "MonsterAdapter",
                        $"passive acquire enemy={DescribeEnemy(enemy)}, soldier={soldier.MemberIndex}, distance={distance:0.0}m.");
                    // #874 S4.0b: an adapter acquire is how this member learned about the monster.
                    BlacksitePerceptionDiagnostics.NoteContact(
                        soldier.MemberIndex, soldier.NetworkObjectId, BlacksiteDiagnosticFormat.Target(enemy),
                        BlacksitePerceptionDiagnostics.SourceAdapter, distance, exact: false);
                }
            }
        }

        private static BlacksiteSoldierAI FindBestSoldier(EnemyAI enemy, out float bestDistance)
        {
            bestDistance = float.MaxValue;
            BlacksiteSoldierAI best = null;
            for (int i = 0; i < SoldierScratch.Count; i++)
            {
                BlacksiteSoldierAI soldier = SoldierScratch[i];
                if (!CanUseSoldier(soldier) || !CanShareCombatSpace(enemy, soldier))
                    continue;
                float distance = Vector3.Distance(enemy.transform.position, soldier.transform.position);
                if (distance > PassiveAcquireRange || distance >= bestDistance)
                    continue;
                if (distance > CloseAcquireRange && !HasLineOfEffect(enemy, soldier))
                    continue;
                best = soldier;
                bestDistance = distance;
            }
            return best;
        }

        private static void ReleaseTarget(EnemyAI enemy, string reason)
        {
            if (ReferenceEquals(enemy, null) || !Targets.TryGetValue(enemy, out TargetState state))
                return;
            // #947 S12 U1: the native sapsucker lets go of a dead member; a no-op for every other type.
            if (enemy != null)
                BlacksiteKiwiThreatPatches.ReleaseDeadThreat(enemy, null);
            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "MonsterAdapter",
                    $"release enemy={DescribeEnemy(enemy)}, soldier={state.Soldier?.MemberIndex.ToString() ?? "<none>"}, reason={reason}.");
            }
            Targets.Remove(enemy);
        }

        private static void ReleaseAllTargets(string reason)
        {
            if (Targets.Count == 0)
                return;
            EnemyScratch.Clear();
            foreach (EnemyAI enemy in Targets.Keys)
                EnemyScratch.Add(enemy);
            for (int i = 0; i < EnemyScratch.Count; i++)
                ReleaseTarget(EnemyScratch[i], reason);
            Targets.Clear();
        }

        private static string DescribeEnemy(EnemyAI enemy)
        {
            return enemy == null
                ? "<null>"
                : $"{enemy.GetType().Name}/{enemy.GetInstanceID()}";
        }

        private static bool HasBetterPlayerTarget(EnemyAI enemy, float soldierDistance)
        {
            PlayerControllerB player = enemy.targetPlayer;
            if (player == null || !player.isPlayerControlled || player.isPlayerDead)
                return false;
            float playerDistance = Vector3.Distance(enemy.transform.position, player.transform.position);
            return playerDistance <= Mathf.Max(CloseAcquireRange, soldierDistance * 0.85f);
        }

        private static bool HasLineOfEffect(EnemyAI enemy, BlacksiteSoldierAI soldier)
        {
            return HasUnobstructedRay(
                enemy.transform.position + Vector3.up * 1.1f,
                soldier.GetThreatLookTransform().position,
                soldier);
        }

        private static bool HasUnobstructedRay(Vector3 origin, Vector3 target, BlacksiteSoldierAI expected)
        {
            Vector3 delta = target - origin;
            if (delta.sqrMagnitude <= 0.01f)
                return true;
            int mask = StartOfRound.Instance != null
                ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
                : ~0;
            if (!Physics.Raycast(origin, delta.normalized, out RaycastHit hit, delta.magnitude, mask, QueryTriggerInteraction.Ignore))
                return true;
            return hit.collider != null && hit.collider.GetComponentInParent<BlacksiteSoldierAI>() == expected;
        }

        private static bool CanUseEnemy(EnemyAI enemy)
        {
            return enemy != null
                && !enemy.isEnemyDead && enemy.isActiveAndEnabled
                && !(enemy is BlacksiteSoldierAI)
                && enemy.gameObject != null
                && enemy.gameObject.activeInHierarchy;
        }

        private static bool CanUseSoldier(BlacksiteSoldierAI soldier)
        {
            return soldier != null
                && soldier.isActiveAndEnabled && !soldier.isEnemyDead && !soldier.ReplicatedDead && !soldier.HasBoardedShip
                && soldier.gameObject != null
                && soldier.gameObject.activeInHierarchy;
        }

        private static bool CanShareCombatSpace(EnemyAI enemy, BlacksiteSoldierAI soldier)
        {
            // #1219 B1: the world term is BlacksiteWorld's; the close-acquire range below is this
            // gate's own perception term, unchanged.
            bool sameWorld = BlacksiteWorld.Same(enemy, soldier);
            if (sameWorld)
                return true;
            Vector3 delta = enemy.transform.position - soldier.transform.position;
            delta.y = 0f;
            return delta.magnitude <= CloseAcquireRange;
        }

        private static void CollectSoldiers()
        {
            SoldierScratch.Clear();
            MoonEscalationState.CollectActiveBlacksiteSoldiers(SoldierScratch);
        }

        private static bool IsServerAuthority()
        {
            return RoundManager.Instance != null && RoundManager.Instance.IsServer;
        }

        private sealed class TargetState
        {
            internal BlacksiteSoldierAI Soldier;
            internal float ForcedUntil;
            internal float NextAttackAllowedAt;
        }
    }

    [HarmonyPatch(typeof(RadMechAI))]
    internal static class BlacksiteRadMechAttackEventPatches
    {
        [HarmonyPatch(nameof(RadMechAI.StompLeftFoot))]
        [HarmonyPostfix]
        private static void AfterLeftStomp(RadMechAI __instance)
        {
            BlacksiteMonsterCombatAdapterRegistry.NotifyRadMechStomp(__instance);
        }

        [HarmonyPatch(nameof(RadMechAI.StompRightFoot))]
        [HarmonyPostfix]
        private static void AfterRightStomp(RadMechAI __instance)
        {
            BlacksiteMonsterCombatAdapterRegistry.NotifyRadMechStomp(__instance);
        }

        [HarmonyPatch(nameof(RadMechAI.StompBothFeet))]
        [HarmonyPostfix]
        private static void AfterBothFeetStomp(RadMechAI __instance)
        {
            BlacksiteMonsterCombatAdapterRegistry.NotifyRadMechStomp(__instance);
        }
    }

}
