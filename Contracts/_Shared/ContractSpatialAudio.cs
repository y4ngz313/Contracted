using UnityEngine;
using UnityEngine.Audio;

namespace Y4NGZCompany.Contracts._Shared
{
    // Shared spatial-audio routing for contract props, lifted from the model proven on
    // Bundy (BundyAIBase.Setup.cs:250-259 — "Task E").
    //
    // A bare AudioSource with outputAudioMixerGroup = none bypasses Lethal Company's
    // diagetic mixer entirely: no occlusion low-pass through walls, no room reverb, no
    // volume slider, no deafness ducking. Combined with the default Linear rolloff that
    // is "a speaker playing clips" which dies one room away. Routing through the group a
    // vanilla enemy uses, adding vanilla's OccludeAudio, and replacing Linear with a
    // carry curve is what lets a prop be heard facility-wide while still reading as
    // distant rather than as quiet-and-next-to-your-ear.
    internal static class ContractSpatialAudio
    {
        private static AudioMixerGroup _mixerGroup;

        // Prefer the group a live vanilla enemy voice sits on: that is the group whose
        // occlusion and reverb behaviour we are explicitly trying to match. Fall back to
        // the diagetic mixer's own SFX/Master group when no enemy has spawned yet, and to
        // vanilla's ambience source last.
        internal static AudioMixerGroup ResolveMixerGroup()
        {
            if (_mixerGroup != null)
                return _mixerGroup;

            var enemies = RoundManager.Instance?.SpawnedEnemies;
            if (enemies != null)
            {
                for (int i = 0; i < enemies.Count; i++)
                {
                    EnemyAI enemy = enemies[i];
                    if (enemy != null && enemy.creatureVoice != null && enemy.creatureVoice.outputAudioMixerGroup != null)
                    {
                        _mixerGroup = enemy.creatureVoice.outputAudioMixerGroup;
                        return _mixerGroup;
                    }
                }
            }

            SoundManager sound = SoundManager.Instance;
            if (sound == null)
                return null;

            if (sound.diageticMixer != null)
            {
                AudioMixerGroup[] groups = sound.diageticMixer.FindMatchingGroups("SFX");
                if (groups != null && groups.Length > 0)
                {
                    _mixerGroup = groups[0];
                    return _mixerGroup;
                }

                groups = sound.diageticMixer.FindMatchingGroups("Master");
                if (groups != null && groups.Length > 0)
                {
                    _mixerGroup = groups[0];
                    return _mixerGroup;
                }
            }

            if (sound.ambienceAudio != null && sound.ambienceAudio.outputAudioMixerGroup != null)
                _mixerGroup = sound.ambienceAudio.outputAudioMixerGroup;

            return _mixerGroup;
        }

        internal static void ResetRunState()
        {
            // The mixer group belongs to a scene object; a cached reference from a previous
            // round is a destroyed Unity object by the time the next one lands.
            _mixerGroup = null;
        }

        /// <summary>
        /// Route <paramref name="source"/> through the diagetic mixer, give it vanilla's
        /// OccludeAudio (reverb send included) and a carry rolloff over
        /// <paramref name="maxDistance"/>. Returns the per-source propagation driver, which
        /// the caller must Tick() each frame for air absorption and occlusion.
        /// </summary>
        /// <remarks>
        /// #984: the signature is fixed. The live weapon-audio fixture
        /// (tests/BlacksiteLandingCombatChecks/WeaponAudioChecks.cs) reflects this method and
        /// invokes it with exactly five arguments, which neither an appended optional
        /// parameter nor an overload survives — so the near-field variant is its own named
        /// entry point (<see cref="ApplyNearFieldCarryProfile"/>) over the same core.
        /// </remarks>
        internal static ContractAudioPropagation ApplyFacilityCarryProfile(
            AudioSource source,
            float maxDistance,
            float minDistance = 2f,
            float spread = 25f,
            int priority = 110)
        {
            return ApplyCarryProfileCore(source, maxDistance, minDistance, spread, priority, 0f, null);
        }

        /// <summary>
        /// #984: <see cref="ApplyFacilityCarryProfile"/> with a near-field plateau and an
        /// optional air-absorption override, for a source that has to read as "right next to
        /// you" rather than "somewhere in the facility" — footsteps.
        /// </summary>
        /// <param name="nearFieldPlateauMeters">
        /// Metres of FULL gain before the carry fade starts. The shared shape is already at
        /// half gain by 2.6 m from a 26 m source, because <c>AudioRolloffMode.Custom</c>
        /// evaluates the curve over <c>distance / maxDistance</c> and ignores
        /// <c>minDistance</c> entirely.
        /// </param>
        /// <param name="absorption">
        /// The air-absorption shape; null keeps the broadband impacts/beeps profile every
        /// other contract prop uses.
        /// </param>
        internal static ContractAudioPropagation ApplyNearFieldCarryProfile(
            AudioSource source,
            float maxDistance,
            float minDistance,
            float nearFieldPlateauMeters,
            ContractAudioAbsorptionProfile absorption = null,
            float spread = 25f,
            int priority = 110)
        {
            return ApplyCarryProfileCore(
                source, maxDistance, minDistance, spread, priority, nearFieldPlateauMeters, absorption);
        }

