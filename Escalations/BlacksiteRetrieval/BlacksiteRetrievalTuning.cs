using UnityEngine;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal static class BlacksiteRetrievalTuning
    {
        private const float BaseAcceleration = 18f;
        private const float MinAccelerationMultiplier = 0.94f;
        private const float MaxAccelerationMultiplier = 1.06f;
        private const float MinAcquisitionRampSeconds = 0.35f;
        private const float MaxAcquisitionRampSeconds = 0.9f;
        private const float MinBurstCadenceWindowSeconds = 2f;
        private const float MaxBurstCadenceWindowSeconds = 3f;

        internal const float AlertedAcquisitionRampMultiplier = 0.7f;
        internal const float AcquisitionRampLineOfSightGraceSeconds = 0.5f;

        // #316: how the hosted MX16A4 world model sits in the soldier's BlacksiteWeaponRoot socket.
        // These are eyeballed against the soldier rig - the humanoid clips already pose the hands
        // around the authored weapon volume, so identity is the correct starting point and any
        // adjustment here is a fit correction against a live capture, not a computed value.
        internal static readonly Vector3 HostedWeaponLocalPosition = Vector3.zero;
        internal static readonly Vector3 HostedWeaponLocalEuler = Vector3.zero;
        internal const float HostedWeaponUniformScale = 1f;

        // A soldier's rifle reads at a slightly smaller flash than a player's, because it is seen
        // from across a room rather than from behind the sights.
        internal const float HostedWeaponMuzzleFlashScale = 0.8f;

        // #314: a soldier commits to burst or single fire for a couple of seconds at a time
        // instead of re-rolling per trigger pull, so his cadence reads as a decision.
        internal static float GetBurstCadenceWindowSeconds(int animationSeed)
        {
            return Mathf.Lerp(
                MinBurstCadenceWindowSeconds,
                MaxBurstCadenceWindowSeconds,
                GetUnit(animationSeed, 127));
        }

        // How many bursts he will take from one spot before he shuffles off it.
        internal static int GetBurstsBeforeReposition(int animationSeed)
        {
            return 2 + Mathf.RoundToInt(GetUnit(animationSeed, 131));
        }

        internal static int CreateAnimationSeed(ulong squadId, int memberIndex)
        {
            unchecked
            {
                uint hash = (uint)squadId ^ (uint)(squadId >> 32);
                hash ^= (uint)(memberIndex + 1) * 0x9E3779B9u;
                hash = Mix(hash);
                return hash == 0u ? memberIndex + 1 : (int)hash;
            }
        }

        internal static float GetDeploymentDelay(
            BlacksiteSoldierTacticalRole tacticalRole,
            int memberIndex,
            int animationSeed)
        {
            return .05f + Mathf.Max(0, memberIndex) * .08f;
        }

        internal static float GetMovementAcceleration(int animationSeed)
        {
            return BaseAcceleration * Mathf.Lerp(
                MinAccelerationMultiplier,
                MaxAccelerationMultiplier,
                GetUnit(animationSeed, 23));
        }

        // Each soldier tracks a target for his own beat before the first shot, so a squad opens
        // fire in a ragged sweep instead of a single frame-perfect volley.
        internal static float GetAcquisitionRampSeconds(int animationSeed)
        {
            return Mathf.Lerp(
                MinAcquisitionRampSeconds,
                MaxAcquisitionRampSeconds,
                GetUnit(animationSeed, 89));
        }

        internal static float GetAnimationPhase(int animationSeed)
        {
            return GetUnit(animationSeed, 53);
        }

        /// <summary>
        /// #957 S12A U4. The idle facing sweep's per-target hold. The value, the 3 to 6 s band and
        /// their derivations live in BlacksiteMotionPolicy.FacingSweepHold, which is pinned
        /// headlessly by tests/BlacksiteMotionStallRegression; this file cannot be compiled there
        /// because GetDeploymentDelay needs BlacksiteSoldierTacticalRole.
        /// </summary>
        internal const float SweepHoldMinSeconds = BlacksiteMotionPolicy.SweepHoldMinSeconds;
        internal const float SweepHoldMaxSeconds = BlacksiteMotionPolicy.SweepHoldMaxSeconds;

        internal static float GetFacingSweepHoldSeconds(int animationSeed, int targetIndex)
            => BlacksiteMotionPolicy.FacingSweepHold(animationSeed, targetIndex);

        /// <summary>
        /// #957 S12A U4. The step a member takes through its sweep list, seeded and coprime with the
        /// target count so two members cannot shadow each other round the room. Derivation beside
        /// BlacksiteMotionPolicy.FacingSweepStride.
        /// </summary>
        internal static int GetFacingSweepStride(int animationSeed, int targetCount)
            => BlacksiteMotionPolicy.FacingSweepStride(animationSeed, targetCount);

        private static float GetSignedUnit(int seed, int salt)
        {
            return GetUnit(seed, salt) * 2f - 1f;
        }

        // #956 S12A U3: one hash in the runtime. It lives in BlacksiteMotionPolicy so the seeded
        // values drawn from it can be pinned headlessly.
        private static float GetUnit(int seed, int salt) => BlacksiteMotionPolicy.SeededUnit(seed, salt);

        private static uint Mix(uint value) => BlacksiteMotionPolicy.Mix(value);
    }
}
