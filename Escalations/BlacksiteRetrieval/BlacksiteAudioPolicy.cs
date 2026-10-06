using System.Collections.Generic;
using UnityEngine;

using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// #929. Every gain and range the Blacksite squad's own audio uses, with the measurement each
    /// one is derived from. Nothing here is a taste multiplier: a Blacksite source is either
    /// presented at the level it used to be presented at (the baton cues, which changed routing but
    /// not intent) or at a measured ceiling (the rifle shot, which was quiet because its clips sat
    /// below full scale and nothing scaled them).
    /// </summary>
    internal static class BlacksiteAudioPolicy
    {
        // ---- The reference distance ---------------------------------------------------------
        // Two metres. It is BlacksiteCombatPolicy.BatonAttackRange (2.17 m) rounded down, so it is
        // the distance a baton hit actually happens at, and it is the near field that
        // ContractSpatialAudio.ApplyFacilityCarryProfile already uses for every contract prop.
        internal const float ReferenceDistance = 2f;

        // ---- Baton cue and loop ranges (unchanged from the pre-#929 sources) ----------------
        internal const float BatonCueMaxDistance = 15f;
        internal const float BatonLoopMaxDistance = 8f;
        internal const float BatonCueMinDistance = 2f;
        internal const float BatonLoopMinDistance = 1.5f;

        // ---- The baton cue's near-field plateau (#929, 2026-09-15 playtest) --------------------
        // Three metres of FULL gain before the carry fade starts.
        //
        // AudioRolloffMode.Custom evaluates its curve over distance/maxDistance and ignores
        // minDistance entirely (ContractSpatialAudio.ApplyCarryProfileCore), so BatonCueMinDistance
        // above bought the cue nothing: on a 15 m source the shared carry shape is already at 0.50
        // gain by 1.5 m and 0.30 by 3.75 m - the whole melee envelope sits on the steepest part of
        // the curve, which is why the 2026-09-15 host could not hear a baton land in front of him.
        //
        // Three metres is the reach, not a taste value. BlacksiteEngagementGeometry's widest body
        // profile is the Giant row at MaxReach 2.97 m; every other row (Humanoid 2.17 m, crawler
        // 2.07 m) is inside it, so an accepted contact is BY CONSTRUCTION within 2.97 m of the
        // emitter on the soldier. Rounded up to 3.00 m so the whole envelope, and the listener
        // standing at the far edge of it, is on the plateau rather than on the fade. As a fraction
        // of BatonCueMaxDistance this is 3/15 = 0.20, well inside the 0.6 ceiling
        // BuildNearFieldCarryFalloffCurve clamps to, and the far tail keeps its authored shape
        // because the remaining span is compressed rather than flattened.
        //
        // This applies to the WHOLE cue source, not only to the hit, and that is intended. Every clip
        // BlacksiteBatonEffects._cues plays - PowerOn and PowerOff (Power), Impact (Impact), Swing and
        // the embedded draw and stow cues (#1262 P6; the soldier's baton-phase writer in
        // BlacksiteSoldierAI.Equipment.cs) - is a weapon event happening in a soldier's hand at the
        // reach measured above, none of them a room sound. Splitting Impact
        // onto its own source to give only it the plateau would also split the filter chain: the
        // file's own note records that OccludeAudio and the low-pass/reverb filters it drives are
        // per GameObject, so two sources are two occlusion solves for one event at one point, and
        // the #988 S2.3 audit that counts one Impact per accepted contact per peer would be
        // counting across two emitters. The loop keeps the facility profile on its own object.
        internal const float BatonCueNearFieldPlateau = 3f;

        // ---- The carry curve at the reference distance ---------------------------------------
        // ContractSpatialAudio.BuildCarryFalloffCurve is normalised over [0, maxDistance], so its
        // value at 2 m depends on the source's own range. Evaluated (and asserted in
        // BlacksiteWeaponAudioChecks against the real curve):
        //   cue  2/15 = 0.13333 -> 0.427517
        //   loop 2/8  = 0.25000 -> 0.300000 (a keyframe, exactly)
        // Unity's Linear rolloff, which both sources used before #929, over the same 2 m from the
        // engine default minDistance of 1: cue (15-2)/(15-1) = 0.928571, loop (8-2)/(8-1) = 0.857143.
        internal const float BatonCueCarryAtReference = 0.427517f;
        internal const float BatonLoopCarryAtReference = 0.3f;

        // ---- Baton cue gain -------------------------------------------------------------------
        // Pre-#929: AudioSource.volume 0.55 on Linear rolloff. The requirement is that the NEW hit
        // reads at least as loud at 2 m as the old zap did, so the gain is the level ratio the
        // routing change costs, times the loudness ratio between the two clips:
        //
        //   0.55 * (0.928571 / 0.427517) * 10^((-22.0 - -21.0)/20) = 0.55 * 2.171891 * 0.891251
        //                                                          = 1.0646
        //
        // -22.0 LUFS is the S2.3 zap's integrated loudness, -21.0 LUFS the new hit's, both measured
        // with ffmpeg loudnorm on the source wavs (docs/assets/26-blacksite-equipment-sidecar.md). AudioSource.volume is
        // clamped to 1 by the engine, so the derived 1.0646 lands on 1.0 and the cue is presented
        // 0.54 dB below the old level at 2 m - inside the roughly 1 dB just-noticeable difference,
        // and recovered several times over by the reverb send and the held far tail the carry
        // profile adds. Recorded here rather than rounded away: the profile costs more near-field
        // level on a 15 m source than the source gain can give back.
        //
        // #929 round 2 (2026-09-15): the gain is UNCHANGED at the engine ceiling. The gain was never
        // the defect - the curve was, and BatonCueNearFieldPlateau above is the whole fix. What that
        // costs is stated rather than hidden: the presented level at the 2 m reference goes from
        // 1.0 x 0.427517 = 0.4275 to 1.0 x 1.0, i.e. +7.4 dB of curve, leaving the cue 5.8 dB above
        // the pre-#929 zap's own 0.55 x 0.928571 = 0.5107 (6.8 dB once the new clip's extra 1.0 LU
        // of content is counted). That is deliberate and it is the CURVE's number, not a taste
        // multiplier: equal presentation on the plateau would be a gain of 0.4551, and the
        // 2026-09-15 host reported a baton landing in front of him as inaudible. Every derivation
        // above still holds unchanged - only the distance the curve is evaluated on moved.
        internal const float BatonCueVolume = 1f;

        // ---- Baton loop gain ------------------------------------------------------------------
        // Same rule, no clip change: 0.16 * (0.857143 / 0.300000) = 0.457143. The electric hum is
        // presented at exactly the level it was, on the new curve.
        internal const float BatonLoopVolume = 0.457f;

        // ---- Weapon carry range ---------------------------------------------------------------
        // 36 m is WhistleblowerEnemyAI.ConfigureSpatialAudio's voice range: a human shout that has
        // to stay trackable across a facility. An unsuppressed rifle report is never quieter than a
        // shout, so 36 m is a floor and not a target. When vanilla's own ShotgunItem prefab can be
        // read at bind, its gunShootAudio range is the game's own answer for a gun going off
        // indoors and wins outright; BlacksiteSoldierAI logs which of the two it used.
        internal const float WeaponVoiceCarryFloor = 36f;
        internal const float WeaponSourceMinDistance = 2f;
        internal const float WeaponSourceSpread = 25f;
        internal const int WeaponSourcePriority = 96;

        internal static float ResolveWeaponMaxDistance(float vanillaGunMaxDistance)
            => vanillaGunMaxDistance > 0f
                ? Mathf.Max(WeaponVoiceCarryFloor, vanillaGunMaxDistance)
                : WeaponVoiceCarryFloor;

        // ---- The shot ceiling -----------------------------------------------------------------
        // -1.0 dBFS. The same headroom the #929 baton clips were mastered to: 16-bit PCM and Unity's
        // ADPCM import both rail at 0 dBFS, an ADPCM reconstruction can overshoot its input, and a
        // sample peak at full scale hides inter-sample peaks above it. One decibel is the
        // conventional margin that keeps both off the rail.
        internal const float ShotPeakCeilingDb = -1f;
        internal static readonly float ShotPeakCeiling = Mathf.Pow(10f, ShotPeakCeilingDb / 20f); // 0.891251

        // A clip whose peak is already within this of the ceiling is played as it is: copying it
        // would cost memory and a decode for a gain change below audibility.
        private const float ShotGainDeadband = 0.02f;

        private static readonly Dictionary<AudioClip, AudioClip> ScaledShots = new Dictionary<AudioClip, AudioClip>();
        private static readonly HashSet<AudioClip> Unscalable = new HashSet<AudioClip>();

        internal static void ResetRunState()
        {
            // Bundle clips and their scaled copies both belong to the previous round's asset
            // lifetime; a cached copy of a destroyed clip is a destroyed Unity object.
            ScaledShots.Clear();
            Unscalable.Clear();
        }

        /// <summary>
        /// Measure a clip's linear sample peak and RMS. False when the clip cannot be read (a
        /// streaming or still-loading clip), which is the caller's signal to play it unscaled.
        /// </summary>
        internal static bool Measure(AudioClip clip, out float peak, out float rms)
        {
            peak = 0f; rms = 0f;
            if (clip == null || clip.samples <= 0 || clip.channels <= 0)
                return false;
            if (clip.loadState != AudioDataLoadState.Loaded && !clip.LoadAudioData())
                return false;

            var data = new float[clip.samples * clip.channels];
            if (!clip.GetData(data, 0))
                return false;

            double sum = 0d;
            for (int i = 0; i < data.Length; i++)
            {
                float magnitude = data[i] < 0f ? -data[i] : data[i];
                if (magnitude > peak) peak = magnitude;
                sum += (double)data[i] * data[i];
            }
            rms = data.Length > 0 ? (float)System.Math.Sqrt(sum / data.Length) : 0f;
            return true;
        }

        /// <summary>
        /// #929. The shot clip, pre-scaled so its peak sits on the -1 dBFS ceiling, cached per
        /// source clip. This is what makes the rifle louder without a blanket PlayOneShot
        /// multiplier: the gain is exactly the distance between where the authored clip's own peak
        /// sits and the ceiling, measured, and it never places a peak above the ceiling.
        /// </summary>
        internal static AudioClip NormalizedShot(AudioClip clip)
        {
            if (clip == null)
                return null;
            if (ScaledShots.TryGetValue(clip, out AudioClip cached))
            {
                if (cached != null)
                    return cached;
                ScaledShots.Remove(clip);
            }
            if (Unscalable.Contains(clip))
                return clip;

            if (!Measure(clip, out float peak, out float rms) || peak <= 0.0001f)
            {
                Unscalable.Add(clip);
                return clip;
            }

            float gain = ShotPeakCeiling / peak;
            LogShotMeasurement(clip, peak, rms, gain);
            if (Mathf.Abs(gain - 1f) <= ShotGainDeadband)
            {
                Unscalable.Add(clip);
                return clip;
            }

            var data = new float[clip.samples * clip.channels];
            if (!clip.GetData(data, 0))
            {
                Unscalable.Add(clip);
                return clip;
            }
            for (int i = 0; i < data.Length; i++)
                data[i] = Mathf.Clamp(data[i] * gain, -ShotPeakCeiling, ShotPeakCeiling);

            var scaled = AudioClip.Create(clip.name + " (#929 -1dBFS)", clip.samples, clip.channels, clip.frequency, stream: false);
            scaled.SetData(data, 0);
            ScaledShots[clip] = scaled;
            return scaled;
        }

        private static void LogShotMeasurement(AudioClip clip, float peak, float rms, float gain)
        {
            if (!BlacksiteDiagnostics.Enabled)
                return;
            BlacksiteDiagnostics.Log(
                "Audio",
                $"event=shotClip, clip={clip.name}, channels={clip.channels}, frequency={clip.frequency}, "
                + $"samples={clip.samples}, peak={Db(peak):0.00}, rms={Db(rms):0.00}, gain={Db(gain):+0.00;-0.00}, "
                + $"scaledPeak={Db(peak * gain):0.00}, ceiling={ShotPeakCeilingDb:0.00}");
        }

        private static float Db(float amplitude) => amplitude <= 0f ? -999f : 20f * Mathf.Log10(amplitude);

        /// <summary>
        /// Route the soldier's weapon source onto the facility carry path and report, in one line,
        /// what the prefab serialized before and what the profile left behind. Returns the
        /// propagation driver the caller must Tick.
        /// </summary>
        internal static ContractAudioPropagation ConfigureWeaponSource(AudioSource source, int memberIndex)
        {
            if (source == null)
                return null;

            float vanillaReference = ReadVanillaGunRange(out string vanillaName);
            float maxDistance = ResolveWeaponMaxDistance(vanillaReference);

            string mixerBefore = source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.name : "none";
            AudioRolloffMode rolloffBefore = source.rolloffMode;
            float minBefore = source.minDistance, maxBefore = source.maxDistance, volumeBefore = source.volume;
            bool occludeBefore = source.GetComponent<OccludeAudio>() != null;

            ContractAudioPropagation propagation = ContractSpatialAudio.ApplyFacilityCarryProfile(
                source, maxDistance, WeaponSourceMinDistance, WeaponSourceSpread, WeaponSourcePriority);

            OccludeAudio occlude = source.GetComponent<OccludeAudio>();
            BlacksiteDiagnostics.Log(
                "Audio",
                $"event=weaponSource, member={memberIndex}, "
                + $"mixerBefore={mixerBefore}, rolloffBefore={rolloffBefore}, minBefore={minBefore:0.00}, maxBefore={maxBefore:0.00}, volumeBefore={volumeBefore:0.00}, occludeBefore={occludeBefore}, "
                + $"mixerAfter={(source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.name : "none")}, rolloffAfter={source.rolloffMode}, "
                + $"minAfter={source.minDistance:0.00}, maxAfter={source.maxDistance:0.00}, volumeAfter={source.volume:0.00}, "
                + $"reverb={(occlude != null && occlude.useReverb)}, vanillaGun={vanillaName}, vanillaGunMax={vanillaReference:0.00}, floor={WeaponVoiceCarryFloor:0.00}");
            return propagation;
        }

        /// <summary>
        /// The vanilla reference for "how far a gun going off carries indoors": the ShotgunItem
        /// prefab's own shoot source. Zero when no shotgun prefab is loaded, which is the caller's
        /// signal to fall back to the shout floor.
        /// </summary>
        private static float ReadVanillaGunRange(out string name)
        {
            name = "none";
            ShotgunItem[] shotguns = Resources.FindObjectsOfTypeAll<ShotgunItem>();
            if (shotguns == null)
                return 0f;
            for (int i = 0; i < shotguns.Length; i++)
            {
                ShotgunItem shotgun = shotguns[i];
                if (shotgun == null || shotgun.gunShootAudio == null)
                    continue;
                name = shotgun.name;
                return shotgun.gunShootAudio.maxDistance;
            }
            return 0f;
        }
    }
}
