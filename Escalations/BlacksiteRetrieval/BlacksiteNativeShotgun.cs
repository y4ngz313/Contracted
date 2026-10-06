using System;
using HarmonyLib;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteNativeShotgun
    {
        internal sealed class Shot { internal long Id; internal NutcrackerEnemyAI Enemy; }
        internal static Shot Current;
        private static long _sequence;
        internal static Shot Begin(NutcrackerEnemyAI enemy) => new Shot { Id = ++_sequence, Enemy = enemy };
        internal static void ApplyDedicated(NutcrackerEnemyAI enemy)
        {
            if (!BlacksiteShotgunTrace.DedicatedFallback(enemy.IsServer, enemy.IsClient) || enemy.gun == null) return;
            var before = Current; Current = Begin(enemy);
            try
            {
                var muzzle = enemy.gun.shotgunRayPoint;
                BlacksiteShotgunTrace.Visit(muzzle.position, muzzle.forward, StartOfRound.Instance.collidersAndRoomMaskAndDefault,
                    c => { var soldier = c.GetComponentInParent<BlacksiteSoldierAI>(); return soldier != null && !soldier.isEnemyDead ? soldier.GetInstanceID() : 0; },
                    (c, force) => c.GetComponentInParent<BlacksiteSoldierAI>().HitEnemy(force, null, true));
            }
            finally { Current = before; }
        }
    }
    // Observe the actual native damage call, never add a second hit in an RPC postfix.
    [HarmonyPatch(typeof(ShotgunItem), nameof(ShotgunItem.ShootGun))]
    internal static class BlacksiteNativeShotgunPatch
    {
        private static readonly AccessTools.FieldRef<ShotgunItem, EnemyAI> Holder = AccessTools.FieldRefAccess<ShotgunItem, EnemyAI>("heldByEnemy");
        private static void Prefix(ShotgunItem __instance, out BlacksiteNativeShotgun.Shot __state)
        {
            __state = BlacksiteNativeShotgun.Current;
            BlacksiteNativeShotgun.Current = Holder(__instance) is NutcrackerEnemyAI enemy ? BlacksiteNativeShotgun.Begin(enemy) : null;
        }
        private static void Finalizer(BlacksiteNativeShotgun.Shot __state) => BlacksiteNativeShotgun.Current = __state;
    }
}
