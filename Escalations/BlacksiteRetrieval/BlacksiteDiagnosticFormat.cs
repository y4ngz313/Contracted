using System.Text;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    // #874 S4.0b: shared formatting for the [UAT] diagnostic line contract. Every helper here is
    // read-only observation: nothing samples, mutates or schedules game state, and every caller is
    // expected to be inside a BlacksiteDiagnostics.Enabled guard so a disabled run allocates nothing.
    internal static class BlacksiteDiagnosticFormat
    {
        internal const string None = "none";

        // The contract prints vectors as Unity ToString("0.00"): "(x.xx, y.yy, z.zz)".
        internal static string V(Vector3 value) => value.ToString("0.00");

        internal static string B(Bounds value) => V(value.min) + "-" + V(value.max);

        // Full transform path, root to leaf. "AnimContainer" alone cannot tell a door jamb from a
        // far wall (findings-elevator.md section 3), which is why the leaf name is not enough.
        internal static string Path(Transform transform)
        {
            if (transform == null)
                return None;
            var builder = new StringBuilder(transform.name);
            Transform current = transform.parent;
            while (current != null)
            {
                builder.Insert(0, '/').Insert(0, current.name);
                current = current.parent;
            }
            return builder.ToString();
        }

        // blockerPath/layer/colliderType/convex/bounds as one comma-separated fragment, in the
        // order the contract lists them. A null collider still prints every key so the score
        // script never has to special-case a missing blocker.
        internal static string Blocker(Collider collider)
        {
            if (collider == null)
                return $"blockerPath={None}, layer={None}, colliderType={None}, convex={None}, bounds={None}";
            string convex = collider is MeshCollider mesh ? mesh.convex.ToString() : None;
            return $"blockerPath={Path(collider.transform)}, layer={collider.gameObject.layer}, "
                + $"colliderType={collider.GetType().Name}, convex={convex}, bounds={B(collider.bounds)}";
        }

        internal static string Seconds(float value) => value < 0f ? None : value.ToString("0.00");

        internal static string Target(PlayerControllerB player) =>
            player != null ? "player:" + player.actualClientId : None;

        internal static string Target(EnemyAI enemy) =>
            enemy != null ? "monster:" + enemy.GetType().Name : None;
    }
}
