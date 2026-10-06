using System;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.Survey
{
    /// <summary>
    /// #807 temporary tuning aid: two config strings that override the held Survey Beacon's
    /// position and rotation offsets live, so the chest pose can be dialled in one session
    /// without a rebuild. Empty strings leave the authored defaults alone. Remove once the
    /// values are baked into <see cref="SurveyBeaconItemRegistry"/>.
    /// </summary>
    internal static class SurveyHeldPoseTuning
    {
        // Optional deltas around the pose solved from HoldForward's two animated wrists.
        internal static readonly Vector3 DefaultPositionOffset = Vector3.zero;
        internal static readonly Vector3 DefaultRotationOffset = Vector3.zero;

        private static ConfigEntry<string> _position;
        private static ConfigEntry<string> _rotation;
        private static int _version;

        /// <summary>Bumps whenever either entry changes so items can re-read cheaply.</summary>
        internal static int Version => _version;

        internal static void Bind(ConfigFile config, string section)
        {
            _position = config.Bind(section, "Held Position Offset (tuning)", string.Empty,
                "Temporary. Held beacon position offset as \"x, y, z\" metres in the built-in carry frame after centering it between both hands; empty keeps the built-in value.");
            _rotation = config.Bind(section, "Held Rotation Offset (tuning)", string.Empty,
                "Temporary. Held beacon rotation offset as \"x, y, z\" degrees from the built-in carry; empty keeps the built-in value.");
            _position.SettingChanged += (_, __) => _version++;
            _rotation.SettingChanged += (_, __) => _version++;
        }

        /// <summary>Writes the current overrides into the item; returns true when either applied.</summary>
        internal static bool Apply(Item item)
        {
            if (item == null)
                return false;

            bool applied = false;
            if (TryParse(_position?.Value, out Vector3 position))
            {
                item.positionOffset = position;
                applied = true;
            }
            if (TryParse(_rotation?.Value, out Vector3 rotation))
            {
                item.rotationOffset = rotation;
                applied = true;
            }
            if (applied)
            {
                ContractsBootstrap.Log?.LogInfo(
                    $"[MoonContracts.Survey] Held pose tuning applied: position={item.positionOffset:F3} rotation={item.rotationOffset:F1}");
            }
            return applied;
        }

        private static bool TryParse(string text, out Vector3 value)
        {
            value = Vector3.zero;
            if (string.IsNullOrWhiteSpace(text))
                return false;
            string[] parts = text.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3)
                return false;
            float[] parsed = new float[3];
            for (int i = 0; i < 3; i++)
            {
                if (!float.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out parsed[i]))
                    return false;
            }
            value = new Vector3(parsed[0], parsed[1], parsed[2]);
            return true;
        }
    }
}
