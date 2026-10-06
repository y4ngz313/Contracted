using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>
    /// #321 (A1): during a paired finisher Bundy keeps his solid layer-19 body capsule and the
    /// victim keeps a live <see cref="CharacterController"/>, so the two physics bodies shove each
    /// other apart while both are pinned to authored paired-root positions. Nothing in the
    /// Interactions path ever suppressed that, which is the visible "they push each other" defect.
    ///
    /// This helper pairs every one of Bundy's colliders against every one of the victim's for the
    /// duration of the finisher and restores them on release. It is deliberately a plain static
    /// registry rather than a component: the begin/release ClientRpcs already run on every client,
    /// and the state has to survive the victim dying (the ragdoll swap destroys the player's
    /// colliders) without leaking an ignored pair onto Bundy.
    /// </summary>
    internal static class BundyFinisherCollisionSuppressor
    {
        private static readonly Dictionary<PairKey, SuppressedPair> Suppressed =
            new Dictionary<PairKey, SuppressedPair>();
        private static readonly List<PairKey> ScratchKeys = new List<PairKey>();

        /// <summary>
        /// Ignore collisions between every collider owned by <paramref name="bundy"/> and every
        /// collider owned by <paramref name="victim"/>. Idempotent: a second call for the same pair
        /// refreshes nothing and leaves the first registration authoritative.
        /// </summary>
        internal static bool Suppress(EnemyAI bundy, PlayerControllerB victim, string reason)
        {
            if (bundy == null || victim == null)
                return false;

            var key = new PairKey(bundy.GetInstanceID(), victim.GetInstanceID());
            if (Suppressed.ContainsKey(key))
                return true;

            Collider[] bundyColliders = CollectColliders(bundy.transform);
            Collider[] victimColliders = CollectColliders(victim.transform);
            if (bundyColliders.Length == 0 || victimColliders.Length == 0)
            {
                LogInfo(
                    $"Finisher collision suppression skipped for {PlayerLabel(victim)}; " +
                    $"bundyColliders={bundyColliders.Length}, victimColliders={victimColliders.Length}, reason={reason}.");
                return false;
            }

            // Prior ignore state, flattened [bundyIndex * victimColliders.Length + victimIndex].
            // Recorded rather than assumed: another mod (or vanilla's own player/enemy setup) may
            // already be ignoring one of these pairs, and restoring such a pair to "collide" would
            // hand that owner back a state it never asked for.
            var priorIgnore = new bool[bundyColliders.Length * victimColliders.Length];
            int applied = 0;
            for (int i = 0; i < bundyColliders.Length; i++)
            {
                Collider bundyCollider = bundyColliders[i];
                if (bundyCollider == null)
                    continue;

                for (int j = 0; j < victimColliders.Length; j++)
                {
                    Collider victimCollider = victimColliders[j];
                    if (victimCollider == null)
                        continue;

                    try
                    {
                        priorIgnore[(i * victimColliders.Length) + j] =
                            Physics.GetIgnoreCollision(bundyCollider, victimCollider);
                        Physics.IgnoreCollision(bundyCollider, victimCollider, true);
                        applied++;
                    }
                    catch (Exception e)
                    {
                        // Disabled or already-destroyed colliders throw rather than no-op; one bad
                        // pair must not cost the rest of the suppression.
                        LogWarning(
                            $"Finisher collision suppression pair failed ({bundyCollider.name} <-> {victimCollider.name}): " +
                            $"{e.GetType().Name}: {e.Message}");
                    }
                }
            }

            Suppressed[key] = new SuppressedPair(bundy, victim, bundyColliders, victimColliders, priorIgnore);
            LogInfo(
                $"Finisher collision suppressed for {PlayerLabel(victim)}; pairs={applied}, " +
                $"bundyColliders={bundyColliders.Length}, victimColliders={victimColliders.Length}, reason={reason}.");
            return applied > 0;
        }

        /// <summary>Restore the pair recorded by <see cref="Suppress"/>, if any.</summary>
        internal static bool Restore(EnemyAI bundy, PlayerControllerB victim, string reason)
        {
            // Deliberately the C# null check and not Unity's: a Bundy destroyed mid-finisher still
            // has to be able to purge its own entries, and Unity's operator== reports a destroyed
            // object as null while GetInstanceID() keeps returning the id the entries are keyed on.
            if (bundy is null || victim is null)
                return false;

            var key = new PairKey(bundy.GetInstanceID(), victim.GetInstanceID());
            if (!Suppressed.TryGetValue(key, out SuppressedPair pair))
                return false;

            Suppressed.Remove(key);
            RestorePair(pair, reason);
            return true;
        }

        /// <summary>
        /// Restore every pair this Bundy owns. Called from action cleanup and enemy teardown so a
        /// victim who died, despawned, or was interrupted mid-finisher can never leave Bundy
        /// permanently phased through a living player.
        /// </summary>
        internal static void RestoreAllForBundy(EnemyAI bundy, string reason)
        {
            // `is null`, not `== null`: this is called from KillEnemy/OnNetworkDespawn/OnDestroy,
            // where Unity's operator== already reports the Bundy as null. Early-returning there is
            // what used to strand an entry — holding references to destroyed Collider arrays — in
            // the static registry for the rest of the session.
            if (bundy is null)
                return;

            RestoreAllForBundyId(bundy.GetInstanceID(), reason);
        }

        /// <summary>Id-keyed half of <see cref="RestoreAllForBundy"/>, usable after destruction.</summary>
        internal static void RestoreAllForBundyId(int bundyId, string reason)
        {
            if (Suppressed.Count == 0)
                return;

            ScratchKeys.Clear();
            foreach (KeyValuePair<PairKey, SuppressedPair> entry in Suppressed)
            {
                if (entry.Key.BundyId == bundyId)
                    ScratchKeys.Add(entry.Key);
            }

            for (int i = 0; i < ScratchKeys.Count; i++)
            {
                if (!Suppressed.TryGetValue(ScratchKeys[i], out SuppressedPair pair))
                    continue;

                Suppressed.Remove(ScratchKeys[i]);
                RestorePair(pair, reason);
            }

            ScratchKeys.Clear();
        }

        /// <summary>
        /// Restore and forget every pair in the registry. Wired to the same round-reset path
        /// <c>BloodFXFinisherGore.StopAll</c> (in the Y4NGZBloodFX plugin) uses, for the
        /// same reason: a static registry is the only thing here that can survive a round boundary,
        /// and a Bundy destroyed by the scene unload never runs a teardown of its own.
        /// </summary>
        internal static void StopAll(string reason)
        {
            if (Suppressed.Count == 0)
                return;

            ScratchKeys.Clear();
            foreach (KeyValuePair<PairKey, SuppressedPair> entry in Suppressed)
                ScratchKeys.Add(entry.Key);

            for (int i = 0; i < ScratchKeys.Count; i++)
            {
                if (Suppressed.TryGetValue(ScratchKeys[i], out SuppressedPair pair))
                    RestorePair(pair, reason);
            }

            Suppressed.Clear();
            ScratchKeys.Clear();
        }

        private static void RestorePair(SuppressedPair pair, string reason)
        {
            int restored = 0;
            for (int i = 0; i < pair.BundyColliders.Length; i++)
            {
                Collider bundyCollider = pair.BundyColliders[i];
                if (bundyCollider == null)
                    continue;

                for (int j = 0; j < pair.VictimColliders.Length; j++)
                {
                    Collider victimCollider = pair.VictimColliders[j];
                    if (victimCollider == null)
                        continue;

                    try
                    {
                        // Back to what it was before this finisher, not to "collide": a pair some
                        // other owner was already ignoring stays ignored.
                        int flat = (i * pair.VictimColliders.Length) + j;
                        bool prior = pair.PriorIgnore != null && flat < pair.PriorIgnore.Length &&
                            pair.PriorIgnore[flat];
                        Physics.IgnoreCollision(bundyCollider, victimCollider, prior);
                        restored++;
                    }
                    catch (Exception e)
                    {
                        LogWarning(
                            $"Finisher collision restore pair failed ({bundyCollider.name} <-> {victimCollider.name}): " +
                            $"{e.GetType().Name}: {e.Message}");
                    }
                }
            }

            LogInfo(
                $"Finisher collision restored for {PlayerLabel(pair.Victim)}; pairs={restored}, reason={reason}.");
        }

        private static Collider[] CollectColliders(Transform root)
        {
            if (root == null)
                return Array.Empty<Collider>();

            Collider[] all = root.GetComponentsInChildren<Collider>(true);
            var solid = new List<Collider>(all.Length);
            for (int i = 0; i < all.Length; i++)
            {
                Collider collider = all[i];
                // Triggers never generate contact impulses, so ignoring them would only cost the
                // gameplay volumes (door triggers, blade hitbox, interact ranges) that must keep
                // firing while the pair is locked together.
                if (collider == null || collider.isTrigger)
                    continue;

                solid.Add(collider);
            }

            return solid.ToArray();
        }

        private static string PlayerLabel(PlayerControllerB player)
        {
            return player == null
                ? "<null>"
                : $"{player.playerUsername} (clientId={player.actualClientId})";
        }

        private static void LogInfo(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogInfo("[Bundy] " + message);
        }

        private static void LogWarning(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogWarning("[Bundy] " + message);
        }

        private readonly struct PairKey : IEquatable<PairKey>
        {
            internal readonly int BundyId;
            private readonly int _victimId;

            internal PairKey(int bundyId, int victimId)
            {
                BundyId = bundyId;
                _victimId = victimId;
            }

            public bool Equals(PairKey other)
            {
                return BundyId == other.BundyId && _victimId == other._victimId;
            }

            public override bool Equals(object obj)
            {
                return obj is PairKey other && Equals(other);
            }

            public override int GetHashCode()
            {
                return (BundyId * 397) ^ _victimId;
            }
        }

        private sealed class SuppressedPair
        {
            internal readonly EnemyAI Bundy;
            internal readonly PlayerControllerB Victim;
            internal readonly Collider[] BundyColliders;
            internal readonly Collider[] VictimColliders;

            /// <summary>Per-pair ignore state as it stood before suppression, flattened
            /// <c>[bundyIndex * VictimColliders.Length + victimIndex]</c>.</summary>
            internal readonly bool[] PriorIgnore;

            internal SuppressedPair(
                EnemyAI bundy,
                PlayerControllerB victim,
                Collider[] bundyColliders,
                Collider[] victimColliders,
                bool[] priorIgnore)
            {
                Bundy = bundy;
                Victim = victim;
                BundyColliders = bundyColliders;
                VictimColliders = victimColliders;
                PriorIgnore = priorIgnore;
            }
        }
    }

    /// <summary>
    /// Round-boundary reset for the suppressor's static registry (#393).
    ///
    /// This used to ride along on BloodFX's own round reset, which was fine while both lived in one
    /// assembly and is not now that blood is a separate, optional plugin: the ignored collider pairs
    /// are gameplay state and have to be cleared in a profile that never installs blood. Same four
    /// vanilla hooks BloodFX uses, for the same reason - a Bundy destroyed by the scene unload never
    /// runs a teardown of its own.
    /// </summary>
    [HarmonyPatch]
    internal static class BundyFinisherRoundLifecyclePatches
    {
        [HarmonyPatch(typeof(StartOfRound), "ShipLeave")]
        [HarmonyPostfix]
        private static void OnShipLeave()
        {
            BundyFinisherCollisionSuppressor.StopAll("round boundary: ship leave");
        }

        [HarmonyPatch(typeof(StartOfRound), "ShipLeaveAutomatically")]
        [HarmonyPostfix]
        private static void OnShipLeaveAutomatically()
        {
            BundyFinisherCollisionSuppressor.StopAll("round boundary: automatic ship leave");
        }

        [HarmonyPatch(typeof(StartOfRound), nameof(StartOfRound.ReviveDeadPlayers))]
        [HarmonyPostfix]
        private static void OnReviveDeadPlayers()
        {
            BundyFinisherCollisionSuppressor.StopAll("round boundary: revive dead players");
        }

        [HarmonyPatch(typeof(GameNetworkManager), "Disconnect")]
        [HarmonyPostfix]
        private static void OnDisconnect()
        {
            BundyFinisherCollisionSuppressor.StopAll("round boundary: disconnect");
        }
    }
}
