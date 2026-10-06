using GameNetcodeStuff;
using Y4NGZCompany.Bootstrap;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.AI;

// #323. The finisher gore soundtrack: a three-phase cue assembled at RUNTIME against the paired
// action's own clock, rather than one baked track per variant.
//
// Why assembled and not baked. The three chainsaw finishers do not share a length (5.00s / 3.33s /
// 4.67s from the paired-action catalog) and the blade is only inside the body for the first 78% of
// each (damageNormalizedTime), so the "inside" window is 3.90s / 2.60s / 3.64s — three different
// durations that a single baked track cannot serve without either trailing into silence or being
// cut off mid-squelch. Assembling from an impact + a fitted bed + a peak-led removal makes the
// same seven source takes cover all three, and covers a fourth case a baked track cannot: an
// INTERRUPTED finisher, where the cut simply stops early and every phase has to fade out now.
//
// Why it is client-local. The paired-finisher begin/release ClientRpcs already run on every client
// at exactly the frames the choreography starts and ends (that is how #297's saw bog and #321's
// collision suppression are replicated), and every client already has the paired-action catalog
// loaded — so each listener can compute the whole schedule itself from the action id the begin RPC
// carries. That is replication for free, in perfect sync with the local visual state, and zero
// extra network traffic. There is no server-authoritative decision to make here: nothing about a
// sound affects simulation.
//
// Where it plays. On its OWN emitter, parented to the victim's chest — never on the engine loop
// sources (those are being low-passed at the same time, see the muffle below) and, since the first
// field report, no longer on Bundy's blade contact emitter either.
//
// Why it moved off the blade emitter (first playtest of #323: "so quiet I could barely hear them;
// they should play at the actual location of the player"). Three things were stacking against it
// there:
//   1. Source level. The takes are gain-matched to -27 LUFS (bed -28), roughly 8-10 dB under a
//      normal game one-shot, and they were played at a flat 1.0 gain. That is the dominant term,
//      and ChainsawFinisherGoreVolume is the direct answer to it.
//   2. The curve. BundyAudioProfile.SawContact is the one saw curve deliberately allowed to
//      collapse (0.36 at 5m, 0.16 at 10m) because it carries hit transients. The blood-FX lines
//      from the same run put the viewer at 3.7-4.5m, i.e. the gore was already down to roughly
//      half amplitude for a bystander standing right next to the execution.
//   3. OccludeAudio. The blade emitter carries one, and during a finisher the blade is INSIDE the
//      victim — the listener-to-source linecast passes through the victim's own body colliders, so
//      the wound sound was being treated as occluded by the body making it. The new emitter carries
//      no OccludeAudio for exactly that reason; the engine loop still carries its own.
// Parenting to the victim also answers the positional half of the report directly: the emitter
// follows the body being cut instead of the tool doing the cutting.
public abstract partial class BundyAIBase
{
    // ---- schedule ------------------------------------------------------------------------------

    private bool _finisherGoreAudioActive;
    private float _finisherGoreCutEndAt;
    private bool _finisherGoreBedStarted;
    private bool _finisherGoreRemoveFired;
    private float _finisherGoreBedStartAt;
    private float _finisherGoreRemoveAt;
    private AudioSource _finisherGoreBedSource;
    private GameObject _finisherGoreEmitter;     // parented to the victim's chest for the cut
    private AudioSource _finisherGoreEmitterSource;
    private bool _finisherGoreLocalVictim;       // this client's own player is the one being cut
    private float _finisherGoreBedGain;          // the bed's own fade envelope, 0..1
    private float _finisherGoreBedFadeOutFrom = float.PositiveInfinity; // time the release fade begins
    private float _finisherGoreBedFadeSeconds = FinisherGoreBedFadeOutSeconds;
    private int _finisherGoreRemoveIndex = -1;

    // The 0.78 damageNormalizedTime the catalog ships for all three variants is the kill frame, and
    // the kill frame IS the pull-out — that is where OnFinisherImpact lands the blood and the death.
    // Used only when the action id cannot be resolved.
    private const float FinisherGoreFallbackCutSeconds = 3.2f;
    private const float FinisherGoreFallbackNormalizedCut = 0.78f;

