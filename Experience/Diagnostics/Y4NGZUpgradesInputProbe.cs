using System;
using System.Reflection;
using System.Text;
using GameNetcodeStuff;
using UnityEngine;

namespace Y4NGZCompany.Experience.Diagnostics
{
    /// <summary>
    /// #453: reflection-only probe of the two Y4NGZUpgrades states that can plausibly strand the
    /// local player's input, for the watchdog's warn line.
    ///
    /// Y4NGZUpgrades is a separate, optional plugin, so nothing here may be a compile-time
    /// reference. Both handles are resolved once and cached; when the plugin is absent the probe
    /// degrades to "n/a" forever rather than paying for a failed lookup every warn.
    ///
    /// <list type="bullet">
    /// <item><c>Y4NGZUpgrades.Interactive.HeldItemStowService.IsStowed(PlayerControllerB)</c> —
    /// true while the held item is temporarily stowed. A stow that never restores leaves the
    /// player holding nothing with the item's slot still occupied.</item>
    /// <item><c>Y4NGZUpgrades.Weapons.Patches.GrenadeThrowPatches._localThrowActive</c> — true
    /// while the local grenade-throw coroutine owns the input. A stranded coroutine leaves the
    /// Discard action disabled, which is exactly the "G stopped working" half of the incident.</item>
    /// </list>
    /// </summary>
    internal static class Y4NGZUpgradesInputProbe
    {
        private const string StowServiceTypeName = "Y4NGZUpgrades.Interactive.HeldItemStowService";
        private const string GrenadeThrowTypeName = "Y4NGZUpgrades.Weapons.Patches.GrenadeThrowPatches";

        private static bool _resolved;
        private static MethodInfo _isStowed;
        private static FieldInfo _localThrowActive;
        private static readonly object[] _isStowedArgs = new object[1];

        internal static void Append(StringBuilder builder, PlayerControllerB player)
        {
            Resolve();

            builder.Append("stow=");
            builder.Append(DescribeStow(player));
            builder.Append(" grenadeThrow=");
            builder.Append(DescribeGrenadeThrow());
        }

        private static string DescribeStow(PlayerControllerB player)
        {
            if (_isStowed == null)
                return "n/a";

            try
            {
                _isStowedArgs[0] = player;
                object result = _isStowed.Invoke(null, _isStowedArgs);
                _isStowedArgs[0] = null;
                return result is bool stowed ? (stowed ? "True" : "False") : "?";
            }
            catch
            {
                _isStowed = null;
                return "err";
            }
        }

        private static string DescribeGrenadeThrow()
        {
            if (_localThrowActive == null)
                return "n/a";

            try
            {
                return _localThrowActive.GetValue(null) is bool active ? (active ? "True" : "False") : "?";
            }
            catch
            {
                _localThrowActive = null;
                return "err";
            }
        }

        private static void Resolve()
        {
            if (_resolved)
                return;
            _resolved = true;

            try
            {
                Type stowType = FindType(StowServiceTypeName);
                if (stowType != null)
                {
                    _isStowed = stowType.GetMethod(
                        "IsStowed",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                        binder: null,
                        types: new[] { typeof(PlayerControllerB) },
                        modifiers: null);
                }

                Type grenadeType = FindType(GrenadeThrowTypeName);
                if (grenadeType != null)
                {
                    _localThrowActive = grenadeType.GetField(
                        "_localThrowActive",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
            }
            catch
            {
                _isStowed = null;
                _localThrowActive = null;
            }
        }

        private static Type FindType(string fullName)
        {
            var direct = Type.GetType(fullName + ", Y4NGZUpgrades", throwOnError: false);
            if (direct != null)
                return direct;

            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                try
                {
                    if (!string.Equals(assemblies[i].GetName().Name, "Y4NGZUpgrades", StringComparison.OrdinalIgnoreCase))
                        continue;

                    return assemblies[i].GetType(fullName, throwOnError: false);
                }
                catch
                {
                    // Dynamic or unloadable assembly; keep scanning.
                }
            }

            return null;
        }
    }
}