        private static ContractAudioPropagation ApplyCarryProfileCore(
            AudioSource source,
            float maxDistance,
            float minDistance,
            float spread,
            int priority,
            float nearFieldPlateauMeters,
            ContractAudioAbsorptionProfile absorption)
        {
            if (source == null)
                return null;

            AudioMixerGroup group = ResolveMixerGroup();
            if (group != null)
                source.outputAudioMixerGroup = group;

            source.spatialBlend = 1f;
            source.dopplerLevel = 0f;
            source.minDistance = minDistance;
            source.maxDistance = maxDistance;
            source.spread = spread;
            source.priority = priority;
            source.rolloffMode = AudioRolloffMode.Custom;
            float plateau = maxDistance > 0.01f ? nearFieldPlateauMeters / maxDistance : 0f;
            source.SetCustomCurve(
                AudioSourceCurveType.CustomRolloff,
                plateau > 0.001f ? BuildNearFieldCarryFalloffCurve(plateau) : BuildCarryFalloffCurve());

            OccludeAudio occlude = source.GetComponent<OccludeAudio>();
            if (occlude == null)
                occlude = source.gameObject.AddComponent<OccludeAudio>();

            // #929: the reverb send belongs to the profile, not to the moment the component is
            // created. Vanilla's AudioReverbTrigger drives the diagetic group a player is standing
            // in, and OccludeAudio.useReverb is what opts a source into it, so a source that already
            // carried an OccludeAudio serialized with useReverb = false was silently outside the
            // facility reverb even after being routed onto the group.
            if (occlude != null)
                occlude.useReverb = true;

            return new ContractAudioPropagation(source, occlude, absorption);
        }

        // The saw's Job 4 carry shape (BuildSawCarryFalloffCurve): full presence up close,
        // fast early fade, then a tail that HOLDS across the mid/far band instead of decaying
        // to zero a third of the way out. The held tail is what makes a sound trackable from
        // across the facility; the air-absorption low-pass below is what stops it floating,
        // because distance then reads spectrally rather than only as low volume.
        //
        // Left parameterless on purpose (#984): it is reached by reflection with no argument
        // array from the live weapon-audio fixture (tests/BlacksiteLandingCombatChecks/
        // WeaponAudioChecks.cs), so neither an optional parameter nor an overload may be added
        // here. The near-field variant is its own named method below.
        internal static AnimationCurve BuildCarryFalloffCurve()
        {
            return new AnimationCurve(CarryFalloffKeys());
        }

        private static Keyframe[] CarryFalloffKeys()
        {
            return new[]
            {
                new Keyframe(0f, 1f, 0f, -5.0f),
                new Keyframe(0.1f, 0.5f, -2.6f, -2.6f),
                new Keyframe(0.25f, 0.30f, -0.9f, -0.9f),
                new Keyframe(0.42f, 0.15f, -0.35f, -0.35f),
                new Keyframe(0.75f, 0.13f, -0.03f, -0.03f),
                new Keyframe(1f, 0f, -0.65f, 0f),
            };
        }

        // #984: the carry shape with a flat run of FULL gain in front of it.
        // <paramref name="nearFieldPlateau"/> is that plateau as a fraction of maxDistance,
        // because AudioRolloffMode.Custom evaluates the curve over distance/maxDistance and
        // ignores minDistance entirely — which is why the plain shape above is already at half
        // gain 2.6 m from a 26 m source. At 0 this returns the plain shape. Above 0 the whole
        // shape is compressed into the remaining span, with every tangent divided by the span
        // so the fade keeps the same steepness in metres rather than being stretched flatter.
        internal static AnimationCurve BuildNearFieldCarryFalloffCurve(float nearFieldPlateau)
        {
            Keyframe[] keys = CarryFalloffKeys();
            float plateau = Mathf.Clamp(nearFieldPlateau, 0f, 0.6f);
            if (plateau <= 0.001f)
                return new AnimationCurve(keys);

            float span = 1f - plateau;
            var held = new Keyframe[keys.Length + 1];
            held[0] = new Keyframe(0f, 1f, 0f, 0f);
            for (int i = 0; i < keys.Length; i++)
            {
                Keyframe key = keys[i];
                held[i + 1] = new Keyframe(
                    plateau + key.time * span,
                    key.value,
                    i == 0 ? 0f : key.inTangent / span,
                    key.outTangent / span);
            }

            return new AnimationCurve(held);
        }
    }

    /// <summary>
    /// #984: the air-absorption shape a <see cref="ContractAudioPropagation"/> drives. It was
    /// a block of consts tuned for machine beeps and body impacts; footsteps are 1-6 kHz
    /// transients, and halving them onto a 4.5 kHz floor from 2 m out is exactly what made a
    /// body walking three metres away read as "somewhere else in the facility".
    /// </summary>
    internal sealed class ContractAudioAbsorptionProfile
    {
        internal readonly float BaseCutoffHz;
        internal readonly float HalvingMeters;
        internal readonly float MinCutoffHz;
        internal readonly float OnsetMeters;