    // The bed enters under the impact's transient rather than after it: the takes both decay past
    // 0.5s and a bed that waited for silence would leave an audible hole where the blade is already
    // buried.
    private const float FinisherGoreBedLeadSeconds = 0.18f;
    private const float FinisherGoreBedFadeInSeconds = 0.1f;
    private const float FinisherGoreBedFadeOutSeconds = 0.25f;
    private const float FinisherGoreBedInterruptFadeSeconds = 0.12f; // a cut that stops stops NOW
    private const float FinisherGoreBedVolume = 0.85f;

    // #323 follow-up. The chest anchor, in victim-local space, used when the spine bones cannot be
    // resolved — roughly sternum height on a vanilla player.
    private static readonly Vector3 FinisherGoreEmitterFallbackLocal = new Vector3(0f, 1.25f, 0f);

    // When the local player IS the victim the emitter is inside their own chest and the listener is
    // their own head: spatialization buys nothing there but attenuation, and the person being cut in
    // half is the one listener who must never miss it. Same motivation as #295's first-person impact
    // layer. Not fully 2D — a trace of blend keeps it seated in the world rather than in the menu.
    private const float FinisherGoreLocalVictimSpatialBlend = 0.2f;

    // Hard ceiling on the configured gain, applied after it, so a hand-edited config cannot clip the
    // master bus with a -27 LUFS squelch.
    private const float FinisherGoreMaxGain = 4f;

    private static float FinisherGoreVolume =>
        Mathf.Clamp(
            (BundyPlugin.ModConfig?.ChainsawFinisherGoreVolume?.Value ?? 1.8f) * ChainsawAudioVolume,
            0f,
            FinisherGoreMaxGain);

    // A bed take counts as fitting the window if its AUDIBLE content covers at least this much of
    // it. Below 1.0 on purpose: an exact-fit-or-longer rule collapses the shortest take out of the
    // pool entirely and makes the two long variants always draw the same file.
    private const float FinisherGoreBedFitTolerance = 0.6f;

    // Floor on how early the removal may fire, so a short or clamped window cannot stack the pull-out
    // on top of the entry stab.
    private const float FinisherGoreRemoveEarliestSeconds = 0.35f;

    private const float FinisherGoreMinCutSeconds = 0.6f;
    private const float FinisherGoreMaxCutSeconds = 8f;

    private static bool FinisherGoreAudioEnabled =>
        FinisherCuttingAudioEnabled && BundyAudioCatalog.HasFinisherGoreSet;

    // ---- muffle --------------------------------------------------------------------------------
    //
    // While the blade is inside a body the engine is heard THROUGH meat. #297 already drops its
    // pitch and its top-end mix; this is the filter half — the loop's air-absorption cutoff is
    // pulled down to a dull 1.2 kHz for the length of the cut and released after it.
    //
    // Implemented as a term inside ApplyAirAbsorption rather than as an AudioLowPassFilter, because
    // the saw emitters are OccludeAudio-managed and their lowPassOverride is REWRITTEN EVERY FRAME
    // by UpdateAudioAirAbsorption — a second filter component on the same object would be fighting a
    // per-frame writer, and Unity applies filters per GameObject, so it would also catch anything
    // else living there. Folding it in gets the whole layered engine (chug/chain/scream all share
    // the host emitter's filter chain by construction) with one number, and leaves the contact
    // emitter — where the gore itself plays — untouched.
    private float _sawMuffleCurrent;
    private const float FinisherGoreMuffleCutoffHz = 1200f;
    private const float FinisherGoreMuffleAttackSeconds = 0.15f;
    private const float FinisherGoreMuffleReleaseSeconds = 0.3f;

    private float SawEngineMuffle01 => _sawMuffleCurrent;

    private bool _sawMuffleEngagedLogged;

    private void UpdateSawMuffle(float dt)
    {
        bool engaged = IsSawCuttingActive && FinisherGoreAudioEnabled;
        float target = engaged ? 1f : 0f;
        float smoothing = target > _sawMuffleCurrent
            ? FinisherGoreMuffleAttackSeconds
            : FinisherGoreMuffleReleaseSeconds;
        _sawMuffleCurrent = Mathf.Lerp(_sawMuffleCurrent, target, 1f - Mathf.Exp(-dt / Mathf.Max(0.01f, smoothing)));
        if (_sawMuffleCurrent < 0.001f)
            _sawMuffleCurrent = 0f;

        // #323 follow-up: the 2026-08-06 log could not answer "did the muffle engage at all" because
        // nothing on this path ever wrote a line. One edge-triggered pair per cut, always on.
        if (engaged == _sawMuffleEngagedLogged)
            return;

        _sawMuffleEngagedLogged = engaged;
        LogInfo(
            $"FinisherSawMuffle {(engaged ? "engaged" : "released")}: depth={_sawMuffleCurrent:F2}, " +
            $"cutoff={FinisherGoreMuffleCutoffHz:F0}Hz, cuttingAudioEnabled={FinisherCuttingAudioEnabled}, " +
            $"goreSetLoaded={BundyAudioCatalog.HasFinisherGoreSet}.");
    }

