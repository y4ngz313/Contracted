using System;

namespace Y4NGZCompany.Escalations.Congregation
{
    // Pure distance/transition rules shared by the server decision and local visual interpolation.
    internal static class CongregantLocomotion
    {
        internal const float RunSpeed = 3.6f;
        internal const float WalkDistance = 3.5f;
        internal const float DistanceBuffer = 3f;
        internal const float BlendSeconds = 1f;
        // Clip cycles of the v12 set at 30 fps. The nested walk/run trees share normalized time.
        internal const float WalkClipSeconds = 40f / 30f;
        internal const float FleeClipSeconds = 0.8f;
        internal const float RunClipSeconds = 22f / 30f;
        // Footsteps follow the clip at its own playback speed: a GaitRate above 1 (walk plays
        // 1.4-2.0x, flee ~2.5x to keep planted feet planted) does not speed steps up, so the
        // cadence stays vanilla's two steps per clip cycle.
        internal const float FootfallMaxGaitRate = 1f;
        // One sample (a hitch frame) advances the clock by at most a quarter cycle, so it lands
        // one step at most, drops the steps it skipped and never bursts to catch up.
        internal const float FootfallMaxSampleStep = .25f;
        // Planted-foot travel at the 2.62 m runtime height, in m/s.
        internal const float WalkStrideSpeed = 1.080f;
        internal const float BlackWalkStrideSpeed = 1.172f;
        internal const float RedRunStrideSpeed = 3.108f;
        internal const float BlackRunStrideSpeed = 3.420f;
        internal const float WhiteRunStrideSpeed = 3.242f;

        internal static bool WantsRun(bool hasDestination, float distance, bool wasRunning, float attackRange)
        {
            if (!hasDestination || float.IsNaN(distance) || float.IsInfinity(distance)) return false;
            float walkDistance = Math.Max(WalkDistance, Math.Max(0, attackRange) + 1.5f);
            return wasRunning ? distance > walkDistance : distance >= walkDistance + DistanceBuffer;
        }

        internal static float Advance(float progress, bool run, float delta)
        {
            float step = Math.Max(0, delta) / BlendSeconds;
            return run ? Math.Min(1, progress + step) : Math.Max(0, progress - step);
        }

        internal static float Blend(float progress)
        {
            float t = Math.Max(0, Math.Min(1, progress));
            return t * t * t * (10 + t * (-15 + 6 * t));
        }

        /// <summary>Blended cycle duration: the walk (or flee) clip blending to the run clip.</summary>
        internal static float CycleSeconds(float walkSeconds, float runBlend)
        {
            float t = Math.Max(0, Math.Min(1, runBlend));
            return walkSeconds + (RunClipSeconds - walkSeconds) * t;
        }

        /// <summary>Advances a footfall clock (in cycles, [0,1)) by one sample and reports whether
        /// it crossed a footfall at 0 or 0.5. Invalid samples leave the phase untouched.</summary>
        internal static bool AdvanceFootfall(ref float phase, float delta, float cycleSeconds, float gaitRate)
        {
            if (!(delta > 0) || !(cycleSeconds > 0) || !(gaitRate > 0))
                return false;

            float step = Math.Min(FootfallMaxSampleStep, delta * Math.Min(FootfallMaxGaitRate, gaitRate) / cycleSeconds);
            float next = phase + step;
            bool footfall = Math.Floor(next * 2) > Math.Floor(phase * 2);
            phase = next - (float)Math.Floor(next);
            return footfall;
        }

        /// <summary>World speed the blended cycle covers at playback rate 1: blended stride length
        /// over blended cycle duration, so the rate matching it keeps planted feet planted.</summary>
        internal static float GaitReferenceSpeed(float walkSpeed, float walkSeconds, float runSpeed, float runBlend)
        {
            float t = Math.Max(0, Math.Min(1, runBlend));
            float seconds = CycleSeconds(walkSeconds, runBlend);
            return (walkSpeed * walkSeconds + (runSpeed * RunClipSeconds - walkSpeed * walkSeconds) * t) / seconds;
        }
    }
}
