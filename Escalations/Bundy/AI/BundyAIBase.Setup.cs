using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using Y4NGZCompany.Escalations.Bundy.HitReactions;
using Y4NGZCompany.Facility.Elevators;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.Bundy.AI;

public abstract partial class BundyAIBase
{
    private void EnsureInstanceBehaviourStates()
    {
        if (enemyBehaviourStates == null || enemyBehaviourStates.Length < 2)
            enemyBehaviourStates = CreateSafeBehaviourStates();

        if (currentBehaviourState == null && enemyBehaviourStates.Length > 0)
            currentBehaviourState = enemyBehaviourStates[Mathf.Clamp(currentBehaviourStateIndex, 0, enemyBehaviourStates.Length - 1)];
    }

    private void ResolveRuntimeReferences()
    {
        if (creatureAnimator == null)
            creatureAnimator = GetComponentInChildren<Animator>(true);
        if (_parameterCacheAnimator != creatureAnimator)
        {
            _parameterCacheAnimator = creatureAnimator;
            _hasSpeedParameter = -1;
            _hasMoveXParameter = -1;
            _hasMoveYParameter = -1;
            // #308: every cached parameter probe belongs here, not just the locomotion floats. A
            // re-resolved Animator that kept a stale "missing" answer would degrade the fall pose and
            // the sprint gait for the rest of the session on a bundle that actually has them.
            _hasFallingParameter = -1;
            _hasSprintingParameter = -1;
        }

        if (agent == null)
            agent = GetComponentInChildren<NavMeshAgent>(true);
        if (agent != null)
            BundySmartPathingBridge.TryRegisterSmartAgent(agent);

        if (_chainsawBladeHitbox == null)
            _chainsawBladeHitbox = GetComponentInChildren<BundyChainsawBladeHitbox>(true);
        if (_chainsawBladeHitbox != null)
            _chainsawBladeHitbox.Configure(this, _chainsawBladeHitbox.bladeRoot);

        if (_footstepAudioSource == null)
            _footstepAudioSource = FindAudioSource("Footsteps") ?? creatureSFX;

        if (eye == null)
            eye = transform.Find("EyeTransform");

        EnsureAudioSourceAnchors();
        EnsureAudioMixerRouting();
        EnsureAudioLateTick();
        EnsureDoorOpener();
        EnsureRuntimeBodyHitbox();
    }

    private void EnsureAudioLateTick()
    {
        if (_audioLateTick != null)
            return;

        _audioLateTick = GetComponent<BundyAudioLateTick>() ?? gameObject.AddComponent<BundyAudioLateTick>();
        _audioLateTick.Owner = this;
    }

    // Lethal Company has no HRTF/binaural spatializer: Unity's stereo pan cannot communicate
    // elevation, so speech emitted from Bundy's roughly 3.3m head reads as disconnected audio
    // above the listener. Keep voice at a configurable root-relative player-head/sternum height,
    // chainsaw SFX on the prop, and footsteps at ground level. This runs idempotently so replaced
    // or recreated sources are corrected before their first playback.
    // #276: EnsureAudioSourceAnchors used to re-run its transform.Find / FindChildRecursive walks and
    // unconditional property rewrites from ResolveRuntimeReferences every Update. Once every source is
    // anchored in its terminal state, latch and only re-run when the cheap allocation-free validity
    // probe below detects a change (source destroyed/reparented, live-tuned voice height, late-created
    // emitter, late-resolved mixer group) or InvalidateAudioSourceAnchors is called.
    private bool _audioAnchorsLatched;
    private Transform _latchedVoiceAnchor;
    private Transform _latchedChainsawProp;
    private float _latchedVoiceAnchorHeight = float.NaN;

    internal void InvalidateAudioSourceAnchors()
    {
        _audioAnchorsLatched = false;
    }

    private bool AudioAnchorLatchStillValid()
    {
        // Resolved anchors must still exist and every source must still hang where the latch left it
        // (Unity fake-null also catches destroyed objects here).
        if (_latchedVoiceAnchor == null || _latchedChainsawProp == null)
            return false;
        if (creatureVoice == null || creatureVoice.transform.parent != _latchedVoiceAnchor)
            return false;
        if (creatureSFX != null && creatureSFX.transform.parent != _latchedChainsawProp)
            return false;
        if (_movementLoopAudioSource != null && _movementLoopAudioSource.transform.parent != _latchedChainsawProp)
            return false;
        if (_movementLoopNearAudioSource != null && _movementLoopNearAudioSource.transform.parent != _latchedChainsawProp)
            return false;
        if (_chainsawContactAudioSource != null && _chainsawContactAudioSource.transform.parent != _latchedChainsawProp)
            return false;

        // The voice anchor height stays live-tunable.
        float height = Mathf.Clamp(BundyPlugin.ModConfig?.BundyVoiceAnchorHeight?.Value ?? 1.7f, 0.5f, 3.6f);
        if (!Mathf.Approximately(height, _latchedVoiceAnchorHeight))
            return false;

        // Emitters created after the latch (or enabled by a live config flip) need the full pass.
        if (_movementLoopAudioSource != null && _movementLoopNearAudioSource == null &&
            (BundyPlugin.ModConfig?.ChainsawNearFieldLayer?.Value ?? true))
            return false;
        if (_chainsawContactAudioSource == null && ContactBladeEmitterEnabled)
            return false;

        // A mixer group that resolved after the latch still needs adopting (Task E defect shape).
        if (_resolvedAudioMixerGroup != null &&
            ((_movementLoopNearAudioSource != null && _movementLoopNearAudioSource.outputAudioMixerGroup == null) ||
             (_chainsawContactAudioSource != null && _chainsawContactAudioSource.outputAudioMixerGroup == null)))
            return false;

        return true;
    }

    private void EnsureAudioSourceAnchors()
    {
        if (_audioAnchorsLatched)
        {
            if (AudioAnchorLatchStillValid())
                return;
            _audioAnchorsLatched = false;
        }

        if (creatureVoice == null)
            creatureVoice = FindAudioSource("CreatureVoice");
        if (creatureSFX == null)
            creatureSFX = FindAudioSource("CreatureSFX");
        if (creatureVoice == null && creatureSFX == null)
            return;

        bool anchorsChanged = false;
        Transform voiceAnchor = transform.Find("BundyVoiceAnchor");
        if (voiceAnchor == null)
        {
            var voiceAnchorObject = new GameObject("BundyVoiceAnchor");
            voiceAnchor = voiceAnchorObject.transform;
            voiceAnchor.SetParent(transform, false);
            anchorsChanged = true;
        }

        float voiceAnchorHeight = Mathf.Clamp(
            BundyPlugin.ModConfig?.BundyVoiceAnchorHeight?.Value ?? 1.7f,
            0.5f,
            3.6f);
        Vector3 desiredVoiceAnchorPosition = new Vector3(0f, voiceAnchorHeight, 0f);
        if ((voiceAnchor.localPosition - desiredVoiceAnchorPosition).sqrMagnitude > 0.000001f)
        {
            voiceAnchor.localPosition = desiredVoiceAnchorPosition;
            anchorsChanged = true;
        }

        Transform chainsawProp = null;
        if (creatureSFX != null &&
            creatureSFX.transform.parent != null &&
            creatureSFX.transform.parent.name == RuntimeChainsawPropName)
        {
            chainsawProp = creatureSFX.transform.parent;
        }
        else if (_movementLoopAudioSource != null &&
                 _movementLoopAudioSource.transform.parent != null &&
                 _movementLoopAudioSource.transform.parent.name == RuntimeChainsawPropName)
        {
            chainsawProp = _movementLoopAudioSource.transform.parent;
        }
        else
        {
            chainsawProp = FindChildRecursive(transform, RuntimeChainsawPropName);
        }

        if (creatureVoice != null)
        {
            if (creatureVoice.transform.parent != voiceAnchor)
            {
                creatureVoice.transform.SetParent(voiceAnchor, false);
                anchorsChanged = true;
            }
            if (creatureVoice.transform.localPosition.sqrMagnitude > 0.000001f ||
                !Mathf.Approximately(creatureVoice.dopplerLevel, 0f))
            {
                anchorsChanged = true;
            }
            creatureVoice.transform.localPosition = Vector3.zero;
            creatureVoice.dopplerLevel = 0f;
        }

        if (creatureSFX != null && chainsawProp != null)
        {
            if (creatureSFX.transform.parent != chainsawProp)
            {
                creatureSFX.transform.SetParent(chainsawProp, false);
                anchorsChanged = true;
            }
            if (creatureSFX.transform.localPosition.sqrMagnitude > 0.000001f ||
                !Mathf.Approximately(creatureSFX.dopplerLevel, 0f))
            {
                anchorsChanged = true;
            }
            creatureSFX.transform.localPosition = Vector3.zero;
            creatureSFX.dopplerLevel = 0f;
        }

        if (_movementLoopAudioSource != null && chainsawProp != null)
        {
            if (_movementLoopAudioSource.transform.parent != chainsawProp)
            {
                _movementLoopAudioSource.transform.SetParent(chainsawProp, false);
                anchorsChanged = true;
            }
            if (_movementLoopAudioSource.transform.localPosition.sqrMagnitude > 0.000001f ||
                !Mathf.Approximately(_movementLoopAudioSource.dopplerLevel, 0f))
            {
                anchorsChanged = true;
            }
            _movementLoopAudioSource.transform.localPosition = Vector3.zero;
            _movementLoopAudioSource.dopplerLevel = 0f;
        }

        // C2.3: create the near-field layer here if the loop source was built before the prop
        // resolved (creation is a no-op until the loop machinery exists and the config allows it).
        if (_movementLoopNearAudioSource == null && chainsawProp != null && _movementLoopAudioSource != null)
            EnsureNearFieldLoopSource(chainsawProp);

        // C2.3: the near-field layer keeps its blade-tip offset (NOT zeroed like the others) —
        // the offset is the point of parenting it: the swing arc moves the emitter.
        if (_movementLoopNearAudioSource != null && chainsawProp != null)
        {
            if (_movementLoopNearAudioSource.transform.parent != chainsawProp)
            {
                _movementLoopNearAudioSource.transform.SetParent(chainsawProp, false);
                anchorsChanged = true;
            }
            if ((_movementLoopNearAudioSource.transform.localPosition - _nearLoopLocalOffset).sqrMagnitude > 0.000001f ||
                !Mathf.Approximately(_movementLoopNearAudioSource.dopplerLevel, 0f))
            {
                anchorsChanged = true;
            }
            _movementLoopNearAudioSource.transform.localPosition = _nearLoopLocalOffset;
            _movementLoopNearAudioSource.dopplerLevel = 0f;
        }

        // Built here rather than lazily on the first hit, so it is present when EnsureAudioMixerRouting
        // runs and gets its mixer group with every other source instead of after its first playback.
        if (_chainsawContactAudioSource == null && chainsawProp != null)
            ResolveChainsawContactAudioSource();

        // #187: the contact emitter tracks the same blade anchor. Its offset is RE-derived here rather
        // than frozen at creation, because the emitter can be built before BundyChainsawBladeHitbox
        // resolves — in which case the first offset is the renderer-bounds estimate and this upgrades it
        // to the authored blade transform as soon as the hitbox appears.
        if (_chainsawContactAudioSource != null && chainsawProp != null)
        {
            Vector3 desiredContactOffset = ComputeChainsawTipLocalOffset(chainsawProp);
            if (_chainsawContactAudioSource.transform.parent != chainsawProp)
            {
                _chainsawContactAudioSource.transform.SetParent(chainsawProp, false);
                anchorsChanged = true;
            }
            if ((_chainsawContactAudioSource.transform.localPosition - desiredContactOffset).sqrMagnitude > 0.000001f ||
                !Mathf.Approximately(_chainsawContactAudioSource.dopplerLevel, 0f))
            {
                anchorsChanged = true;
            }
            _chainsawContactLocalOffset = desiredContactOffset;
            _chainsawContactAudioSource.transform.localPosition = desiredContactOffset;
            _chainsawContactAudioSource.dopplerLevel = 0f;
        }

        // Sources built after EnsureAudioMixerRouting ran (it latches _audioMixerRoutingApplied once)
        // would otherwise keep outputAudioMixerGroup = none — no occlusion low-pass, no volume slider,
        // no deafness. That is the exact Task E defect, re-entering through a later-created emitter.
        AdoptResolvedMixerGroup(_movementLoopNearAudioSource);
        AdoptResolvedMixerGroup(_chainsawContactAudioSource);

        if (!_audioAnchorsApplied || anchorsChanged)
        {
            _audioAnchorsApplied = true;
            string voiceTarget = creatureVoice != null
                ? $"BundyVoiceAnchor@{voiceAnchorHeight:F2}m"
                : "unchanged";
            LogInfo(
                $"Audio anchors applied: voice->{voiceTarget} (worldY={voiceAnchor.position.y:F2}), " +
                $"sfx/loop->{(chainsawProp != null ? "chainsaw" : "unchanged")}.");
        }

        // #276: steady state reached — every source that can exist is anchored and untouched this
        // pass. Latch until the validity probe or an explicit invalidation reopens the full pass.
        // The blade-hitbox requirement keeps the #187 contact-offset upgrade path alive: never latch
        // while the contact emitter still runs on the renderer-bounds estimate.
        if (!anchorsChanged &&
            chainsawProp != null &&
            creatureVoice != null &&
            (_chainsawContactAudioSource == null || _chainsawBladeHitbox != null))
        {
            _audioAnchorsLatched = true;
            _latchedVoiceAnchor = voiceAnchor;
            _latchedChainsawProp = chainsawProp;
            _latchedVoiceAnchorHeight = voiceAnchorHeight;
        }
    }