    // ---- lifecycle -----------------------------------------------------------------------------

    // Called from BeginSawCuttingLocal, i.e. from the paired-finisher begin ClientRpc (and the
    // host's own local begin) — the exact frame the choreography starts on this client.
    private void BeginFinisherGoreAudioLocal(int bodyIndex, string pairedActionId, PlayerControllerB victim = null)
    {
        if (!FinisherGoreAudioEnabled)
            return;

        EndFinisherGoreAudioLocal("re-entry", hardStop: true);
        DestroyFinisherGoreEmitter();
        EnsureFinisherGoreEmitter(victim);

        float cutSeconds = ResolveFinisherGoreCutSeconds(bodyIndex, pairedActionId);
        float now = Time.time;
        _finisherGoreAudioActive = true;
        _finisherGoreCutEndAt = now + cutSeconds;
        _finisherGoreBedStarted = false;
        _finisherGoreRemoveFired = false;
        _finisherGoreBedGain = 0f;
        _finisherGoreBedFadeOutFrom = float.PositiveInfinity;

        // Phase 1 — IMPACT, now. The blade is entering the body this frame.
        int impactIndex = BundyAudioCatalog.PickIndex(BundyAudioCue.FinisherGoreImpact);
        AudioClip impactClip = impactIndex >= 0
            ? BundyAudioCatalog.GetClip(BundyAudioCue.FinisherGoreImpact, impactIndex)
            : null;
        PlayFinisherGoreOneShot(impactClip, FinisherGoreVolume, UnityEngine.Random.Range(0.97f, 1.03f));

        // Phase 2 — DURATION bed, scheduled under the transient.
        _finisherGoreBedStartAt = now + FinisherGoreBedLeadSeconds;

        // Phase 3 — REMOVE, led by the chosen take's own measured peak so the yank lands ON the
        // pull-out instead of near it. The take is chosen NOW (not at fire time) because the lead
        // it needs is a property of the take, and the two differ by 1.4s.
        _finisherGoreRemoveIndex = BundyAudioCatalog.PickIndex(BundyAudioCue.FinisherGoreRemove);
        float removeLead = _finisherGoreRemoveIndex >= 0
            ? BundyAudioCatalog.GetFinisherGoreRemovePeakSeconds(_finisherGoreRemoveIndex)
            : 0f;
        _finisherGoreRemoveAt = Mathf.Max(
            now + FinisherGoreRemoveEarliestSeconds,
            _finisherGoreCutEndAt - removeLead);

        LogInfo(
            BundyLogCategory.Audio,
            1,
            $"FinisherGoreAudio begin: bodyIndex={Mathf.Clamp(bodyIndex, 0, 2)}, action='{pairedActionId ?? "<none>"}', " +
            $"cut={cutSeconds:F2}s, impactClip={(impactClip != null ? impactClip.name : "<none>")}, " +
            $"bedAt=+{FinisherGoreBedLeadSeconds:F2}s, removeAt=+{_finisherGoreRemoveAt - now:F2}s " +
            $"(lead={removeLead:F2}s), removeIndex={_finisherGoreRemoveIndex}, " +
            $"gain={FinisherGoreVolume:F2}, emitter={(_finisherGoreEmitter != null ? _finisherGoreEmitter.transform.parent?.name ?? "<root>" : "<blade-fallback>")}, " +
            $"localVictim={_finisherGoreLocalVictim}.");
    }

