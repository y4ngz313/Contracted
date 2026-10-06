using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    [HarmonyPatch]
    internal static class BlacksiteVanillaStrategyPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // #1096 (WP5): JesterAI takes the DoAIInterval gate and NOT the Update gate, the way the
            // Butler, the Barber and the Coil-Head do. The interval is where vanilla's state-2 arm
            // re-targets a player and calls SetMovingTowardsTargetPlayer, which would overwrite the
            // soldier destination TickPursuit sets every 0.2 s; JesterAI.Update is where the pop
            // presentation, the head rig and the agent-speed ramp live, all of which must keep
            // running. base.DoAIInterval() is the first statement of JesterAI.DoAIInterval (v81), so
            // the gate lands after it and the agent still receives the destination.
            // #1097: CadaverBloomAI joins the interval gate and, like the Butler, the Barber and the
            // Coil-Head, NOT the Update gate. Its interval is what has to stop: state 0 switches
            // straight back to state 1, CheckForVeryClosePlayer re-targets a player up to 50 m away
            // whether or not that player is closer than the leased soldier, and the state-1 branch
            // restarts searchForPlayers - each of which overwrites the lease's destination. Its
            // Update is what has to keep running: it is the only place the leg animation
            // (CalculateAnimationDirection), the hit-stun pose, the breathing/scan-node timers and
            // state 0's agent.speed are driven, and in state 0 - where Acquire parks it - Update
            // writes no destination at all, so nothing there needs gating.
            // #1293 R5: the leased classes are the capability table's LeasedMotor rows - the Thumper and
            // the Nutcracker among them since their registry adapters folded into the lease.
            foreach (var row in BlacksiteMonsterCapabilities.Rows)
            {
                if (!row.LeasedMotor) continue;
                var type = row.Type;
                yield return AccessTools.Method(type, "DoAIInterval");
                if (type != typeof(ButlerEnemyAI) && type != typeof(ClaySurgeonAI) && type != typeof(SpringManAI) && type != typeof(JesterAI) && type != typeof(CadaverBloomAI)) yield return AccessTools.Method(type, "Update");
            }
        }
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod)
        {
            var code = new List<CodeInstruction>(instructions);
            var type = __originalMethod.DeclaringType;
            bool update = __originalMethod.Name == "Update";
            // #1293 R5: the Thumper's and the Nutcracker's first STORED state read is their switch (their
            // earlier reads feed a fear check and the animator), so both gates sit after the native stun,
            // clock and animator housekeeping, exactly where their deleted adapters' gates sat.
            bool switchBoundary = type == typeof(SandSpiderAI) || type == typeof(ForestGiantAI) || type == typeof(CrawlerAI) || type == typeof(NutcrackerEnemyAI);
            bool captureOnly = type == typeof(MouthDogAI) || (type == typeof(CentipedeAI) && update);
            var gate = AccessTools.Method(typeof(BlacksiteVanillaMonsters), captureOnly ? nameof(BlacksiteVanillaMonsters.IsCapturing) : nameof(BlacksiteVanillaMonsters.OwnsStrategy));
            var baseMethod = AccessTools.Method(typeof(EnemyAI), __originalMethod.Name);
            var state = AccessTools.Field(typeof(EnemyAI), nameof(EnemyAI.currentBehaviourStateIndex));
            int boundary = -1;
            bool explicitBase = type == typeof(FlowermanAI) && !update;
            if (explicitBase) boundary = 0; // Native Bracken interval calls base at several exits.
            else for (int i = 0; i < code.Count; i++)
            {
                if (switchBoundary && i + 2 < code.Count && code[i].opcode == OpCodes.Ldarg_0 && code[i + 1].LoadsField(state)
                    && (code[i + 2].IsStloc() || code[i + 2].opcode == OpCodes.Switch)) { boundary = i; break; }
                if (!switchBoundary && code[i].Calls(baseMethod)) { boundary = i + 1; break; }
            }
            if (boundary < 0 || boundary >= code.Count) throw new InvalidOperationException($"Blacksite native strategy boundary missing: {type.Name}.{__originalMethod.Name}");
            var normal = generator.DefineLabel();
            for (int i = 0; i < code.Count; i++)
            {
                if (i == boundary)
                {
                    var load = new CodeInstruction(OpCodes.Ldarg_0);
                    load.labels.AddRange(code[i].labels); code[i].labels.Clear();
                    yield return load;
                    yield return new CodeInstruction(OpCodes.Call, gate);
                    yield return new CodeInstruction(OpCodes.Brfalse, normal);
                    if (explicitBase) { yield return new CodeInstruction(OpCodes.Ldarg_0); yield return new CodeInstruction(OpCodes.Call, baseMethod); }
                    yield return new CodeInstruction(OpCodes.Ret);
                    code[i].labels.Add(normal);
                }
                yield return code[i];
            }
        }
    }
    [HarmonyPatch]
    internal static class BlacksiteVanillaUpdatePatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(SandSpiderAI), typeof(ForestGiantAI), typeof(FlowermanAI), typeof(ButlerEnemyAI), typeof(CentipedeAI), typeof(SpringManAI), typeof(NutcrackerEnemyAI) })
                yield return AccessTools.Method(type, "Update");
        }
        private static void Postfix(EnemyAI __instance) => BlacksiteVanillaMonsters.AfterUpdate(__instance);
    }
    [HarmonyPatch(typeof(SpringManAI), nameof(SpringManAI.Update))]
    internal static class BlacksiteCoilObservationPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var code = new List<CodeInstruction>(instructions);
            var stopping = AccessTools.Field(typeof(SpringManAI), "stoppingMovement");
            var combine = AccessTools.Method(typeof(BlacksiteVanillaMonsters), nameof(BlacksiteVanillaMonsters.AddSoldierObservers));
            bool patched = false;
            for (int i = 0; i < code.Count; i++)
            {
                // The observation result is compared with stoppingMovement, then stored back
                // after the RPC. Update that SAME local so native stop/go hysteresis is retained.
                if (!patched && i + 3 < code.Count && code[i].IsLdloc() && code[i + 1].opcode == OpCodes.Ldarg_0
                    && code[i + 2].LoadsField(stopping) && (code[i + 3].opcode.FlowControl == FlowControl.Cond_Branch))
                {
                    patched = true;
                    var load = new CodeInstruction(code[i]); load.labels.Clear();
                    load.labels.AddRange(code[i].labels); code[i].labels.Clear();
                    yield return load;
                    yield return new CodeInstruction(OpCodes.Ldarg_0);
                    yield return new CodeInstruction(OpCodes.Call, combine);
                    yield return StoreLocal(code[i]);
                }
                yield return code[i];
            }
            if (!patched) throw new InvalidOperationException("Blacksite Coil-Head observer boundary missing.");
        }
        private static CodeInstruction StoreLocal(CodeInstruction load)
        {
            if (load.opcode == OpCodes.Ldloc_0) return new CodeInstruction(OpCodes.Stloc_0);
            if (load.opcode == OpCodes.Ldloc_1) return new CodeInstruction(OpCodes.Stloc_1);
            if (load.opcode == OpCodes.Ldloc_2) return new CodeInstruction(OpCodes.Stloc_2);
            if (load.opcode == OpCodes.Ldloc_3) return new CodeInstruction(OpCodes.Stloc_3);
            return new CodeInstruction(load.opcode == OpCodes.Ldloc_S ? OpCodes.Stloc_S : OpCodes.Stloc, load.operand);
        }
    }
    [HarmonyPatch(typeof(ClaySurgeonAI), "DanceBeat")]
    internal static class BlacksiteBarberBeatPatch
    {
        private static void Postfix(ClaySurgeonAI __instance) => BlacksiteVanillaMonsters.BarberBeat(__instance);
    }
    // #1096 (WP5) step 6: the crank a soldier starts. A postfix rather than an arm inside the
    // strategy transpiler because it must run in behaviour state 0, where nothing owns the strategy
    // and the gate above has already returned. JesterInterval carries the whole rule.
    [HarmonyPatch(typeof(JesterAI), nameof(JesterAI.DoAIInterval))]
    internal static class BlacksiteJesterCrankPatch
    {
        private static void Postfix(JesterAI __instance) => BlacksiteVanillaMonsters.JesterInterval(__instance);
    }
    [HarmonyPatch]
    internal static class BlacksiteCapturedPlayerCollisionPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            // #1293 R5: the classes that take a soldier into their hands - the table's Attaches rows.
            foreach (var row in BlacksiteMonsterCapabilities.Rows)
                if (row.Attaches) yield return AccessTools.Method(row.Type, "OnCollideWithPlayer");
        }
        private static bool Prefix(EnemyAI __instance) => !BlacksiteVanillaMonsters.IsCapturing(__instance);
    }
    [HarmonyPatch]
    internal static class BlacksiteNativePlayerCapturePatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(ForestGiantAI), "GrabPlayerServerRpc");
            yield return AccessTools.Method(typeof(CentipedeAI), "ClingToPlayerServerRpc");
        }
        // A genuine player request may already be in flight when the host notices an NPC
        // collision. These two native RPCs have no busy-victim guard: release the NPC first.
        // Dog and Bracken already reject additional player victims in their native RPCs.
        private static void Prefix(EnemyAI __instance)
        {
            if (BlacksiteVanillaMonsters.Authority() && BlacksiteVanillaMonsters.IsCapturing(__instance))
                BlacksiteVanillaMonsters.EndCapture(__instance, true);
        }
    }
    [HarmonyPatch(typeof(MouthDogAI), nameof(MouthDogAI.OnCollideWithEnemy))]
    internal static class BlacksiteDogCollisionPatch
    {
        private static bool Prefix(MouthDogAI __instance, Collider other, EnemyAI collidedEnemy)
        {
            var soldier = collidedEnemy as BlacksiteSoldierAI ?? other?.GetComponentInParent<BlacksiteSoldierAI>();
            if (soldier == null) return true;
            BlacksiteVanillaMonsters.Contact(__instance, soldier);
            return false; // Native generic force=2 would duplicate the lunge/capture damage.
        }
    }
    [HarmonyPatch(typeof(CentipedeAI), nameof(CentipedeAI.ReactBehaviourToBeingHurt))]
    internal static class BlacksiteFleaRescuePatch
    {
        private static bool Prefix(CentipedeAI __instance)
        {
            if (!BlacksiteVanillaMonsters.IsCapturing(__instance)) return true;
            BlacksiteVanillaMonsters.EndCapture(__instance, BlacksiteVanillaMonsters.Authority());
            return false;
        }
    }
    [HarmonyPatch(typeof(CentipedeAI), "LateUpdate")]
    internal static class BlacksiteFleaPosePatch
    {
        private static bool Prefix(CentipedeAI __instance) => !BlacksiteVanillaMonsters.IsCapturing(__instance);
    }
    [HarmonyPatch(typeof(SandSpiderWebTrap), "OnTriggerStay")]
    internal static class BlacksiteSpiderWebPatch
    {
        private static void Postfix(SandSpiderWebTrap __instance, Collider other) => BlacksiteVanillaMonsters.WebContact(__instance, other);
    }
    [HarmonyPatch]
    internal static class BlacksiteVanillaHitClockPatches
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(SandSpiderAI), "HitPlayerClientRpc");
            yield return AccessTools.Method(typeof(ButlerEnemyAI), "StabPlayerClientRpc");
            // #1293 R5: a Thumper's native player bite also spends the clock its soldier contact row reads.
            yield return AccessTools.Method(typeof(CrawlerAI), "HitPlayerClientRpc");
        }
        private static void Postfix(EnemyAI __instance) => BlacksiteVanillaMonsters.Write(__instance, "timeSinceHittingPlayer", 0f);
    }
    // #1293 R5: the slime's own deforming rays are its contact (BlacksiteVanillaMonsters.SlimeRay).
    [HarmonyPatch(typeof(BlobAI), "RaycastCollisionWithPlayers")]
    internal static class BlacksiteSlimeRayPatch
    {
        private static void Prefix(BlobAI __instance, float maxDistance, Ray ___slimeRay)
            => BlacksiteVanillaMonsters.SlimeRay(__instance, ___slimeRay, maxDistance);
    }
}
