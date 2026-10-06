using System;
using System.Collections.Generic;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #1219 F1.1 (decision D6): the ONE answer to "can a soldier's baton hit this collider", and it
    /// is vanilla's. Both the melee range gate (<c>BlacksiteSoldierAI.MeasureBatonContact</c>) and
    /// the swept hit (<c>BlacksiteSoldierAI.SweepBatonSegment</c>) ask it, so the body a swing is
    /// measured against and the body it can land on are the same collider.
    ///
    /// The contract is <c>Shovel.HitShovel</c> in the v81 Assembly-CSharp, read from the
    /// decompilation rather than recalled:
    /// <list type="bullet">
    /// <item>the query mask is the shovel's own <c>shovelMask = 1084754248</c> - layers 3 Player,
    /// 6 Props, 8 Room, 11 Colliders, 19 Enemies, 21 MapHazards, 23 EnemiesNotRendered and
    /// 30 Vehicle - with triggers collided. LineOfSight (18) and ScanNode (22) are not in it, which
    /// is exactly the two volumes the 2026-09-26 log shows the old heuristic choosing as bodies
    /// (<c>LineOfSightCube</c> around a player, <c>ScanNode</c> on a HoarderBug);</item>
    /// <item>the hit object is <c>RaycastHit.transform</c>, which is the attached rigidbody's
    /// transform when there is one and the collider's own otherwise, and a hit whose object sits
    /// on Room (8) or Colliders (11) is terrain, never a victim;</item>
    /// <item>that object must carry the victim component ITSELF - <c>EnemyAICollisionDetect</c>
    /// with a live <c>mainScript</c> for an enemy, <c>PlayerControllerB</c> for a player - not a
    /// parent of it. The shovel also hits other <c>IHittable</c>s; a soldier's baton hits only
    /// these two, which is the whole of D6.</item>
    /// </list>
    /// Nothing here allocates: the body walk reuses one scratch list and every component probe is
    /// <c>TryGetComponent</c>.
    /// </summary>
    internal static class BlacksiteHitResolver
    {
        /// <summary>Shovel.shovelMask, v81 (decompiled Shovel.cs:35).</summary>
        internal const int ShovelMask = 1084754248;
        private const int RoomLayer = 8, CollidersLayer = 11;

        /// <summary>
        /// The slack a confirmed hit may take past its contact profile's measured outer wall,
        /// root to root. It is the weapon head's own radius: the wall is measured to the head's
        /// CENTRE passing a body, and a body grazed by the head's rim is a real hit.
        /// </summary>
        internal const float RootReachSlack = BlacksiteBatonPolicy.HeadRadius;

        private static readonly List<Collider> BodyScratch = new List<Collider>(32);
        private static readonly HashSet<Type> NotHittableReported = new HashSet<Type>();

        /// <summary>The object vanilla's cast reports for this collider: its rigidbody's, else its own.</summary>
        internal static Transform HitTransform(Collider collider)
            => collider.attachedRigidbody != null ? collider.attachedRigidbody.transform : collider.transform;

        /// <summary>
        /// True when vanilla's shovel would resolve this collider to a victim, and which one. An
        /// enemy may be a <see cref="BlacksiteSoldierAI"/>; the baton sweep refuses those itself,
        /// because a player's shovel on a soldier is exactly this same contract.
        /// </summary>
        internal static bool TryResolve(Collider collider, out EnemyAI enemy, out PlayerControllerB player)
        {
            enemy = null; player = null;
            if (collider == null || !collider.enabled || (ShovelMask & (1 << collider.gameObject.layer)) == 0) return false;
            Transform hit = HitTransform(collider);
            int layer = hit.gameObject.layer;
            if (layer == RoomLayer || layer == CollidersLayer) return false;
            if (hit.TryGetComponent(out EnemyAICollisionDetect detect))
            {
                enemy = detect.mainScript;
                return enemy != null;
            }
            return hit.TryGetComponent(out player);
        }

        /// <summary>
        /// The contact's hittable body: among the colliders under <paramref name="contact"/> that
        /// <see cref="TryResolve"/> maps back to <paramref name="contact"/> itself, the one nearest
        /// its root - a collider containing the root reads zero - with the taller vertical span
        /// breaking a tie inside a centimetre. Null when the contact has no hittable collider at
        /// all, which for an enemy means vanilla's own shovel cannot hit it either.
        /// </summary>
        internal static Collider ResolveBody(Component contact)
        {
            if (contact == null) return null;
            var enemyContact = contact as EnemyAI;
            Vector3 root = contact.transform.position;
            Collider best = null;
            float bestDistance = float.MaxValue, bestSpan = 0f;
            BodyScratch.Clear();
            contact.GetComponentsInChildren(false, BodyScratch);
            for (int i = 0; i < BodyScratch.Count; i++)
            {
                var candidate = BodyScratch[i];
                if (!TryResolve(candidate, out var enemy, out var player)) continue;
                if (enemyContact != null ? !ReferenceEquals(enemy, enemyContact) : !ReferenceEquals(player, contact)) continue;
                Bounds bounds = candidate.bounds;
                if (bounds.size.sqrMagnitude <= 1e-6f) continue;
                float distance = bounds.SqrDistance(root), span = bounds.size.y;
                if (distance > bestDistance + BodyTie || (distance >= bestDistance - BodyTie && span <= bestSpan)) continue;
                best = candidate; bestDistance = distance; bestSpan = span;
            }
            BodyScratch.Clear();
            return best;
        }
        // One square centimetre of squared distance: two bodies this close to the root are the
        // same distance from it as far as the pick is concerned.
        private const float BodyTie = .0001f;

        /// <summary>
        /// D6: an enemy type none of whose colliders vanilla can hit is named once per type per
        /// round - the squad does not close on it or swing at it, and the log says why.
        /// </summary>
        internal static void ReportNotHittable(EnemyAI enemy, int memberIndex)
        {
            if (enemy == null || !NotHittableReported.Add(enemy.GetType())) return;
            BlacksiteDiagnostics.Log("Baton", "member=" + memberIndex + ", not-hittable enemy=" + enemy.GetType().Name
                + ", reason=no enabled EnemyAICollisionDetect collider on a shovel-mask layer");
        }

        internal static void Reset() => NotHittableReported.Clear();
    }
}
