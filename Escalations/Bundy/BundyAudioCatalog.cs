using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Assets;

namespace Y4NGZCompany.Escalations.Bundy
{
    public enum BundyAudioCue
    {
        Punch = 0,
        Grunt = 1,
        Throw = 2,
        Footstep = 3,
        Dialogue = 4,
        ChainsawBasicAttack = 5,
        ChainsawFinisherDamage = 6,
        ChainsawIdle = 7,
        ChainsawRev = 8,
        ChainsawFootstep = 9,
        ChainsawWallImpact = 10,
        Hurt = 11,
        Reaction = 12,
        Search = 13,
        Spot = 14,
        FinisherVocal = 15,
        AttackVocal = 16,
        ChainsawRunningLoop = 17,
        PlayerHit = 18,
        Rant = 19,
        KnockdownScream = 20,
        KnockdownRecover = 21,
        // Issue #209. Per-surface footstep pools. ChainsawFootstep stays as the surface-agnostic
        // fallback (the boots_linoleum set) for the surfaces Lawson has not authored — vanilla
        // ships more surface types than three, and silence is not an acceptable fallback.
        // One cue per surface rather than one cue with a surface argument, because the shuffle bag
        // and the per-clip cooldown in this file are keyed by cue: separate cues give grass its own
        // 5-clip bag and concrete its own 2-clip bag instead of interleaving them into one.
        ChainsawFootstepConcrete = 22,
        ChainsawFootstepGrass = 23,
        ChainsawFootstepMetal = 24,
        // Issue #210. Layered UNDER a PlayerHit, offset past that clip's measured peak.
        Blood = 25,
        // #293. The authored cord-yank -> sputter -> catch -> rev-climb sequence. The running loop
        // defers its audible start to this clip's measured tail and enters at FULL pitch under it —
        // the clip ends at running RPM, so the seam hides in the settle. Replaces the generic
        // ChainsawRev as the pull-start cue (the rev stays for per-swing/acquire blips).
        ChainsawPullStart = 26,
        // #296. Engine-free air/chain swipe fired per swing, detected and played CLIENT-LOCALLY
        // (like the taunt stomps) so each listener's whoosh lands on the arc they can see. The
        // doppler component of the swing comes from the engine layers' pitch, not from this clip —
        // a second motor tone sweeping over the loop reads as a different saw (Lawson, 2026-08-05).
        SawWhoosh = 27,
        // #323. The three phases of the finisher gore soundtrack, assembled at runtime from the
        // paired-action clock rather than baked per variant: the blade ENTERING the body, the
        // sustained bed while it is INSIDE, and the pull-OUT. Cues exist for the shuffle bag and
        // per-clip cooldown only — playback is fully client-local off the paired-finisher
        // begin/release ClientRpcs, so these ids never cross the wire.
        FinisherGoreImpact = 28,
        FinisherGoreDuration = 29,
        FinisherGoreRemove = 30,
    }

