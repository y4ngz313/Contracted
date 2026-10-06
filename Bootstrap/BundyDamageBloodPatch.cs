using System;
using System.Reflection;
using GameNetcodeStuff;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Bootstrap
{
    /// <summary>
    /// Captures the attacker-local contact point before vanilla reduces an enemy hit to force and
    /// direction. Only Bundy crosses the first branch; every other enemy returns immediately.
    /// </summary>
    [HarmonyPatch]
    internal static class BundyDamageBloodPatch
    {
        private static readonly Type[] HitParameterTypes =
        {
            typeof(int),
            typeof(Vector3),
            typeof(PlayerControllerB),
            typeof(bool),
            typeof(int)
        };

        private static MethodBase TargetMethod()
        {
            MethodInfo interfaceMethod = AccessTools.Method(typeof(IHittable), "Hit", HitParameterTypes);
            if (interfaceMethod == null)
                throw new MissingMethodException("IHittable.Hit(int, Vector3, PlayerControllerB, bool, int)");

            InterfaceMapping map = typeof(EnemyAICollisionDetect).GetInterfaceMap(typeof(IHittable));
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i] == interfaceMethod)
                    return map.TargetMethods[i];
            }

            throw new MissingMethodException(
                "EnemyAICollisionDetect's v81 IHittable.Hit implementation was not found.");
        }

        [HarmonyPostfix]
        private static void Postfix(
            EnemyAICollisionDetect __instance,
            int force,
            Vector3 hitDirection,
            PlayerControllerB playerWhoHit,
            bool __result)
        {
            if (__instance?.mainScript is not BundyAIBase bundy)
                return;
            if (!__result || force <= 0)
                return;

            Collider detectorCollider = __instance.GetComponent<Collider>();
            Collider bodyCollider = bundy.DamageBloodFallbackCollider;
            bool detectorIsRoot = __instance.transform == bundy.transform;
            Collider collider;
            string colliderSource;
            if (detectorIsRoot && bodyCollider != null)
            {
                // Bundy's kinematic Rigidbody makes weapon RaycastHit.transform resolve to the
                // enemy root. That root has an authored contact collider near the rig origin, but
                // it is not the 3.45 m damage volume the weapon actually struck. Projecting onto
                // it collapsed every reconstructed hit into the spine/pelvis band. The runtime
                // body capsule is the authoritative fallback for a root-level detector.
                collider = bodyCollider;
                colliderSource = "root->runtime-body";
            }
            else
            {
                collider = detectorCollider != null ? detectorCollider : bodyCollider;
                colliderSource = detectorCollider != null ? "detector" : "runtime-body-fallback";
            }

            Vector3 contactPoint = BundyDamageBloodContactResolver.Resolve(
                bundy, collider, hitDirection, playerWhoHit, out string route);
            if (BundyBloodFXBridge.Verbose)
            {
                string colliderName = collider != null ? collider.name : "<null>";
                string colliderType = collider != null ? collider.GetType().Name : "<null>";
                Bounds bounds = collider != null ? collider.bounds : default;
                BundyBloodFXBridge.Trace(
                    $"creature contact route={route} detector='{__instance.name}' "
                    + $"colliderSource={colliderSource} collider='{colliderName}' type={colliderType} "
                    + $"boundsCenter={BundyBloodFXBridge.Format(bounds.center)} "
                    + $"boundsExtents={BundyBloodFXBridge.Format(bounds.extents)} "
                    + $"point={BundyBloodFXBridge.Format(contactPoint)} "
                    + $"dir={BundyBloodFXBridge.Format(hitDirection)} force={force}.");
            }
            bundy.ReportDamageBlood(contactPoint, hitDirection, force);
        }

    }

    /// <summary>Contact reconstruction kept outside Harmony's patch class so analyzer parameter
    /// rules apply only to the actual postfix.</summary>
    internal static class BundyDamageBloodContactResolver
    {
        internal static Vector3 Resolve(
            BundyAIBase bundy,
            Collider collider,
            Vector3 hitDirection,
            PlayerControllerB playerWhoHit,
            out string route)
        {
            if (collider == null)
            {
                route = "no-collider";
                return bundy.transform.position + Vector3.up;
            }

            Bounds bounds = collider.bounds;
            Vector3 incoming = IsFinite(hitDirection) && hitDirection.sqrMagnitude > 0.0001f
                ? hitDirection.normalized
                : Vector3.forward;
            Vector3 shooterEye;
            Vector3 aimDirection;
            Camera gameplayCamera = playerWhoHit != null ? playerWhoHit.gameplayCamera : null;
            if (gameplayCamera != null)
            {
                Transform cameraTransform = gameplayCamera.transform;
                shooterEye = cameraTransform.position;
                aimDirection = IsFinite(hitDirection) && hitDirection.sqrMagnitude > 0.0001f
                    ? hitDirection.normalized
                    : cameraTransform.forward;
            }
            else if (playerWhoHit != null)
            {
                shooterEye = playerWhoHit.transform.position + Vector3.up * 1.6f;
                aimDirection = incoming;
            }
            else
            {
                aimDirection = incoming;
                shooterEye = bounds.center - aimDirection * (bounds.extents.magnitude + 1f);
            }

            if (!IsFinite(aimDirection) || aimDirection.sqrMagnitude <= 0.0001f)
                aimDirection = bounds.center - shooterEye;
            if (!IsFinite(aimDirection) || aimDirection.sqrMagnitude <= 0.0001f)
                aimDirection = Vector3.forward;
            aimDirection.Normalize();

            var ray = new Ray(shooterEye, aimDirection);
            if (collider.Raycast(ray, out RaycastHit hit, 1000f) && IsFinite(hit.point))
            {
                route = "attack-ray";
                return hit.point;
            }

            // Sphere-cast weapons legitimately hit beside their center ray. Reconstruct that
            // contact near the AIM ray at the target's depth; measuring from the shooter's eye
            // instead collapses every miss onto the same nearest (pelvis-level) point.
            float targetDistance = Vector3.Dot(bounds.center - shooterEye, aimDirection);
            if (float.IsNaN(targetDistance) || float.IsInfinity(targetDistance) || targetDistance <= 0f)
                targetDistance = Vector3.Distance(shooterEye, bounds.center);

            Vector3 aimedPoint = ray.GetPoint(targetDistance);
            Vector3 closest = collider.ClosestPoint(aimedPoint);
            if (IsFinite(closest))
            {
                route = "aim-depth-closest";
                return closest;
            }

            Vector3 bounded = bounds.ClosestPoint(aimedPoint);
            route = "bounds-closest";
            return IsFinite(bounded) ? bounded : bounds.center;
        }

        private static bool IsFinite(Vector3 value)
        {
            return
                !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
                !float.IsNaN(value.y) && !float.IsInfinity(value.y) &&
                !float.IsNaN(value.z) && !float.IsInfinity(value.z);
        }
    }
}