    // Called from EndSawCuttingLocal — the release/interrupt/death half of every cleanup site that
    // already restores #321's collision suppression and ends #322's gore chute.
    private void EndFinisherGoreAudioLocal(string reason, bool hardStop)
    {
        // The bed source is kept (not destroyed) between finishers, so "is there anything to end"
        // has to ask whether it is PLAYING, not whether it exists — otherwise every later frame of
        // the round falls through this and the idle-state fade math below runs forever.
        if (!_finisherGoreAudioActive && !IsFinisherGoreBedAudible)
            return;

        bool wasActive = _finisherGoreAudioActive;
        _finisherGoreAudioActive = false;
        _finisherGoreRemoveFired = true; // nothing more may fire from a schedule that has ended

        if (IsFinisherGoreBedAudible)
        {
            // Even a hard stop fades: a bed that is cut off digitally mid-squelch is the loudest
            // "soundboard" tell there is (the same argument the vocal channel makes).
            //
            // A fade already in flight is never shortened or restarted. Normal teardown reaches
            // here TWICE on the host — the release sets the 0.25s ring-out, then CompleteAction
            // follows with interrupted:true while the bed is still audible — and re-stamping would
            // clip the host's ring-out to 0.12s while every remote client heard the full 0.25s.
            if (float.IsPositiveInfinity(_finisherGoreBedFadeOutFrom))
            {
                _finisherGoreBedFadeOutFrom = Time.time;
                _finisherGoreBedFadeSeconds = hardStop
                    ? FinisherGoreBedInterruptFadeSeconds
                    : FinisherGoreBedFadeOutSeconds;
            }
        }
        else
        {
            StopFinisherGoreBed();
        }

        if (wasActive)
            LogInfo(BundyLogCategory.Audio, 2, $"FinisherGoreAudio end: reason={reason}, hardStop={hardStop}.");
    }

    // Destruction/despawn teardown: those paths get no further Update, so the bed cannot be left to
    // fade out under the per-frame driver and the muffle envelope has nothing left to relax it.
    private void StopFinisherGoreAudioNow(string reason)
    {
        EndFinisherGoreAudioLocal(reason, hardStop: true);
        StopFinisherGoreBed();
        _sawMuffleCurrent = 0f;
    }

    // ---- per-frame driver ----------------------------------------------------------------------
    //
    // Driven from Update directly rather than from UpdateMovementLoopAudioModulation: that method
    // early-returns when the engine loop is not playing (stalled saw, loop not yet resolved), and a
    // finisher whose engine happens to be silent must still make the body sound.
    private void UpdateFinisherGoreAudio()
    {
        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        UpdateSawMuffle(dt);

        if (!_finisherGoreAudioActive && !IsFinisherGoreBedAudible)
            return;

        float now = Time.time;

        if (_finisherGoreAudioActive)
        {
            // Backstop: the release RPC is the normal end, but a dropped one must not leave the bed
            // and the muffle running forever. #297's own SawCuttingMaxSeconds covers the bog; this
            // covers the phases.
            if (now >= _finisherGoreCutEndAt + FinisherGoreBedFadeOutSeconds)
            {
                EndFinisherGoreAudioLocal("cut window elapsed", hardStop: false);
            }
            else
            {
                if (!_finisherGoreBedStarted && now >= _finisherGoreBedStartAt)
                {
                    _finisherGoreBedStarted = true;
                    StartFinisherGoreBed(Mathf.Max(0.1f, _finisherGoreCutEndAt - now));
                }

                if (!_finisherGoreRemoveFired && now >= _finisherGoreRemoveAt)
                {
                    _finisherGoreRemoveFired = true;
                    AudioClip removeClip = _finisherGoreRemoveIndex >= 0
                        ? BundyAudioCatalog.GetClip(BundyAudioCue.FinisherGoreRemove, _finisherGoreRemoveIndex)
                        : null;
                    PlayFinisherGoreOneShot(removeClip, FinisherGoreVolume, UnityEngine.Random.Range(0.98f, 1.02f));
                }

                // The bed holds until the blade is out, then releases under the removal's tail.
                if (float.IsPositiveInfinity(_finisherGoreBedFadeOutFrom) &&
                    now >= _finisherGoreCutEndAt - FinisherGoreBedFadeOutSeconds)
                {
                    _finisherGoreBedFadeOutFrom = now;
                    _finisherGoreBedFadeSeconds = FinisherGoreBedFadeOutSeconds;
                }
            }
        }

        UpdateFinisherGoreBedGain(dt, now);
    }

    private bool IsFinisherGoreBedAudible => _finisherGoreBedSource != null && _finisherGoreBedSource.isPlaying;