    private enum BundyAudioProfile
    {
        Voice,
        Saw,
        SawNear,      // C2.3 short-range blade-tip loop layer
        SawContact,   // #187 blade contact one-shots (hit / basic attack / finisher damage)
        Footsteps,
    }

    // Task E (2026-07-22): every Bundy AudioSource shipped with outputAudioMixerGroup = none,
    // bypassing LC's diagetic mixer — no occlusion low-pass through walls, no distance filtering,
    // no room reverb: exactly "a speaker playing clips". Route all Bundy sources through the same
    // mixer group a vanilla enemy voice uses and add the vanilla OccludeAudio component.
    // 2026-07-22 round 2: the prefab also ships flat LINEAR rolloff (min 1 / max 30) on every
    // source — a voice that is still at ~85% volume from 5m away reads as headphones, not a
    // person. Routing now applies explicit per-role spatial profiles (fast perceptual falloff
    // curve, spread, priority) instead of trusting the prefab or copying an arbitrary template.
    // Local-only per client; falls back to the diagetic mixer's SFX group after ~10s if no
    // vanilla enemy has spawned yet.
    private void EnsureAudioMixerRouting()
    {
        if (_audioMixerRoutingApplied || Time.time < _nextAudioMixerRoutingAttemptAt)
            return;

        _nextAudioMixerRoutingAttemptAt = Time.time + 2f;
        _audioMixerRoutingAttempts++;

        AudioSource template = FindVanillaEnemyVoiceTemplate();
        UnityEngine.Audio.AudioMixerGroup group = template != null ? template.outputAudioMixerGroup : null;
        if (group == null && _audioMixerRoutingAttempts >= 5)
            group = FindDiageticSfxMixerGroup();
        if (group == null)
            return;

        _resolvedAudioMixerGroup = group;
        int routed = 0;
        routed += ApplyMixerRoutingToSource(creatureVoice, BundyAudioProfile.Voice, addOcclude: true) ? 1 : 0;
        routed += ApplyMixerRoutingToSource(creatureSFX, BundyAudioProfile.Saw, addOcclude: true) ? 1 : 0;
        // #212: was addOcclude: false, alone among Bundy's six sources. No OccludeAudio meant no
        // occlusion low-pass, no reverb send AND — because UpdateAudioAirAbsorption drives
        // OccludeAudio.overridingLowPass — no air absorption either, so footsteps were the one cue
        // excluded from the whole distance-realism model. That is the diagnosed cause of Lawson's
        // "you are never really able to hear them at all" and of facilities not giving them the
        // reverb vanilla monster footsteps get.
        routed += ApplyMixerRoutingToSource(_footstepAudioSource, BundyAudioProfile.Footsteps, addOcclude: true) ? 1 : 0;
        routed += ApplyMixerRoutingToSource(_movementLoopAudioSource, BundyAudioProfile.Saw, addOcclude: true) ? 1 : 0;
        routed += ApplyMixerRoutingToSource(_movementLoopNearAudioSource, BundyAudioProfile.SawNear, addOcclude: true) ? 1 : 0;
        routed += ApplyMixerRoutingToSource(_chainsawContactAudioSource, BundyAudioProfile.SawContact, addOcclude: true) ? 1 : 0;

        // Round 4: stop hand-tuning against an invisible reference — make Bundy's voice source
        // spatially IDENTICAL to a live vanilla enemy voice (min/max/rolloff/spread/priority),
        // and adopt the vanilla rolloff curve for the saw sources too. If audio still "floats"
        // with provably identical settings, the delta is the assets, not the code.
        bool matchVanilla = BundyPlugin.ModConfig?.BundyMatchVanillaVoiceSpatial?.Value ?? true;
        if (template != null)
            _vanillaVoiceTestClip = template.clip; // independent Debug/BundyVanillaVoiceClipTest A/B

        if (template != null && matchVanilla)
        {
            CloneVanillaVoiceSpatial(creatureVoice, template);
            // Job 4: with the range extension on, the saw carries a purpose-built carry curve
            // over a 60-85m domain; a vanilla roar curve stretched over that domain is neither
            // the vanilla sound nor ours. Voice cloning is unaffected.
            if (!RangeExtensionEnabled)
            {
                AdoptTemplateRolloff(creatureSFX, template);
                AdoptTemplateRolloff(_movementLoopAudioSource, template);
            }
        }

        _audioMixerRoutingApplied = true;
        // ApplyBundySpatialProfile just wrote the BASE voice range; force the #186 environment profile
        // to re-evaluate so an outdoor listener does not silently get the indoor range from here on.
        _voiceEnvironmentIsInside = -1;
        LogInfo(
            $"Audio mixer routing applied: group='{group.name}', " +
            $"template={(template != null ? template.gameObject.name : "<diagetic-mixer-fallback>")}, sources={routed}, " +
            $"vanillaSpatialClone={(template != null && matchVanilla)}, testClip={(_vanillaVoiceTestClip != null ? _vanillaVoiceTestClip.name : "<none>")}, " +
            $"rangeExtension={RangeExtensionEnabled}, sawMax={(creatureSFX != null ? creatureSFX.maxDistance : 0f):F0}m, " +
            $"footstepMax={(_footstepAudioSource != null ? _footstepAudioSource.maxDistance : 0f):F0}m.");

        // Forensics: full source dumps so vanilla-vs-Bundy settings can be diffed line by line.
        LogAudioSourceForensics("vanilla-template", template);
        LogAudioSourceForensics("bundy-voice", creatureVoice);
        LogAudioSourceForensics("bundy-sfx", creatureSFX);
        LogAudioSourceForensics("bundy-footsteps", _footstepAudioSource);
        LogAudioSourceForensics("bundy-loop", _movementLoopAudioSource);
        LogAudioSourceForensics("bundy-loop-near", _movementLoopNearAudioSource);
        LogAudioSourceForensics("bundy-blade-contact", _chainsawContactAudioSource);
    }

    private void AdoptResolvedMixerGroup(AudioSource source)
    {
        if (source == null || _resolvedAudioMixerGroup == null || source.outputAudioMixerGroup != null)
            return;
        source.outputAudioMixerGroup = _resolvedAudioMixerGroup;
    }

    private static void CloneVanillaVoiceSpatial(AudioSource target, AudioSource template)
    {
        if (target == null || template == null)
            return;

        target.spatialBlend = 1f;
        target.dopplerLevel = 0f;
        target.minDistance = template.minDistance;
        target.maxDistance = template.maxDistance;
        target.spread = template.spread;
        target.priority = template.priority;
        target.rolloffMode = template.rolloffMode;
        if (template.rolloffMode == AudioRolloffMode.Custom)
        {
            AnimationCurve curve = template.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
            if (curve != null)
                target.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
        }
    }

    private static void AdoptTemplateRolloff(AudioSource target, AudioSource template)
    {
        if (target == null || template == null || template.rolloffMode != AudioRolloffMode.Custom)
            return;

        AnimationCurve curve = template.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
        if (curve == null)
            return;

        target.rolloffMode = AudioRolloffMode.Custom;
        target.SetCustomCurve(AudioSourceCurveType.CustomRolloff, curve);
    }

