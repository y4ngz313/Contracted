using System.Runtime.CompilerServices;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteBatonStun
    {
        private sealed class Recovery { internal float Until; }
        private static readonly ConditionalWeakTable<EnemyAI, Recovery> Victims = new ConditionalWeakTable<EnemyAI, Recovery>();
        // #988 B4. Both numbers are the baton's own cadence rather than chosen ones, and neither is
        // a damage, health or force change.
        //
        // StunSeconds 1.0: a crawler bite is armed every 0.77 s (its BlacksiteVanillaMonsters contact
        // row's NextHit hold) and a baton swing releases its actor at ReadyAt, 1.933 to 2.433 s.
        // At 0.6 s the stun skipped less than one bite, so a soldier that connected was still bitten
        // while re-cocking; 1.0 s skips one whole bite and covers roughly half a recovery cycle.
        //
        // RearmSeconds 2.5: one baton swing cycle. At 8 s the stun could apply at most once per
        // fight and did exactly that in the 2026-09-12 run (L11570, the only one). The connect is
        // meant to be the skill check, not the cooldown, so the re-arm is the fastest a soldier can
        // possibly land a second hit and no faster - a monster the squad keeps hitting keeps being
        // stunned, and one they miss recovers immediately. Vanilla's own postStunInvincibilityTimer
        // is still the anti-chain guard and is unchanged below.
        internal const float StunSeconds = 1f;
        internal const float RearmSeconds = 2.5f;
        /// <summary>
        /// #988 round 2: the verdict is RETURNED. Every caller used to fire the presentation - and,
        /// since #988 B4, a victim-side arc, light and cue - whether or not a stun was actually
        /// applied, so a Forest Giant (<c>canBeStunned</c> false) or any hit inside the 2.5 s re-arm
        /// advertised a stun that never happened. True means this call put the enemy into
        /// <c>SetEnemyStunned</c>; every early return is false.
        /// </summary>
        internal static bool TryApply(EnemyAI enemy, PlayerControllerB player)
        {
            // #1083 (WP I): BY CLASS, deliberately. This method is the one weapon-exclusion reader
            // that is NOT a host decision - the two postfixes below run it on whichever peer applied
            // or mirrored a PLAYER's baton hit - so it must ask a predicate that answers the same on
            // every machine. The give-up memo is host-only state, and folding it in here would let
            // the host refuse a stun a remote client still applies, leaving a 1 s stun alive on one
            // peer. The squad's own stun is unaffected: a soldier never swings at a given-up
            // instance, because SweepBatonSegment consults the memo-inclusive predicate first.
            if (enemy == null || enemy.isEnemyDead || enemy.enemyType == null || !enemy.enemyType.canBeStunned || BlacksiteThreatAdapterRegistry.WeaponsExcludedByClassFor(enemy)) return false;
            var recovery = Victims.GetOrCreateValue(enemy);
            if (Time.time < recovery.Until || enemy.stunNormalizedTimer > 0 || enemy.postStunInvincibilityTimer >= 0) return false;
            recovery.Until = Time.time + RearmSeconds; enemy.SetEnemyStunned(true, StunSeconds, player);
            return true;
        }
        private static bool HoldsBaton(PlayerControllerB player) => player?.currentlyHeldObjectServer?.itemProperties?.name == "Blacksite Stun Baton Item";
        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.HitEnemyOnLocalClient)), HarmonyPostfix]
        private static void LocalBatonHit(EnemyAI __instance, PlayerControllerB playerWhoHit, int force, int hitID)
        { if (force > 0 && hitID == 1 && HoldsBaton(playerWhoHit)) TryApply(__instance, playerWhoHit); }
        [HarmonyPatch(typeof(EnemyAI), nameof(EnemyAI.HitEnemyClientRpc)), HarmonyPostfix]
        private static void ReplicatedBatonHit(EnemyAI __instance, int playerWhoHit, int force, int hitID)
        {
            var players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || playerWhoHit < 0 || playerWhoHit >= players.Length || force <= 0 || hitID != 1) return;
            var player = players[playerWhoHit]; if (HoldsBaton(player)) TryApply(__instance, player);
        }
    }
}