    private void UpdateFinisherGoreBedGain(float dt, float now)
    {
        if (!IsFinisherGoreBedAudible)
            return;

        float target;
        if (now >= _finisherGoreBedFadeOutFrom)
        {
            float span = Mathf.Max(0.01f, _finisherGoreBedFadeSeconds);
            target = 1f - Mathf.Clamp01((now - _finisherGoreBedFadeOutFrom) / span);
        }
        else
        {
            target = 1f;
        }

        float rate = target > _finisherGoreBedGain ? FinisherGoreBedFadeInSeconds : Mathf.Max(0.01f, _finisherGoreBedFadeSeconds);
        _finisherGoreBedGain = Mathf.MoveTowards(_finisherGoreBedGain, target, dt / rate);
        // AudioSource.volume is hard-clamped to 1 by Unity, so the knob saturates here rather than
        // amplifying — the bed's real loudness win comes from the emitter's flat near-field curve and
        // from losing the blade emitter's occlusion, not from this line.
        _finisherGoreBedSource.volume = Mathf.Clamp01(
            FinisherGoreBedVolume * FinisherGoreVolume * _finisherGoreBedGain);

        if (_finisherGoreBedGain <= 0.001f && target <= 0f)
            StopFinisherGoreBed();
    }

    // ---- phase 2 helpers -----------------------------------------------------------------------

    private void StartFinisherGoreBed(float windowSeconds)
    {
        int index = PickFinisherGoreBedIndex(windowSeconds, out float audibleTail);
        AudioClip clip = index >= 0 ? BundyAudioCatalog.GetClip(BundyAudioCue.FinisherGoreDuration, index) : null;
        if (clip == null)
            return;

        AudioSource host = EnsureFinisherGoreEmitter(null) ?? ResolveChainsawContactAudioSource();
        if (host == null)
            return;

        if (_finisherGoreBedSource == null)
        {
            EnsureLoopLayerSource(ref _finisherGoreBedSource, host, BundyAudioProfile.SawContact);
            // The layer helper stamps the shared SawContact profile; the gore emitter's own shape is
            // wider and flatter, and the bed must match the one-shots it sits under.
            if (_finisherGoreBedSource != null && host == _finisherGoreEmitterSource)
                ApplyFinisherGoreSpatialProfile(_finisherGoreBedSource);
        }
        if (_finisherGoreBedSource == null)
            return;

        // Loop only when the take genuinely runs out of audible content before the blade comes out —
        // otherwise the seam is a defect for no gain.
        bool needsLoop = audibleTail > 0f && audibleTail < windowSeconds;
        _finisherGoreBedGain = 0f;
        _finisherGoreBedFadeOutFrom = float.PositiveInfinity;
        _finisherGoreBedFadeSeconds = FinisherGoreBedFadeOutSeconds;
        StartLoopLayer(_finisherGoreBedSource, clip, needsLoop, restart: true);
        if (_finisherGoreBedSource != null)
            _finisherGoreBedSource.pitch = UnityEngine.Random.Range(0.97f, 1.03f);

        LogInfo(
            BundyLogCategory.Audio,
            2,
            $"FinisherGoreAudio bed: clip={clip.name}, window={windowSeconds:F2}s, audibleTail={audibleTail:F2}s, loop={needsLoop}.");
    }

    // Fit, then shuffle among what fits. A pure "shortest take that covers the window" rule is
    // deterministic per variant, which means the 5.00s and 4.67s finishers would draw the SAME bed
    // every single time — the pool exists to stop that.
    private int PickFinisherGoreBedIndex(float windowSeconds, out float audibleTail)
    {
        audibleTail = 0f;
        AudioClip[] pool = BundyAudioCatalog.FinisherGoreDurationClips;
        if (pool == null || pool.Length == 0)
            return -1;

        float need = windowSeconds * FinisherGoreBedFitTolerance;
        int fitting = 0;
        for (int i = 0; i < pool.Length; i++)
        {
            if (BundyAudioCatalog.GetFinisherGoreDurationTailSeconds(i) >= need)
                fitting++;
        }

        int index;
        if (fitting > 0)
        {
            // Draw through the cue's shuffle bag so repeats are spaced, then fall back to the first
            // fitting take if the bag hands back one that is too short for this window.
            index = BundyAudioCatalog.PickIndex(BundyAudioCue.FinisherGoreDuration);
            if (index < 0 || BundyAudioCatalog.GetFinisherGoreDurationTailSeconds(index) < need)
            {
                index = -1;
                for (int i = 0; i < pool.Length && index < 0; i++)
                {
                    if (BundyAudioCatalog.GetFinisherGoreDurationTailSeconds(i) >= need)
                        index = i;
                }
            }
        }
        else
        {
            // Nothing covers the window: take the longest and let it loop.
            index = 0;
            float best = -1f;
            for (int i = 0; i < pool.Length; i++)
            {
                float tail = BundyAudioCatalog.GetFinisherGoreDurationTailSeconds(i);
                if (tail > best)
                {
                    best = tail;
                    index = i;
                }
            }
        }

        if (index < 0 || index >= pool.Length)
            return -1;

        audibleTail = BundyAudioCatalog.GetFinisherGoreDurationTailSeconds(index);
        return index;
    }