    // Distance realism beyond volume: in the real world high frequencies die with distance
    // faster than lows (air absorption) — that spectral shift, not loudness, is what tells the
    // brain "10m away" instead of "quiet and next to my ear". Neither stock LC nor Unity does
    // this; LC's OccludeAudio exposes overridingLowPass/lowPassOverride, so we drive its filter
    // per frame with an exponential air-absorption model (plus the vanilla occlusion formula
    // when a wall blocks the path). Runs on ALL clients.
    private void UpdateAudioAirAbsorption()
    {
        if (!(BundyPlugin.ModConfig?.BundyDistanceAirAbsorption?.Value ?? true))
            return;

        Transform listener = StartOfRound.Instance?.audioListener != null
            ? StartOfRound.Instance.audioListener.transform
            : null;
        if (listener == null)
            return;

        if (Time.time >= _nextAirAbsorptionResolveAt)
        {
            _nextAirAbsorptionResolveAt = Time.time + 2f;
            if (_voiceOcclude == null && creatureVoice != null)
                _voiceOcclude = creatureVoice.GetComponent<OccludeAudio>();
            if (_sfxOcclude == null && creatureSFX != null)
                _sfxOcclude = creatureSFX.GetComponent<OccludeAudio>();
            if (_loopOcclude == null && _movementLoopAudioSource != null)
                _loopOcclude = _movementLoopAudioSource.GetComponent<OccludeAudio>();
            if (_nearLoopOcclude == null && _movementLoopNearAudioSource != null)
                _nearLoopOcclude = _movementLoopNearAudioSource.GetComponent<OccludeAudio>();
            if (_contactOcclude == null && _chainsawContactAudioSource != null)
                _contactOcclude = _chainsawContactAudioSource.GetComponent<OccludeAudio>();
            if (_footstepOcclude == null && _footstepAudioSource != null)
                _footstepOcclude = _footstepAudioSource.GetComponent<OccludeAudio>();
        }

        if (_voiceOcclude == null && _sfxOcclude == null && _loopOcclude == null &&
            _nearLoopOcclude == null && _contactOcclude == null && _footstepOcclude == null)
        {
            return;
        }

        UpdateSawAudioRangeEscalation();
        UpdateVoiceEnvironmentProfile();

        Vector3 sourcePos = transform.position + Vector3.up * 2.2f;
        float distance = Vector3.Distance(listener.position, sourcePos);

        // One shared linecast (sources sit within ~1m of each other); mask 256 = vanilla's
        // occlusion mask (OccludeAudio.Start/Update use the same).
        if (Time.time >= _nextAudioOcclusionCheckAt)
        {
            _nextAudioOcclusionCheckAt = Time.time + 0.25f;
            _audioPathOccluded = Physics.Linecast(sourcePos, listener.position, 256, QueryTriggerInteraction.Ignore);
        }

        // Job 4: the occluded formula's divisor is maxDistance/2 (vanilla's shape). With the
        // saw's range extended, keeping the old 11 would pin every through-wall saw at the
        // 900Hz floor over its whole audible band — muffled is the goal, inaudible is not.
        // C3.5 gives voice the same treatment (maxDistance/2 = 20 at the default 40m) when the
        // range extension is on; OFF restores the hardcoded 11 exactly.
        float sawOccludedHalfMax = _currentSawAudioMaxDistance > 0f ? _currentSawAudioMaxDistance * 0.5f : 11f;
        float voiceOccludedHalfMax = RangeExtensionEnabled ? CurrentVoiceMaxDistance() * 0.5f : 11f;
        // #186: two voice absorption profiles, switched by the same condition vanilla uses to enable its
        // reverb. Indoors the room carries him, so absorption goes STEEPER than the old 6m halving —
        // 4.5m onto a 2.6k floor — which is what pays for the raised curve: 10m is now +6.6 dB louder
        // but 4.6kHz instead of 6.4kHz, so the extra level reads as a man down the corridor rather than
        // a man at your shoulder. Outdoors there is no reverb at all and open air genuinely loses far
        // less HF over tens of metres, so it halves every 9m onto a 3.4k floor — the lift has to stay
        // intelligible out there or it buys nothing. Occluded ceiling is voice-specific: see
        // BundyVoiceOccludedCutoff.
        bool inside = _voiceEnvironmentIsInside != 0;
        // 2026-07-31: the outdoor pair was relaxed again (9m/3.4k -> 14m/4.2k). The first pass
        // reasoned "outdoors loses less HF than indoors" and moved the halving distance, but still
        // landed on a figure that damps a shout far harder than open air actually does over tens of
        // metres — and a voice that is both quiet and dull does not read as distant, it reads as
        // absent. Level was the other half of "he doesn't carry outside"; this is the half that
        // decides whether what does arrive is intelligible enough to be worth carrying.
        ApplyAirAbsorption(
            _voiceOcclude,
            distance,
            16000f,
            inside ? 4.5f : 14f,
            inside ? 2600f : 4200f,
            voiceOccludedHalfMax,
            VoiceOccludedCutoffCeiling);
        UpdateVoiceDistanceSpread(distance, inside);
        // #323: the ENGINE emitters take the finisher muffle — the saw is the thing buried in the
        // body. Carry loop, near-field loop and the general saw SFX channel; all three carry their
        // layer sources on the same GameObjects, so the whole engine dulls as one.
        float sawMuffle = SawEngineMuffle01;
        ApplyAirAbsorption(_sfxOcclude, distance, 20000f, 8f, 4500f, sawOccludedHalfMax, muffle01: sawMuffle);
        ApplyAirAbsorption(_loopOcclude, distance, 20000f, 8f, 4500f, sawOccludedHalfMax, muffle01: sawMuffle);
        // C2.3: the near layer occludes on its own short domain (divisor = its max/2, ~3.5) so a
        // wall inside the terror radius muffles it hard instead of leaving a point-blank saw
        // playing clean through a doorframe.
        ApplyAirAbsorption(
            _nearLoopOcclude,
            distance,
            20000f,
            8f,
            4500f,
            GetConfiguredNearFieldMaxDistance() * 0.5f,
            muffle01: sawMuffle);
        // #187: the contact emitter absorbs on its own (shorter) domain, so a hit heard through a wall
        // is muffled against its own range rather than the saw's 60-85m one.
        // #323 deliberately does NOT muffle it: the gore phases and the cut-resistance texture live
        // on this emitter, and they are the sound of the inside of the body — the thing the muffle
        // exists to put the engine behind.
        ApplyAirAbsorption(_contactOcclude, distance, 20000f, 8f, 4500f, GetConfiguredContactMaxDistance() * 0.5f);
        // #212: footsteps join the model on the same broadband profile the saw uses — they are impacts,
        // not speech, so they want the saw's gentler 8 m halving onto a 4.5 kHz floor rather than the
        // voice's steep intelligibility-driven pair. Occluded divisor is their OWN range's half, and it
        // reads the live maxDistance rather than the configured base so #213's sprint/taunt widening
        // moves the through-wall behaviour with it instead of leaving it pinned to the walk's domain.
        float footstepHalfMax = _footstepAudioSource != null && _footstepAudioSource.maxDistance > 0f
            ? _footstepAudioSource.maxDistance * 0.5f
            : GetConfiguredFootstepMaxDistance() * 0.5f;
        ApplyAirAbsorption(_footstepOcclude, distance, 20000f, 8f, 4500f, footstepHalfMax);
    }

    // Job 4: the saw's audible range is the escalation clock's first audio expression — 60m at
    // arrival, ~85m at full escalation. The normalized carry curve stretches with maxDistance,
    // so escalation widens the held far band rather than just moving the silence line.
    // Escalation elapsed is owner-only state, so remote clients hold the base range; the floor
    // is the load-bearing part of the change.
    private void UpdateSawAudioRangeEscalation()
    {
        if (!_audioMixerRoutingApplied || !RangeExtensionEnabled)
            return;
        if (Time.time < _nextAudioRangeEscalationUpdateAt)
            return;

        _nextAudioRangeEscalationUpdateAt = Time.time + 2f;
        float baseMax = GetConfiguredSawBaseMaxDistance();
        float escalatedMax = Mathf.Max(baseMax, BundyPlugin.ModConfig?.ChainsawLoopEscalatedMaxDistance?.Value ?? 85f);
        float target = Mathf.Lerp(baseMax, escalatedMax, GetStrangerEscalationProgress());
        if (Mathf.Abs(target - _currentSawAudioMaxDistance) < 0.5f)
            return;

        _currentSawAudioMaxDistance = target;
        if (creatureSFX != null)
            creatureSFX.maxDistance = target;
        if (_movementLoopAudioSource != null)
            _movementLoopAudioSource.maxDistance = target;
        // #294: the carry-side engine layers stretch with the main loop source — a far-field saw
        // whose chain/scream died at 60m while its chug carried to 85m would fall apart spectrally.
        if (_loopChainLayerSource != null)
            _loopChainLayerSource.maxDistance = target;
        if (_loopHighLayerSource != null)
            _loopHighLayerSource.maxDistance = target;
    }

    // #186. Lethal Company gives every OccludeAudio source a reverb send, but `OccludeAudio.Update`
    // enables that AudioReverbFilter ONLY while the local (or spectated) player is inside the factory —
    // outdoors it disables the filter every frame. So the same voice that has a room helping it carry
    // indoors is completely dry on the surface, under a loud wind bed. That is the whole of "outside you
    // literally can't hear him".
    //
    // The compensation has to be range, not gain: Unity hard-clamps AudioSource.volume to 1 and the
    // voice already sits near the top of its band. The rolloff curve is normalized over maxDistance, so
    // stretching the range outdoors lifts every distance on the same shape. Switched on the transition
    // only — the curve/range writes are not per-frame work — and mirrors vanilla's own inside test
    // exactly, so our lift turns on precisely when its reverb turns off.
    // -1 = unknown/not applied, 0 = outside, 1 = inside.
    private int _voiceEnvironmentIsInside = -1;

    private static float VoiceOutdoorRangeScale =>
        Mathf.Clamp(BundyPlugin.ModConfig?.BundyVoiceOutdoorRangeScale?.Value ?? 1.4f, 1f, 2.5f);

    private static float VoiceOccludedCutoffCeiling =>
        Mathf.Clamp(BundyPlugin.ModConfig?.BundyVoiceOccludedCutoff?.Value ?? 2600f, 1200f, 4000f);

    private float CurrentVoiceMaxDistance()
    {
        float baseMax = GetConfiguredVoiceMaxDistance();
        return _voiceEnvironmentIsInside == 0 ? baseMax * VoiceOutdoorRangeScale : baseMax;
    }

    private static bool IsLocalListenerInsideFactory()
    {
        PlayerControllerB local = GameNetworkManager.Instance?.localPlayerController;
        if (local == null)
            return true; // unknown: behave exactly as before, no compensation
        if (local.isInsideFactory)
            return true;
        return local.isPlayerDead &&
            local.spectatedPlayerScript != null &&
            local.spectatedPlayerScript.isInsideFactory;
    }

    private void UpdateVoiceEnvironmentProfile()
    {
        if (!RangeExtensionEnabled || creatureVoice == null)
            return;

        int inside = IsLocalListenerInsideFactory() ? 1 : 0;
        if (inside == _voiceEnvironmentIsInside)
            return;

        _voiceEnvironmentIsInside = inside;
        float target = CurrentVoiceMaxDistance();
        creatureVoice.maxDistance = target;
        LogInfo(
            BundyLogCategory.Audio,
            1,
            $"Voice environment profile: listener={(inside == 1 ? "inside" : "outside")}, " +
            $"voiceMax={target:F1}m (base {GetConfiguredVoiceMaxDistance():F1}m), " +
            $"reverbSendAvailable={(inside == 1)}.");
    }

    // ---- Voice distance realism (2026-07-31) ---------------------------------------------------
    // Level and low-pass were the two cues #186 moved, and they are the two weakest ones. What
    // actually tells a listener how far away a talker is, in order of strength: how much of what
    // arrives is ROOM rather than mouth (wet/dry), how WIDE the arrival is, then spectrum, then
    // level. Lethal Company holds the first two constant — its reverb send is a fixed level and
    // Unity's spread is a fixed number — so a line at 2m and a line at 25m arrive with identical
    // spatial character and differ only in how loud they are. That is the definition of a
    // soundboard. These two functions vary the first two cues with distance.

    private const float VoiceSpreadNearMeters = 2f;
    private const float VoiceSpreadFarMeters = 25f;
    private const float VoiceSpreadNear = 12f;
    private const float VoiceSpreadFarInside = 65f;
    private const float VoiceSpreadFarOutside = 45f;
    // Below this he is effectively dry; at and beyond the far mark the room owns the sound.
    private const float VoiceReverbDryMeters = 3f;
    private const float VoiceReverbWetMeters = 28f;

    private void UpdateVoiceDistanceSpread(float distance, bool inside)
    {
        if (!VoiceDistanceSpreadEnabled || creatureVoice == null)
            return;

        // Narrower outdoors: there is no room to smear it, and the far tail is the only bearing a
        // player has when he is shouting at them across open ground.
        float far = inside ? VoiceSpreadFarInside : VoiceSpreadFarOutside;
        float t = Mathf.InverseLerp(VoiceSpreadNearMeters, VoiceSpreadFarMeters, distance);
        creatureVoice.spread = Mathf.Lerp(VoiceSpreadNear, far, t);
    }

    /// <summary>
    /// Called from <see cref="BundyAudioLateTick"/>, i.e. after every Update in the frame — see
    /// that class for why this cannot live in Update.
    /// </summary>
    internal void UpdateAudioLate()
    {
        UpdateVoiceDistanceReverb();
    }

