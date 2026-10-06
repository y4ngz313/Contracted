using System;
using System.Globalization;
using System.Text;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Escalations.Bundy
{
    /// <summary>
    /// Reflection-facing debug surface for the Y4NGZDebugTools "Bundy" tab
    /// (same pattern as LGUShipSystems.ShipBatteryDebugApi). Everything here
    /// reads/writes the live BepInEx config entries, so changes persist to
    /// the profile config automatically and the pose controls (read from config
    /// every frame) react immediately. Tint setters also re-apply to any spawned
    /// Bundy instances so sliders are visible without a respawn.
    /// </summary>
    public static class BundyDebugApi
    {
        public static string GetStatus()
        {
            var sb = new StringBuilder();
            BundyAIBase[] instances = UnityEngine.Object.FindObjectsOfType<BundyAIBase>();
            sb.Append("live Bundy instances: ").Append(instances.Length);
            if (BundyPlugin.ModConfig == null)
                sb.Append("  (config unavailable)");
            else if (BundyPlugin.ModConfig.ChainsawDisableGenericGripSolver?.Value ?? false)
                sb.Append("  grip solver: DISABLED (isolation)");
            else
                sb.Append("  grip solver: enabled");
            if (BundyPlugin.ModConfig != null)
                sb.Append(BundyPlugin.ModConfig.ChainsawHeadAimEnabled?.Value ?? true
                    ? "  head aim: enabled"
                    : "  head aim: DISABLED");
            if (BundyPlugin.ModConfig?.ChainsawForceSprint?.Value ?? false)
                sb.Append("  sprint: FORCED");
            return sb.ToString();
        }

        // Issue #205: the overhead sprint. Forcing it separates "does the gait read" from "does the AI
        // pick it sensibly" — the two judgements the in-game test has to make, and the ones that are
        // impossible to tell apart if a wrong entry condition is the only way to see the clip.
        public static bool GetForceSprint()
        {
            return BundyPlugin.ModConfig?.ChainsawForceSprint?.Value ?? false;
        }

        public static void SetForceSprint(bool enabled)
        {
            if (BundyPlugin.ModConfig?.ChainsawForceSprint == null)
                return;
            BundyPlugin.ModConfig.ChainsawForceSprint.Value = enabled;
        }

        /// <summary>
        /// Issue #272: arm one owner-authoritative drop jump so the traversal arc and Jump_Fall
        /// bundle receipt can be tested without waiting for the normal path policy to select it.
        /// The AI consumes the request only after all root-authority and safe-landing guards pass.
        /// </summary>
        public static string ForceDropJump()
        {
            if (BundyPlugin.ModConfig?.DropDownFollowForceTrigger == null)
                return "drop jump: config unavailable.";

            int liveInstances = UnityEngine.Object.FindObjectsOfType<BundyAIBase>().Length;
            BundyPlugin.ModConfig.DropDownFollowForceTrigger.Value = true;
            return $"drop jump: armed for the next eligible owner-authoritative Bundy; live={liveInstances}.";
        }

        public static float GetSprintSpeed()
        {
            return BundyPlugin.ModConfig?.ChainsawSprintSpeed?.Value ?? 7.65f;
        }

        /// <summary>
        /// Dialable, but the gait is authored FOR 7.65 m/s and its clips are in-place: the stance foot
        /// is stationary in armature space and only cancels to zero world skate at that exact speed.
        /// Moving this slides his feet in proportion. Present so the slide can be SEEN on demand, not
        /// because the number is a preference.
        /// </summary>
        public static void SetSprintSpeed(float metresPerSecond)
        {
            if (BundyPlugin.ModConfig?.ChainsawSprintSpeed == null)
                return;
            BundyPlugin.ModConfig.ChainsawSprintSpeed.Value = Mathf.Clamp(metresPerSecond, 4.25f, 12f);
        }

        public static float GetElbowsOutDegrees()
        {
            return BundyPlugin.ModConfig?.ChainsawArmElbowsOutDegrees?.Value ?? 0f;
        }

        public static void SetElbowsOutDegrees(float degrees)
        {
            if (BundyPlugin.ModConfig?.ChainsawArmElbowsOutDegrees == null)
                return;
            BundyPlugin.ModConfig.ChainsawArmElbowsOutDegrees.Value = Mathf.Clamp(degrees, -30f, 30f);
        }

        public static float GetClavicleProtractLeftDegrees()
        {
            return BundyPlugin.ModConfig?.ChainsawClavicleProtractLeftDegrees?.Value ?? 0f;
        }

        public static void SetClavicleProtractLeftDegrees(float degrees)
        {
            if (BundyPlugin.ModConfig?.ChainsawClavicleProtractLeftDegrees == null)
                return;
            BundyPlugin.ModConfig.ChainsawClavicleProtractLeftDegrees.Value =
                Mathf.Clamp(degrees, -30f, 30f);
        }

        public static float GetClavicleProtractRightDegrees()
        {
            return BundyPlugin.ModConfig?.ChainsawClavicleProtractRightDegrees?.Value ?? 0f;
        }

        public static void SetClavicleProtractRightDegrees(float degrees)
        {
            if (BundyPlugin.ModConfig?.ChainsawClavicleProtractRightDegrees == null)
                return;
            BundyPlugin.ModConfig.ChainsawClavicleProtractRightDegrees.Value =
                Mathf.Clamp(degrees, -30f, 30f);
        }

        public static bool GetHeadAimEnabled()
        {
            return BundyPlugin.ModConfig?.ChainsawHeadAimEnabled?.Value ?? true;
        }

        public static void SetHeadAimEnabled(bool enabled)
        {
            if (BundyPlugin.ModConfig?.ChainsawHeadAimEnabled == null)
                return;
            BundyPlugin.ModConfig.ChainsawHeadAimEnabled.Value = enabled;
        }

        public static float GetHeadAimYawClampDegrees()
        {
            return BundyPlugin.ModConfig?.ChainsawHeadAimYawClampDegrees?.Value ?? 60f;
        }

        public static void SetHeadAimYawClampDegrees(float degrees)
        {
            if (BundyPlugin.ModConfig?.ChainsawHeadAimYawClampDegrees == null)
                return;
            BundyPlugin.ModConfig.ChainsawHeadAimYawClampDegrees.Value = Mathf.Clamp(degrees, 0f, 120f);
        }

        public static float GetHeadAimPitchClampDegrees()
        {
            return BundyPlugin.ModConfig?.ChainsawHeadAimPitchClampDegrees?.Value ?? 25f;
        }

        public static void SetHeadAimPitchClampDegrees(float degrees)
        {
            if (BundyPlugin.ModConfig?.ChainsawHeadAimPitchClampDegrees == null)
                return;
            BundyPlugin.ModConfig.ChainsawHeadAimPitchClampDegrees.Value = Mathf.Clamp(degrees, 0f, 60f);
        }

        public static float[] GetSkinTint()
        {
            return new[]
            {
                BundyPlugin.ModConfig?.ChainsawBodySkinTintR?.Value ?? 0.72f,
                BundyPlugin.ModConfig?.ChainsawBodySkinTintG?.Value ?? 0.81f,
                BundyPlugin.ModConfig?.ChainsawBodySkinTintB?.Value ?? 0.91f,
            };
        }

        public static void SetSkinTint(float r, float g, float b)
        {
            if (BundyPlugin.ModConfig?.ChainsawBodySkinTintR == null)
                return;
            BundyPlugin.ModConfig.ChainsawBodySkinTintR.Value = Mathf.Clamp(r, 0f, 3f);
            BundyPlugin.ModConfig.ChainsawBodySkinTintG.Value = Mathf.Clamp(g, 0f, 3f);
            BundyPlugin.ModConfig.ChainsawBodySkinTintB.Value = Mathf.Clamp(b, 0f, 3f);
            ReapplyTints();
        }

        public static float[] GetHairTint()
        {
            return new[]
            {
                BundyPlugin.ModConfig?.ChainsawHairTintR?.Value ?? 0.70f,
                BundyPlugin.ModConfig?.ChainsawHairTintG?.Value ?? 0.85f,
                BundyPlugin.ModConfig?.ChainsawHairTintB?.Value ?? 1.00f,
            };
        }

        public static void SetHairTint(float r, float g, float b)
        {
            if (BundyPlugin.ModConfig?.ChainsawHairTintR == null)
                return;
            BundyPlugin.ModConfig.ChainsawHairTintR.Value = Mathf.Clamp(r, 0f, 3f);
            BundyPlugin.ModConfig.ChainsawHairTintG.Value = Mathf.Clamp(g, 0f, 3f);
            BundyPlugin.ModConfig.ChainsawHairTintB.Value = Mathf.Clamp(b, 0f, 3f);
            ReapplyTints();
        }

        /// <summary>
        /// #374 Phase 0 gate: force blood decals onto every live Bundy's chest, left forearm and
        /// head. Answers "can his skin receive an HDRP decal at all" by making one visible,
        /// which gates the whole Bundy half of #374.
        ///
        /// Same shape as the tint setters — reach every live instance and re-apply — so the
        /// Bundy debug tab binds it exactly like SetSkinTint. A Ctrl+Alt+F3 hotkey did this
        /// first and did not work in practice; the "Force Decal Probe" button replaced it.
        ///
        /// RENAME HAZARD: Y4NGZDebugTools binds this, <see cref="GetSkinDecalStatus"/> and
        /// <see cref="ReapplySkinMaterials"/> by string, and that project is gitignored, so a
        /// rename here disables the buttons with no compile error and no diff to review.
        /// </summary>
        public static string ForceSkinDecalProbe()
        {
            try
            {
                BundyAIBase[] instances = UnityEngine.Object.FindObjectsOfType<BundyAIBase>();
                if (instances.Length == 0)
                    return "decal probe: no live Bundy instances.";

                int placed = 0;
                foreach (BundyAIBase instance in instances)
                    placed += instance.ForceSkinDecalProbe();

                return $"decal probe: {placed} decal(s) across {instances.Length} Bundy instance(s); "
                       + "check the log for the per-material verdict.";
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BundyDebugApi] Decal probe failed: " + e.Message);
                return "decal probe failed: " + e.Message;
            }
        }

        /// <summary>
        /// #374 Phase 0: the current decal-reception verdict for every live Bundy's skin
        /// materials, short enough for a GUI label. The long per-material audit stays in the log.
        /// Read-only — it reports what the last skin-material pass concluded, it does not re-run it.
        /// </summary>
        public static string GetSkinDecalStatus()
        {
            try
            {
                BundyAIBase[] instances = UnityEngine.Object.FindObjectsOfType<BundyAIBase>();
                if (instances.Length == 0)
                    return "decal gate: no live Bundy instances.";

                var sb = new StringBuilder("decal gate: ");
                for (int i = 0; i < instances.Length; i++)
                {
                    if (i > 0)
                        sb.Append("  |  ");
                    sb.Append(instances[i].SkinDecalVerdict);
                }

                return sb.ToString();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BundyDebugApi] Decal status failed: " + e.Message);
                return "decal gate: status failed - " + e.Message;
            }
        }

        /// <summary>
        /// #374 Phase 0: re-run the skin-material pass on every live Bundy, which re-asserts decal
        /// reception on the per-instance copies and re-logs the full per-material audit. Useful
        /// when something has replaced the materials since spawn, and as a way to reprint the
        /// verdict on demand without respawning him.
        /// </summary>
        public static string ReapplySkinMaterials()
        {
            try
            {
                BundyAIBase[] instances = UnityEngine.Object.FindObjectsOfType<BundyAIBase>();
                if (instances.Length == 0)
                    return "skin materials: no live Bundy instances.";

                ReapplyTints();
                return $"skin materials re-applied on {instances.Length} instance(s). "
                       + GetSkinDecalStatus();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BundyDebugApi] Skin material re-apply failed: " + e.Message);
                return "skin materials: re-apply failed - " + e.Message;
            }
        }

        public static string CopyValuesSummary()
        {
            float[] skin = GetSkinTint();
            float[] hair = GetHairTint();
            return string.Format(
                CultureInfo.InvariantCulture,
                "HeadAim={0}  YawClamp={1:F1}  PitchClamp={2:F1}  ElbowsOut={3:F1}  ClavProtractL={4:F1}  ClavProtractR={5:F1}  Skin=({6:F2},{7:F2},{8:F2})  Hair=({9:F2},{10:F2},{11:F2})",
                GetHeadAimEnabled(), GetHeadAimYawClampDegrees(), GetHeadAimPitchClampDegrees(),
                GetElbowsOutDegrees(), GetClavicleProtractLeftDegrees(),
                GetClavicleProtractRightDegrees(), skin[0], skin[1], skin[2],
                hair[0], hair[1], hair[2]);
        }

        private static void ReapplyTints()
        {
            try
            {
                foreach (BundyAIBase instance in UnityEngine.Object.FindObjectsOfType<BundyAIBase>())
                    instance.ReapplyCosmeticTints();
            }
            catch (Exception e)
            {
                Debug.LogWarning("[BundyDebugApi] Tint re-apply failed: " + e.Message);
            }
        }
    }
}