    private void StopFinisherGoreBed()
    {
        StopLoopLayer(_finisherGoreBedSource);
        _finisherGoreBedGain = 0f;
        _finisherGoreBedFadeOutFrom = float.PositiveInfinity;
        // The bed stopping is the last thing this cut will play, so it is also the emitter's end of
        // life — it is parented to a player object that outlives the finisher and would otherwise
        // accumulate one dead emitter per execution.
        DestroyFinisherGoreEmitter();
    }

    // ---- shared helpers ------------------------------------------------------------------------

    // The victim's own emitter, falling back to the blade and then to Bundy's body sources only if
    // the victim could not be resolved on this client.
    private void PlayFinisherGoreOneShot(AudioClip clip, float volume, float pitch)
    {
        if (clip == null)
            return;

        AudioSource source = _finisherGoreEmitterSource ??
            ResolveChainsawContactAudioSource() ??
            creatureSFX ??
            creatureVoice;
        if (source == null)
            return;

        source.pitch = Mathf.Clamp(pitch, 0.5f, 1.5f);
        // Not Clamp01: PlayOneShot's volumeScale multiplies into the mix and is the one place the
        // gore gain can genuinely exceed unity, which is what the -27 LUFS takes need.
        source.PlayOneShot(clip, Mathf.Clamp(volume, 0f, FinisherGoreMaxGain));
    }

    // ---- the victim-anchored emitter -----------------------------------------------------------

    /// <summary>
    /// Create (or reuse) the gore emitter parented to the victim's chest. Passing null reuses an
    /// emitter an earlier phase already built for this cut and never creates a new one, so the bed
    /// can ask for a host without knowing who the victim was.
    /// </summary>
    private AudioSource EnsureFinisherGoreEmitter(PlayerControllerB victim)
    {
        if (_finisherGoreEmitterSource != null)
            return _finisherGoreEmitterSource;
        if (victim == null)
            return null;

        // Chest first, hips second, player root last. spine.003 is the upper chest on the vanilla
        // metarig — the closest authored bone to where a chainsaw is going through the body.
        Transform anchor = FindChildRecursive(victim.transform, "spine.003") ??
            FindChildRecursive(victim.transform, "spine.002") ??
            FindChildRecursive(victim.transform, "spine");
        bool bonedAnchor = anchor != null;
        if (anchor == null)
            anchor = victim.transform;

        _finisherGoreEmitter = new GameObject("BundyFinisherGoreAudio");
        _finisherGoreEmitter.transform.SetParent(anchor, false);
        _finisherGoreEmitter.transform.localPosition = bonedAnchor
            ? Vector3.zero
            : FinisherGoreEmitterFallbackLocal;

        _finisherGoreEmitterSource = _finisherGoreEmitter.AddComponent<AudioSource>();
        _finisherGoreEmitterSource.playOnAwake = false;
        _finisherGoreEmitterSource.loop = false;
        _finisherGoreEmitterSource.dopplerLevel = 0f;

        PlayerControllerB localPlayer = GameNetworkManager.Instance != null
            ? GameNetworkManager.Instance.localPlayerController
            : null;
        _finisherGoreLocalVictim = localPlayer != null && localPlayer == victim;

        ApplyFinisherGoreSpatialProfile(_finisherGoreEmitterSource);

        LogInfo(
            BundyLogCategory.Audio,
            1,
            $"Finisher gore emitter created: victim={PlayerLabel(victim)}, anchor='{anchor.name}', " +
            $"bonedAnchor={bonedAnchor}, localVictim={_finisherGoreLocalVictim}, " +
            $"spatialBlend={_finisherGoreEmitterSource.spatialBlend:F2}, " +
            $"max={_finisherGoreEmitterSource.maxDistance:F1}m, occlude=none.");
        return _finisherGoreEmitterSource;
    }