    private void UpdateVoiceDistanceReverb()
    {
        if (!VoiceDistanceReverbEnabled || creatureVoice == null || isEnemyDead)
            return;

        if (_voiceReverb == null)
        {
            // OccludeAudio creates it in its own Start; until then there is nothing to drive and
            // adding a second filter to the same GameObject would just chain two reverbs.
            _voiceReverb = creatureVoice.GetComponent<AudioReverbFilter>();
            if (_voiceReverb == null)
                return;
        }

        Transform listener = StartOfRound.Instance?.audioListener != null
            ? StartOfRound.Instance.audioListener.transform
            : null;
        if (listener == null)
            return;

        float distance = Vector3.Distance(listener.position, creatureVoice.transform.position);
        float wet = Mathf.Clamp01(Mathf.InverseLerp(VoiceReverbDryMeters, VoiceReverbWetMeters, distance));
        bool inside = IsLocalListenerInsideFactory();

        // Re-enabled unconditionally: outdoors this is the write that undoes OccludeAudio's own
        // per-frame disable, and it is the reason this runs in LateUpdate.
        _voiceReverb.enabled = true;
        _voiceReverb.reverbPreset = AudioReverbPreset.User;
        _voiceReverb.dryLevel = 0f;

        if (inside)
        {
            // Indoors the room already exists; what was missing is that its share of the sound did
            // not change with distance. Near is a man in front of you, far is a corridor talking.
            _voiceReverb.room = Mathf.Lerp(-4500f, -1400f, wet);
            _voiceReverb.roomHF = Mathf.Lerp(-900f, -2200f, wet);
            _voiceReverb.decayTime = Mathf.Lerp(0.55f, 1.5f, wet);
            _voiceReverb.decayHFRatio = 0.55f;
            _voiceReverb.reflectionsLevel = Mathf.Lerp(-1500f, -600f, wet);
            _voiceReverb.reflectionsDelay = 0.012f;
            _voiceReverb.reverbLevel = Mathf.Lerp(-2000f, 200f, wet);
            _voiceReverb.reverbDelay = 0.022f;
            _voiceReverb.diffusion = 100f;
            _voiceReverb.density = 100f;
        }
        else
        {
            // Outdoors there is no room, but there is ground and there is terrain: a shout across
            // open ground arrives with a short bright slap and almost no tail. Low level, short
            // decay, low density — enough that the far voice stops sounding like a dry file played
            // at you, nowhere near enough to put a man on a moon inside a hall.
            _voiceReverb.room = Mathf.Lerp(-6000f, -2400f, wet);
            _voiceReverb.roomHF = -600f;
            _voiceReverb.decayTime = Mathf.Lerp(0.35f, 0.9f, wet);
            _voiceReverb.decayHFRatio = 0.85f;
            _voiceReverb.reflectionsLevel = Mathf.Lerp(-2000f, -900f, wet);
            _voiceReverb.reflectionsDelay = 0.03f;
            _voiceReverb.reverbLevel = Mathf.Lerp(-3000f, -300f, wet);
            _voiceReverb.reverbDelay = 0.045f;
            _voiceReverb.diffusion = 60f;
            _voiceReverb.density = 40f;
        }
    }

    private void ApplyAirAbsorption(
        OccludeAudio occlude,
        float distance,
        float baseCutoff,
        float halvingMeters,
        float minCutoff,
        float occludedHalfMaxDistance,
        float occludedCutoffCeiling = 4000f,
        float muffle01 = 0f)
    {
        if (occlude == null)
            return;

        float cutoff;
        if (_audioPathOccluded)
        {
            // Vanilla OccludeAudio's own occluded formula (divisor = maxDistance/2 per source). The
            // ceiling is per-source since #186 — vanilla's shared 4000 left a walled-off voice nearly as
            // bright as an open one, which combined with the level-independent reverb send is how a
            // further-away Bundy behind a wall could out-read a closer one in the open.
            cutoff = Mathf.Clamp(
                2500f / Mathf.Max(0.1f, distance / occludedHalfMaxDistance),
                900f,
                Mathf.Max(900f, occludedCutoffCeiling));
        }
        else
        {
            // Exponential: cutoff halves every `halvingMeters` beyond 2m. Voice: 16k@2m,
            // ~11k@5m, ~8k@8m, ~4k@14m. Saw halves slower (louder, lower-frequency machine).
            cutoff = Mathf.Clamp(
                baseCutoff * Mathf.Pow(0.5f, Mathf.Max(0f, distance - 2f) / halvingMeters),
                minCutoff,
                baseCutoff);
        }

        // #323: the finisher muffle. While the blade is inside a body the engine is heard THROUGH
        // meat, so its cutoff is dragged down to FinisherGoreMuffleCutoffHz on a smoothed envelope.
        // Applied here rather than as an AudioLowPassFilter because this method rewrites
        // lowPassOverride every frame — a separate filter would be fighting it — and because the
        // layered engine sources deliberately share their host emitter's filter chain, so one term
        // here muffles chug, chain and scream together. Min, never Lerp alone: through a wall the
        // occluded formula can already sit below the muffle target and must not be BRIGHTENED by it.
        if (muffle01 > 0.001f)
            cutoff = Mathf.Min(cutoff, Mathf.Lerp(cutoff, FinisherGoreMuffleCutoffHz, Mathf.Clamp01(muffle01)));

        occlude.overridingLowPass = true;
        occlude.lowPassOverride = cutoff;
    }

    private void LogAudioSourceForensics(string label, AudioSource source)
    {
        if (source == null)
        {
            LogInfo($"AudioForensics {label}: <null>");
            return;
        }

        string curveText = "n/a";
        if (source.rolloffMode == AudioRolloffMode.Custom)
        {
            AnimationCurve curve = source.GetCustomCurve(AudioSourceCurveType.CustomRolloff);
            if (curve != null)
                curveText = $"{curve.Evaluate(0f):F2}/{curve.Evaluate(0.25f):F2}/{curve.Evaluate(0.5f):F2}/{curve.Evaluate(0.75f):F2}/{curve.Evaluate(1f):F2}";
        }

        OccludeAudio occlude = source.GetComponent<OccludeAudio>();
        LogInfo(
            $"AudioForensics {label}: obj={source.gameObject.name}, " +
            $"mixer={(source.outputAudioMixerGroup != null ? source.outputAudioMixerGroup.name : "<none>")}, " +
            $"spatialBlend={source.spatialBlend:F2}, vol={source.volume:F2}, prio={source.priority}, " +
            $"min={source.minDistance:F1}, max={source.maxDistance:F1}, rolloff={source.rolloffMode}, " +
            $"curve@0/.25/.5/.75/1={curveText}, spread={source.spread:F0}, doppler={source.dopplerLevel:F2}, " +
            $"occlude={(occlude != null ? $"yes(reverb={occlude.useReverb})" : "no")}, " +
            $"lowpass={(source.GetComponent<AudioLowPassFilter>() != null)}, reverbFilter={(source.GetComponent<AudioReverbFilter>() != null)}, " +
            $"parent={(source.transform.parent != null ? source.transform.parent.name : "<root>")}, " +
            $"worldPos={FormatVector(source.transform.position)}, heightAboveRoot={(source.transform.position.y - transform.position.y):F2}");
    }

    private bool ApplyMixerRoutingToSource(AudioSource source, BundyAudioProfile profile, bool addOcclude)
    {
        if (source == null)
            return false;

        if (_resolvedAudioMixerGroup != null)
            source.outputAudioMixerGroup = _resolvedAudioMixerGroup;

        ApplyBundySpatialProfile(source, profile);

        if (addOcclude && source.GetComponent<OccludeAudio>() == null)
        {
            OccludeAudio occlude = source.gameObject.AddComponent<OccludeAudio>();
            occlude.useReverb = true;
        }

        return true;
    }

    // #213 made this a shared answer rather than an inline expression: the spatial profile writes it
    // once at routing time and UpdateFootstepGaitRange scales it per gait every frame, so the two
    // must agree about what "the base range" is or the walk quietly inherits the sprint's width.
    private static float GetConfiguredFootstepMaxDistance()
    {
        return RangeExtensionEnabled
            ? Mathf.Max(20f, BundyPlugin.ModConfig?.BundyFootstepMaxDistance?.Value ?? 35f)
            : 20f;
    }

    private static void ApplyBundySpatialProfile(AudioSource source, BundyAudioProfile profile)
    {
        source.spatialBlend = 1f;
        source.dopplerLevel = 0f;
        source.rolloffMode = AudioRolloffMode.Custom;
        source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, BuildFastFalloffCurve());

