using UnityEngine;
using UnityEngine.Rendering.HighDefinition;
using Y4NGZCompany.ShipSystems.Rendering;

namespace Y4NGZCompany.Contracts.ObjectiveHints
{
    /// <summary>
    /// #1197, #1219 G2. The gameplay camera's HDRP <c>CustomPass</c> frame-setting bit while an
    /// objective hint is open. A camera with its own frame settings may have the bit off, and the
    /// hint's outline is a custom pass, so the window holds it on through Core's
    /// <see cref="CameraRenderProfile"/> lease. LethalCCTV's squad ping takes a lease on the same bit of
    /// the same camera; Core counts both and restores the camera's own value only when the last one
    /// ends, so neither package ever switches the bit off under the other, and neither needs to know
    /// the other is installed.
    /// </summary>
    internal static class ObjectiveHintCustomPassBit
    {
        private const string EffectName = "contracted-objective-hint";

        private static CameraRenderLease _lease;

        /// <summary>
        /// A window opened on <paramref name="camera"/>. A camera without HDRP camera data runs its
        /// defaults and is left alone; so is a CCTV feed, which Core refuses.
        /// </summary>
        internal static void Acquire(Camera camera)
        {
            Release();
            if (camera != null)
                CameraRenderProfile.TryAcquireFrameSetting(camera, FrameSettingsField.CustomPass, true, EffectName, out _lease);
        }

        /// <summary>The window closed. Idempotent.</summary>
        internal static void Release()
        {
            if (_lease != null)
                _lease.Dispose();
            _lease = null;
        }
    }
}
