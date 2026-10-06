namespace Y4NGZCompany.Experience.Diagnostics
{
    /// <summary>
    /// #1087: how long a monitored input flag may stay asserted before
    /// <see cref="LocalInputStateWatchdog"/> calls it stuck.
    ///
    /// <para>Split out of the watchdog because that type reads <c>PlayerControllerB</c> and binds a
    /// BepInEx config, so no headless suite can link it; everything here is plain BCL arithmetic
    /// and is exercised by <c>tests/ModuleLifecycleRegression</c>.</para>
    ///
    /// <para>The rule has two halves:</para>
    /// <list type="bullet">
    /// <item>A seated operator station (the CCTV monitor desk) legitimately holds
    /// <c>disableLookInput</c>, <c>disableMoveInput</c>, <c>disableInteract</c> and
    /// <c>inSpecialInteractAnimation</c> together for the whole session — 234s in the 2026-09-16
    /// playtest log — so while <b>all four</b> are asserted those four flags get
    /// <see cref="SeatedStationStuckSeconds"/> instead of <see cref="StuckSeconds"/>. This is a
    /// mask test only: nothing here knows or asks which plugin seated the player.</item>
    /// <item>Any partial subset keeps <see cref="StuckSeconds"/>, because a single flag left
    /// asserted after the station releases the player is the real defect. Its own hold timer
    /// started at the top of the session, so the grace is measured from the moment the complete
    /// mask broke instead: the leak reports 3s after the drop, not instantly on the frame the
    /// other three clear (they need not clear in one frame) and not 600s later.</item>
    /// </list>
    /// </summary>
    internal static class LocalInputStuckThreshold
    {
        /// <summary>Seconds a monitored flag may stay set before it counts as stuck.</summary>
        internal const float StuckSeconds = 3f;

        /// <summary>
        /// Seconds the four seated-station flags may stay set <i>together</i> before they count as
        /// stuck. Long enough that no plausible station session trips it, short enough that a
        /// session that never ends is still eventually on record.
        /// </summary>
        internal const float SeatedStationStuckSeconds = 600f;

        /// <summary>True when every bit of <paramref name="seatedStationMask"/> is asserted.</summary>
        internal static bool IsSeatedStationMask(int assertedMask, int seatedStationMask) =>
            seatedStationMask != 0 && (assertedMask & seatedStationMask) == seatedStationMask;

        /// <summary>
        /// The hold threshold for one flag. Only a seated-station flag, and only while the whole
        /// seated mask is asserted, gets the long one.
        /// </summary>
        internal static float ThresholdSeconds(bool seatedStationFlag, bool seatedStationMaskComplete) =>
            seatedStationFlag && seatedStationMaskComplete ? SeatedStationStuckSeconds : StuckSeconds;

        /// <summary>
        /// Whether a flag held for <paramref name="heldSeconds"/> is reportable now.
        /// <paramref name="secondsSinceSeatedRelease"/> is the time since the complete seated mask
        /// last broke while this flag stayed asserted, and is <c>float.PositiveInfinity</c> when no
        /// seated session is behind it.
        /// </summary>
        internal static bool IsStuck(
            bool seatedStationFlag,
            bool seatedStationMaskComplete,
            float heldSeconds,
            float secondsSinceSeatedRelease)
        {
            if (heldSeconds < ThresholdSeconds(seatedStationFlag, seatedStationMaskComplete))
                return false;

            // Post-release grace: the leaked flag's own timer still points at the start of the
            // seated session, so without this the drop frame would report a 234s-old flag
            // instantly and every ordinary station exit that clears the four flags across two
            // frames would warn.
            if (seatedStationFlag
                && !seatedStationMaskComplete
                && secondsSinceSeatedRelease < StuckSeconds)
            {
                return false;
            }

            return true;
        }
    }
}