        switch (profile)
        {
            case BundyAudioProfile.Voice:
                // SPOKEN dialogue, not a monster roar: full presence only inside ~2.5m, ~38% at
                // 5m, ~13% at 10m. Spread 25: a pinpoint mono source at head height (3.3m —
                // stereo panning cannot render elevation) reads as "floating nearby"; width blurs
                // the unresolvable height cue.
                // C3.5 (2026-07-30): with the range extension on, voice carries to ~40m on a
                // held-tail re-key of the same near shape (the 20m voice was the outlier against
                // a 60-85m saw — at 10m his saw was 2x his voice and he went silent one room
                // away). OFF restores the exact pre-C3.5 20m curve.
                source.minDistance = 1f;
                source.maxDistance = RangeExtensionEnabled ? GetConfiguredVoiceMaxDistance() : 20f;
                source.spread = 25f;
                source.priority = 100;
                source.SetCustomCurve(
                    AudioSourceCurveType.CustomRolloff,
                    RangeExtensionEnabled ? BuildVoiceCarryFalloffCurve() : BuildVoiceFalloffCurve());
                break;
            case BundyAudioProfile.Saw:
                // A running saw is a big loud body: carries further and is acoustically WIDE up
                // close (spread) so it never collapses into a single hard-panned pin point.
                // Job 4: the old 30m range made "he's coming" a one-corridor feeling; the far
                // field is now held by the carry curve and kept honest by the air-absorption
                // low-pass (spectral distance, not loudness). Escalation scales maxDistance
                // further in UpdateSawAudioRangeEscalation.
                source.minDistance = 2f;
                source.maxDistance = RangeExtensionEnabled
                    ? GetConfiguredSawBaseMaxDistance()
                    : 30f;
                source.spread = 25f;
                source.priority = 110;
                if (RangeExtensionEnabled)
                    source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, BuildSawCarryFalloffCurve());
                break;
            case BundyAudioProfile.SawNear:
                // C2.3 near-field layer: steep, short, loud only when he is effectively on top of
                // you. minDistance is inert under Custom rolloff (kept small for hygiene); the
                // curve alone defines the band. NOT touched by UpdateSawAudioRangeEscalation —
                // escalation widens the far field, the terror radius stays fixed.
                source.minDistance = 0.5f;
                source.maxDistance = GetConfiguredNearFieldMaxDistance();
                source.spread = 45f;
                source.priority = 110;
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, BuildSawNearFalloffCurve());
                break;
            case BundyAudioProfile.SawContact:
                // #187: the contact transients get their own emitter and their own SHAPE. They used to
                // ride the Saw profile, whose Job 4 carry curve is deliberately flat close in so the far
                // field stays present — which meant a blade in your face measured only ~7 dB over the same
                // blade 10m away. A hit is a local event, so this curve is allowed to collapse: ~0.91 at
                // 1m, 0.36 at 5m, 0.16 at 10m, ~15 dB of point-blank contrast. Wide spread because the
                // emitter is inside the player's face at contact and must not read as a hard-panned pin.
                source.minDistance = 0.5f;
                source.maxDistance = GetConfiguredContactMaxDistance();
                source.spread = 35f;
                source.priority = 105;
                source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, BuildSawContactFalloffCurve());
                break;
            case BundyAudioProfile.Footsteps:
                source.minDistance = 1.3f;
                // Job 4: at 20m an unobserved far-sprint was silent to anyone past one room.
                source.maxDistance = GetConfiguredFootstepMaxDistance();
                source.spread = 0f;
                source.priority = 120;
                break;
        }
    }

    // Perceptual ~1/d falloff over each source's maxDistance: real presence dynamics up close,
    // fast early fade, long quiet tail. Replaces the prefab's flat Linear rolloff, which held a
    // voice at ~85% volume from 5m away.
    private static AnimationCurve BuildFastFalloffCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 1f, 0f, -4.2f),
            new Keyframe(0.1f, 0.62f, -2.4f, -2.4f),
            new Keyframe(0.25f, 0.34f, -1.05f, -1.05f),
            new Keyframe(0.5f, 0.15f, -0.42f, -0.42f),
            new Keyframe(0.75f, 0.055f, -0.2f, -0.2f),
            new Keyframe(1f, 0f, -0.08f, 0f));
    }

    // Job 4 carry curve for the saw (domain = d/60m at base range): full presence up close,
    // fast early fade like the old curve, but the tail HOLDS across the 25-45m band instead of
    // decaying to zero by 22m — a far-field engine drone you orient by. Escalation stretches the
    // same normalized curve toward 85m, widening the audible band as the clock runs. The
    // air-absorption low-pass supplies the "far away" read so the held tail cannot float;
    // loudness stays low, spectrum does the distance work.
    // C2.1 compensation (2026-07-30): the loop base volume dropped 1.0 -> 0.65 (headroom for the
    // swell/speed boosts) and the curve is normalized, so the whole field scaled down with it.
    // The mid/far keys are raised ~1.5x (0.22->0.30, 0.10->0.15, 0.085->0.13) so the effective
    // far-field loudness (key x 0.65 base) matches the playtested Job 4 field almost exactly;
    // the 0-6m keys stay untouched because the near-field layer (C2.3) now owns close contrast.
    private static AnimationCurve BuildSawCarryFalloffCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 1f, 0f, -5.0f),
            new Keyframe(0.1f, 0.5f, -2.6f, -2.6f),
            new Keyframe(0.25f, 0.30f, -0.9f, -0.9f),
            new Keyframe(0.42f, 0.15f, -0.35f, -0.35f),
            new Keyframe(0.75f, 0.13f, -0.03f, -0.03f),
            new Keyframe(1f, 0f, -0.65f, 0f));
    }

    // C2.3 near-field curve (domain = d/7m default): ~95% inside 1m, ~40% at 3m, gone past ~6m.
    // This is the layer that makes point-blank read categorically louder than one room away.
    private static AnimationCurve BuildSawNearFalloffCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 1f, 0f, -0.8f),
            new Keyframe(0.2f, 0.82f, -1.4f, -1.4f),
            new Keyframe(0.45f, 0.38f, -1.5f, -1.5f),
            new Keyframe(0.75f, 0.08f, -0.5f, -0.5f),
            new Keyframe(1f, 0f, -0.15f, 0f));
    }

    // #187 blade contact curve (domain = d/30m default). Steep on purpose — this is the only saw curve
    // allowed to collapse with distance, because it carries hit transients rather than presence.
    // 1m→0.91, 2m→0.80, 5m→0.36, 10m→0.16, 20m→0.05. The carry loop still owns the far field.
    private static AnimationCurve BuildSawContactFalloffCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 1f, 0f, -3.0f),
            new Keyframe(0.067f, 0.80f, -4.0f, -4.0f),
            new Keyframe(0.167f, 0.36f, -2.4f, -2.4f),
            new Keyframe(0.333f, 0.16f, -0.6f, -0.6f),
            new Keyframe(0.667f, 0.05f, -0.15f, -0.15f),
            new Keyframe(1f, 0f, -0.12f, 0f));
    }

    private static bool ContactBladeEmitterEnabled =>
        BundyPlugin.ModConfig?.ChainsawContactBladeEmitter?.Value ?? true;

    private static float GetConfiguredContactMaxDistance() =>
        Mathf.Clamp(BundyPlugin.ModConfig?.ChainsawContactMaxDistance?.Value ?? 30f, 12f, 60f);

    private static bool RangeExtensionEnabled =>
        BundyPlugin.ModConfig?.BundyAudioRangeExtension?.Value ?? true;

    private static float GetConfiguredSawBaseMaxDistance() =>
        Mathf.Max(30f, BundyPlugin.ModConfig?.ChainsawLoopMaxDistance?.Value ?? 60f);

    private static float GetConfiguredVoiceMaxDistance() =>
        Mathf.Clamp(BundyPlugin.ModConfig?.BundyVoiceMaxDistance?.Value ?? 40f, 20f, 60f);

    private static float GetConfiguredNearFieldMaxDistance() =>
        Mathf.Clamp(BundyPlugin.ModConfig?.ChainsawNearFieldMaxDistance?.Value ?? 7f, 4f, 12f);

    // Steeper still, for SPOKEN lines (domain = d/20m): conversation collapses with distance in
    // a way roars don't. 2.5m→0.80, 5m→0.38, 10m→0.13, 15m→0.04.
    private static AnimationCurve BuildVoiceFalloffCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 1f, 0f, -2.0f),
            new Keyframe(0.125f, 0.8f, -2.6f, -2.6f),
            new Keyframe(0.25f, 0.38f, -1.9f, -1.9f),
            new Keyframe(0.5f, 0.13f, -0.55f, -0.55f),
            new Keyframe(0.75f, 0.04f, -0.16f, -0.16f),
            new Keyframe(1f, 0f, -0.06f, 0f));
    }

    // C3.5 held-tail voice curve (domain = d/40m at the default range), mirroring the saw's Job 4
    // treatment: a low tail holds across the mid band instead of dying at 18m — muffled talking you
    // can track through walls. The air-absorption low-pass + occlusion keep the tail spectrally "far",
    // so it cannot float the way a loud flat extension would.
    //
    // #186 (2026-07-31): the 2.5-32m band is raised. C3.5's shape was right and its level was not —
    // 0.38 at 5m and 0.14 at 10m, times the 0.75 base, put a spoken line 20 dB down at 10m, which is
    // inaudible under any ambience. The near field (0-2.5m) is deliberately UNCHANGED: he must not get
    // louder in your face, only reachable further out. The lift is in the curve rather than in
    // BundyVoiceVolume because a persisted cfg file would pin an old default and silently keep the bug
    // (the ChainsawLoopBaseVolume lesson).
    //   was  ->  now      2.5m 0.80 -> 0.82   5m 0.38 -> 0.55   10m 0.14 -> 0.30
    //                      18m 0.075 -> 0.17  32m 0.055 -> 0.10
    private static AnimationCurve BuildVoiceCarryFalloffCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 1f, 0f, -2.6f),
            new Keyframe(0.0625f, 0.82f, -3.4f, -3.4f),
            new Keyframe(0.125f, 0.55f, -2.6f, -2.6f),
            new Keyframe(0.25f, 0.30f, -1.15f, -1.15f),
            new Keyframe(0.45f, 0.17f, -0.4f, -0.4f),
            new Keyframe(0.8f, 0.10f, -0.12f, -0.12f),
            new Keyframe(1f, 0f, -0.5f, 0f));
    }

    private AudioSource FindVanillaEnemyVoiceTemplate()
    {
        List<EnemyAI> enemies = RoundManager.Instance?.SpawnedEnemies;
        if (enemies == null)
            return null;

        for (int i = 0; i < enemies.Count; i++)
        {
            EnemyAI enemy = enemies[i];
            if (enemy == null || ReferenceEquals(enemy, this) || enemy is BundyAIBase)
                continue;

            if (enemy.creatureVoice != null && enemy.creatureVoice.outputAudioMixerGroup != null)
                return enemy.creatureVoice;
        }

        return null;
    }

    private static UnityEngine.Audio.AudioMixerGroup FindDiageticSfxMixerGroup()
    {
        UnityEngine.Audio.AudioMixer mixer = SoundManager.Instance?.diageticMixer;
        if (mixer == null)
            return null;

        UnityEngine.Audio.AudioMixerGroup[] groups = mixer.FindMatchingGroups("SFX");
        if (groups != null && groups.Length > 0)
            return groups[0];

        groups = mixer.FindMatchingGroups("Master");
        return groups != null && groups.Length > 0 ? groups[0] : null;
    }

    private void EnsureDoorOpener()
    {
        if (_doorOpener != null)
            return;

        Transform existing = transform.Find("BundyDoorOpener");
        GameObject openerObject;
        if (existing == null)
        {
            openerObject = new GameObject("BundyDoorOpener");
            openerObject.transform.SetParent(transform, false);
            openerObject.transform.localPosition = new Vector3(0f, 1.05f, 1.35f);
        }
        else
        {
            openerObject = existing.gameObject;
        }

        var collider = openerObject.GetComponent<BoxCollider>();
        if (collider == null)
            collider = openerObject.AddComponent<BoxCollider>();
        collider.isTrigger = true;
        collider.center = Vector3.zero;
        collider.size = new Vector3(1.35f, 1.65f, 2.1f);

        var rb = openerObject.GetComponent<Rigidbody>();
        if (rb == null)
            rb = openerObject.AddComponent<Rigidbody>();
        rb.isKinematic = true;
        rb.useGravity = false;

        _doorOpener = openerObject.GetComponent<BundyDoorOpener>();
        if (_doorOpener == null)
            _doorOpener = openerObject.AddComponent<BundyDoorOpener>();
        _doorOpener.Init(this);
    }

    // #288 tier 1 host. Built lazily off the first hit rather than eagerly from
    // ResolveRuntimeReferences: the flinch needs the armature's spine/head bones, and the visual
    // child is not guaranteed to be resolvable on the frame the AI wakes. One failed attempt latches
    // (BundyProceduralFlinch leaves an inert component behind), so this never re-walks the hierarchy
    // in a loop on a rig that cannot support it.
    private BundyProceduralFlinch _proceduralFlinch;
    private bool _proceduralFlinchAttempted;

    private BundyProceduralFlinch EnsureProceduralFlinch()
    {
        if (_proceduralFlinch != null)
            return _proceduralFlinch;
        if (_proceduralFlinchAttempted || creatureAnimator == null)
            return null;

        _proceduralFlinchAttempted = true;
        _proceduralFlinch = BundyProceduralFlinch.EnsureOn(transform, creatureAnimator);
        // Ungated on purpose (one-shot, same rationale as the bundle-fingerprint line): the 2026-08-05
        // playtest could not distinguish "flinch too subtle" from "flinch never built" because this
        // line was verbosity-gated away.
        LogWarning(
            $"[BundyFlinch] procedural hit reactions {(_proceduralFlinch != null ? "attached" : "unavailable (no spine/head bones resolved)")}.");
        return _proceduralFlinch;
    }

    private void EnsureRuntimeBodyHitbox()
    {
        if (_runtimeBodyHitbox != null)
            return;

        Transform existing = transform.Find(RuntimeBodyHitboxName);
        bool created = existing == null;
        GameObject hitboxObject;
        if (created)
        {
            hitboxObject = new GameObject(RuntimeBodyHitboxName);
            hitboxObject.transform.SetParent(transform, false);
        }
        else
        {
            hitboxObject = existing.gameObject;
        }

        hitboxObject.transform.localPosition = Vector3.zero;
        hitboxObject.transform.localRotation = Quaternion.identity;
        hitboxObject.transform.localScale = Vector3.one;
        hitboxObject.layer = EnemyHitboxLayer;
        try
        {
            hitboxObject.tag = "Enemy";
        }
        catch
        {
            // "Enemy" is a vanilla tag; leave the object untagged if another runtime stripped it.
        }

        var collider = hitboxObject.GetComponent<CapsuleCollider>();
        if (collider == null)
            collider = hitboxObject.AddComponent<CapsuleCollider>();
        collider.isTrigger = false;
        collider.direction = 1;
        collider.center = RuntimeBodyHitboxCenter;
        collider.radius = RuntimeBodyHitboxRadius;
        collider.height = RuntimeBodyHitboxHeight;

        _runtimeBodyHitbox = hitboxObject.GetComponent<EnemyAICollisionDetect>();
        if (_runtimeBodyHitbox == null)
            _runtimeBodyHitbox = hitboxObject.AddComponent<EnemyAICollisionDetect>();
        _runtimeBodyHitbox.mainScript = this;
        _runtimeBodyHitbox.onlyCollideWhenGrounded = false;
        _runtimeBodyHitbox.canCollideWithEnemies = false;

        // The capsule is raycastable but has no Rigidbody, so it cannot drive the NavMeshAgent.
        // Ignore every sibling collider explicitly to keep the door trigger and any authored attack
        // hitboxes out of this body's contact callbacks. Player collision follows vanilla layer 19.
        Collider[] ownedColliders = GetComponentsInChildren<Collider>(true);
        for (int i = 0; i < ownedColliders.Length; i++)
        {
            Collider ownedCollider = ownedColliders[i];
            if (ownedCollider != null && ownedCollider != collider)
                Physics.IgnoreCollision(collider, ownedCollider, true);
        }

        // The prefab root carries a kinematic Rigidbody, so Unity attributes every child collider
        // hit to the ROOT transform. Vanilla weapons resolve EnemyAICollisionDetect via
        // RaycastHit.transform (the Rigidbody object) and dedup via the collider's own component,
        // so the detect component must live on the root AND on every solid layer-19 collider object.
        var detectObjects = new StringBuilder();
        EnsureCollisionDetectOn(gameObject, detectObjects);
        for (int i = 0; i < ownedColliders.Length; i++)
        {
            Collider ownedCollider = ownedColliders[i];
            if (ownedCollider == null || ownedCollider.isTrigger ||
                ownedCollider.gameObject.layer != EnemyHitboxLayer)
                continue;
            EnsureCollisionDetectOn(ownedCollider.gameObject, detectObjects);
        }

        if (created && DebugLogs)
        {
            LogInfo(
                $"[BundyHitbox] created bone={transform.name} layer={hitboxObject.layer} " +
                $"capsuleCenter={FormatVector(collider.center)} radius={collider.radius:F2} height={collider.height:F2} " +
                $"isTrigger={collider.isTrigger} mainScriptWired={_runtimeBodyHitbox.mainScript == this} " +
                $"detectObjects=[{detectObjects}]");
        }
    }

    private void EnsureCollisionDetectOn(GameObject target, StringBuilder detectObjects)
    {
        var detect = target.GetComponent<EnemyAICollisionDetect>();
        if (detect == null)
            detect = target.AddComponent<EnemyAICollisionDetect>();
        detect.mainScript = this;
        detect.onlyCollideWhenGrounded = false;
        detect.canCollideWithEnemies = false;
        if (detectObjects.Length > 0)
            detectObjects.Append(", ");
        detectObjects.Append(target.name).Append(':').Append(target.layer);
    }

    private void LogRuntimeDiagnosticsOnce()
    {
        if (_loggedRuntimeDiagnostics)
            return;

        _loggedRuntimeDiagnostics = true;

        try
        {
            Transform meshTransform = FindChildRecursive(transform, "Mesh_0");
            string meshScale = meshTransform != null
                ? $"Mesh_0Local={FormatVector(meshTransform.localScale)}, Mesh_0Lossy={FormatVector(meshTransform.lossyScale)}"
                : "Mesh_0=<missing>";

            LogDiagnostic(
                $"Runtime diagnostics scale: rootLocal={FormatVector(transform.localScale)}, " +
                $"rootLossy={FormatVector(transform.lossyScale)}, {meshScale}");

            LogAnimatorDiagnostics();
            LogSkinnedMeshDiagnostics();
        }
        catch (Exception e)
        {
            LogDiagnostic($"Runtime diagnostics exception: {e}");
        }
    }

    private void RunSkinMaterialDiagnosticsForInstance()
    {
        if (!IsRigLabMode)
            return;

        bool diagnosticsEnabled = BundyPlugin.ModConfig?.ChainsawMaterialDiagnostics?.Value ?? false;
        bool lowSpecTest = BundyPlugin.ModConfig?.ChainsawMaterialLowSpecTest?.Value ?? false;
        bool normalOffTest = BundyPlugin.ModConfig?.ChainsawMaterialNormalOffTest?.Value ?? false;
        bool matteSkinTest = BundyPlugin.ModConfig?.ChainsawMaterialMatteSkinTest?.Value ?? false;
        bool headMatchTest = BundyPlugin.ModConfig?.ChainsawMaterialHeadMatchTest?.Value ?? false;
        if (!diagnosticsEnabled && !lowSpecTest && !normalOffTest && !matteSkinTest && !headMatchTest)
            return;

        Transform visual = FindChildRecursive(transform, "Visual") ?? transform;
        BundySkinMaterialDiagnostics diagnostics = visual.GetComponent<BundySkinMaterialDiagnostics>();
        bool existed = diagnostics != null;
        if (diagnostics == null)
            diagnostics = visual.gameObject.AddComponent<BundySkinMaterialDiagnostics>();

        diagnostics.diagnosticsEnabled = diagnosticsEnabled;
        diagnostics.lowSpecTest = lowSpecTest;
        diagnostics.normalOffTest = normalOffTest;
        diagnostics.matteSkinTest = matteSkinTest;
        diagnostics.headMatchTest = headMatchTest;
        int matchedMaterials = BundySkinMaterialDiagnostics.RunMaterialPass(
            visual,
            message => LogDiagnostic(message),
            diagnosticsEnabled,
            lowSpecTest,
            normalOffTest,
            matteSkinTest,
            "ai-start",
            headMatchTest);
        diagnostics.RunNow("ai-start", force: true);

        LogDiagnostic(
            $"BundyMaterialDiag runtime trigger visual='{visual.name}', componentExisted={existed}, " +
            $"matchedMaterials={matchedMaterials}, diagnostics={diagnosticsEnabled}, lowSpecTest={lowSpecTest}, " +
            $"normalOffTest={normalOffTest}, matteSkinTest={matteSkinTest}, headMatchTest={headMatchTest}");
    }

    // Re-runs the cosmetic tint pass on this live instance. Called by
    // BundyDebugApi when the Bundy debug tab changes a tint slider so the
    // change is visible without respawning.
    // #374 Phase 0: the last decal-reception verdict for this instance's per-instance skin
    // material copies, in the short form the Bundy debug tab shows. Set by
    // ApplyBodySkinTintForInstance on every spawn and on every re-apply.
    private string _skinDecalVerdict = "not evaluated";

    public string SkinDecalVerdict => _skinDecalVerdict;

    public void ReapplyCosmeticTints()
    {
        ApplyBodySkinTintForInstance();
    }

    // #374 Phase 0 gate. Forces blood decals onto this instance so "can Bundy's skin show an
    // HDRP decal at all" can be answered by looking at him, without waiting on the damage
    // replication and multi-bone anchoring the rest of #374 needs. Reached from BundyDebugApi
    // and from Ctrl+Alt+F3, the same route SetSkinTint -> ReapplyCosmeticTints takes.
    //
    // Three bones, not one, because a single chest probe cannot tell "the material rejects
    // decals" apart from "the chest is under the apron". If the forearm paints and the chest
    // does not, the answer is coverage and the gate has passed.
    //
    // Returns the number of decals actually placed.
    public int ForceSkinDecalProbe()
    {
        try
        {
            // Forces the bundle load; HasBodyPrefabs on its own does not.
            if (!BundyBloodFXBridge.BodyPrefabsAvailable)
            {
                LogWarning("[BundyDecalGate] probe skipped: BloodFX body decal prefabs are unavailable.");
                return 0;
            }

            if (creatureAnimator == null)
            {
                LogWarning("[BundyDecalGate] probe skipped: no creatureAnimator, so the rig cannot be resolved.");
                return 0;
            }

            // Same resolution the rest of the rig code uses (BundyChainsawHandIK.ResolveSpineBones,
            // BundyProceduralFlinch.Build): canonical armature first, animator root as fallback.
            Transform armature = FindChildRecursive(creatureAnimator.transform, "Armature");
            Transform searchRoot = armature != null ? armature : creatureAnimator.transform;

            PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            if (local == null && StartOfRound.Instance != null)
                local = StartOfRound.Instance.localPlayerController;
            Transform eye = local != null && local.gameplayCamera != null ? local.gameplayCamera.transform : null;

            int placed = 0;
            // Left forearm, not right: the chainsaw is held in the right hand, and a decal that
            // lands on the saw prop instead of the arm would read as a pass it has not earned.
            placed += PlaceGateProbeOnBone(searchRoot, eye, "spine_03", "spine_02", 0.55f, 0.55f, 1.10f);
            placed += PlaceGateProbeOnBone(searchRoot, eye, "lowerarm_l", "upperarm_l", 0.28f, 0.30f, 0.60f);
            placed += PlaceGateProbeOnBone(searchRoot, eye, "head", "neck_01", 0.30f, 0.32f, 0.64f);

            LogWarning(
                $"[BundyDecalGate] probe placed {placed} decal(s) on '{name}' " +
                $"(armature={(armature != null ? armature.name : "<fallback: animator root>")}, " +
                $"camera={(eye != null ? "local" : "<none, using Bundy forward>")}). " +
                "Look at his chest, left forearm and head.");
            return placed;
        }
        catch (Exception e)
        {
            LogWarning($"[BundyDecalGate] probe exception: {e}");
            return 0;
        }
    }

    private int PlaceGateProbeOnBone(
        Transform searchRoot,
        Transform eye,
        string boneName,
        string fallbackBoneName,
        float size,
        float standoff,
        float depth)
    {
        Transform bone = FindChildRecursive(searchRoot, boneName);
        if (bone == null && !string.IsNullOrEmpty(fallbackBoneName))
            bone = FindChildRecursive(searchRoot, fallbackBoneName);
        if (bone == null)
        {
            LogWarning($"[BundyDecalGate] bone '{boneName}' (fallback '{fallbackBoneName}') not found on the rig.");
            return 0;
        }

        // Face the projector at whoever is looking, so "I was standing behind him" is never the
        // explanation for an invisible probe. Bundy's own forward is the fallback, and it is only
        // a fallback because ApplyVisualYawCorrection can put the visual off the AI root's facing.
        Vector3 outward = eye != null ? eye.position - bone.position : transform.forward;

        return BundyBloodFXBridge.PlaceGateProbeDecal(
            GetInstanceID(), bone, outward, size, standoff, depth, boneName)
            ? 1
            : 0;
    }

    // The bundle bakes a default body-skin tint into Bundy_R27_Skin_HDRP, but
    // matching the CC5 head under game lighting has taken several rebuild
    // round-trips already. These config knobs re-apply the tint on the spawned
    // instance's material copies so the tone can be dialed live; with the
    // config values equal to the baked default this is a no-op visually.
    // #277: no rig-lab gate here — the DebugTools tint sliders must work on normally
    // spawned Bundys too. Rig-lab-only material DIAGNOSTICS stay gated above.
    private void ApplyBodySkinTintForInstance()
    {
        try
        {
            float r = BundyPlugin.ModConfig?.ChainsawBodySkinTintR?.Value ?? 0.72f;
            float g = BundyPlugin.ModConfig?.ChainsawBodySkinTintG?.Value ?? 0.81f;
            float b = BundyPlugin.ModConfig?.ChainsawBodySkinTintB?.Value ?? 0.91f;
            float hairR = BundyPlugin.ModConfig?.ChainsawHairTintR?.Value ?? 0.70f;
            float hairG = BundyPlugin.ModConfig?.ChainsawHairTintG?.Value ?? 0.85f;
            float hairB = BundyPlugin.ModConfig?.ChainsawHairTintB?.Value ?? 1.00f;
            var skinTint = new Color(r, g, b, 1f);
            var hairTint = new Color(hairR, hairG, hairB, 1f);

            // The shipping CC5/R35b candidate keeps its Animator and armature under Visual, but
            // R27_CurrentBody_Renderers is a sibling on the Bundy instance root. Material work
            // therefore scans the whole instance; restricting this to Visual silently finds zero.
            Transform materialRoot = transform;
            Renderer[] materialRenderers = materialRoot.GetComponentsInChildren<Renderer>(true);
            int appliedSkin = 0;
            int appliedHair = 0;

            // #374 Phase 0 tallies. Built unconditionally rather than behind a log gate: this
            // runs a handful of times a session (spawn, plus each debug tint edit), and the
            // gate answer is exactly the kind that has previously gone missing because the
            // flag that would have printed it was off.
            int decalsReceptive = 0;
            int decalsTransparent = 0;
            int decalsBlocked = 0;
            var decalAudit = new StringBuilder(384);

            // The hair/scalp live on a plain MeshRenderer (the one unskinned
            // facial part), so scan all renderer types, not just skinned ones.
            foreach (Renderer renderer in materialRenderers)
            {
                bool touchesSkin = false;
                bool touchesHair = false;
                foreach (Material shared in renderer.sharedMaterials)
                {
                    if (shared == null)
                        continue;
                    if (BundySkinMaterialDiagnostics.IsBundyBodySkinMaterial(renderer, shared))
                        touchesSkin = true;
                    if (shared.name.IndexOf("Hair_Transparency", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        shared.name.IndexOf("Scalp_Transparency", StringComparison.OrdinalIgnoreCase) >= 0)
                        touchesHair = true;
                }
                if (!touchesSkin && !touchesHair)
                    continue;

                // renderer.materials instantiates per-instance copies, so the
                // shared bundle materials (and other spawns) stay untouched.
                foreach (Material material in renderer.materials)
                {
                    if (material == null)
                        continue;

                    bool isSkin = BundySkinMaterialDiagnostics.IsBundyBodySkinMaterial(renderer, material);
                    bool isHair = material.name.IndexOf("Hair_Transparency", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                  material.name.IndexOf("Scalp_Transparency", StringComparison.OrdinalIgnoreCase) >= 0;

                    // #374 Phase 0: make the skin a decal receiver on these same per-instance
                    // copies. Sits ahead of the _BaseColor guard on purpose — decal reception is
                    // _SupportDecals / _DISABLE_DECALS / render queue, none of which involve
                    // _BaseColor, so a skin material without that property must still be fixed.
                    // Hair and scalp are alpha-blended and can never receive decals; they are
                    // left out rather than attempted and reported as failures every spawn.
                    if (isSkin)
                    {
                        decalAudit.Append(" [").Append(renderer.name).Append(']');
                        switch (BundyBloodFXBridge.MakeInstanceMaterialReceiveDecals(material, decalAudit))
                        {
                            case BundyDecalReception.OpaqueDecalBand:
                                decalsReceptive++;
                                break;
                            case BundyDecalReception.Transparent:
                                decalsTransparent++;
                                break;
                            default:
                                decalsBlocked++;
                                break;
                        }
                    }

                    if (!material.HasProperty("_BaseColor"))
                        continue;

                    if (isSkin)
                    {
                        material.SetColor("_BaseColor", skinTint);
                        appliedSkin++;
                    }
                    else if (isHair)
                    {
                        // Hair/scalp are alpha-blended; keep the authored base
                        // alpha so the tint never changes coverage/opacity.
                        Color existing = material.GetColor("_BaseColor");
                        material.SetColor("_BaseColor", new Color(hairTint.r, hairTint.g, hairTint.b, existing.a));
                        appliedHair++;
                    }
                }
            }

            LogDiagnostic(
                $"Body skin tint applied: skin=({r:F2},{g:F2},{b:F2}) x{appliedSkin}, " +
                $"hair=({hairR:F2},{hairG:F2},{hairB:F2}) x{appliedHair}");

            bool decalGatePassed = decalsReceptive > 0 && decalsTransparent == 0 && decalsBlocked == 0;

            // One compact status feeds both the string-bound Bundy debug tab and the durable log.
            // The explicit re-apply button is an acceptance probe, so PASS must not disappear
            // behind the normal diagnostic log gate.
            _skinDecalVerdict =
                $"{(decalGatePassed ? "PASS" : "FAIL")} receptive={decalsReceptive} " +
                $"transparent={decalsTransparent} blocked={decalsBlocked}";

            string decalVerdict =
                $"[BundyDecalGate] {_skinDecalVerdict} root='{materialRoot.name}' " +
                $"renderers={materialRenderers.Length}." + decalAudit;
            LogWarning(decalVerdict);
        }
        catch (Exception e)
        {
            LogDiagnostic($"Body skin tint exception: {e}");
        }
    }

    private void LogAnimatorDiagnostics()
    {
        if (creatureAnimator == null)
        {
            LogDiagnostic("Runtime diagnostics animator: <null>");
            return;
        }

        Animator animator = creatureAnimator;
        RuntimeAnimatorController controller = animator.runtimeAnimatorController;
        Avatar avatar = animator.avatar;
        int parameterCount = -1;
        bool motionStateExists = false;
        bool motionStateIsInt = false;
        string motionStateValue = "<unavailable>";

        try
        {
            AnimatorControllerParameter[] parameters = animator.parameters;
            parameterCount = parameters != null ? parameters.Length : 0;

            if (parameters != null)
            {
                foreach (AnimatorControllerParameter parameter in parameters)
                {
                    if (parameter.name != BundyAnimatorParams.MotionState)
                        continue;

                    motionStateExists = true;
                    motionStateIsInt = parameter.type == AnimatorControllerParameterType.Int;
                    break;
                }
            }

            if (motionStateIsInt)
                motionStateValue = animator.GetInteger(BundyAnimatorParams.MotionState).ToString();
        }
        catch (Exception e)
        {
            motionStateValue = "<error: " + e.GetType().Name + ">";
        }

        LogDiagnostic(
            $"Runtime diagnostics animator: object='{animator.name}', enabled={animator.enabled}, " +
            $"controller='{ObjectName(controller)}', avatar='{ObjectName(avatar)}', " +
            $"avatarValid={(avatar != null && avatar.isValid)}, avatarHuman={(avatar != null && avatar.isHuman)}, " +
            $"parameterCount={parameterCount}, MotionStateExists={motionStateExists}, " +
            $"MotionStateIsInt={motionStateIsInt}, MotionStateValue={motionStateValue}");

        LogAnimatorStateDiagnostics(animator);
    }

    private void LogAnimatorStateDiagnostics(Animator animator)
    {
        try
        {
            if (animator.runtimeAnimatorController == null)
            {
                LogDiagnostic("Runtime diagnostics animator state: no runtimeAnimatorController.");
                return;
            }

            if (animator.layerCount <= 0)
            {
                LogDiagnostic(
                    $"Runtime diagnostics animator state: layerCount={animator.layerCount}, " +
                    $"isInitialized={animator.isInitialized}");
                return;
            }

            AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
            LogDiagnostic(
                $"Runtime diagnostics animator state: layerCount={animator.layerCount}, " +
                $"isInitialized={animator.isInitialized}, shortHash={state.shortNameHash}, " +
                $"fullHash={state.fullPathHash}, normalizedTime={state.normalizedTime:F3}");
        }
        catch (Exception e)
        {
            LogDiagnostic($"Runtime diagnostics animator state exception: {e.GetType().Name}: {e.Message}");
        }
    }

    private void LogThrottledAnimatorState()
    {
        if (!ShouldLog(BundyLogCategory.Anim, 2) || Time.time < _nextAnimatorStateDiagnosticAt)
            return;

        _nextAnimatorStateDiagnosticAt = Time.time + AnimatorStateDiagnosticIntervalSeconds;
        if (creatureAnimator == null || creatureAnimator.runtimeAnimatorController == null || creatureAnimator.layerCount <= 0)
            return;

        try
        {
            AnimatorStateInfo state = creatureAnimator.GetCurrentAnimatorStateInfo(0);
            LogInfo(
                BundyLogCategory.Anim,
                2,
                $"[BundyAnimState] clips={FormatClipInfo(creatureAnimator.GetCurrentAnimatorClipInfo(0))} " +
                $"shortNameHash={state.shortNameHash} normalizedTime={state.normalizedTime:F3} " +
                $"inTransition={creatureAnimator.IsInTransition(0)} actionKind={_actionKind} " +
                $"agentVelocityMagnitude={(agent != null ? agent.velocity.magnitude : 0f):F3} " +
                $"MotionState={creatureAnimator.GetInteger(BundyAnimatorParams.MotionState)} " +
                $"MoveX={creatureAnimator.GetFloat(BundyAnimatorParams.MoveX):F3} " +
                $"MoveY={creatureAnimator.GetFloat(BundyAnimatorParams.MoveY):F3} " +
                $"Speed={creatureAnimator.GetFloat(BundyAnimatorParams.Speed):F3} " +
                FormatSkeletonTruth());
        }
        catch (Exception e)
        {
            LogInfo($"[BundyAnimState] error={e.GetType().Name}:{e.Message}");
        }
    }

    // Skeleton-truth fields for the in-game pose divergence investigation (#87):
    // world hand positions, hand-to-hand distance (editor Animator repro reads a
    // constant 0.5201 m on every movement clip), saw prop position relative to
    // hand_r, and body yaw vs travel yaw. Read in Update, so values are the
    // previous frame's fully evaluated pose — after the Animator and every
    // LateUpdate writer, i.e. what was actually rendered.
    private string FormatSkeletonTruth()
    {
        if (_animStateHandL == null || _animStateHandR == null)
        {
            Transform armature = creatureAnimator != null
                ? FindChildRecursive(creatureAnimator.transform, "Armature")
                : null;
            if (armature != null)
            {
                if (_animStateHandL == null)
                    _animStateHandL = FindChildRecursive(armature, "hand_l");
                if (_animStateHandR == null)
                    _animStateHandR = FindChildRecursive(armature, "hand_r");
            }
        }

        if (_animStateSawProp == null)
            _animStateSawProp = FindChildRecursive(transform, RuntimeChainsawPropName);

        if (_animStateHandL == null || _animStateHandR == null)
            return "skel=<hands-unresolved>";

        Vector3 handL = _animStateHandL.position;
        Vector3 handR = _animStateHandR.position;
        string sawInfo = "saw=<unresolved>";
        if (_animStateSawProp != null)
        {
            if (_animStateSawRenderer == null)
                _animStateSawRenderer = _animStateSawProp.GetComponentInChildren<Renderer>(true);

            Vector3 saw = _animStateSawProp.position;
            sawInfo =
                $"saw={FormatVector(saw)} sawToHandR={Vector3.Distance(saw, handR):F4} " +
                $"sawToHandL={Vector3.Distance(saw, handL):F4}";
            // The saw's own Animator (Chainsaw_Anims) writes constant TRS onto
            // children of BundyProp_Chainsaw every frame, so the prop root can
            // read correct while the visible mesh sits elsewhere — log the
            // rendered bounds center too.
            if (_animStateSawRenderer != null)
            {
                Vector3 sawVis = _animStateSawRenderer.bounds.center;
                sawInfo +=
                    $" sawVis={FormatVector(sawVis)} sawVisToHandR={Vector3.Distance(sawVis, handR):F4} " +
                    $"sawVisToHandL={Vector3.Distance(sawVis, handL):F4}";
            }
        }

        Vector3 velocity = agent != null ? agent.velocity : Vector3.zero;
        string travelYaw = velocity.sqrMagnitude > 0.01f
            ? $"{Mathf.Atan2(velocity.x, velocity.z) * Mathf.Rad2Deg:F1}"
            : "n/a";

        return
            $"handL={FormatVector(handL)} handR={FormatVector(handR)} " +
            $"handDist={Vector3.Distance(handL, handR):F4} {sawInfo} " +
            $"bodyYaw={transform.eulerAngles.y:F1} travelYaw={travelYaw}";
    }

    // [BundyAttackSeam] per-frame window: ±0.5 s around the attack exit seam, whichever exit path
    // runs (DLL visual-exit CrossFade in rig-lab, authored 0.85 -> Idle transition in gameplay).
    // Expected H1 signature: pelvis-vs-transform yaw delta grows through the swing, then collapses
    // to ~0 across 2-3 frames at the seam.
    private void UpdateAttackSeamSampler()
    {
        // Gated here as well as in LogAttackSeamEvent so the per-frame animator state reads and clip
        // lookups below are skipped too, not just the write.
        if (!ShouldLog(BundyLogCategory.Anim, 2))
            return;

        bool inTail = Time.time < _attackSeamTailLogUntil;
        bool preExitWindow = false;
        if (_actionKind == ActionKind.Attack && _activeAttackIndex >= 0 &&
            creatureAnimator != null && creatureAnimator.runtimeAnimatorController != null &&
            creatureAnimator.layerCount > 0)
        {
            float threshold = GetBasicAttackVisualExitNormalizedTime(_activeAttackIndex);
            if (!IsValidBasicAttackVisualExitThreshold(threshold))
                threshold = 0.85f; // authored controller exit time when no DLL-side exit is configured
            try
            {
                // ~0.5 s of pre-exit frames on the 2.3-3.5 s attack clips ~= 0.22 normalized. The
                // attack state stays current through the exit transition, so the blend frames land here.
                float normalizedTime = creatureAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                AnimatorClipInfo[] clips = creatureAnimator.GetCurrentAnimatorClipInfo(0);
                preExitWindow = TryGetBasicAttackClip(clips, out _, out _) &&
                    normalizedTime >= threshold - 0.22f;
            }
            catch (Exception)
            {
                preExitWindow = false;
            }
        }

        if (preExitWindow)
        {
            _attackSeamPreExitActiveLastFrame = true;
        }
        else if (_attackSeamPreExitActiveLastFrame)
        {
            // The attack clip just left layer 0 (CrossFade landed or the authored transition
            // finished) — keep sampling through the pose that replaced it.
            _attackSeamPreExitActiveLastFrame = false;
            _attackSeamTailLogUntil = Mathf.Max(_attackSeamTailLogUntil, Time.time + 0.5f);
            inTail = true;
        }

        if (preExitWindow || inTail)
            LogAttackSeamEvent(preExitWindow ? "pre-exit-frame" : "post-exit-frame");
    }

    private void LogAttackSeamEvent(string phase)
    {
        // Job 1: the single largest source of Bundy log volume — 1,275 lines in run 5, 43% of
        // everything he printed, because it scales with attacks and run 5 finally had ten of them.
        // Rig-lab forensics for a finished workstream (#87), so LogAnim / verbosity 2.
        if (!ShouldLog(BundyLogCategory.Anim, 2))
            return;

        try
        {
            ResolveAttackSeamPelvis();
            float transformYaw = transform.eulerAngles.y;
            string pelvisInfo = "pelvis=<unresolved>";
            if (_attackSeamPelvis != null)
            {
                // The rig's bone-forward convention is unproven (R32: the HEAD's look axis was
                // local -Y), so log both candidate body-forward projections; the seam collapse
                // shows up in whichever axis is the true forward.
                float pelvisFwdYaw = ProjectedYawDegrees(_attackSeamPelvis.rotation * Vector3.forward);
                float pelvisNegYYaw = ProjectedYawDegrees(_attackSeamPelvis.rotation * -Vector3.up);
                pelvisInfo =
                    $"pelvisFwdYaw={pelvisFwdYaw:F1} deltaFwd={Mathf.DeltaAngle(transformYaw, pelvisFwdYaw):F1} " +
                    $"pelvisNegYYaw={pelvisNegYYaw:F1} deltaNegY={Mathf.DeltaAngle(transformYaw, pelvisNegYYaw):F1}";
            }

            string animatorInfo = "animator=<unavailable>";
            if (creatureAnimator != null && creatureAnimator.runtimeAnimatorController != null &&
                creatureAnimator.layerCount > 0)
            {
                AnimatorStateInfo state = creatureAnimator.GetCurrentAnimatorStateInfo(0);
                animatorInfo =
                    $"clips={FormatClipInfo(creatureAnimator.GetCurrentAnimatorClipInfo(0))} " +
                    $"normalizedTime={state.normalizedTime:F3} inTransition={creatureAnimator.IsInTransition(0)}";
            }

            Vector3 velocity = agent != null ? agent.velocity : Vector3.zero;
            string travelYaw = velocity.sqrMagnitude > 0.01f
                ? $"{Mathf.Atan2(velocity.x, velocity.z) * Mathf.Rad2Deg:F1}"
                : "n/a";
            int writerAge = _attackSeamLastFacingWriterFrame >= 0
                ? Time.frameCount - _attackSeamLastFacingWriterFrame
                : -1;

            LogInfo(
                $"[BundyAttackSeam] phase={phase} frame={Time.frameCount} attackIndex={_activeAttackIndex} " +
                $"actionKind={_actionKind} attackPhase={_attackPhase} maneuver={_currentCombatBehavior} " +
                $"transformYaw={transformYaw:F1} {pelvisInfo} {animatorInfo} " +
                $"agentVel={velocity.magnitude:F3} travelYaw={travelYaw} " +
                $"facingWriter={_attackSeamLastFacingWriter} writerFrameAge={writerAge}");
        }
        catch (Exception e)
        {
            LogInfo($"[BundyAttackSeam] phase={phase} error={e.GetType().Name}:{e.Message}");
        }
    }

    private void ResolveAttackSeamPelvis()
    {
        if (_attackSeamPelvisResolved || creatureAnimator == null)
            return;

        Transform armature = FindChildRecursive(creatureAnimator.transform, "Armature");
        if (armature != null)
            _attackSeamPelvis = FindChildRecursive(armature, "pelvis");
        _attackSeamPelvisResolved = _attackSeamPelvis != null;
    }

    private void NoteFacingWriter(string writer)
    {
        _attackSeamLastFacingWriter = writer;
        _attackSeamLastFacingWriterFrame = Time.frameCount;
    }

    private static float ProjectedYawDegrees(Vector3 direction)
    {
        direction.y = 0f;
        if (direction.sqrMagnitude < 0.0001f)
            return float.NaN;
        return Mathf.Atan2(direction.x, direction.z) * Mathf.Rad2Deg;
    }

    private void LogSkinnedMeshDiagnostics()
    {
        SkinnedMeshRenderer[] renderers = GetComponentsInChildren<SkinnedMeshRenderer>(true);
        LogDiagnostic($"Runtime diagnostics SkinnedMeshRenderer count={renderers.Length}");

        for (int i = 0; i < renderers.Length; i++)
        {
            SkinnedMeshRenderer renderer = renderers[i];
            if (renderer == null)
                continue;

            LogDiagnostic(
                $"Runtime diagnostics SkinnedMeshRenderer[{i}] '{renderer.name}': " +
                $"enabled={renderer.enabled}, activeInHierarchy={renderer.gameObject.activeInHierarchy}, " +
                $"localBoundsSize={FormatVector(renderer.localBounds.size)}, " +
                $"worldBoundsSize={FormatVector(renderer.bounds.size)}");
        }
    }

    // 2026-07-23: a pushed-out Stranger spawn produced "Failed to create agent because it is not
    // close enough to the NavMesh" and Bundy stood frozen at the spawn point for the whole round.
    // Nothing in vanilla recovers from a failed agent placement, so the owner re-seats the agent.
    private static readonly float[] AgentNavMeshRecoveryRadii = { 2f, 5f, 10f, 20f, 35f };
    private float _nextAgentNavMeshRecoveryAt;

    private void EnsureAgentOnNavMesh()
    {
        if (!IsOwner || isEnemyDead || agent == null || !agent.enabled || agent.isOnNavMesh)
            return;
        // #188: this is the 35m warp that teleported a knocked-down Bundy across the room. While the
        // knockdown pin owns the root it also owns re-seating the agent (from a position sampled onto
        // the graph at capture), so this recovery must not race it.
        if (_knockdownRootPinActive)
            return;
        if (Time.time < _nextAgentNavMeshRecoveryAt)
            return;

        _nextAgentNavMeshRecoveryAt = Time.time + 1f;
        Vector3 origin = transform.position;
        foreach (float radius in AgentNavMeshRecoveryRadii)
        {
            if (!NavMesh.SamplePosition(origin, out NavMeshHit hit, radius, NavMesh.AllAreas))
                continue;
            if (!agent.Warp(hit.position))
                continue;

            LogWarning(
                $"Agent was off the NavMesh at {FormatVector(origin)}; warped " +
                $"{Vector3.Distance(origin, hit.position):F1}m to {FormatVector(hit.position)}.");
            return;
        }

        LogWarning($"Agent off the NavMesh at {FormatVector(origin)} and no recovery sample found within 35m; retrying.");
    }

    private void ConfigureAgent()
    {
        if (agent == null)
        {
            WarnOnce(ref _warnedMissingAgent, "Missing NavMeshAgent; Bundy cannot move.");
            return;
        }

        if (creatureAnimator != null)
            creatureAnimator.applyRootMotion = false;

        openDoorSpeedMultiplier = VanillaDoorOpenSpeedMultiplier;
        agent.speed = WalkSpeed;
        agent.stoppingDistance = StopDistance;
        agent.angularSpeed = AgentAngularSpeedDegrees;
        agent.acceleration = Mathf.Max(agent.acceleration, AgentAcceleration);
        agent.autoBraking = true;
        agent.autoRepath = true;
        agent.autoTraverseOffMeshLink = true;
        agent.obstacleAvoidanceType = ObstacleAvoidanceType.HighQualityObstacleAvoidance;
        agent.avoidancePriority = NavigationAgentAvoidancePriority;
        if (agent.radius > NavigationAgentMaxInteriorRadius)
            agent.radius = NavigationAgentMaxInteriorRadius;
        agent.updatePosition = true;
        agent.updateRotation = !ManualPursuitControlsAgentRotation;
        BundySmartPathingBridge.TryRegisterSmartAgent(agent);
    }

    // Applied live every frame (Stage 3) so the yaw can be dialed in-game via config without a
    // respawn. Rigidly yaws the visual child relative to the root; the animated bones ride on top,
    // so the internal pose is preserved while the whole body/feet re-square to the facing/travel
    // direction. Runs on all clients (cosmetic). Logs only when the value changes, to avoid spam.
    private void ApplyVisualYawCorrection()
    {
        if (creatureAnimator == null || creatureAnimator.transform == transform)
            return;

        // Only square the locomotion/idle stance. During actions (basic attacks, paired finishers)
        // the victim proxy and damage-contact checks are anchored to the ROOT, so the body must stay
        // aligned to root — apply identity there to protect finisher/attack alignment. Run uses its
        // own yaw + roll (the run clip can be bladed/leaned differently from the walk). Run/walk is
        // read from the synced MotionState param so the correction is correct on every client.
        float yaw;
        float roll;
        if (IsActionLocked())
        {
            yaw = 0f;
            roll = 0f;
        }
        else if (creatureAnimator.GetInteger(BundyAnimatorParams.MotionState) == BundyAnimatorParams.MotionRun)
        {
            yaw = RunVisualYawCorrectionDegrees;
            roll = RunVisualRollCorrectionDegrees;
        }
        else
        {
            yaw = VisualYawCorrectionDegrees;
            roll = 0f;
        }

        creatureAnimator.transform.localRotation = Quaternion.Euler(0f, yaw, roll);

        if (!Mathf.Approximately(yaw, _lastAppliedVisualYaw) || !Mathf.Approximately(roll, _lastAppliedVisualRoll))
        {
            _lastAppliedVisualYaw = yaw;
            _lastAppliedVisualRoll = roll;
            LogInfo($"Visual stance correction applied: yaw={yaw:F1}°, roll={roll:F1}°.");
        }
    }

    private bool RoundReady()
    {
        return StartOfRound.Instance != null
            && RoundManager.Instance != null
            && !StartOfRound.Instance.allPlayersDead;
    }

    private bool AgentReadyForMovement()
    {
        if (agent == null)
        {
            WarnOnce(ref _warnedMissingAgent, "Missing NavMeshAgent; Bundy cannot move.");
            return false;
        }

        if (!agent.enabled)
            return false;

        if (!agent.isOnNavMesh)
        {
            if (TryRecoverAgentOntoNavMesh())
                return true;

            WarnOnce(ref _warnedAgentNotOnNavMesh, "NavMeshAgent is not on a NavMesh; movement paused.");
            return false;
        }

        return true;
    }

    private bool TryRecoverAgentOntoNavMesh()
    {
        if (agent == null || !agent.enabled)
            return false;

        if (NavMesh.SamplePosition(transform.position, out NavMeshHit hit, 5f, NavMesh.AllAreas))
        {
            if (TryWarpAgentToNavPosition(hit.position, "nearby navmesh recovery"))
            {
                return true;
            }
        }

        PlayerControllerB target = GetCurrentHuntTarget() ?? _primaryTarget ?? targetPlayer;
        if (TryFindAINodeRecoveryPosition(target, out Vector3 recoveryPosition, out string recoveryMode) &&
            TryWarpAgentToNavPosition(recoveryPosition, $"AI-node navmesh recovery/{recoveryMode}"))
        {
            return true;
        }

        return false;
    }

}
