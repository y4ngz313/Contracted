using System;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #947 S12 U1: a Giant Sapsucker that kills a soldier keeps pecking the corpse for the rest of
    /// the round. The cause is entirely vanilla and entirely about a retained interface reference:
    ///
    /// - <c>GiantKiwiAI.cs:621</c> replaces <c>watchingThreat</c> only when the held threat is more
    ///   than 4 m farther from <c>protectingPosition</c> than the nearest living threat. A corpse
    ///   lying where the bird killed it is the nearest thing there is, so the test never passes.
    /// - <c>:1943-1946</c> reassigns <c>attackingThreat</c> only when it differs from
    ///   <c>watchingThreat</c>; both are the corpse, so it never fires.
    /// - <c>flag2 = attackingThreat.IsThreatDead()</c> at <c>:1903</c> is computed and used only for
    ///   lost-egg timing. It never releases the target.
    /// - <c>timeSinceSeeingThreat</c> is reset at <c>:1929-1935</c> because a living soldier IS
    ///   visible, so the shipped 4 s give-up at <c>:1970</c> never runs.
    /// - <c>BlacksiteSoldierAI.HandleDeadState</c> disables every collider, so the 40 m overlap can
    ///   never re-scan the corpse, but the reference the bird already holds is unaffected and
    ///   <c>GetThreatTransform()</c> keeps returning a valid position forever.
    ///
    /// The fix is the minimum that lets vanilla's own code do the work: null the held reference and
    /// prune the egg-thief list. With <c>watchingThreat == null</c> the <c>:621</c> branch adopts the
    /// nearest living threat on the very next scan and syncs it; with nothing living visible
    /// <c>CheckLOSForCreatures</c> returns false, <c>timeSinceSeeingThreat</c> accumulates and the
    /// shipped 4 s give-up runs for the first time. No vanilla decision, clock or RPC is replaced,
    /// and <c>SyncWatchingThreatServerRpc</c> is left to vanilla.
    ///
    /// One deliberate asymmetry, forced by the decompile: <c>attackingThreat</c> is NOT nulled while
    /// the bird is in behaviour state 2, because <c>GiantKiwiAI.cs:1903</c> dereferences it
    /// unconditionally at the top of that case, one line before its own null check. Nulling it there
    /// turns the latch into a NullReferenceException every AI interval. In state 2 the release nulls
    /// <c>watchingThreat</c>, which is exactly what <c>:621</c> and <c>:1943</c> need in order to
    /// re-adopt and reassign; if there is nothing living to adopt, <c>:1970</c> leaves state 2 within
    /// 4 s and the next release then clears <c>attackingThreat</c> as well.
    ///
    /// Prefix only, resolved with <see cref="AccessTools"/>, server authority only, no transpiler.
    /// A target that cannot be resolved logs once and disables itself rather than throwing.
    /// </summary>
    internal static class BlacksiteKiwiThreatPatches
    {
        // GiantKiwiAI.cs:3342. Layer 19 only (524288), the body layer the bird's own hit reaction
        // scans; 6 m is the shipped radius. Both are quoted, never re-derived.
        private const int HitReactionBodyMask = 524288;
        private const float HitReactionRadius = 6f;
        // GiantKiwiAI.cs:1887. The state whose first statements dereference attackingThreat.
        private const int AttackBehaviourState = 2;

        private static FieldInfo _watchingThreat;
        private static FieldInfo _attackingThreat;
        private static FieldInfo _seenThreatsHoldingEgg;
        private static FieldInfo _lastPlayerWhoAttacked;
        private static FieldInfo _timeSinceGettingHit;
        private static bool _resolved;
        private static bool _disabled;
        private static bool _applied;

        // One release line per soldier death per bird, not per AI interval. Instance ids are unique
        // for the lifetime of the object, so the pair is exact; Reset clears it with the round.
        private static readonly HashSet<long> ReleaseReported = new HashSet<long>();

        /// <summary>
        /// Applied manually rather than through <c>PatchAll</c> so a missing member disables this
        /// file instead of throwing out of plugin initialisation.
        /// </summary>
        internal static void Apply(Harmony harmony)
        {
            if (harmony == null || _applied)
                return;
            _applied = true;
            if (!Resolve())
                return;

            MethodInfo scan = AccessTools.Method(typeof(GiantKiwiAI), "CheckLOSForCreatures");
            MethodInfo interval = AccessTools.Method(typeof(GiantKiwiAI), "DoAIInterval");
            MethodInfo hit = AccessTools.Method(typeof(GiantKiwiAI), "HitEnemy");
            if (scan == null && interval == null)
            {
                Disable("neither GiantKiwiAI.CheckLOSForCreatures nor GiantKiwiAI.DoAIInterval could be resolved");
                return;
            }

            try
            {
                if (scan != null)
                    harmony.Patch(scan, prefix: new HarmonyMethod(typeof(BlacksiteKiwiThreatPatches), nameof(ReleasePrefix)));
                else
                    Warn("GiantKiwiAI.CheckLOSForCreatures was not found; the release runs on the AI interval only.");

                if (interval != null)
                    harmony.Patch(interval, prefix: new HarmonyMethod(typeof(BlacksiteKiwiThreatPatches), nameof(ReleasePrefix)));
                else
                    Warn("GiantKiwiAI.DoAIInterval was not found; the release runs on the threat scan only.");

                if (hit != null)
                    harmony.Patch(hit, prefix: new HarmonyMethod(typeof(BlacksiteKiwiThreatPatches), nameof(HitEnemyPrefix)));
                else
                    Warn("GiantKiwiAI.HitEnemy was not found; the empty-overlap index guard is not installed.");
            }
            catch (Exception e)
            {
                Disable($"patching failed: {e.Message}");
            }
        }

        /// <summary>Round teardown. Member indices and instance ids are reused by the next deployment.</summary>
        internal static void Reset()
        {
            ReleaseReported.Clear();
        }

        /// <summary>
        /// #947 S12 U1: the entry S12 U3's hostile latch calls when it cools. Vanilla never prunes
        /// <c>seenThreatsHoldingEgg</c> for a non-player threat, and membership makes
        /// <c>AttackIfThreatened</c> return true at any distance (<c>GiantKiwiAI.cs:1028</c>), so a
        /// soldier that once walked within 3 m of an egg is hunted for the rest of the round and
        /// avoidance is unachievable after a single mistake. Returns true when something was removed.
        /// </summary>
        internal static bool PruneLiveSoldier(EnemyAI enemy, Transform soldierTransform)
        {
            if (!Resolve() || !IsServerAuthority())
                return false;
            if (!(enemy is GiantKiwiAI kiwi) || kiwi == null || soldierTransform == null)
                return false;
            if (!(_seenThreatsHoldingEgg.GetValue(kiwi) is List<Transform> thieves))
                return false;

            bool removed = false;
            for (int i = thieves.Count - 1; i >= 0; i--)
            {
                if (thieves[i] != null && thieves[i] != soldierTransform)
                    continue;
                thieves.RemoveAt(i);
                removed = true;
            }
            return removed;
        }

        /// <summary>
        /// #947 S12 U1: the release itself. Called from the two prefixes and, in the death frame,
        /// from <c>NativeVisibleThreatAdapter.Reset</c> through
        /// <c>BlacksiteMonsterCombatAdapterRegistry.NotifySoldierUnavailable</c>, which is what
        /// <c>BlacksiteSoldierAI.KillEnemy</c> already calls. Returns the number of egg-thief entries
        /// pruned; safe and idempotent on every other enemy type.
        /// </summary>
        internal static int ReleaseDeadThreat(EnemyAI enemy, BlacksiteSoldierAI soldier)
        {
            if (!Resolve() || !IsServerAuthority())
                return 0;
            if (!(enemy is GiantKiwiAI kiwi) || kiwi == null)
                return 0;

            BlacksiteSoldierAI released = null;
            bool watchingCleared = false;
            bool attackingCleared = false;
            // GiantKiwiAI.cs:1903 dereferences attackingThreat before its own null check, so state 2
            // must keep a non-null reference. Vanilla's :1943 replaces it on the next scan that finds
            // a living threat, and :1970 leaves the state within 4 s when there is none.
            bool mayClearAttacking = kiwi.currentBehaviourStateIndex != AttackBehaviourState;

            if (_watchingThreat.GetValue(kiwi) is BlacksiteSoldierAI watching && IsGone(watching))
            {
                _watchingThreat.SetValue(kiwi, null);
                watchingCleared = true;
                released = watching;
            }

            if (_attackingThreat.GetValue(kiwi) is BlacksiteSoldierAI attacking && IsGone(attacking))
            {
                if (released == null)
                    released = attacking;
                if (mayClearAttacking)
                {
                    _attackingThreat.SetValue(kiwi, null);
                    attackingCleared = true;
                }
            }

            int pruned = PruneDeadEggThieves(kiwi);
            if (!watchingCleared && !attackingCleared && pruned == 0)
                return 0;

            BlacksiteSoldierAI subject = soldier != null ? soldier : released;
            long key = ((long)kiwi.GetInstanceID() << 32) ^ (uint)(subject != null ? subject.GetInstanceID() : 0);
            if (ReleaseReported.Add(key) && BlacksiteDiagnostics.Enabled)
            {
                string member = subject != null ? subject.MemberIndex.ToString() : "-1";
                BlacksiteDiagnostics.Log(
                    "KiwiThreat",
                    $"bird={kiwi.GetInstanceID()}, release, soldier={member}, reason=dead, watching={watchingCleared}, attacking={attackingCleared}, eggThiefPruned={pruned}");
            }
            return pruned;
        }

        /// <summary>
        /// The prefix both <c>CheckLOSForCreatures</c> and <c>DoAIInterval</c> carry. It reads and
        /// writes nothing else, so a vanilla body that runs straight after it sees exactly the state
        /// vanilla would have produced had the corpse simply stopped being a candidate.
        /// </summary>
        private static void ReleasePrefix(GiantKiwiAI __instance)
        {
            try
            {
                ReleaseDeadThreat(__instance, null);
            }
            catch (Exception e)
            {
                Disable($"release threw: {e.Message}");
            }
        }

        /// <summary>
        /// #947 S12 U1, the review's first flag. <c>GiantKiwiAI.HitEnemy:3342-3359</c> takes the
        /// nearest layer-19 collider within 6 m of the eye when <c>playerWhoHit == null</c> - which is
        /// exactly what a soldier rifle passes - and indexes <c>tempColliderResults[num3]</c> with
        /// <c>num3</c> still -1 when the overlap returns nothing. The bird's own collider normally
        /// saves it; a mod that strips colliders (ReXuvination does, on this very enemy) makes it
        /// reachable, and it throws AFTER the damage is applied. So the guard runs the same overlap
        /// first and, only when it is genuinely empty, applies the identical damage vanilla would
        /// have applied and skips the indexing. A non-empty overlap runs vanilla untouched.
        /// </summary>
        private static bool HitEnemyPrefix(GiantKiwiAI __instance, int force, PlayerControllerB playerWhoHit, bool playHitSFX)
        {
            if (_disabled || __instance == null || __instance.isEnemyDead || playerWhoHit != null)
                return true;
            RoundManager manager = RoundManager.Instance;
            if (manager == null || manager.tempColliderResults == null || __instance.eye == null)
                return true;

            int found;
            try
            {
                found = Physics.OverlapSphereNonAlloc(
                    __instance.eye.position,
                    HitReactionRadius,
                    manager.tempColliderResults,
                    HitReactionBodyMask,
                    QueryTriggerInteraction.Collide);
            }
            catch (Exception)
            {
                return true;
            }
            if (found > 0)
                return true;

            // EnemyAI.HitEnemy:2435-2455 is the hit SFX and two debug logs; this is its audible half.
            if (playHitSFX && __instance.enemyType != null && __instance.enemyType.hitBodySFX != null && __instance.creatureSFX != null)
            {
                __instance.creatureSFX.PlayOneShot(__instance.enemyType.hitBodySFX);
                WalkieTalkie.TransmitOneShotAudio(__instance.creatureSFX, __instance.enemyType.hitBodySFX);
                if (__instance.creatureVoice != null && __instance.enemyType.hitEnemyVoiceSFX != null)
                {
                    __instance.creatureVoice.PlayOneShot(__instance.enemyType.hitEnemyVoiceSFX);
                    WalkieTalkie.TransmitOneShotAudio(__instance.creatureVoice, __instance.enemyType.hitEnemyVoiceSFX);
                }
            }
            // GiantKiwiAI.cs:3325-3341, in its own order. Ownership gates the kill exactly as vanilla.
            __instance.enemyHP -= force;
            _timeSinceGettingHit?.SetValue(__instance, 0f);
            if (__instance.IsOwner && __instance.enemyHP <= 0)
                __instance.KillEnemyOnOwnerClient();
            _lastPlayerWhoAttacked?.SetValue(__instance, null);
            return false;
        }

        private static int PruneDeadEggThieves(GiantKiwiAI kiwi)
        {
            if (!(_seenThreatsHoldingEgg.GetValue(kiwi) is List<Transform> thieves))
                return 0;

            int removed = 0;
            for (int i = thieves.Count - 1; i >= 0; i--)
            {
                Transform thief = thieves[i];
                if (thief == null)
                {
                    thieves.RemoveAt(i);
                    removed++;
                    continue;
                }
                BlacksiteSoldierAI soldier = thief.GetComponent<BlacksiteSoldierAI>();
                if (soldier == null || !soldier.IsThreatDead())
                    continue;
                thieves.RemoveAt(i);
                removed++;
            }
            return removed;
        }

        /// <summary>A destroyed soldier is gone too, and cannot be asked whether it is dead.</summary>
        private static bool IsGone(BlacksiteSoldierAI soldier)
        {
            return soldier == null || soldier.IsThreatDead();
        }

        private static bool Resolve()
        {
            if (_resolved)
                return !_disabled;
            _resolved = true;

            _watchingThreat = AccessTools.Field(typeof(GiantKiwiAI), "watchingThreat");
            _attackingThreat = AccessTools.Field(typeof(GiantKiwiAI), "attackingThreat");
            _seenThreatsHoldingEgg = AccessTools.Field(typeof(GiantKiwiAI), "seenThreatsHoldingEgg");
            if (_watchingThreat == null || _attackingThreat == null || _seenThreatsHoldingEgg == null)
            {
                Disable("GiantKiwiAI.watchingThreat, attackingThreat or seenThreatsHoldingEgg could not be resolved");
                return false;
            }
            _lastPlayerWhoAttacked = AccessTools.Field(typeof(GiantKiwiAI), "lastPlayerWhoAttacked");
            _timeSinceGettingHit = AccessTools.Field(typeof(GiantKiwiAI), "timeSinceGettingHit");
            return true;
        }

        private static void Disable(string reason)
        {
            if (_disabled)
                return;
            _disabled = true;
            _resolved = true;
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.Blacksite] #947 S12 U1: the Giant Sapsucker dead-threat release is disabled for this session ({reason}).");
        }

        private static void Warn(string message)
        {
            ContractsBootstrap.Log?.LogWarning($"[MoonContracts.Blacksite] #947 S12 U1: {message}");
        }

        private static bool IsServerAuthority()
        {
            return RoundManager.Instance != null && RoundManager.Instance.IsServer;
        }
    }
}