    // Deliberately NOT ApplyMixerRoutingToSource/ApplyBundySpatialProfile: this emitter wants the
    // mixer routing but neither the SawContact collapse curve nor an OccludeAudio (see the header —
    // it sits inside the body whose colliders the occlusion linecast would hit).
    private void ApplyFinisherGoreSpatialProfile(AudioSource source)
    {
        if (source == null)
            return;

        if (_resolvedAudioMixerGroup != null)
            source.outputAudioMixerGroup = _resolvedAudioMixerGroup;

        source.dopplerLevel = 0f;
        source.spread = 55f; // a wet, wide, all-around sound, not a pin point in the victim's chest
        source.priority = 96; // above the contact transients: this is the event, not a garnish
        source.minDistance = 1f;
        source.maxDistance = 24f;
        source.rolloffMode = AudioRolloffMode.Custom;
        source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, BuildFinisherGoreFalloffCurve());
        source.spatialBlend = _finisherGoreLocalVictim ? FinisherGoreLocalVictimSpatialBlend : 1f;
    }

    // Flat where an execution is actually watched from, then a normal tail. The SawContact curve was
    // already down to 0.36 at 5m, which is where the 2026-08-06 run's own blood-FX lines put the
    // viewer — that curve is right for a hit transient and wrong for a five-second wound.
    // Domain = d/24m: 1.0 to ~2.5m, ~0.85 at 5m, ~0.5 at 10m, gone by 24m.
    private static AnimationCurve BuildFinisherGoreFalloffCurve()
    {
        return new AnimationCurve(
            new Keyframe(0f, 1f, 0f, 0f),
            new Keyframe(0.104f, 1f, 0f, -0.9f),
            new Keyframe(0.208f, 0.85f, -1.2f, -1.2f),
            new Keyframe(0.417f, 0.5f, -1.4f, -1.4f),
            new Keyframe(0.7f, 0.16f, -0.7f, -0.7f),
            new Keyframe(1f, 0f, -0.3f, 0f));
    }

    private void DestroyFinisherGoreEmitter()
    {
        _finisherGoreLocalVictim = false;
        if (_finisherGoreEmitter == null)
        {
            _finisherGoreEmitterSource = null;
            return;
        }

        // Only drop the bed reference when the bed actually lived on the emitter — on the blade
        // fallback it is a component of Bundy's own contact emitter and must be reused, not orphaned
        // (a new one would be added on every finisher for the rest of the round).
        if (_finisherGoreBedSource != null &&
            _finisherGoreBedSource.gameObject == _finisherGoreEmitter)
        {
            _finisherGoreBedSource = null;
        }

        _finisherGoreEmitterSource = null;
        Destroy(_finisherGoreEmitter);
        _finisherGoreEmitter = null;
    }

    // The window the blade is INSIDE the body, from this client's own copy of the paired-action
    // catalog. Shipped clips carry zero AnimationEvents (#125), so the visuals themselves run off
    // this same duration/damageNormalizedTime pair through ConfigurePairedFinisherTiming — reading
    // it here is what keeps the audio on the picture rather than on a parallel guess.
    private float ResolveFinisherGoreCutSeconds(int bodyIndex, string pairedActionId)
    {
        if (TryResolveFinisherGoreAction(pairedActionId, out BundyPairedActionDefinition action) ||
            TryResolveFinisherGoreAction($"chainsaw.finisher.body{Mathf.Clamp(bodyIndex, 0, 2)}", out action))
        {
            float duration = action.duration > 0.1f ? action.duration : FinisherGoreFallbackCutSeconds;
            float normalized = action.damageNormalizedTime > 0f
                ? Mathf.Clamp01(action.damageNormalizedTime)
                : FinisherGoreFallbackNormalizedCut;
            return Mathf.Clamp(duration * normalized, FinisherGoreMinCutSeconds, FinisherGoreMaxCutSeconds);
        }

        return FinisherGoreFallbackCutSeconds;
    }

    private static bool TryResolveFinisherGoreAction(string actionId, out BundyPairedActionDefinition action)
    {
        action = null;
        return !string.IsNullOrEmpty(actionId) &&
            BundyPairedActionCatalog.TryGet(actionId, out action) &&
            action != null;
    }
}