    internal static class BundyAudioCatalog
    {
        // Legacy 2026-06 sound set pools. Intentionally never loaded anymore — Lawson's
        // 2026-07-22 direction is that none of the old sounds may play. The cues remain in the
        // enum so RPC ints and call sites stay valid; an empty pool simply produces silence.
        // Exception: Grunt was re-seeded 2026-07-30 with NEW recordings (grunt_01/02 +
        // search_heavy_breath) — the legacy grunt audio itself stays retired.
        internal static AudioClip[] PunchClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] GruntClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] DialogueClips { get; private set; } = Array.Empty<AudioClip>();
        // #277: the ThrowClip slot is gone. It was never assigned after the standard-Bundy throw
        // pipeline was deleted (#274), so BundyAudioCue.Throw already played silence; the enum
        // member stays because cue ids cross the network as ints (PlayBundyAudioClientRpc).
        internal static AudioClip FootstepClip { get; private set; }

        internal static AudioClip[] ChainsawFootstepClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] ChainsawFootstepConcreteClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] ChainsawFootstepGrassClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] ChainsawFootstepMetalClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] BloodClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] ChainsawBasicAttackClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] ChainsawFinisherDamageClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] ChainsawWallImpactClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] HurtClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] ReactionClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] SearchClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] SpotClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] FinisherVocalClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] AttackVocalClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] PlayerHitClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] RantClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] KnockdownScreamClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] KnockdownRecoverClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip ChainsawIdleClip { get; private set; }
        internal static AudioClip ChainsawRunningLoopClip { get; private set; }
        internal static AudioClip ChainsawRevClip { get; private set; }

        // #294 layered engine model. Three seamless loops (engine chug / chain rattle / high RPM
        // scream) that play together on the loop emitters, crossfaded by a throttle value, instead
        // of pitch-warping the single running loop. Pitch changes speed; the layer MIX is what
        // changes character. All three must be present for the layered path — a partial set falls
        // back to the single-clip loop rather than shipping a thinner engine.
        internal static AudioClip ChainsawLayerLowClip { get; private set; }
        internal static AudioClip ChainsawLayerChainClip { get; private set; }
        internal static AudioClip ChainsawLayerHighClip { get; private set; }

        internal static bool HasChainsawLayerSet =>
            ChainsawLayerLowClip != null && ChainsawLayerChainClip != null && ChainsawLayerHighClip != null;

        // #293 pull-start pool. pullstart_fail is loaded but deliberately NOT in the pool yet —
        // a dramatic failed pull needs its own retry choreography (animation + action timing),
        // which is follow-up work; shipping it as a random pool member would play a fail sound
        // over a succeeding pull animation.
        internal static AudioClip[] ChainsawPullStartClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip ChainsawPullStartFailClip { get; private set; }

        // #296 swing whooshes (pool) and #297 cutting resistance (loop layer, not a cue): both
        // authored WITHOUT engine tone so they sum with the real engine instead of replacing it.
        internal static AudioClip[] SawWhooshClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip CutResistanceClip { get; private set; }

        // #323 finisher gore phases. Three pools rather than one: the assembler picks an impact,
        // then a bed sized against the remaining cut time, then a removal led by its own measured
        // peak — three independent choices that a single pool could not express.
        internal static AudioClip[] FinisherGoreImpactClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] FinisherGoreDurationClips { get; private set; } = Array.Empty<AudioClip>();
        internal static AudioClip[] FinisherGoreRemoveClips { get; private set; } = Array.Empty<AudioClip>();

        internal static bool HasFinisherGoreSet =>
            FinisherGoreImpactClips.Length > 0 || FinisherGoreDurationClips.Length > 0 || FinisherGoreRemoveClips.Length > 0;

        // Loose WAV files deployed next to the plugin in bundy-audio/. They take priority over
        // bundle assets so audio can be iterated without rebuilding the SHA-pinned Unity bundle.
        private static Dictionary<string, AudioClip> s_looseClips;

        internal static void Register(AssetBundle bundle)
        {
            LoadLooseClips();

            if (bundle == null && (s_looseClips == null || s_looseClips.Count == 0))
            {
                LogWarning("Audio catalog registration skipped; bundle is null and no loose audio found.");
                return;
            }

            string[] assetNames = bundle != null ? bundle.GetAllAssetNames() : Array.Empty<string>();

            // 2026-07-22 sound set (Assets/Bundy Sounds). Basic-attack damage reuses the
            // finisher-impact pool: the new set ships no dedicated basic-attack chainsaw hit,
            // and the old chainsaw_basic_attack.wav is retired with the rest of the old audio.
            ChainsawFinisherDamageClips = LoadPool(bundle, assetNames, "finisher_impact_01", "finisher_impact_02");
            ChainsawBasicAttackClips = ChainsawFinisherDamageClips;
            // 2026-08-01 (#211): the two-take chainsaw_wall_impact pool is replaced by Lawson's five
            // new wall-hit takes. ONE FLAT POOL rather than surface-gating the three concrete-named
            // takes — his call, 2026-08-01: gating would drop non-concrete hits to a 2-clip pool that
            // audibly alternates, and the concrete takes are generic enough not to read as wrong.
            // 01-03 are the wall_concrete_* takes, 04-05 the generic wall_hit* ones.
            ChainsawWallImpactClips = LoadPool(
                bundle,
                assetNames,
                "wall_hit_01",
                "wall_hit_02",
                "wall_hit_03",
                "wall_hit_04",
                "wall_hit_05");
            // 2026-07-22 loop split: the saw now has a RUNNING loop (normal pursuit) and an IDLE
            // loop (stalled after a wall hit). The old single chainsaw_idle.wav is retired.
            ChainsawIdleClip = LoadClip(bundle, assetNames, "chainsaw_idle_loop");
            ChainsawRunningLoopClip = LoadClip(bundle, assetNames, "chainsaw_running_loop");
            ChainsawRevClip = LoadClip(bundle, assetNames, "chainsaw_rev");
            // #294: the 2026-08-05 layered engine drop (ElevenLabs, converted 48k mono, -1.5 dB
            // ceiling). Loaded as named clips rather than a cue pool — they are loop plumbing, not
            // selectable one-shots.
            ChainsawLayerLowClip = LoadClip(bundle, assetNames, "saw_layer_low");
            ChainsawLayerChainClip = LoadClip(bundle, assetNames, "saw_layer_chain");
            ChainsawLayerHighClip = LoadClip(bundle, assetNames, "saw_layer_high");
            // #293: authored pull-start sequence(s). Pool-loaded so more takes can drop in later.
            ChainsawPullStartClips = LoadPool(bundle, assetNames, "pullstart_catch");
            ChainsawPullStartFailClip = LoadClip(bundle, assetNames, "pullstart_fail");
            // #296/#297: engine-free interaction textures from the same 2026-08-05 drop.
            SawWhooshClips = LoadPool(bundle, assetNames, "saw_whoosh_01", "saw_whoosh_02", "saw_whoosh_03");
            CutResistanceClip = LoadClip(bundle, assetNames, "cut_resistance");
            // #323: the finisher gore drop (Assets/Bundy Sounds/Chainsaw/New/finishers), converted
            // 44.1k stereo -> 48k mono 16-bit (LoadWavFile is 16-bit PCM only and Unity will not
            // spatialize a stereo clip on a 3D source) and gain-matched AFTER the downmix — the
            // mono fold cost these wide takes 2-6 dB of correlated energy, so matching before it
            // would have left the pool 4 dB apart. Impacts/removals sit at the finisher-impact
            // family's -27 LUFS; the bed sits a dB under at -28 because it plays UNDER them.
            FinisherGoreImpactClips = LoadPool(bundle, assetNames, "finisher_gore_impact_01", "finisher_gore_impact_02");
            FinisherGoreDurationClips = LoadPool(
                bundle,
                assetNames,
                "finisher_gore_duration_01",
                "finisher_gore_duration_02",
                "finisher_gore_duration_03");
            FinisherGoreRemoveClips = LoadPool(bundle, assetNames, "finisher_gore_remove_01", "finisher_gore_remove_02");
            // 2026-07-23: pool reduced to the two Adobe-enhanced v2 takes; the old player_hit_03 retired.
            // 2026-07-31 (#189): two gore-impact takes added back as 03/04 from a fresh source folder —
            // unrelated to the retired clip that once held the 03 name. Converted 44.1k stereo -> 48k mono
            // (LoadWavFile is 16-bit PCM only, and Unity will not spatialize a stereo clip on a 3D source)
            // and gain-matched to the pool: 03 lands on -20.8 LUFS, 04 is true-peak-capped at -1.5 dBTP and
            // so lands 3 dB short — the ceiling-wins case the ambience pipeline documents. Its energy is one
            // sharp transient, which integrated LUFS under-weights, so it still reads level with the others.
            PlayerHitClips = LoadPool(bundle, assetNames, "player_hit_01", "player_hit_02", "player_hit_03", "player_hit_04");
            ChainsawFootstepClips = LoadPool(
                bundle,
                assetNames,
                "boots_linoleum_01",
                "boots_linoleum_02",
                "boots_linoleum_03",
                "boots_linoleum_04",
                "boots_linoleum_05");
            // 2026-08-01 (#209): Lawson's per-material footstep drop. Converted 44.1k stereo -> 48k
            // mono (LoadWavFile is 16-bit PCM only and Unity will not spatialize a stereo clip on a
            // 3D source) and level-matched to the boots_linoleum pool's -25.1 dB mean. Both concrete
            // takes land ~1-2 dB short because the -1.5 dBTP ceiling bound before the mean target did,
            // which is the same ceiling-wins case player_hit_04 documents.
            ChainsawFootstepConcreteClips = LoadPool(bundle, assetNames, "footstep_concrete_01", "footstep_concrete_02");
            ChainsawFootstepGrassClips = LoadPool(
                bundle,
                assetNames,
                "footstep_grass_01",
                "footstep_grass_02",
                "footstep_grass_03",
                "footstep_grass_04",
                "footstep_grass_05");
            ChainsawFootstepMetalClips = LoadPool(bundle, assetNames, "footstep_metal_01", "footstep_metal_02", "footstep_metal_03");
            // 2026-08-01 (#210): layered under the impact, not mixed into it. The takes are matched to
            // EACH OTHER at the PlayerHit pool's level and their subordination is a mix decision that
            // lives in code (BundyBloodLayerVolume), so it stays visible and dialable instead of being
            // baked into the asset where nobody can find it again.
            BloodClips = LoadPool(bundle, assetNames, "blood_01", "blood_02");
            // 2026-07-30 voice drop (C3.1 ingest). All loose 48kHz/16-bit/mono WAV, loudness-matched
            // per pool against the pre-existing set. Retired from pools this pass:
            // hurt_dmg/hurt_scream_02/hurt_stand_up_recover (superseded by hurt_01..03 + the
            // knockdown scream/recover system), spot_circle_no_cube (moved to Rant per the drop's
            // folder layout), spot_he_knows (moved to AttackVocal — same line must not live in two
            // pools).
            HurtClips = LoadPool(bundle, assetNames, "hurt_01", "hurt_02", "hurt_03");
            KnockdownScreamClips = LoadPool(bundle, assetNames, "knockdown_scream_01", "knockdown_scream_02");
            KnockdownRecoverClips = LoadPool(bundle, assetNames, "knockdown_recover");
            // Reactions: 2026-07-30 v2 (Adobe enhanced-speech) remasters chosen over v1 originals.
            ReactionClips = LoadPool(bundle, assetNames, "reaction_masked_01", "reaction_masked_02", "reaction_wtf");
            SearchClips = LoadPool(
                bundle,
                assetNames,
                "search_big_strong_man",
                "search_mystery_mr_e",
                "search_where_am_i",
                "search_bigfoot_coming",
                "search_lost_squad",
                "search_where_dave",
                "search_where_is_he",
                "search_hello",
                "search_heavy_breath");
            RantClips = LoadPool(
                bundle,
                assetNames,
                "rant_bad_grade",
                "rant_circle_no_cube",
                "rant_coins",
                "rant_griefed_server",
                "rant_ladies",
                "rant_social_media_star",
                "rant_tone_1",
                "rant_tone_2",
                "rant_y4ngz_rapper");
            SpotClips = LoadPool(
                bundle,
                assetNames,
                "spot_help_you",
                "spot_good_in_the_hood",
                "spot_hey_guys",
                "spot_one_interesting_thing",
                "spot_real_work",
                "spot_star_spot",
                "spot_whats_going_on",
                "spot_whats_going_on_2",
                "spot_who_are_you");
            // Finisher: v2 remasters for deserved_it/evil_laugh/gonna_wanna_see_this; asleep_you_go
            // stays on the v1 WAV because its only v2 source is a lossy MP3.
            FinisherVocalClips = LoadPool(bundle, assetNames, "finisher_asleep_you_go", "finisher_deserved_it", "finisher_evil_laugh", "finisher_gonna_wanna_see_this");
            AttackVocalClips = LoadPool(bundle, assetNames, "attack_box_dirt", "attack_with_me_against_me", "attack_he_knows");
            // Grunt cue revived (was legacy-empty) for non-verbal exertion barks — pull-start yank,
            // etc. (C4.4). search_heavy_breath is deliberately shared with the Search pool: it is a
            // non-verbal breath, not a spoken line, so the no-double-pool rule for lines doesn't apply.
            GruntClips = LoadPool(bundle, assetNames, "grunt_01", "grunt_02", "search_heavy_breath");

            AnalyseImpactEnvelopes();

            LogInfo(
                $"Audio catalog loaded (2026-07 set): " +
                $"chainsawImpacts={ChainsawFinisherDamageClips.Length}, wallImpacts={ChainsawWallImpactClips.Length}, " +
                $"chainsawIdle={(ChainsawIdleClip != null)}, chainsawRunning={(ChainsawRunningLoopClip != null)}, " +
                $"layerSet={(HasChainsawLayerSet ? "low+chain+high" : "incomplete")}, " +
                $"chainsawRev={(ChainsawRevClip != null)}, playerHits={PlayerHitClips.Length}, " +
                $"blood={BloodClips.Length}, " +
                $"chainsawFootsteps={ChainsawFootstepClips.Length} " +
                $"(concrete={ChainsawFootstepConcreteClips.Length}, grass={ChainsawFootstepGrassClips.Length}, " +
                $"metal={ChainsawFootstepMetalClips.Length}), " +
                $"hurt={HurtClips.Length}, knockdownScreams={KnockdownScreamClips.Length}, " +
                $"knockdownRecover={KnockdownRecoverClips.Length}, reactions={ReactionClips.Length}, " +
                $"search={SearchClips.Length}, rant={RantClips.Length}, " +
                $"spot={SpotClips.Length}, finisherVocals={FinisherVocalClips.Length}, attackVocals={AttackVocalClips.Length}, " +
                $"grunts={GruntClips.Length}, legacyPools=empty-by-design.");
        }

        // ---- Issue #210: where the impact actually peaks -------------------------------------
        //
        // The blood take has to start just AFTER the impact's loudest point or it reads as one
        // thicker hit instead of cause and effect. That point is nowhere near constant across the
        // pool — measured on the shipped files it ranges from 0.038 s (player_hit_03, a bare
        // transient) to 0.301 s (player_hit_02, which has a windup) — so a single guessed offset
        // would be ~0.26 s wrong on one end of the pool.
        //
        // Measured at load rather than stored as a table, so swapping a clip cannot leave a stale
        // number behind. It is four short clips scanned once per game session.
        private static float[] s_playerHitPeakSeconds = Array.Empty<float>();
        private static float[] s_bloodOnsetSeconds = Array.Empty<float>();

        // A 5 ms sliding-RMS window, not the single loudest SAMPLE: one stray sample is not what a
        // listener hears as "the hit". On the shipped pool the two agree to within 30 ms anyway.
        private const float ImpactEnvelopeWindowSeconds = 0.005f;

        // The blood takes do not all start at their own beginning — blood_01 becomes audible 15 ms
        // in, blood_02 61 ms in. Subtracting each take's own onset is what makes the two land in the
        // same place relative to the impact instead of 46 ms apart.
        private const float OnsetThresholdOfPeak = 0.1f;

        // ---- #293: where the running loop enters under the pull-start clip -------------------
        //
        // The catch clip ends at running RPM, so the loop starts a beat BEFORE the clip's audible
        // end and the settle tail masks the seam. Measured backwards from the end (last point the
        // envelope still holds 10% of peak) rather than from the envelope PEAK, because the peak
        // of a yank->sputter->catch take can be the yank transient itself — an entry keyed on it
        // would drop the loop in at 0.2s, under the sputter.
        private const float PullStartLoopOverlapSeconds = 0.5f;
        private static float[] s_pullStartLoopEntrySeconds = Array.Empty<float>();

        internal static float GetPullStartLoopEntrySeconds(int index)
        {
            return index >= 0 && index < s_pullStartLoopEntrySeconds.Length ? s_pullStartLoopEntrySeconds[index] : 0f;
        }

        // #296: each whoosh take's envelope peak, so playback can be led such that the PEAK — not
        // the clip start — lands on the blade's extension apex (Lawson's timing spec).
        private static float[] s_sawWhooshPeakSeconds = Array.Empty<float>();

        internal static float GetSawWhooshPeakSeconds(int index)
        {
            return index >= 0 && index < s_sawWhooshPeakSeconds.Length ? s_sawWhooshPeakSeconds[index] : 0f;
        }

        internal static float GetPlayerHitPeakSeconds(int index)
        {
            return index >= 0 && index < s_playerHitPeakSeconds.Length ? s_playerHitPeakSeconds[index] : 0f;
        }

        // #323. Two numbers the phase assembler cannot guess:
        //
        // The removal takes disagree about WHERE the yank is by 1.4 s — remove_01 builds for 1.61 s
        // before the extraction, remove_02 is a 0.20 s bare rip. A fixed pre-roll would put one of
        // them a second and a half away from the pull-out on every kill, so each take is led by its
        // OWN measured peak (the same argument #296 makes for the swing whoosh).
        //
        // The bed takes carry 1.5-2.5 s of trailing decay past their last audible content, so the
        // fit test against the remaining cut time has to run on the AUDIBLE tail, not clip.length —
        // otherwise a 5.37 s file "fits" a 3.4 s cut on paper and delivers 3.36 s of sound.
        private static float[] s_finisherGoreRemovePeakSeconds = Array.Empty<float>();
        private static float[] s_finisherGoreDurationTailSeconds = Array.Empty<float>();

        internal static float GetFinisherGoreRemovePeakSeconds(int index)
        {
            return index >= 0 && index < s_finisherGoreRemovePeakSeconds.Length ? s_finisherGoreRemovePeakSeconds[index] : 0f;
        }

        internal static float GetFinisherGoreDurationTailSeconds(int index)
        {
            return index >= 0 && index < s_finisherGoreDurationTailSeconds.Length ? s_finisherGoreDurationTailSeconds[index] : 0f;
        }

        internal static float GetBloodOnsetSeconds(int index)
        {
            return index >= 0 && index < s_bloodOnsetSeconds.Length ? s_bloodOnsetSeconds[index] : 0f;
        }

        private static void AnalyseImpactEnvelopes()
        {
            s_playerHitPeakSeconds = new float[PlayerHitClips.Length];
            for (int i = 0; i < PlayerHitClips.Length; i++)
                s_playerHitPeakSeconds[i] = MeasureEnvelopePeakSeconds(PlayerHitClips[i]);

            s_bloodOnsetSeconds = new float[BloodClips.Length];
            for (int i = 0; i < BloodClips.Length; i++)
                s_bloodOnsetSeconds[i] = MeasureAudibleOnsetSeconds(BloodClips[i]);

            // #293: loop-entry point per pull-start take = audible tail minus the overlap the
            // settle needs to mask the seam. Zero (unreadable clip) means "no deferral".
            s_pullStartLoopEntrySeconds = new float[ChainsawPullStartClips.Length];
            for (int i = 0; i < ChainsawPullStartClips.Length; i++)
            {
                float tail = MeasureAudibleTailSeconds(ChainsawPullStartClips[i]);
                s_pullStartLoopEntrySeconds[i] = Mathf.Max(0f, tail - PullStartLoopOverlapSeconds);
            }

            s_sawWhooshPeakSeconds = new float[SawWhooshClips.Length];
            for (int i = 0; i < SawWhooshClips.Length; i++)
                s_sawWhooshPeakSeconds[i] = MeasureEnvelopePeakSeconds(SawWhooshClips[i]);

            // #323: removal lead = the take's own peak; bed fit = the take's own audible tail.
            s_finisherGoreRemovePeakSeconds = new float[FinisherGoreRemoveClips.Length];
            for (int i = 0; i < FinisherGoreRemoveClips.Length; i++)
                s_finisherGoreRemovePeakSeconds[i] = MeasureEnvelopePeakSeconds(FinisherGoreRemoveClips[i]);

            s_finisherGoreDurationTailSeconds = new float[FinisherGoreDurationClips.Length];
            for (int i = 0; i < FinisherGoreDurationClips.Length; i++)
            {
                float tail = MeasureAudibleTailSeconds(FinisherGoreDurationClips[i]);
                AudioClip clip = FinisherGoreDurationClips[i];
                s_finisherGoreDurationTailSeconds[i] = tail > 0.05f ? tail : (clip != null ? clip.length : 0f);
            }

            if (PlayerHitClips.Length == 0 && BloodClips.Length == 0 &&
                ChainsawPullStartClips.Length == 0 && SawWhooshClips.Length == 0 && !HasFinisherGoreSet)
                return;

            LogInfo(
                "Impact envelopes measured (#210/#293/#296/#323): " +
                $"playerHitPeaks=[{string.Join(", ", Array.ConvertAll(s_playerHitPeakSeconds, v => v.ToString("F3")))}]s, " +
                $"bloodOnsets=[{string.Join(", ", Array.ConvertAll(s_bloodOnsetSeconds, v => v.ToString("F3")))}]s, " +
                $"pullStartLoopEntries=[{string.Join(", ", Array.ConvertAll(s_pullStartLoopEntrySeconds, v => v.ToString("F3")))}]s, " +
                $"whooshPeaks=[{string.Join(", ", Array.ConvertAll(s_sawWhooshPeakSeconds, v => v.ToString("F3")))}]s, " +
                $"gorePhases=[impact={FinisherGoreImpactClips.Length}, bed={FinisherGoreDurationClips.Length}, remove={FinisherGoreRemoveClips.Length}], " +
                $"goreRemovePeaks=[{string.Join(", ", Array.ConvertAll(s_finisherGoreRemovePeakSeconds, v => v.ToString("F3")))}]s, " +
                $"goreBedTails=[{string.Join(", ", Array.ConvertAll(s_finisherGoreDurationTailSeconds, v => v.ToString("F3")))}]s.");
        }

        private static bool TryReadSamples(AudioClip clip, out float[] samples, out int channels, out int frequency)
        {
            samples = null;
            channels = 0;
            frequency = 0;
            if (clip == null || clip.samples <= 0 || clip.channels <= 0 || clip.frequency <= 0)
                return false;

            try
            {
                var data = new float[clip.samples * clip.channels];
                if (!clip.GetData(data, 0))
                    return false;

                samples = data;
                channels = clip.channels;
                frequency = clip.frequency;
                return true;
            }
            catch (Exception e)
            {
                LogWarning($"Could not read samples from '{clip.name}': {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        private static float MeasureEnvelopePeakSeconds(AudioClip clip)
        {
            if (!TryReadSamples(clip, out float[] data, out int channels, out int frequency))
                return 0f;

            int frames = data.Length / channels;
            int window = Math.Max(1, (int)(frequency * ImpactEnvelopeWindowSeconds));
            double running = 0d;
            double best = -1d;
            int bestFrame = 0;

            for (int f = 0; f < frames; f++)
            {
                float mono = FrameAmplitude(data, channels, f);
                running += mono * mono;
                if (f >= window)
                {
                    float leaving = FrameAmplitude(data, channels, f - window);
                    running -= leaving * leaving;
                    if (running > best)
                    {
                        best = running;
                        bestFrame = f - window / 2;
                    }
                }
            }

            return Mathf.Clamp((float)bestFrame / frequency, 0f, clip.length);
        }

        // #293: the audible END of a clip — last frame still holding OnsetThresholdOfPeak of the
        // peak. The backwards mirror of MeasureAudibleOnsetSeconds, so trailing silence in a take
        // cannot push the loop entry past the actual sound.
        private static float MeasureAudibleTailSeconds(AudioClip clip)
        {
            if (!TryReadSamples(clip, out float[] data, out int channels, out int frequency))
                return 0f;

            int frames = data.Length / channels;
            float peak = 0f;
            for (int f = 0; f < frames; f++)
                peak = Mathf.Max(peak, Mathf.Abs(FrameAmplitude(data, channels, f)));

            if (peak <= 0f)
                return 0f;

            float threshold = peak * OnsetThresholdOfPeak;
            for (int f = frames - 1; f >= 0; f--)
            {
                if (Mathf.Abs(FrameAmplitude(data, channels, f)) >= threshold)
                    return (float)f / frequency;
            }

            return 0f;
        }

        private static float MeasureAudibleOnsetSeconds(AudioClip clip)
        {
            if (!TryReadSamples(clip, out float[] data, out int channels, out int frequency))
                return 0f;

            int frames = data.Length / channels;
            float peak = 0f;
            for (int f = 0; f < frames; f++)
                peak = Mathf.Max(peak, Mathf.Abs(FrameAmplitude(data, channels, f)));

            if (peak <= 0f)
                return 0f;

            float threshold = peak * OnsetThresholdOfPeak;
            for (int f = 0; f < frames; f++)
            {
                if (Mathf.Abs(FrameAmplitude(data, channels, f)) >= threshold)
                    return (float)f / frequency;
            }

            return 0f;
        }

        private static float FrameAmplitude(float[] data, int channels, int frame)
        {
            if (channels == 1)
                return data[frame];

            float sum = 0f;
            int baseIndex = frame * channels;
            for (int c = 0; c < channels; c++)
                sum += data[baseIndex + c];
            return sum / channels;
        }

        // ---- Issue #209: which authored pool a vanilla surface maps to -----------------------
        //
        // Matched on the TAG STRING, not on the surface's index. The index order of
        // StartOfRound.footstepSurfaces is serialized prefab data with no contract behind it, and it
        // is not readable outside a running game — a hardcoded index table would be a guess that
        // silently plays grass on catwalks after any LC update. Substring matching against the tag is
        // stable across reorders and degrades safely: a tag that matches nothing keeps playing
        // vanilla's own clip for that surface, which is exactly today's behaviour.
        //
        // BundyAIBase logs the full discovered tag list and what each one resolved to, once per
        // session, so an unmapped surface shows up as a line in the log rather than as silence.
        internal static bool TryResolveSurfaceFootstepCue(string surfaceTag, out BundyAudioCue cue)
        {
            cue = BundyAudioCue.ChainsawFootstep;
            if (string.IsNullOrEmpty(surfaceTag))
                return false;

            if (ContainsAny(surfaceTag, "grass", "moss"))
                return HasAny(cue = BundyAudioCue.ChainsawFootstepGrass);

            if (IsMetalSurfaceTag(surfaceTag))
                return HasAny(cue = BundyAudioCue.ChainsawFootstepMetal);

            if (ContainsAny(surfaceTag, "concrete", "cement", "tile", "stone"))
                return HasAny(cue = BundyAudioCue.ChainsawFootstepConcrete);

            return false;
        }

        // Shared metal classification (#284): the chainsaw wall-impact spark decision must agree
        // with the footstep pool mapping about what counts as metal.
        internal static bool IsMetalSurfaceTag(string surfaceTag)
        {
            return !string.IsNullOrEmpty(surfaceTag) &&
                ContainsAny(surfaceTag, "metal", "catwalk", "aluminum", "aluminium", "grate", "steel");
        }

        private static bool ContainsAny(string value, params string[] needles)
        {
            foreach (string needle in needles)
            {
                if (value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return false;
        }

        // C3.4 anti-repeat: per-cue shuffle bag (reshuffle when exhausted, first draw of a new bag
        // never equals the previous draw) plus a per-clip cooldown. When every candidate in the
        // bag is still cooling down, fall back to the least-recently-played clip so the cue never
        // goes silent. The old avoid-last-only logic degenerated to strict A-B alternation on
        // 2-clip pools; small pools still effectively alternate here (LRU fallback), which is the
        // best possible order for them.
        private const float ClipCooldownSeconds = 90f;

        private sealed class CueSelectionState
        {
            public readonly List<int> Bag = new List<int>();
            public float[] LastPlayedAt = Array.Empty<float>();
            public int LastPicked = -1;
        }

        private static readonly Dictionary<BundyAudioCue, CueSelectionState> s_selection =
            new Dictionary<BundyAudioCue, CueSelectionState>();

        // Availability probe WITHOUT selection side effects. PickIndex is stateful since C3.4
        // (shuffle bag + per-clip cooldown stamps), so callers that only want to know "does this
        // cue have anything to play" must use this instead — a PickIndex probe would consume a
        // bag draw and start a 90s cooldown for a clip that never plays.
        internal static bool HasAny(BundyAudioCue cue)
        {
            AudioClip[] pool = GetPool(cue);
            if (pool != null && pool.Length > 0)
                return true;
            return GetClip(cue, 0) != null;
        }

        internal static int PickIndex(BundyAudioCue cue, int avoidIndex = -1)
        {
            AudioClip[] pool = GetPool(cue);
            if (pool == null || pool.Length == 0)
                return GetClip(cue, 0) != null ? 0 : -1;

            if (pool.Length == 1)
                return 0;

            if (!s_selection.TryGetValue(cue, out CueSelectionState state))
            {
                state = new CueSelectionState();
                s_selection[cue] = state;
            }

            if (state.LastPlayedAt.Length != pool.Length)
            {
                // Pool (re)registered since the last pick — reset selection memory.
                state.Bag.Clear();
                state.LastPlayedAt = new float[pool.Length];
                for (int i = 0; i < state.LastPlayedAt.Length; i++)
                    state.LastPlayedAt[i] = float.NegativeInfinity;
                state.LastPicked = -1;
            }

            int avoid = avoidIndex >= 0 && avoidIndex < pool.Length ? avoidIndex : state.LastPicked;

            if (state.Bag.Count == 0)
                RefillBag(state, pool.Length, avoid);

            float now = Time.realtimeSinceStartup;

            // Prefer the first bag entry that is off cooldown (draw order is the shuffle order).
            int chosenBagSlot = -1;
            for (int i = 0; i < state.Bag.Count; i++)
            {
                int candidate = state.Bag[i];
                if (candidate == avoid && state.Bag.Count > 1)
                    continue;
                if (now - state.LastPlayedAt[candidate] >= ClipCooldownSeconds)
                {
                    chosenBagSlot = i;
                    break;
                }
            }

            int index;
            if (chosenBagSlot >= 0)
            {
                index = state.Bag[chosenBagSlot];
                state.Bag.RemoveAt(chosenBagSlot);
            }
            else
            {
                // Everything in the bag is cooling down — take the least-recently-played clip in
                // the whole pool (still avoiding an immediate repeat) and remove it from the bag
                // if present.
                index = -1;
                float oldest = float.PositiveInfinity;
                for (int i = 0; i < pool.Length; i++)
                {
                    if (i == avoid)
                        continue;
                    if (state.LastPlayedAt[i] < oldest)
                    {
                        oldest = state.LastPlayedAt[i];
                        index = i;
                    }
                }

                if (index < 0)
                    index = UnityEngine.Random.Range(0, pool.Length);
                state.Bag.Remove(index);
            }

            state.LastPlayedAt[index] = now;
            state.LastPicked = index;
            return index;
        }

        private static void RefillBag(CueSelectionState state, int poolLength, int avoid)
        {
            state.Bag.Clear();
            for (int i = 0; i < poolLength; i++)
                state.Bag.Add(i);

            // Fisher-Yates shuffle.
            for (int i = poolLength - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                int tmp = state.Bag[i];
                state.Bag[i] = state.Bag[j];
                state.Bag[j] = tmp;
            }

            // First draw of the fresh bag must not repeat the previous pick.
            if (poolLength > 1 && avoid >= 0 && state.Bag[0] == avoid)
            {
                int swap = UnityEngine.Random.Range(1, poolLength);
                state.Bag[0] = state.Bag[swap];
                state.Bag[swap] = avoid;
            }
        }

        internal static AudioClip GetClip(BundyAudioCue cue, int index)
        {
            switch (cue)
            {
                case BundyAudioCue.Footstep:
                    return FootstepClip;
                case BundyAudioCue.ChainsawIdle:
                    return ChainsawIdleClip;
                case BundyAudioCue.ChainsawRunningLoop:
                    return ChainsawRunningLoopClip;
                case BundyAudioCue.ChainsawRev:
                    return ChainsawRevClip;
                default:
                    AudioClip[] pool = GetPool(cue);
                    return pool != null && index >= 0 && index < pool.Length ? pool[index] : null;
            }
        }

        private static AudioClip[] GetPool(BundyAudioCue cue)
        {
            switch (cue)
            {
                case BundyAudioCue.Punch:
                    return PunchClips;
                case BundyAudioCue.Grunt:
                    return GruntClips;
                case BundyAudioCue.Dialogue:
                    return DialogueClips;
                case BundyAudioCue.ChainsawFootstep:
                    return ChainsawFootstepClips;
                case BundyAudioCue.ChainsawFootstepConcrete:
                    return ChainsawFootstepConcreteClips;
                case BundyAudioCue.ChainsawFootstepGrass:
                    return ChainsawFootstepGrassClips;
                case BundyAudioCue.ChainsawFootstepMetal:
                    return ChainsawFootstepMetalClips;
                case BundyAudioCue.Blood:
                    return BloodClips;
                case BundyAudioCue.ChainsawPullStart:
                    return ChainsawPullStartClips;
                case BundyAudioCue.SawWhoosh:
                    return SawWhooshClips;
                case BundyAudioCue.FinisherGoreImpact:
                    return FinisherGoreImpactClips;
                case BundyAudioCue.FinisherGoreDuration:
                    return FinisherGoreDurationClips;
                case BundyAudioCue.FinisherGoreRemove:
                    return FinisherGoreRemoveClips;
                case BundyAudioCue.ChainsawBasicAttack:
                    return ChainsawBasicAttackClips;
                case BundyAudioCue.ChainsawFinisherDamage:
                    return ChainsawFinisherDamageClips;
                case BundyAudioCue.ChainsawWallImpact:
                    return ChainsawWallImpactClips;
                case BundyAudioCue.Hurt:
                    return HurtClips;
                case BundyAudioCue.Reaction:
                    return ReactionClips;
                case BundyAudioCue.Search:
                    return SearchClips;
                case BundyAudioCue.Spot:
                    return SpotClips;
                case BundyAudioCue.FinisherVocal:
                    return FinisherVocalClips;
                case BundyAudioCue.AttackVocal:
                    return AttackVocalClips;
                case BundyAudioCue.PlayerHit:
                    return PlayerHitClips;
                case BundyAudioCue.Rant:
                    return RantClips;
                case BundyAudioCue.KnockdownScream:
                    return KnockdownScreamClips;
                case BundyAudioCue.KnockdownRecover:
                    return KnockdownRecoverClips;
                default:
                    return Array.Empty<AudioClip>();
            }
        }

        private static AudioClip[] LoadPool(AssetBundle bundle, string[] assetNames, params string[] shortNames)
        {
            var clips = new List<AudioClip>();
            foreach (string shortName in shortNames)
            {
                AudioClip clip = LoadClip(bundle, assetNames, shortName);
                if (clip != null)
                    clips.Add(clip);
                else
                    LogWarning($"Audio clip missing from bundle: {shortName}");
            }

            return clips.ToArray();
        }

        private static AudioClip LoadClip(AssetBundle bundle, string[] assetNames, string shortName)
        {
            if (string.IsNullOrEmpty(shortName))
                return null;

            if (s_looseClips != null && s_looseClips.TryGetValue(shortName, out AudioClip looseClip) && looseClip != null)
                return looseClip;

            if (bundle == null)
                return null;

            AudioClip clip = bundle.LoadAsset<AudioClip>(shortName);
            if (clip != null)
                return clip;

            clip = bundle.LoadAsset<AudioClip>(shortName + ".wav") ??
                bundle.LoadAsset<AudioClip>(shortName + ".WAV") ??
                bundle.LoadAsset<AudioClip>(shortName + ".mp3");
            if (clip != null)
                return clip;

            if (assetNames == null)
                return null;

            for (int i = 0; i < assetNames.Length; i++)
            {
                string assetName = assetNames[i];
                string fileName = Path.GetFileNameWithoutExtension(assetName);
                if (string.Equals(fileName, shortName, StringComparison.OrdinalIgnoreCase))
                    return bundle.LoadAsset<AudioClip>(assetName);
            }

            return null;
        }

        private static void LoadLooseClips()
        {
            s_looseClips = new Dictionary<string, AudioClip>(StringComparer.OrdinalIgnoreCase);
            foreach (AudioClip clip in CompressedContractAudio.LoadBundyClips())
                s_looseClips[clip.name] = clip;
            try
            {
                string assemblyDir = Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
                if (string.IsNullOrEmpty(assemblyDir))
                    return;

                string audioDir = Path.Combine(assemblyDir, "bundy-audio");
                if (!Directory.Exists(audioDir))
                {
                    if (s_looseClips.Count == 0) LogWarning($"Loose audio folder not found: {audioDir}");
                    return;
                }

                foreach (string file in Directory.GetFiles(audioDir, "*.wav"))
                {
                    if (s_looseClips.ContainsKey(Path.GetFileNameWithoutExtension(file))) continue;
                    try
                    {
                        AudioClip clip = LoadWavFile(file);
                        if (clip != null)
                            s_looseClips[Path.GetFileNameWithoutExtension(file)] = clip;
                        else
                            LogWarning($"Loose audio skipped (unsupported wav layout): {file}");
                    }
                    catch (Exception e)
                    {
                        LogWarning($"Loose audio failed to load '{file}': {e.GetType().Name}: {e.Message}");
                    }
                }

                LogInfo($"Loose audio loaded: {s_looseClips.Count} clips from {audioDir}.");
            }
            catch (Exception e)
            {
                LogWarning($"Loose audio scan failed: {e.GetType().Name}: {e.Message}");
            }
        }

        // Minimal RIFF/WAVE reader for the clips we ship: 16-bit PCM, any channel count/rate.
        private static AudioClip LoadWavFile(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            if (bytes.Length < 44 ||
                bytes[0] != 'R' || bytes[1] != 'I' || bytes[2] != 'F' || bytes[3] != 'F' ||
                bytes[8] != 'W' || bytes[9] != 'A' || bytes[10] != 'V' || bytes[11] != 'E')
                return null;

            int channels = 0;
            int sampleRate = 0;
            int bitsPerSample = 0;
            int audioFormat = 0;
            int dataOffset = -1;
            int dataLength = 0;

            int pos = 12;
            while (pos + 8 <= bytes.Length)
            {
                string chunkId = System.Text.Encoding.ASCII.GetString(bytes, pos, 4);
                int chunkSize = BitConverter.ToInt32(bytes, pos + 4);
                int chunkBody = pos + 8;
                if (chunkId == "fmt ")
                {
                    audioFormat = BitConverter.ToInt16(bytes, chunkBody);
                    channels = BitConverter.ToInt16(bytes, chunkBody + 2);
                    sampleRate = BitConverter.ToInt32(bytes, chunkBody + 4);
                    bitsPerSample = BitConverter.ToInt16(bytes, chunkBody + 14);
                }
                else if (chunkId == "data")
                {
                    dataOffset = chunkBody;
                    dataLength = Math.Min(chunkSize, bytes.Length - chunkBody);
                }

                pos = chunkBody + chunkSize + (chunkSize & 1);
            }

            if (audioFormat != 1 || bitsPerSample != 16 || channels <= 0 || sampleRate <= 0 || dataOffset < 0 || dataLength <= 0)
                return null;

            int sampleCount = dataLength / 2;
            int frameCount = sampleCount / channels;
            if (frameCount <= 0)
                return null;

            var samples = new float[sampleCount];
            for (int i = 0; i < sampleCount; i++)
                samples[i] = BitConverter.ToInt16(bytes, dataOffset + i * 2) / 32768f;

            AudioClip clip = AudioClip.Create(Path.GetFileNameWithoutExtension(path), frameCount, channels, sampleRate, false);
            clip.SetData(samples, 0);
            return clip;
        }

        private static void LogInfo(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogInfo("[Bundy] " + message);
        }

        private static void LogWarning(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogWarning("[Bundy] " + message);
        }
    }
}