        internal ContractAudioAbsorptionProfile(
            float baseCutoffHz,
            float halvingMeters,
            float minCutoffHz,
            float onsetMeters)
        {
            BaseCutoffHz = baseCutoffHz;
            HalvingMeters = halvingMeters;
            MinCutoffHz = minCutoffHz;
            OnsetMeters = onsetMeters;
        }

        /// <summary>The saw's gentle 8 m halving onto a 4.5 kHz floor. Every pre-#984 caller.</summary>
        internal static readonly ContractAudioAbsorptionProfile Broadband =
            new ContractAudioAbsorptionProfile(20000f, 8f, 4500f, 2f);

        /// <summary>
        /// Footsteps and other near-field transients: absorption starts only past the carry
        /// curve's plateau and halves half as fast onto a 7 kHz floor, so the click of a heel
        /// survives the first few metres and distance still reads spectrally further out.
        /// </summary>
        internal static readonly ContractAudioAbsorptionProfile NearFieldTransient =
            new ContractAudioAbsorptionProfile(20000f, 14f, 7000f, 3.5f);
    }

    // Per-source distance realism. Neither stock Lethal Company nor Unity models air
    // absorption, but OccludeAudio exposes overridingLowPass/lowPassOverride, so the filter
    // is driven per frame with an exponential high-frequency loss model (plus vanilla's own
    // occluded formula whenever a wall blocks the path).
    internal sealed class ContractAudioPropagation
    {
        // Broadband profile: these are impacts and machine beeps, not speech, so they want
        // the saw's gentle 8 m halving onto a 4.5 kHz floor rather than the steeper
        // intelligibility-driven pair used for voice. #984 moved the three distance-dependent
        // numbers onto ContractAudioAbsorptionProfile so a near-field source can opt out of
        // this shape; the occluded pair below is shared by every profile.
        private const float OccludedFloorHz = 900f;
        private const float OccludedCeilingHz = 4000f;
        private const float OcclusionRecheckSeconds = 0.25f;

        // 256 = vanilla's occlusion mask; OccludeAudio.Start/Update use the same.
        private const int OcclusionMask = 256;

        private readonly AudioSource _source;
        private readonly OccludeAudio _occlude;
        private readonly ContractAudioAbsorptionProfile _absorption;
        private float _nextOcclusionCheckAt;
        private bool _pathOccluded;

        internal ContractAudioPropagation(
            AudioSource source,
            OccludeAudio occlude,
            ContractAudioAbsorptionProfile absorption = null)
        {
            _source = source;
            _occlude = occlude;
            _absorption = absorption ?? ContractAudioAbsorptionProfile.Broadband;
        }

        internal void Tick()
        {
            if (_source == null || _occlude == null)
                return;

            // Routing usually runs before any vanilla enemy exists, so the group can still be
            // null from the initial ApplyFacilityCarryProfile call. Adopt it once it resolves
            // rather than leaving this source permanently off the diagetic mixer.
            if (_source.outputAudioMixerGroup == null)
            {
                AudioMixerGroup group = ContractSpatialAudio.ResolveMixerGroup();
                if (group != null)
                    _source.outputAudioMixerGroup = group;
            }

            Transform listener = StartOfRound.Instance?.audioListener != null
                ? StartOfRound.Instance.audioListener.transform
                : null;
            if (listener == null)
                return;

            Vector3 sourcePosition = _source.transform.position;
            float distance = Vector3.Distance(listener.position, sourcePosition);

            if (Time.time >= _nextOcclusionCheckAt)
            {
                _nextOcclusionCheckAt = Time.time + OcclusionRecheckSeconds;
                _pathOccluded = Physics.Linecast(sourcePosition, listener.position, OcclusionMask, QueryTriggerInteraction.Ignore);
            }

            float cutoff;
            if (_pathOccluded)
            {
                // Vanilla's occluded shape, with the divisor taken from this source's OWN
                // range (maxDistance/2). Muffled through a wall is the goal; inaudible is not.
                float occludedHalfMax = Mathf.Max(1f, _source.maxDistance * 0.5f);
                cutoff = Mathf.Clamp(
                    2500f / Mathf.Max(0.1f, distance / occludedHalfMax),
                    OccludedFloorHz,
                    OccludedCeilingHz);
            }
            else
            {
                cutoff = Mathf.Clamp(
                    _absorption.BaseCutoffHz * Mathf.Pow(
                        0.5f,
                        Mathf.Max(0f, distance - _absorption.OnsetMeters) / _absorption.HalvingMeters),
                    _absorption.MinCutoffHz,
                    _absorption.BaseCutoffHz);
            }

            _occlude.overridingLowPass = true;
            _occlude.lowPassOverride = cutoff;
        }
    }
}
