using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Bootstrap
{
    internal static class BundyShotgunProbePatch
    {
        private const string RuntimeBodyHitboxName = "BundyBodyHitbox";
        private const float SweepOriginBackstep = 10f;
        private const float SweepRadius = 5f;
        private const float SweepDistance = 15f;
        private const int EnemyHitboxMask = 524288;
        private const int VanillaHitCapacity = 10;
        private static int _shotSequence;

        [HarmonyPatch(
            typeof(ShotgunItem),
            nameof(ShotgunItem.ShootGun),
            new[] { typeof(Vector3), typeof(Vector3) })]
        [HarmonyPostfix]
        private static void ShootGunPostfix(ShotgunItem __instance, Vector3 __0, Vector3 __1)
        {
            if (!BundyPlugin.DebugLogsEnabled ||
                __instance == null ||
                !__instance.IsOwner ||
                GameNetworkManager.Instance?.localPlayerController == null)
            {
                return;
            }

            int spawnedBundyCount = 0;
            try
            {
                List<BundyAIBase> bundys = FindSpawnedBundys();
                spawnedBundyCount = bundys.Count;
                if (spawnedBundyCount == 0)
                    return;

                WriteProbe(__instance, __0, __1, bundys);
            }
            catch (Exception exception)
            {
                // A diagnostic postfix must never alter ShootGun's result by surfacing an exception.
                if (spawnedBundyCount > 0)
                {
                    BundyPlugin.Log?.LogWarning(
                        "[BundyShotgunProbe] BEGIN status=error" + Environment.NewLine +
                        $"[BundyShotgunProbe] ERROR type={Quote(exception.GetType().Name)} message={Quote(exception.Message)}" +
                        Environment.NewLine +
                        "[BundyShotgunProbe] END status=error");
                }
            }
        }

        private static List<BundyAIBase> FindSpawnedBundys()
        {
            BundyAIBase[] candidates = UnityEngine.Object.FindObjectsByType<BundyAIBase>(
                FindObjectsInactive.Include,
                FindObjectsSortMode.None);
            var result = new List<BundyAIBase>(candidates.Length);

            for (int i = 0; i < candidates.Length; i++)
            {
                BundyAIBase bundy = candidates[i];
                if (bundy != null && bundy.gameObject.scene.IsValid())
                    result.Add(bundy);
            }

            result.Sort((left, right) => left.GetInstanceID().CompareTo(right.GetInstanceID()));
            return result;
        }

        private static void WriteProbe(
            ShotgunItem shotgun,
            Vector3 shotgunPosition,
            Vector3 shotgunForward,
            List<BundyAIBase> bundys)
        {
            int shot = Interlocked.Increment(ref _shotSequence);
            Ray sweepRay = new Ray(
                shotgunPosition - shotgunForward * SweepOriginBackstep,
                shotgunForward);
            var hits = new RaycastHit[VanillaHitCapacity];
            int hitCount = Physics.SphereCastNonAlloc(
                sweepRay,
                SweepRadius,
                hits,
                SweepDistance,
                EnemyHitboxMask,
                QueryTriggerInteraction.Collide);

            Vector3 sweepEnd = sweepRay.origin + sweepRay.direction * SweepDistance;
            var builder = new StringBuilder(1024);
            builder.Append("[BundyShotgunProbe] BEGIN")
                .Append(" shot=").Append(shot)
                .Append(" shotgun=").Append(Quote(shotgun.name))
                .Append(" owner=").Append(shotgun.IsOwner)
                .Append(" position=").Append(FormatVector(shotgunPosition))
                .Append(" forward=").Append(FormatVector(shotgunForward))
                .Append(" sphereOrigin=").Append(FormatVector(sweepRay.origin))
                .Append(" sphereRadius=").Append(FormatFloat(SweepRadius))
                .Append(" sphereDistance=").Append(FormatFloat(SweepDistance))
                .Append(" mask=").Append(EnemyHitboxMask)
                .Append(" bundyCount=").Append(bundys.Count)
                .Append(" hitCount=").Append(hitCount)
                .AppendLine();

            for (int i = 0; i < bundys.Count; i++)
                AppendBundy(builder, i, bundys[i], shotgunPosition, sweepRay.origin, sweepEnd, hits, hitCount);

            for (int i = 0; i < hitCount; i++)
                AppendHit(builder, i, hits[i], shotgunPosition);

            builder.Append("[BundyShotgunProbe] END shot=").Append(shot);
            BundyPlugin.Log?.LogInfo(builder.ToString());
        }

        private static void AppendBundy(
            StringBuilder builder,
            int index,
            BundyAIBase bundy,
            Vector3 shotgunPosition,
            Vector3 sweepStart,
            Vector3 sweepEnd,
            RaycastHit[] hits,
            int hitCount)
        {
            Transform hitbox = bundy.transform.Find(RuntimeBodyHitboxName);
            Collider collider = hitbox != null ? hitbox.GetComponent<Collider>() : null;
            EnemyAICollisionDetect detector =
                hitbox != null ? hitbox.GetComponent<EnemyAICollisionDetect>() : null;
            Vector3 center = GetWorldCenter(hitbox, collider);
            float sweepClearance = GetSweepClearance(center, collider, sweepStart, sweepEnd);
            bool spherecastReturnedHit = SpherecastReturnedBundy(hits, hitCount, bundy);

            builder.Append("[BundyShotgunProbe] BUNDY")
                .Append(" index=").Append(index)
                .Append(" name=").Append(Quote(bundy.name))
                .Append(" instanceId=").Append(bundy.GetInstanceID())
                .Append(" transformPosition=").Append(FormatVector(bundy.transform.position))
                .Append(" distanceFromShotgun=")
                .Append(FormatFloat(Vector3.Distance(shotgunPosition, bundy.transform.position)))
                .Append(" hitboxExists=").Append(hitbox != null)
                .Append(" activeInHierarchy=").Append(hitbox != null && hitbox.gameObject.activeInHierarchy)
                .Append(" colliderExists=").Append(collider != null)
                .Append(" colliderEnabled=").Append(collider != null && collider.enabled)
                .Append(" layer=").Append(hitbox != null ? hitbox.gameObject.layer : -1)
                .Append(" center=").Append(hitbox != null ? FormatVector(center) : "<missing>")
                .Append(" enemyDetect=").Append(detector != null)
                .Append(" mainScriptWired=").Append(detector != null && detector.mainScript == bundy)
                .Append(" withinVanillaSweepReach=").Append(sweepClearance <= SweepRadius)
                .Append(" sweepClearance=").Append(FormatFloat(sweepClearance))
                .Append(" spherecastReturnedHit=").Append(spherecastReturnedHit)
                .AppendLine();
        }

        private static void AppendHit(
            StringBuilder builder,
            int index,
            RaycastHit hit,
            Vector3 shotgunPosition)
        {
            Collider collider = hit.collider;
            Transform hitTransform = hit.transform;
            EnemyAICollisionDetect detector =
                hitTransform != null ? hitTransform.GetComponent<EnemyAICollisionDetect>() : null;

            bool linecastEvaluated = detector != null && StartOfRound.Instance != null;
            bool linecastBlocked = false;
            RaycastHit blockingHit = default;
            if (linecastEvaluated)
            {
                linecastBlocked = Physics.Linecast(
                    shotgunPosition,
                    hit.point,
                    out blockingHit,
                    StartOfRound.Instance.collidersAndRoomMaskAndDefault,
                    QueryTriggerInteraction.Ignore);
            }

            string blockerName = !linecastEvaluated
                ? "<not-evaluated>"
                : linecastBlocked
                    ? blockingHit.collider != null ? blockingHit.collider.name : "<unknown>"
                    : "<none>";
            int blockerLayer =
                linecastEvaluated && linecastBlocked && blockingHit.collider != null
                    ? blockingHit.collider.gameObject.layer
                    : -1;

            builder.Append("[BundyShotgunProbe] HIT")
                .Append(" index=").Append(index)
                .Append(" collider=").Append(Quote(collider != null ? collider.name : "<null>"))
                .Append(" hitTransform=").Append(Quote(hitTransform != null ? hitTransform.name : "<null>"))
                .Append(" layer=").Append(collider != null ? collider.gameObject.layer : -1)
                .Append(" distance=").Append(FormatFloat(hit.distance))
                .Append(" point=").Append(FormatVector(hit.point))
                .Append(" distanceZero=").Append(hit.distance == 0f)
                .Append(" enemyDetect=").Append(detector != null)
                .Append(" detectedMainScript=")
                .Append(Quote(detector != null && detector.mainScript != null
                    ? detector.mainScript.GetType().Name
                    : "<none>"))
                .Append(" linecastEvaluated=").Append(linecastEvaluated)
                .Append(" linecastBlocked=").Append(linecastBlocked)
                .Append(" blocker=").Append(Quote(blockerName))
                .Append(" blockerLayer=").Append(blockerLayer)
                .AppendLine();
        }

        private static bool SpherecastReturnedBundy(
            RaycastHit[] hits,
            int hitCount,
            BundyAIBase bundy)
        {
            for (int i = 0; i < hitCount; i++)
            {
                Transform hitTransform = hits[i].transform;
                if (hitTransform == null)
                    continue;

                EnemyAICollisionDetect detector = hitTransform.GetComponent<EnemyAICollisionDetect>();
                if ((detector != null && detector.mainScript == bundy) ||
                    hitTransform == bundy.transform ||
                    hitTransform.IsChildOf(bundy.transform))
                {
                    return true;
                }
            }

            return false;
        }

        private static Vector3 GetWorldCenter(Transform hitbox, Collider collider)
        {
            if (collider is CapsuleCollider capsule)
                return capsule.transform.TransformPoint(capsule.center);
            if (collider != null)
                return collider.bounds.center;
            return hitbox != null ? hitbox.position : Vector3.zero;
        }

        private static float GetSweepClearance(
            Vector3 center,
            Collider collider,
            Vector3 sweepStart,
            Vector3 sweepEnd)
        {
            Vector3 closestOnSweep = ClosestPointOnSegment(center, sweepStart, sweepEnd);
            Vector3 closestOnHitbox =
                collider != null ? collider.ClosestPoint(closestOnSweep) : center;
            return Vector3.Distance(closestOnSweep, closestOnHitbox);
        }

        private static Vector3 ClosestPointOnSegment(Vector3 point, Vector3 start, Vector3 end)
        {
            Vector3 segment = end - start;
            float squaredLength = segment.sqrMagnitude;
            if (squaredLength <= Mathf.Epsilon)
                return start;

            float t = Mathf.Clamp01(Vector3.Dot(point - start, segment) / squaredLength);
            return start + segment * t;
        }

        private static string FormatVector(Vector3 value)
        {
            return "<" +
                   FormatFloat(value.x) + "," +
                   FormatFloat(value.y) + "," +
                   FormatFloat(value.z) + ">";
        }

        private static string FormatFloat(float value)
        {
            return value.ToString("F3", CultureInfo.InvariantCulture);
        }

        private static string Quote(string value)
        {
            string escaped = (value ?? "<null>")
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n");
            return "\"" + escaped + "\"";
        }
    }
}
