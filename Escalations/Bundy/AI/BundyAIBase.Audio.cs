using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy;
using Y4NGZCompany.Escalations.Bundy.AI;
using Y4NGZCompany.Facility.Elevators;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.AI;

namespace Y4NGZCompany.Escalations.Bundy.AI;

public abstract partial class BundyAIBase
{
    private void ResetDialogueTimer()
    {
        _nextDialogueAt = Time.time + NextSearchChatterIntervalSeconds();
        _queuedDialogueAt = float.PositiveInfinity;
    }

    // ---- C3.6 search-chatter cadence -----------------------------------------------------------
    // The old hardcoded 20s interval is replaced by a state-scaled, jittered clock: actively
    // working a lead talks a lot, cold patrol talks a little, and no two gaps are the same length.
    private const float SearchChatterJitter = 0.3f;
    // How fresh the last-known position must be for SEARCH/TRACK to count as "actively searching".
    private const float SearchChatterRecentKnowledgeSeconds = 45f;
    // Event vocals no longer reset the search clock; they only guarantee this much spacing.
    private const float PostEventVocalChatterGapSeconds = 4f;
    // While chatter is suppressed (HUNT) the clock is held at least this far ahead, so leaving
    // HUNT can never fire a bark on the exit frame (run 6 showed exit-clustered barks).
    private const float HuntExitChatterGraceSeconds = 5f;

    private float NextSearchChatterIntervalSeconds()
    {
        bool activelySearching =
            (_strangerPerceptionState == StrangerPerceptionState.Search ||
                _strangerPerceptionState == StrangerPerceptionState.Track) &&
            _hasStrangerLastKnownPosition &&
            Time.time - _strangerLastKnownAt <= SearchChatterRecentKnowledgeSeconds;
        float baseInterval = activelySearching
            ? BundyPlugin.ModConfig?.SearchChatterActiveIntervalSeconds?.Value ?? 13.5f
            : BundyPlugin.ModConfig?.SearchChatterIdleIntervalSeconds?.Value ?? 27.5f;
        return Mathf.Max(3f, baseInterval * UnityEngine.Random.Range(1f - SearchChatterJitter, 1f + SearchChatterJitter));
    }

    // C3.2 Rant integration: search chatter draws from BOTH the Search and Rant folders. A
    // two-slot shuffle bag over the CUES (one of each per refill, random order) guarantees the
    // folders interleave within every pair of consecutive barks without degenerating to strict
    // A-B alternation; PickIndex then applies each pool's own shuffle-bag/cooldown state.
    private readonly List<BundyAudioCue> _searchChatterCueBag = new List<BundyAudioCue>();

    private BundyAudioCue PickSearchChatterCue()
    {
        bool hasSearch = BundyAudioCatalog.HasAny(BundyAudioCue.Search);
        bool hasRant = BundyAudioCatalog.HasAny(BundyAudioCue.Rant);
        if (!hasRant)
            return BundyAudioCue.Search;
        if (!hasSearch)
            return BundyAudioCue.Rant;

        if (_searchChatterCueBag.Count == 0)
        {
            _searchChatterCueBag.Add(BundyAudioCue.Search);
            _searchChatterCueBag.Add(BundyAudioCue.Rant);
            if (UnityEngine.Random.value < 0.5f)
                _searchChatterCueBag.Reverse();
        }

        BundyAudioCue cue = _searchChatterCueBag[_searchChatterCueBag.Count - 1];
        _searchChatterCueBag.RemoveAt(_searchChatterCueBag.Count - 1);
        return cue;
    }

    private static int[] CreateUnsetIndexArray()
    {
        var values = new int[Enum.GetValues(typeof(BundyAudioCue)).Length];
        for (int i = 0; i < values.Length; i++)
            values[i] = -1;
        return values;
    }

    // Event vocals (Spot on pursuit changes, FinisherVocal after finishers, ...) go through
    // this queue; the interval timer plays Search chatter. The old generic Dialogue pool is
    // retired — every vocal names its cue.
    private void QueueVocal(BundyAudioCue cue, float delay, string reason)
    {
        // HasAny, not PickIndex: since C3.4 PickIndex is stateful (shuffle bag + 90s per-clip
        // cooldown), so probing it here consumed a draw for a clip that never played.
        if (!BundyAudioCatalog.HasAny(cue))
            return;

        float scheduledAt = Time.time + Mathf.Max(0f, delay);
        // C3.4 race fix: the single slot used to accept only EARLIER schedules, so a
        // later-but-more-important vocal (AttackVocal behind a pending Spot) was silently
        // dropped. Priority breaks that now; equal priority keeps earliest-wins.
        bool replaces = float.IsPositiveInfinity(_queuedDialogueAt) ||
            scheduledAt <= _queuedDialogueAt ||
            GetVocalPriority(cue) > GetVocalPriority(_queuedVocalCue);
        if (replaces)
        {
            _queuedDialogueAt = scheduledAt;
            _queuedVocalCue = cue;
        }
        LogAnimEvent(
            nameof(QueueVocal),
            $"{(replaces ? "queued" : "dropped (lower priority than queued " + _queuedVocalCue + ")")} {cue} after {delay:F2}s; reason={reason}");
    }

    private void UpdateDialoguePlayback()
    {
        float now = Time.time;
        if (now >= _queuedDialogueAt)
        {
            _queuedDialogueAt = float.PositiveInfinity;
            PlayRandomVocal(_queuedVocalCue);
            // C3.6: event vocals no longer RESET the search-chatter clock (a chatty fight used
            // to push the next search bark a full interval out on every line); they only
            // guarantee a short gap so consecutive lines don't stack.
            _nextDialogueAt = Mathf.Max(_nextDialogueAt, now + PostEventVocalChatterGapSeconds);
            return;
        }

        // HUNT suppression stays, and covers BOTH chatter cues (Search and Rant): less voice in
        // combat, more while searching (Lawson, C3.6).
        bool searchChatterAllowed =
            !_strangerTargetingActive ||
            _pursuitMode == PursuitMode.NonTargetInterrupt ||
            _strangerPerceptionState != StrangerPerceptionState.Hunt;
        if (!searchChatterAllowed)
        {
            _nextDialogueAt = Mathf.Max(_nextDialogueAt, now + HuntExitChatterGraceSeconds);
            return;
        }

        if (now >= _nextDialogueAt)
        {
            PlayRandomVocal(PickSearchChatterCue());
            _nextDialogueAt = now + NextSearchChatterIntervalSeconds();
        }
    }

    private void PlayRandomVocal(BundyAudioCue cue)
    {
        int cueIndex = (int)cue;
        int avoid = cueIndex >= 0 && cueIndex < _lastVocalClipIndex.Length ? _lastVocalClipIndex[cueIndex] : -1;
        int index = BundyAudioCatalog.PickIndex(cue, avoid);
        if (index < 0)
            return;

        if (cueIndex >= 0 && cueIndex < _lastVocalClipIndex.Length)
            _lastVocalClipIndex[cueIndex] = index;

        // C1.5: the ONLY voice-cue selection log. Verifies pool selection and exposes A-B-A-B
        // alternation (depth-1 anti-repeat) from a run log. (Audio, 1) prints at the default
        // LogVerbosity=1 without needing the LogAudio reopener.
        if (ShouldLog(BundyLogCategory.Audio, 1))
        {
            AudioClip picked = BundyAudioCatalog.GetClip(cue, index);
            LogInfo(
                BundyLogCategory.Audio,
                1,
                $"BundyVocal cue={cue} index={index} avoid={avoid} clip={(picked != null ? picked.name : "<null>")}");
        }

        // Volume varies per line too (±): identical loudness on every delivery is a machine tell.
        float lineVolume = VoiceAudioVolume * UnityEngine.Random.Range(0.88f, 1.05f);
        PlayBundyAudio(cue, index, lineVolume, UnityEngine.Random.Range(1f - VoicePitchJitter, 1f + VoicePitchJitter));
    }

    private const float MaskedReactionScanIntervalSeconds = 1f;
    private const float MaskedReactionRangeMeters = 14f;
    private const float MaskedReactionCooldownSeconds = 45f;

    // "See a masked player" reaction. Masked mimics are MaskedPlayerEnemy instances, so the
    // spawned-enemy list is the thing to scan, not the player list.
    private void UpdateMaskedReaction()
    {
        if (!IsServer || isEnemyDead)
            return;

        float now = Time.time;
        if (now < _nextMaskedScanAt || now < _nextMaskedReactionAt)
            return;
        _nextMaskedScanAt = now + MaskedReactionScanIntervalSeconds;

        if (RoundManager.Instance == null || RoundManager.Instance.SpawnedEnemies == null)
            return;

        foreach (EnemyAI enemy in RoundManager.Instance.SpawnedEnemies)
        {
            if (enemy == null || enemy.isEnemyDead || !(enemy is MaskedPlayerEnemy))
                continue;
            if (Vector3.Distance(transform.position, enemy.transform.position) > MaskedReactionRangeMeters)
                continue;
            if (!CheckLineOfSightForPosition(enemy.transform.position + Vector3.up * 1.6f, 70f, (int)MaskedReactionRangeMeters))
                continue;

            _nextMaskedReactionAt = now + MaskedReactionCooldownSeconds;
            PlayRandomVocal(BundyAudioCue.Reaction);
            LogAnimEvent(nameof(UpdateMaskedReaction), $"reacted to masked '{enemy.name}'");
            break;
        }
    }

    // Returns the clip index actually played, or -1. #210 needs it: the blood layer's delay is
    // derived from WHICH impact clip was chosen, and the choice is made in here.
    private int PlayRandomBundyAudio(BundyAudioCue cue, float volume, float minPitch, float maxPitch)
    {
        int index = BundyAudioCatalog.PickIndex(cue);
        if (index < 0)
            return -1;

        PlayBundyAudio(cue, index, volume, UnityEngine.Random.Range(minPitch, maxPitch));
        return index;
    }

    private void UpdateMovementLoopAudioState()
    {
        if (!IsServer)
            return;

        BundyAudioCue? loopCue = MovementLoopAudioCue;
        if (!loopCue.HasValue)
            return;

        if (UsesFiniteMovementLoopAudioWindow)
        {
            // A stalled saw keeps its idle loop audible even if the finite window lapses while stalled.
            bool windowActive = !isEnemyDead && (_sawStalled || Time.time < _movementLoopAudioReadyUntil);
            SetMovementLoopAudioRequested(windowActive);
            return;
        }

        bool movingLoopState = _lastMotionState == BundyAnimatorParams.MotionWalk ||
            _lastMotionState == BundyAnimatorParams.MotionRun;
        bool attackCanKeepLoop =
            KeepMovementLoopAudioUntilBasicAttackImpact &&
            _actionKind == ActionKind.Attack &&
            _movementLoopAudioRequested;
        bool shouldPlay = !isEnemyDead &&
            movingLoopState &&
            (!IsActionLocked() || attackCanKeepLoop);
        SetMovementLoopAudioRequested(shouldPlay);
    }

    private void SetMovementLoopAudioRequested(bool shouldPlay, bool forceUpdate = false)
    {
        BundyAudioCue? loopCue = MovementLoopAudioCue;
        if (!loopCue.HasValue)
            return;

        if (!forceUpdate && _movementLoopAudioRequested == shouldPlay)
            return;

        _movementLoopAudioRequested = shouldPlay;
        SetMovementLoopAudioClientRpc(shouldPlay, (int)loopCue.Value, MovementLoopAudioVolume, forceUpdate);
    }

    [ClientRpc]
    public void SetMovementLoopAudioClientRpc(bool shouldPlay, int cue, float volume, bool forceUpdate)
    {
        SetMovementLoopAudioLocal(shouldPlay, (BundyAudioCue)cue, volume, forceUpdate);
    }

    private void SetMovementLoopAudioLocal(bool shouldPlay, BundyAudioCue cue, float volume, bool forceUpdate = false)
    {
        // #293: any explicit request supersedes a deferred pull-start loop entry in flight.
        if (_pendingLoopStartRoutine != null)
        {
            StopCoroutine(_pendingLoopStartRoutine);
            _pendingLoopStartRoutine = null;
        }

        // #293: the running loop yields to a playing catch clip — it enters under the clip's tail
        // (at full pitch; see the ramp arming below) instead of over its sputter. Client-local:
        // each client latched its own entry time when its catch clip started.
        if (shouldPlay && cue == BundyAudioCue.ChainsawRunningLoop && Time.time < _pullStartLoopEntryAt)
        {
            _pendingLoopStartRoutine = StartCoroutine(StartLoopAtPullStartEntry(cue, volume));
            return;
        }

        if (!shouldPlay && _movementLoopAudioSource == null)
            return;

        AudioSource source = ResolveMovementLoopAudioSource();
        if (source == null)
            return;

        AudioSource near = _movementLoopNearAudioSource;
        if (!shouldPlay)
        {
            if (source.isPlaying)
                source.Stop();
            source.clip = null;
            if (near != null)
            {
                if (near.isPlaying)
                    near.Stop();
                near.clip = null;
            }
            StopLoopLayerExtras();
            _loopLayeredActive = false;
            return;
        }

        // #294: the running loop routes to the layered engine when the full layer set is present.
        // The main source carries the LOW layer; chain/high ride extra co-located sources. Gated on
        // UsesMovementLoopSwingModulation because that per-frame writer is the only thing that ever
        // raises the extra layers off their silent entry volume. The stall idle stays single-clip
        // on purpose — dropping from a three-layer machine to the lone idle recording IS the stall.
        bool layered = cue == BundyAudioCue.ChainsawRunningLoop &&
            UsesMovementLoopSwingModulation &&
            LayeredEngineLoopEnabled &&
            BundyAudioCatalog.HasChainsawLayerSet;
        AudioClip clip = layered ? BundyAudioCatalog.ChainsawLayerLowClip : BundyAudioCatalog.GetClip(cue, 0);
        if (clip == null)
            return;

        bool shouldLoopSource = MovementLoopAudioClipLoops || !UsesFiniteMovementLoopAudioWindow;
        if (!forceUpdate && source.clip == clip && source.isPlaying && source.loop == shouldLoopSource)
        {
            // Carry source already correct; make sure the near layer (possibly created later)
            // is running too.
            StartNearFieldLoopPlayback(near, clip, shouldLoopSource, volume, restart: false);
            SyncLoopLayerExtras(layered, shouldLoopSource, restart: false);
            return;
        }

        // C4.2 engine-start ramp: whenever the RUNNING loop (re)starts — pull-start window,
        // stall end's idle->running swap, HUNT-acquire force restart — it enters low and slow
        // and climbs to full over LoopStartRampSeconds. Armed here, applied by the single
        // per-frame writer (UpdateMovementLoopAudioModulation) so nothing fights it; only for
        // variants that HAVE that writer, or the low entry values would never be released.
        // #293: NOT when entering under a catch clip's tail — the clip already performed the
        // spin-up and ends at running RPM, so a pitch ramp here is precisely the tape-speedup
        // tell the authored sequence exists to kill. A short volume-only fade hides the seam.
        // Covers BOTH running-loop entries: the deferred coroutine path (initial pull start) and a
        // direct start landing shortly AFTER the entry point — the stall-recovery swap fires at
        // pull-start completion, past the latched entry, and must not fall back to the ramp.
        bool underPullStartTail = cue == BundyAudioCue.ChainsawRunningLoop &&
            (_loopEntryUnderPullStartTail ||
                (!float.IsNegativeInfinity(_pullStartLoopEntryAt) &&
                    Time.time < _pullStartLoopEntryAt + PullStartTailGraceSeconds));
        bool armStartRamp = UsesMovementLoopSwingModulation &&
            cue == BundyAudioCue.ChainsawRunningLoop &&
            !underPullStartTail;
        if (armStartRamp)
        {
            _loopStartRampBeginAt = Time.time;
        }
        else if (cue != BundyAudioCue.ChainsawRunningLoop)
        {
            _loopStartRampBeginAt = float.NegativeInfinity; // idle loop never ramps
        }
        else if (underPullStartTail)
        {
            _loopStartRampBeginAt = float.NegativeInfinity;
            _loopEntryFadeBeginAt = UsesMovementLoopSwingModulation ? Time.time : float.NegativeInfinity;
        }

        if (forceUpdate && source.isPlaying)
            source.Stop();

        source.loop = shouldLoopSource;
        source.clip = clip;
        float entryVolumeScale = armStartRamp
            ? LoopStartRampVolumeStart
            : (!float.IsNegativeInfinity(_loopEntryFadeBeginAt) && underPullStartTail ? LoopEntryFadeVolumeStart : 1f);
        source.volume = Mathf.Clamp01(volume) * entryVolumeScale;
        source.pitch = armStartRamp ? LoopStartRampPitchStart : 1f; // #293: catch-tail entry is full pitch by design
        source.Play();
        // C2.3: the near layer plays the SAME clip, started the same frame at the same pitch, so
        // the two emitters stay sample-locked (Unity mixes them sample-aligned — pure summation).
        StartNearFieldLoopPlayback(near, clip, shouldLoopSource, volume, restart: true);
        SyncLoopLayerExtras(layered, shouldLoopSource, restart: true);
        // Keep the near layer coherent on the very first frame too — the per-frame modulation
        // takes over from the next frame, but pitch must NEVER diverge between the two emitters.
        if (armStartRamp && near != null && near.isPlaying)
        {
            near.pitch = source.pitch;
            near.volume *= LoopStartRampVolumeStart;
        }
    }

    private void StartNearFieldLoopPlayback(AudioSource near, AudioClip clip, bool loopSource, float baseVolume, bool restart)
    {
        if (near == null)
            return;

        if (!restart && near.clip == clip && near.isPlaying && near.loop == loopSource)
            return;

        if (near.isPlaying)
            near.Stop();
        near.loop = loopSource;
        near.clip = clip;
        near.volume = Mathf.Clamp01(baseVolume * NearFieldLoopVolume);
        near.pitch = 1f;
        near.Play();
        // Re-sync to the carry source's playhead when joining late (created after the loop began).
        AudioSource carry = _movementLoopAudioSource;
        if (carry != null && carry.isPlaying && carry.clip == clip)
            near.timeSamples = carry.timeSamples;
    }

    private static float NearFieldLoopVolume =>
        Mathf.Clamp01(BundyPlugin.ModConfig?.ChainsawNearFieldVolume?.Value ?? 0.6f);

    // C4.2 engine-start ramp state: armed (client-local) when the running loop (re)starts; the
    // per-frame modulation lerps pitch 0.6->1.0 and volume 0.4->1.0 over 0.6s, on the carry AND
    // near-field layers with the identical pitch so they cannot flange.
    private float _loopStartRampBeginAt = float.NegativeInfinity;
    private const float LoopStartRampSeconds = 0.6f;
    private const float LoopStartRampPitchStart = 0.6f;
    private const float LoopStartRampVolumeStart = 0.4f;

    // #293 pull-start loop entry state (client-local). When a ChainsawPullStart clip starts, the
    // client latches the clip's measured loop-entry time; a running-loop start request arriving
    // inside that window defers (coroutine) and then enters at FULL pitch with a short
    // volume-only fade, hidden under the clip's settle tail. The C4.2 pitch ramp is suppressed
    // for that entry — the authored clip IS the spin-up now.
    private float _pullStartLoopEntryAt = float.NegativeInfinity;
    private Coroutine _pendingLoopStartRoutine;
    private bool _loopEntryUnderPullStartTail;
    private float _loopEntryFadeBeginAt = float.NegativeInfinity;
    private const float LoopEntryFadeSeconds = 0.3f;
    private const float LoopEntryFadeVolumeStart = 0.45f;
    // How long past the latched entry point a running-loop start still counts as "under the catch
    // clip" for ramp suppression: the ~0.5s masked overlap plus the settle tail's audible remainder.
    private const float PullStartTailGraceSeconds = 1.5f;

    private IEnumerator StartLoopAtPullStartEntry(BundyAudioCue cue, float volume)
    {
        float wait = _pullStartLoopEntryAt - Time.time;
        if (wait > 0f)
            yield return new WaitForSeconds(wait);

        _pendingLoopStartRoutine = null;
        _loopEntryUnderPullStartTail = true;
        try
        {
            SetMovementLoopAudioLocal(true, cue, volume, forceUpdate: true);
        }
        finally
        {
            _loopEntryUnderPullStartTail = false;
        }
    }

    // ---- #294 layered engine loop -----------------------------------------------------------
    // The running saw is built from three loops playing together — low engine chug (carried by
    // the MAIN loop source, whose isPlaying is the lifecycle signal everything keys on), chain
    // rattle and high-RPM scream on extra co-located sources — crossfaded by a smoothed 0-1
    // throttle. Pitch-warping one clip changes speed; the layer MIX is what changes character,
    // which is what "angrier as he revs" actually is. The near-field emitter gets low + chain
    // (body and mechanical detail are what "at your face" is made of); the scream carries fine
    // on the long-range source. All client-local, derived from replicated loop state.
    private AudioSource _loopChainLayerSource;
    private AudioSource _loopHighLayerSource;
    private AudioSource _nearChainLayerSource;
    private bool _loopLayeredActive;
    private float _loopThrottleSmoothed;
    // Floor keeps a running-but-quiet saw audibly ANGRIER than the stall idle clip (running is a
    // HUNT-only tell); full drive comes from swing speed, the contact swell, or the blade being
    // at the listener. Deliberately quick smoothing: a throttle that lags the swing sounds like
    // an automatic transmission, not a maniac's trigger finger.
    private const float LoopThrottleFloor = 0.3f;
    private const float LoopThrottleSmoothingSeconds = 0.12f;
    private const float LoopThrottleSpeedFullMetersPerSecond = 6f;
    private const float LoopThrottleProximityDrive = 0.6f;
    private const float LayerChainMixFloor = 0.45f;
    private const float LayerHighMixFloor = 0.1f;

    private static bool LayeredEngineLoopEnabled =>
        BundyPlugin.ModConfig?.ChainsawLayeredEngineLoop?.Value ?? true;

    private float ChainLayerMix() => LayerChainMixFloor + (1f - LayerChainMixFloor) * _loopThrottleSmoothed;
    private float HighLayerMix() => LayerHighMixFloor + (1f - LayerHighMixFloor) * _loopThrottleSmoothed;

    private void UpdateLoopThrottle(float dt, float bladeProximity)
    {
        float speedNorm = Mathf.Clamp01(_loopModSmoothedSpeed / LoopThrottleSpeedFullMetersPerSecond);
        float drive = Mathf.Max(speedNorm, _loopSwellCurrent);
        drive = Mathf.Max(drive, bladeProximity * LoopThrottleProximityDrive);
        float target = _sawStalled ? 0f : LoopThrottleFloor + (1f - LoopThrottleFloor) * drive;
        // #297: a bogged engine loses its top end no matter how hard the other signals push —
        // riding the smoothed bog means the ceiling clamps on contact and lifts between cuts.
        target = Mathf.Lerp(target, Mathf.Min(target, CuttingThrottleCeiling), _sawBogCurrent);
        _loopThrottleSmoothed = Mathf.Lerp(
            _loopThrottleSmoothed,
            target,
            1f - Mathf.Exp(-dt / LoopThrottleSmoothingSeconds));
    }

    private void SyncLoopLayerExtras(bool layered, bool loopSource, bool restart)
    {
        if (!layered)
        {
            StopLoopLayerExtras();
            _loopLayeredActive = false;
            return;
        }

        _loopLayeredActive = true;
        EnsureLoopLayerSource(ref _loopChainLayerSource, _movementLoopAudioSource, BundyAudioProfile.Saw);
        EnsureLoopLayerSource(ref _loopHighLayerSource, _movementLoopAudioSource, BundyAudioProfile.Saw);
        if (_movementLoopNearAudioSource != null)
            EnsureLoopLayerSource(ref _nearChainLayerSource, _movementLoopNearAudioSource, BundyAudioProfile.SawNear);

        StartLoopLayer(_loopChainLayerSource, BundyAudioCatalog.ChainsawLayerChainClip, loopSource, restart);
        StartLoopLayer(_loopHighLayerSource, BundyAudioCatalog.ChainsawLayerHighClip, loopSource, restart);
        StartLoopLayer(_nearChainLayerSource, BundyAudioCatalog.ChainsawLayerChainClip, loopSource, restart);
        // The same-clip carry/near chain pair stays phase-locked, like the low pair does.
        if (_loopChainLayerSource != null && _nearChainLayerSource != null &&
            _loopChainLayerSource.isPlaying && _nearChainLayerSource.isPlaying)
            _nearChainLayerSource.timeSamples = _loopChainLayerSource.timeSamples;
    }

    // Extra layers live ON the host emitter's GameObject: they must share its position, its
    // OccludeAudio and its filter chain (Unity filters apply to every AudioSource on the object),
    // so occlusion/air absorption keep answering once per emitter instead of once per layer.
    private void EnsureLoopLayerSource(ref AudioSource slot, AudioSource host, BundyAudioProfile profile)
    {
        if (slot != null || host == null)
            return;

        slot = host.gameObject.AddComponent<AudioSource>();
        slot.playOnAwake = false;
        slot.loop = true;
        slot.dopplerLevel = 0f;
        ApplyMixerRoutingToSource(slot, profile, addOcclude: false);
    }

    private static void StartLoopLayer(AudioSource layer, AudioClip clip, bool loopSource, bool restart)
    {
        if (layer == null || clip == null)
            return;
        if (!restart && layer.clip == clip && layer.isPlaying && layer.loop == loopSource)
            return;

        if (layer.isPlaying)
            layer.Stop();
        layer.loop = loopSource;
        layer.clip = clip;
        layer.volume = 0f; // the per-frame modulation writer raises it from the next frame
        layer.pitch = 1f;
        layer.Play();
    }

    private void StopLoopLayerExtras()
    {
        StopLoopLayer(_loopChainLayerSource);
        StopLoopLayer(_loopHighLayerSource);
        StopLoopLayer(_nearChainLayerSource);
    }

    // ---- #297 finisher cutting state ----------------------------------------------------------
    // While the blade is in a body the engine BOGS: pitch drops, the throttle (and with it the
    // scream layer) collapses, and an engine-free grinding-resistance texture plays from the
    // blade. The bog DEEPENS on each finisher damage tick and relaxes between them — bog on
    // contact, scream free between cuts. Armed/disarmed on every client by the paired-finisher
    // begin/release RPCs; the max-duration backstop covers a lost release.
    private float _sawCuttingUntil = float.NegativeInfinity;
    private float _sawBogCurrent;
    private float _sawBogPulseUntil = float.NegativeInfinity;
    private AudioSource _cutResistanceSource;
    private const float SawCuttingMaxSeconds = 12f;
    private const float SawBogBaseline = 0.55f;          // bog depth while cutting, between ticks
    private const float SawBogPulseSeconds = 0.6f;       // full-depth bog after each damage tick
    private const float SawBogAttackSeconds = 0.06f;     // the blade meets the body NOW
    private const float SawBogReleaseSeconds = 0.4f;     // the engine climbs back up, it doesn't snap
    private const float SawBogPitchDrop = 0.16f;         // pitch multiplier = 1 - drop * bog
    private const float CuttingThrottleCeiling = 0.3f;   // a laboring motor loses its top end
    private const float CutResistanceVolume = 0.9f;

    private static bool FinisherCuttingAudioEnabled =>
        BundyPlugin.ModConfig?.ChainsawFinisherCuttingAudio?.Value ?? true;

    private bool IsSawCuttingActive => FinisherCuttingAudioEnabled && Time.time < _sawCuttingUntil;

    // #323 takes the same arguments the paired-finisher begin RPC already carries, so the gore
    // phases are scheduled from the same client-local moment #297's bog is armed from — one
    // lifecycle, not two clocks that can drift apart.
    private void BeginSawCuttingLocal(int bodyIndex = -1, string pairedActionId = null, PlayerControllerB victim = null)
    {
        _sawCuttingUntil = Time.time + SawCuttingMaxSeconds;
        _sawBogPulseUntil = Time.time + SawBogPulseSeconds; // the entry stab bogs immediately
        // #321/#322/#323: this is the one call every client makes at the start of a cut, so it is
        // also where the client-local teardown backstop for a lost release RPC is armed.
        _finisherLocalTeardownAt = Time.time + FinisherLocalTeardownBackstopSeconds;
        BeginFinisherGoreAudioLocal(bodyIndex, pairedActionId, victim); // #323
    }

    private void EndSawCuttingLocal(string reason = "saw cutting end", bool interrupted = false)
    {
        _sawCuttingUntil = float.NegativeInfinity;
        _finisherLocalTeardownAt = float.PositiveInfinity;
        // #323: an interrupted cut hard-fades the phases; a normal release lets the bed ring out
        // under the removal's tail. The muffle releases either way off IsSawCuttingActive.
        EndFinisherGoreAudioLocal(reason, interrupted);
        // #321: the universal client-local end of a cut is also the end of the pair's separation hold.
        EndPairedFinisherAlignment(reason);
    }

    private void UpdateSawBog(float dt)
    {
        float target = 0f;
        if (IsSawCuttingActive)
            target = Time.time < _sawBogPulseUntil ? 1f : SawBogBaseline;

        float smoothing = target > _sawBogCurrent ? SawBogAttackSeconds : SawBogReleaseSeconds;
        _sawBogCurrent = Mathf.Lerp(_sawBogCurrent, target, 1f - Mathf.Exp(-dt / smoothing));
    }

    // The resistance texture rides the smoothed bog, so it fades in with the strain and drains
    // out with it after release instead of gating on and off. Lives on the blade contact emitter:
    // the cutting point, already swing-carried and occlusion-managed.
    private void UpdateCutResistanceLayer()
    {
        bool wanted = IsSawCuttingActive || _sawBogCurrent > 0.02f;
        if (!wanted)
        {
            StopLoopLayer(_cutResistanceSource);
            return;
        }

        AudioClip clip = BundyAudioCatalog.CutResistanceClip;
        if (clip == null)
            return;

        if (_cutResistanceSource == null)
        {
            AudioSource host = ResolveChainsawContactAudioSource();
            if (host == null)
                return;
            EnsureLoopLayerSource(ref _cutResistanceSource, host, BundyAudioProfile.SawContact);
        }

        StartLoopLayer(_cutResistanceSource, clip, loopSource: true, restart: false);
        if (_cutResistanceSource != null && _cutResistanceSource.isPlaying)
            _cutResistanceSource.volume = Mathf.Clamp01(CutResistanceVolume * _sawBogCurrent) * _loopDuckGainCurrent;
    }

    // ---- #296 swing whoosh --------------------------------------------------------------------
    // Client-local, like the taunt stomps: the whoosh must land on the arc THIS client can see,
    // and the swing window (_loopSwellUntil, server-pulsed at swing begin) gates it so locomotion
    // arm-sway can't fire it. Rising-edge detection on the blade emitter's raw world speed marks
    // the fast arc starting; playback is led so the clip's measured PEAK — not its start — lands
    // at the extension apex (Lawson's spec: peak at furthest extension, supplementary volume).
    private float _lastLoopRawSpeed;
    private float _nextSwingWhooshAllowedAt;
    private const float SwingWhooshSpeedThreshold = 4.5f;   // m/s — swing arcs exceed this, gait sway doesn't
    private const float SwingWhooshApexLeadSeconds = 0.12f; // rising edge -> estimated extension apex
    private const float SwingWhooshDedupSeconds = 0.55f;    // one whoosh per rep, even in chain attacks
    private const float SwingWhooshProximityFloor = 0.35f;  // distant swings whisper, close ones crack

    private static bool SwingWhooshEnabled =>
        BundyPlugin.ModConfig?.ChainsawSwingWhoosh?.Value ?? true;

    private static float SwingWhooshVolume =>
        Mathf.Clamp01(BundyPlugin.ModConfig?.ChainsawSwingWhooshVolume?.Value ?? 0.7f);

    private void UpdateSwingWhoosh(float rawSpeed)
    {
        float previousSpeed = _lastLoopRawSpeed;
        _lastLoopRawSpeed = rawSpeed;
        if (!SwingWhooshEnabled || isEnemyDead)
            return;
        if (Time.time >= _loopSwellUntil || Time.time < _nextSwingWhooshAllowedAt)
            return;
        if (previousSpeed >= SwingWhooshSpeedThreshold || rawSpeed < SwingWhooshSpeedThreshold)
            return; // not the rising edge of a fast arc

        int index = BundyAudioCatalog.PickIndex(BundyAudioCue.SawWhoosh);
        if (index < 0)
            return;

        _nextSwingWhooshAllowedAt = Time.time + SwingWhooshDedupSeconds;
        float delay = Mathf.Max(0f, SwingWhooshApexLeadSeconds - BundyAudioCatalog.GetSawWhooshPeakSeconds(index));
        StartCoroutine(PlaySwingWhooshAfter(delay, index));
    }

    private IEnumerator PlaySwingWhooshAfter(float delay, int index)
    {
        if (delay > 0f)
            yield return new WaitForSeconds(delay);
        if (isEnemyDead)
            yield break;

        AudioClip clip = BundyAudioCatalog.GetClip(BundyAudioCue.SawWhoosh, index);
        AudioSource source = ResolveChainsawContactAudioSource() ?? _movementLoopAudioSource;
        if (clip == null || source == null)
            yield break;

        // Proximity read at PLAY time, not detection time — the blade has moved since the edge.
        // Supplementary by design: it garnishes the swing the loop and contact layers carry.
        float volume = SwingWhooshVolume * Mathf.Lerp(SwingWhooshProximityFloor, 1f, _bladeProximity01);
        source.pitch = UnityEngine.Random.Range(0.96f, 1.04f);
        source.PlayOneShot(clip, Mathf.Clamp01(volume));
    }

    private static void StopLoopLayer(AudioSource layer)
    {
        if (layer == null)
            return;
        if (layer.isPlaying)
            layer.Stop();
        layer.clip = null;
    }

    private void PlayBundyAudio(BundyAudioCue cue, int clipIndex, float volume, float pitch)
    {
        if (!IsServer)
            return;

        PlayBundyAudioClientRpc((int)cue, clipIndex, volume, pitch);
    }

    [ClientRpc]
    public void PulseChainsawSwellClientRpc(float seconds)
    {
        // Max, not assign: a swing pulse must never TRUNCATE a contact swell already in flight (C2.2).
        _loopSwellUntil = Mathf.Max(_loopSwellUntil, Time.time + Mathf.Clamp(seconds, 0.4f, 3f));
    }

    [ClientRpc]
    public void PlayBundyAudioClientRpc(int cue, int clipIndex, float volume, float pitch)
    {
        PlayBundyAudioLocal((BundyAudioCue)cue, clipIndex, volume, pitch);
    }

    private void PlayBundyAudioLocal(BundyAudioCue cue, int clipIndex, float volume, float pitch)
    {
        // #761: the vanilla-friendly Stranger is mute. Gate here, at the one point every vocal
        // cue passes through on every client, so the server keeps sending the same RPCs and a
        // peer with the setting off still hears him. Returning before the reservation, the
        // speech duck and the audible-noise event means a muted line leaves no trace at all —
        // the saw does not let off the throttle for words nobody hears.
        if (IsVocalCue(cue) && BundyStrangerSack.VoicelinesMuted)
            return;

        AudioClip clip = BundyAudioCatalog.GetClip(cue, clipIndex);
        if (clip == null)
            return;

        ResolveRuntimeReferences();
        AudioSource source = ResolveAudioSourceForCue(cue);
        if (source == null)
            return;

        float clampedPitch = Mathf.Clamp(pitch, 0.5f, 1.5f);
        if (IsVocalCue(cue))
        {
            // Single-vocal channel: voicelines were PlayOneShot-layered and audibly talked
            // over each other (worst around basic attacks). Lower-priority vocals yield to a
            // playing one WITHOUT burning their dedup window; equal/higher priority interrupts.
            int priority = GetVocalPriority(cue);
            bool channelBusy = source.isPlaying && Time.time < _vocalChannelEndsAt;
            if (channelBusy && priority < _vocalChannelPriority)
                return;

            if (!TryReserveAudioCue(cue))
                return;

            bool interrupting = source.isPlaying;
            _vocalChannelPriority = priority;
            _vocalChannelEndsAt = Time.time + clip.length / clampedPitch + (interrupting ? VocalInterruptFadeSeconds : 0f);

            // While he talks, the chainsaw loop ducks (he "lets off the throttle") so the voice
            // sits in the mix like a person over a machine, not a clip over a bed.
            _movementLoopSpeechDuckUntil = Time.time + clip.length / clampedPitch;

            if (interrupting)
            {
                // A hard digital cutoff mid-word is the loudest "soundboard" tell there is.
                // Fade the current line out over ~70ms before the new one starts.
                if (_vocalInterruptFadeRoutine != null)
                    StopCoroutine(_vocalInterruptFadeRoutine);
                _vocalInterruptFadeRoutine = StartCoroutine(FadeOutThenPlayVocal(source, clip, clampedPitch, Mathf.Clamp01(volume)));
            }
            else
            {
                StartVocalPlayback(source, clip, clampedPitch, Mathf.Clamp01(volume));
            }
        }
        else
        {
            if (!TryReserveAudioCue(cue))
                return;

            source.pitch = clampedPitch;
            source.PlayOneShot(clip, Mathf.Clamp01(volume));

            // #293: latch when THIS client's catch clip started and where its loop entry point is.
            // The running loop's start request (arriving right behind this RPC) defers to it —
            // computed locally from the received clip index, same trick as the #210 blood layer,
            // so nothing timing-critical crosses the network.
            if (cue == BundyAudioCue.ChainsawPullStart)
            {
                float entry = BundyAudioCatalog.GetPullStartLoopEntrySeconds(clipIndex) / clampedPitch;
                _pullStartLoopEntryAt = entry > 0.05f ? Time.time + entry : float.NegativeInfinity;
            }

            // C2.2 (2026-07-30): contact SWELLS the loop instead of ducking it. The old duck cut
            // the saw -4dB at the exact moment the blade met flesh/wall — the one moment it should
            // be loudest. Extend the existing attack-swell window through the transient; the one-shot
            // rides on its own emitter so it still reads on top. Ducking is reserved for speech.
            // #187: the two chainsaw DAMAGE cues extend the swell as well. They were missing, so a
            // finisher's saw-through-flesh moment — the loudest thing he does — left the loop unswollen.
            if (cue == BundyAudioCue.PlayerHit ||
                cue == BundyAudioCue.ChainsawWallImpact ||
                cue == BundyAudioCue.ChainsawBasicAttack ||
                cue == BundyAudioCue.ChainsawFinisherDamage)
            {
                _loopSwellUntil = Mathf.Max(_loopSwellUntil, Time.time + ContactSwellSeconds);
            }

            // #297: each finisher damage tick is a cut going in — pulse the bog to full depth.
            // Rides the replicated one-shot every client already receives, so the bog lands on
            // the impact each listener actually hears, with no extra RPC.
            if (cue == BundyAudioCue.ChainsawFinisherDamage && IsSawCuttingActive)
                _sawBogPulseUntil = Time.time + SawBogPulseSeconds;
        }

        // #212: every footstep cue propagates through the one emitter, so the two playback paths can
        // no longer disagree about the same event and #213's gait boost lands on both.
        if (IsFootstepCue(cue))
        {
            EmitFootstepNoise(source.transform.position);
            return;
        }

        if (RoundManager.Instance != null)
        {
            float noiseRange = IsVocalCue(cue) ||
                cue == BundyAudioCue.Throw ||
                cue == BundyAudioCue.ChainsawBasicAttack ||
                cue == BundyAudioCue.ChainsawFinisherDamage ||
                cue == BundyAudioCue.ChainsawRev ||
                cue == BundyAudioCue.ChainsawPullStart ||
                cue == BundyAudioCue.ChainsawWallImpact ||
                cue == BundyAudioCue.PlayerHit
                ? 18f
                : 8f;
            RoundManager.Instance.PlayAudibleNoise(source.transform.position, noiseRange, 0.65f);
        }
    }

    // Issue #209 gave footsteps four cues instead of one. Everything that treated "is this a
    // footstep" as a two-way comparison has to ask here instead, or the per-surface pools silently
    // fall out of the footstep-specific handling (emitter, dedup window, noise propagation).
    private static bool IsFootstepCue(BundyAudioCue cue)
    {
        return cue == BundyAudioCue.Footstep ||
            cue == BundyAudioCue.ChainsawFootstep ||
            cue == BundyAudioCue.ChainsawFootstepConcrete ||
            cue == BundyAudioCue.ChainsawFootstepGrass ||
            cue == BundyAudioCue.ChainsawFootstepMetal;
    }

    private bool TryReserveAudioCue(BundyAudioCue cue)
    {
        int cueIndex = (int)cue;
        if (cueIndex < 0 || cueIndex >= _nextAudioCueAllowedAt.Length)
            return true;

        float now = Time.time;
        if (now < _nextAudioCueAllowedAt[cueIndex])
            return false;

        _nextAudioCueAllowedAt[cueIndex] = now + GetAudioCueDedupSeconds(cue);
        return true;
    }

    private static float GetAudioCueDedupSeconds(BundyAudioCue cue)
    {
        switch (cue)
        {
            case BundyAudioCue.Grunt:
            case BundyAudioCue.Throw:
            case BundyAudioCue.Dialogue:
            case BundyAudioCue.Reaction:
            case BundyAudioCue.Search:
            case BundyAudioCue.Rant:
            case BundyAudioCue.Spot:
            case BundyAudioCue.FinisherVocal:
                return VoiceAudioDedupSeconds;
            case BundyAudioCue.Hurt:
                return HurtVocalDedupSeconds;
            case BundyAudioCue.AttackVocal:
                return AttackVocalDedupSeconds;
            // C4.1: the cord-rip now fires unconditionally at BeginPullStart, and HUNT acquire
            // fires the same cue (UsesPerceptionAcquireRev) — when both happen in the same beat
            // this window collapses them into one rev. Well under the 2s attack cooldown, so
            // per-swing revs are untouched. KnockdownScream/Recover stay on the short default:
            // a restarted down-hold must be allowed to re-scream within seconds.
            case BundyAudioCue.ChainsawRev:
            // #293: the pull-start sequence shares the rev's window — a doubled catch (stall
            // watchdog + hunt loop racing to pull the same cord) must collapse to one.
            case BundyAudioCue.ChainsawPullStart:
                return ChainsawRevDedupSeconds;
            case BundyAudioCue.ChainsawFootstep:
            case BundyAudioCue.ChainsawFootstepConcrete:
            case BundyAudioCue.ChainsawFootstepGrass:
            case BundyAudioCue.ChainsawFootstepMetal:
                return FootstepAudioMinInterval;
            case BundyAudioCue.ChainsawWallImpact:
                return ChainsawWallImpactCooldownSeconds;
            default:
                return ShortAudioDedupSeconds;
        }
    }

    // Higher interrupts lower on the single voice channel. Finishers narrate a kill, attack
    // vocals are commitment barks, hurt reacts to damage; ambient chatter always yields.
    private static int GetVocalPriority(BundyAudioCue cue)
    {
        switch (cue)
        {
            // Knockdown vocals are the knockdown system's own timeline (the down-hold IS the
            // scream length) — nothing on the channel may block or outrank them.
            case BundyAudioCue.KnockdownScream:
            case BundyAudioCue.KnockdownRecover:
                return 6;
            case BundyAudioCue.FinisherVocal:
                return 5;
            case BundyAudioCue.AttackVocal:
                return 4;
            case BundyAudioCue.Hurt:
                return 3;
            case BundyAudioCue.Spot:
                return 2;
            case BundyAudioCue.Reaction:
                return 1;
            default:
                return 0;
        }
    }

    // The ship v02 locomotion clips carry no OnFootstep AnimationEvents (dropped by the R40
    // rebake chain — see 2026-07-22 audio session), so no footsteps ever played. This fires
    // the AUTHORED source-pack event times from animator playback: same cycle fractions, same
    // OnFootstep entry point (which keeps its own min-interval dedup and RPC fan-out). Scope
    // matches the authored events: WalkForward/RunForward only.
    private void UpdateSyntheticFootsteps()
    {
        if (isEnemyDead || creatureAnimator == null ||
            creatureAnimator.runtimeAnimatorController == null || creatureAnimator.layerCount <= 0)
        {
            _syntheticFootstepLastFrac = -1f;
            _hasFootHeightSample = false;
            return;
        }

        string clipName = GetBaseLayerClip(out float frac);
        if (clipName.Length == 0)
        {
            _syntheticFootstepLastFrac = -1f;
            _hasFootHeightSample = false;
            return;
        }

        // The taunt is not in the authored event set (those times were captured from WalkForward and
        // RunForward only), so it stomped in silence. It gets foot-plant detection instead of clip
        // fractions: no captured timings exist for it, and a detector stays correct if the clip is
        // ever re-timed. Deliberately runs on EVERY client, not just the owner — the local half arms
        // the saw rev so the throttle blip lands on the stomp this client can see, while the audio
        // itself still goes out once from the owner through the existing ClientRpc.
        if (clipName.IndexOf("Taunt", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            _syntheticFootstepLastFrac = -1f;
            UpdateTauntStompFootsteps();
            return;
        }

        _hasFootHeightSample = false;
        if (!IsOwner && !IsServer)
        {
            _syntheticFootstepLastFrac = -1f;
            return;
        }

        float[] fractions;
        int kind;
        if (clipName.IndexOf("RunForward", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            fractions = RunFootstepCycleFractions;
            kind = 2;
        }
        else if (clipName.IndexOf("WalkForward", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            fractions = WalkFootstepCycleFractions;
            kind = 1;
        }
        // Issue #213. Before this the sprint fell through to the return below and played NOTHING —
        // the gait was silent-footed, not mistimed. The intro and the loop get separate tables and
        // separate kinds because they are different cadences (the intro is an acceleration ramp whose
        // steps are not evenly spaced) and because the kind change is what resets the crossing
        // tracker at the seam, so the hand-over cannot manufacture a spurious step.
        else if (clipName.IndexOf("Sprint_Overhead_Loop", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            fractions = SprintLoopFootstepCycleFractions;
            kind = 3;
        }
        else if (clipName.IndexOf("Sprint_Overhead_Intro", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            fractions = SprintIntroFootstepCycleFractions;
            kind = 4;
        }
        else
        {
            // Sprint_Outro deliberately has no table: it is 0.3 s of deceleration into an attack's
            // opening pose and its legs interpolate rather than plant, so there is no contact to
            // sound. If a re-authored outro ever grows a real plant, it wants its own kind here.
            _syntheticFootstepLastFrac = -1f;
            return;
        }

        if (kind != _syntheticFootstepCycleKind || _syntheticFootstepLastFrac < 0f)
        {
            _syntheticFootstepCycleKind = kind;
            _syntheticFootstepLastFrac = frac;
            return;
        }

        float prev = _syntheticFootstepLastFrac;
        _syntheticFootstepLastFrac = frac;
        for (int i = 0; i < fractions.Length; i++)
        {
            float f = fractions[i];
            bool crossed = prev <= frac
                ? f > prev && f <= frac
                : f > prev || f <= frac; // cycle wrapped this frame
            if (crossed)
            {
                OnFootstep();
                break;
            }
        }
    }

    // Taunt stomps. A plant is the frame a DESCENDING foot stops descending while it is near the
    // ground: that is the impact frame regardless of how the clip is timed, which is the whole
    // reason this is a detector and not another fractions table.
    private void UpdateTauntStompFootsteps()
    {
        if (!TauntStompFootstepsEnabled)
        {
            _hasFootHeightSample = false;
            return;
        }

        EnsureFootBones();
        if (_leftFootBone == null && _rightFootBone == null)
        {
            _hasFootHeightSample = false;
            return;
        }

        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        float rootY = transform.position.y;
        float leftHeight = _leftFootBone != null ? _leftFootBone.position.y - rootY : 0f;
        float rightHeight = _rightFootBone != null ? _rightFootBone.position.y - rootY : 0f;
        if (!_hasFootHeightSample)
        {
            _hasFootHeightSample = true;
            _leftFootLastHeight = leftHeight;
            _rightFootLastHeight = rightHeight;
            _leftFootDescending = false;
            _rightFootDescending = false;
            return;
        }

        bool planted = DetectFootPlant(_leftFootBone, leftHeight, dt, ref _leftFootLastHeight, ref _leftFootDescending);
        planted |= DetectFootPlant(_rightFootBone, rightHeight, dt, ref _rightFootLastHeight, ref _rightFootDescending);
        if (!planted)
            return;

        // Local, on every client: the rev has to land on the stomp this client is watching.
        _tauntLoopStompBlipAt = Time.time;

        // The sound itself is still owner/server-authored and fans out through the same ClientRpc
        // as every other footstep, so it cannot double-play on a listen server.
        if (IsOwner || IsServer)
            PlayFootstepImpact(TauntStompVolumeScale, TauntStompPitchScale, skipWalkAttenuation: true);
    }

    private bool DetectFootPlant(Transform foot, float height, float dt, ref float lastHeight, ref bool descending)
    {
        if (foot == null)
            return false;

        float verticalSpeed = (height - lastHeight) / dt;
        lastHeight = height;

        bool falling = verticalSpeed < -TauntFootDescendSpeed;
        bool planted = descending && !falling && height <= TauntFootPlantMaxHeight;
        descending = falling;
        return planted;
    }

    // Humanoid avatar first; the direct-sampled transform bundles are avatarless, so fall back to
    // the same authored bone names BundyChainsawFootGrounding resolves. Latched on the animator so
    // a rig that has neither does not pay for a full recursive hierarchy scan every taunt frame,
    // while a rebuilt visual (new animator instance) still re-resolves.
    private void EnsureFootBones()
    {
        if ((_leftFootBone != null && _rightFootBone != null) || creatureAnimator == null)
            return;
        if (_footBoneSearchAnimator == creatureAnimator)
            return;

        _footBoneSearchAnimator = creatureAnimator;
        if (creatureAnimator.isHuman)
        {
            if (_leftFootBone == null)
                _leftFootBone = creatureAnimator.GetBoneTransform(HumanBodyBones.LeftFoot);
            if (_rightFootBone == null)
                _rightFootBone = creatureAnimator.GetBoneTransform(HumanBodyBones.RightFoot);
        }

        if (_leftFootBone == null)
            _leftFootBone = FindChildRecursive(creatureAnimator.transform, "foot_l");
        if (_rightFootBone == null)
            _rightFootBone = FindChildRecursive(creatureAnimator.transform, "foot_r");
    }

    private void StartVocalPlayback(AudioSource source, AudioClip clip, float pitch, float volume)
    {
        // Debug A/B isolator: substitute a VANILLA enemy voice clip through Bundy's exact
        // pipeline (same source, anchors, mixer, rolloff, ducking). Native-sounding result =
        // the recordings are the problem; still-floating result = settings/pipeline.
        if ((BundyPlugin.ModConfig?.BundyVanillaVoiceClipTest?.Value ?? false) && _vanillaVoiceTestClip != null)
        {
            LogInfo($"VanillaVoiceClipTest: substituting '{_vanillaVoiceTestClip.name}' for '{clip.name}'.");
            clip = _vanillaVoiceTestClip;
        }

        source.pitch = pitch;
        source.clip = clip;
        // The outdoor lift is applied HERE and not at the call site because the call site is the
        // server: the environment that matters is each listener's own, and this method is what runs
        // on every client. #186 could only compensate for the missing outdoor reverb send with
        // range because AudioSource.volume clamps at 1 — but BundyVoiceVolume sits at 0.75, so
        // there was headroom under that ceiling the range fix never spent.
        source.volume = Mathf.Clamp01(volume * CurrentVoiceEnvironmentVolumeScale());
        source.loop = false;
        source.Play();

        // Vanilla enemies transmit their voice over held walkie-talkies; do the same so
        // Bundy's lines exist in the world's comms layer, not just at his head.
        WalkieTalkie.TransmitOneShotAudio(source, clip, volume);

        // Vocals drive the amplitude-follow lipsync from clip data; GetOutputData on the
        // spatialized CreatureVoice reads ~0 (proven in-game 2026-07-22), so the jaw must
        // be fed the clip directly.
        NotifyLipsyncVocal(clip);
    }

    private static float CurrentVoiceEnvironmentVolumeScale()
    {
        return IsLocalListenerInsideFactory() ? 1f : VoiceOutdoorVolumeScale;
    }

    private IEnumerator FadeOutThenPlayVocal(AudioSource source, AudioClip clip, float pitch, float volume)
    {
        float startVolume = source.volume;
        float elapsed = 0f;
        while (elapsed < VocalInterruptFadeSeconds && source.isPlaying)
        {
            elapsed += Time.deltaTime;
            source.volume = Mathf.Lerp(startVolume, 0f, elapsed / VocalInterruptFadeSeconds);
            yield return null;
        }

        source.Stop();
        StartVocalPlayback(source, clip, pitch, volume);
        _vocalInterruptFadeRoutine = null;
    }

    private static bool IsVocalCue(BundyAudioCue cue)
    {
        switch (cue)
        {
            case BundyAudioCue.Grunt:
            case BundyAudioCue.Dialogue:
            case BundyAudioCue.Hurt:
            case BundyAudioCue.Reaction:
            case BundyAudioCue.Search:
            case BundyAudioCue.Spot:
            case BundyAudioCue.FinisherVocal:
            case BundyAudioCue.AttackVocal:
            // C3.2: Rant is interval chatter like Search; the knockdown scream/recover are his
            // voice too — before this they bypassed the vocal channel, the speech duck and
            // lipsync entirely (knockdown agent request).
            case BundyAudioCue.Rant:
            case BundyAudioCue.KnockdownScream:
            case BundyAudioCue.KnockdownRecover:
                return true;
            default:
                return false;
        }
    }

    private void NotifyLipsyncVocal(AudioClip clip)
    {
        if (_lipsyncForVocals == null)
            _lipsyncForVocals = GetComponentInChildren<BundyLipsync>(true);
        if (_lipsyncForVocals != null)
            _lipsyncForVocals.NotifyVocal(clip);
    }

    private AudioSource ResolveAudioSourceForCue(BundyAudioCue cue)
    {
        switch (cue)
        {
            case BundyAudioCue.ChainsawBasicAttack:
            case BundyAudioCue.ChainsawFinisherDamage:
            case BundyAudioCue.PlayerHit:
            // #323: the gore phases play client-locally through PlayFinisherGoreOneShot, which
            // resolves the same emitter directly. Listed here so the mapping is correct anyway if
            // one is ever routed through the generic RPC path.
            case BundyAudioCue.FinisherGoreImpact:
            case BundyAudioCue.FinisherGoreDuration:
            case BundyAudioCue.FinisherGoreRemove:
                // #187: the three CONTACT cues moved off creatureSFX onto a blade-anchored emitter with
                // the steep SawContact curve. On the carry source their point-blank/10m ratio was ~7 dB,
                // which is why a saw filling the screen sounded no louder than a saw one room away.
                // Everything else on the saw (rev, wall impact) stays on the carry source: those are
                // "he is over there" tells and want the long range.
                return ResolveChainsawContactAudioSource() ?? creatureSFX ?? creatureVoice ?? _footstepAudioSource;
            case BundyAudioCue.Punch:
            case BundyAudioCue.ChainsawRev:
            case BundyAudioCue.ChainsawPullStart:
            case BundyAudioCue.ChainsawWallImpact:
                return creatureSFX ?? creatureVoice ?? _footstepAudioSource;
            case BundyAudioCue.Footstep:
            case BundyAudioCue.ChainsawFootstep:
            case BundyAudioCue.ChainsawFootstepConcrete:
            case BundyAudioCue.ChainsawFootstepGrass:
            case BundyAudioCue.ChainsawFootstepMetal:
                return _footstepAudioSource ?? creatureSFX ?? creatureVoice;
            case BundyAudioCue.Grunt:
            case BundyAudioCue.Throw:
            case BundyAudioCue.Dialogue:
            default:
                return creatureVoice ?? creatureSFX ?? _footstepAudioSource;
        }
    }

    private AudioSource ResolveMovementLoopAudioSource()
    {
        if (_movementLoopAudioSource != null)
            return _movementLoopAudioSource;

        ResolveRuntimeReferences();
        var sourceObject = new GameObject("BundyMovementLoopAudio");
        // The loop is the chainsaw idle — anchor it to the saw prop so it tracks the hand.
        Transform loopParent = transform;
        foreach (Transform t in GetComponentsInChildren<Transform>(true))
        {
            if (t.name == "BundyProp_Chainsaw")
            {
                loopParent = t;
                break;
            }
        }
        sourceObject.transform.SetParent(loopParent, false);
        _movementLoopAudioSource = sourceObject.AddComponent<AudioSource>();
        _movementLoopAudioSource.playOnAwake = false;
        _movementLoopAudioSource.loop = true;

        AudioSource template = creatureSFX ?? creatureVoice ?? _footstepAudioSource;
        if (template != null)
        {
            _movementLoopAudioSource.spatialBlend = template.spatialBlend;
            _movementLoopAudioSource.minDistance = template.minDistance;
            _movementLoopAudioSource.maxDistance = template.maxDistance;
            _movementLoopAudioSource.rolloffMode = template.rolloffMode;
            _movementLoopAudioSource.dopplerLevel = template.dopplerLevel;
        }
        else
        {
            _movementLoopAudioSource.spatialBlend = 1f;
            _movementLoopAudioSource.minDistance = 2f;
            _movementLoopAudioSource.maxDistance = 24f;
            _movementLoopAudioSource.rolloffMode = AudioRolloffMode.Linear;
        }

        _movementLoopAudioSource.dopplerLevel = 0f;

        // Mixer routing usually runs before this source exists; apply group (if resolved yet)
        // and the saw spatial profile now. The profile is deterministic, so applying it here
        // unconditionally keeps the loop correct even if routing is still retrying.
        ApplyMixerRoutingToSource(_movementLoopAudioSource, BundyAudioProfile.Saw, addOcclude: true);

        // Only on the real prop: with the root fallback the tip-offset scan would pick a corner
        // of Bundy's whole body. EnsureAudioSourceAnchors retries once the prop resolves.
        if (loopParent != null && loopParent.name == RuntimeChainsawPropName)
            EnsureNearFieldLoopSource(loopParent);

        // #276: a source created after the anchor latch needs the full anchor pass once.
        InvalidateAudioSourceAnchors();

        return _movementLoopAudioSource;
    }

    // C2.3 (2026-07-30): near-field/carry split. All Bundy sources use Custom rolloff evaluated
    // over d/maxDistance, so minDistance is inert and the Job 4 range stretch (30 -> 60/85m)
    // flattened close-range contrast on the single loop source. This second, short-range emitter
    // layers a steep 0-7m curve OVER the carry source and is parented at the BLADE TIP (computed
    // from the prop's renderer geometry, capped at the modeled 1.2m contact reach) so every swing
    // arc physically moves the loudest emitter. Same clip, started the same frame and driven with
    // the identical per-frame pitch as the carry source, so the two layers stay sample-locked and
    // sum instead of flanging.
    private void EnsureNearFieldLoopSource(Transform chainsawProp)
    {
        if (_movementLoopNearAudioSource != null || chainsawProp == null)
            return;
        if (!(BundyPlugin.ModConfig?.ChainsawNearFieldLayer?.Value ?? true))
            return;

        _nearLoopLocalOffset = ComputeChainsawTipLocalOffset(chainsawProp);
        var nearObject = new GameObject("BundyChainsawNearAudio");
        nearObject.transform.SetParent(chainsawProp, false);
        nearObject.transform.localPosition = _nearLoopLocalOffset;
        _movementLoopNearAudioSource = nearObject.AddComponent<AudioSource>();
        _movementLoopNearAudioSource.playOnAwake = false;
        _movementLoopNearAudioSource.loop = true;
        _movementLoopNearAudioSource.dopplerLevel = 0f;

        // Mixer group + OccludeAudio + the SawNear spatial profile (steep short curve). Occlusion
        // IS wanted even at 6-8m: facility walls/doors inside that radius are common, and an
        // unmuffled point-blank layer through a wall would be the loudest wrong thing in the mix.
        ApplyMixerRoutingToSource(_movementLoopNearAudioSource, BundyAudioProfile.SawNear, addOcclude: true);

        LogInfo(
            BundyLogCategory.Audio,
            1,
            $"Near-field saw layer created: tipOffset={FormatVector(_nearLoopLocalOffset)} " +
            $"({_nearLoopLocalOffset.magnitude:F2}m), max={_movementLoopNearAudioSource.maxDistance:F1}m.");

        // Created late while the loop is already running (prop resolved after loop start):
        // join immediately, synced to the carry source's playhead.
        AudioSource carry = _movementLoopAudioSource;
        if (carry != null && carry.isPlaying && carry.clip != null)
            StartNearFieldLoopPlayback(_movementLoopNearAudioSource, carry.clip, carry.loop, MovementLoopAudioVolume, restart: true);
    }

    // #187: the contact emitter. Lives at the same blade anchor as the C2.3 near-field loop layer and
    // is parented to the saw prop, which is itself hand-parented — so every swing, spin and finisher
    // physically carries the emitter with the blade instead of leaving hits at the prop origin.
    private AudioSource ResolveChainsawContactAudioSource()
    {
        if (!ContactBladeEmitterEnabled)
            return null;
        if (_chainsawContactAudioSource != null)
            return _chainsawContactAudioSource;

        Transform prop = FindChildRecursive(transform, RuntimeChainsawPropName);
        if (prop == null)
            return null;

        Vector3 offset = ComputeChainsawTipLocalOffset(prop);
        var contactObject = new GameObject("BundyChainsawContactAudio");
        contactObject.transform.SetParent(prop, false);
        contactObject.transform.localPosition = offset;
        _chainsawContactAudioSource = contactObject.AddComponent<AudioSource>();
        _chainsawContactAudioSource.playOnAwake = false;
        _chainsawContactAudioSource.loop = false;
        _chainsawContactAudioSource.dopplerLevel = 0f;
        _chainsawContactLocalOffset = offset;

        ApplyMixerRoutingToSource(_chainsawContactAudioSource, BundyAudioProfile.SawContact, addOcclude: true);
        LogInfo(
            BundyLogCategory.Audio,
            1,
            $"Blade contact emitter created: offset={FormatVector(offset)} ({offset.magnitude:F2}m), " +
            $"max={_chainsawContactAudioSource.maxDistance:F1}m, mixer=" +
            $"{(_chainsawContactAudioSource.outputAudioMixerGroup != null ? _chainsawContactAudioSource.outputAudioMixerGroup.name : "<pending>")}.");
        return _chainsawContactAudioSource;
    }

    // The blade anchor, in prop-local space. Prefers the authored blade transform the damage hitbox
    // already resolves (BundyBootstrap.FindChainsawBladeRoot) — that is the actual cutting end, and it
    // is what the contact test uses, so audio and damage now come from the same point. Falls back to
    // the furthest renderer-bounds corner (the mesh's longest reach) when the hitbox has not resolved
    // yet. Capped at 1.2m — the modeled blade-contact distance — and zeroed when the prop has no
    // meaningful geometry resolved yet.
    private Vector3 ComputeChainsawTipLocalOffset(Transform prop)
    {
        if (_chainsawBladeHitbox != null &&
            _chainsawBladeHitbox.bladeRoot != null &&
            _chainsawBladeHitbox.bladeRoot != prop &&
            _chainsawBladeHitbox.bladeRoot.IsChildOf(prop))
        {
            Vector3 bladeLocal = prop.InverseTransformPoint(_chainsawBladeHitbox.bladeRoot.position);
            float bladeLength = bladeLocal.magnitude;
            if (bladeLength > 0.05f)
                return bladeLength > 1.2f ? bladeLocal * (1.2f / bladeLength) : bladeLocal;
        }

        return ComputeChainsawTipLocalOffsetFromRenderers(prop);
    }

    private static Vector3 ComputeChainsawTipLocalOffsetFromRenderers(Transform prop)
    {
        Renderer[] renderers = prop.GetComponentsInChildren<Renderer>(true);
        Vector3 bestLocal = Vector3.zero;
        float bestSq = 0f;
        for (int r = 0; r < renderers.Length; r++)
        {
            if (renderers[r] == null)
                continue;
            Bounds bounds = renderers[r].bounds;
            Vector3 center = bounds.center;
            Vector3 extents = bounds.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    center.x + (((i & 1) == 0) ? -extents.x : extents.x),
                    center.y + (((i & 2) == 0) ? -extents.y : extents.y),
                    center.z + (((i & 4) == 0) ? -extents.z : extents.z));
                Vector3 local = prop.InverseTransformPoint(corner);
                float sq = local.sqrMagnitude;
                if (sq > bestSq)
                {
                    bestSq = sq;
                    bestLocal = local;
                }
            }
        }

        if (bestSq < 0.25f)
            return Vector3.zero;

        float length = Mathf.Sqrt(bestSq);
        return length > 1.2f ? bestLocal * (1.2f / length) : bestLocal;
    }

    // Task D (2026-07-22): swing feel. The loop source is anchored to the saw prop, so its world
    // speed captures every swing — attacks, turns, locomotion arm swing. Pitch/volume track that
    // speed (client-side, animation-driven, no RPC needed). C2.2 (2026-07-30): PlayerHit/WallImpact
    // now EXTEND the swell (contact is the loudest moment); the duck path is speech-only. Runs on
    // ALL clients every frame and drives both the carry loop and the C2.3 near-field layer.
    private void UpdateMovementLoopAudioModulation()
    {
        if (!UsesMovementLoopSwingModulation)
            return;

        AudioSource source = _movementLoopAudioSource;
        if (source == null || !source.isPlaying || source.clip == null)
        {
            _loopModHasLastPosition = false;
            _loopModSmoothedSpeed = 0f;
            StopLoopLayer(_cutResistanceSource); // #297: no engine, no strain texture
            return;
        }

        float dt = Time.deltaTime;
        if (dt <= 0f)
            return;

        Vector3 position = source.transform.position;
        if (!_loopModHasLastPosition)
        {
            _loopModHasLastPosition = true;
            _loopModLastPosition = position;
            return;
        }

        float rawSpeed = (position - _loopModLastPosition).magnitude / dt;
        _loopModLastPosition = position;
        if (rawSpeed > 30f)
            rawSpeed = 0f; // teleport/warp frame; ignore

        // #296: swing-arc rising-edge detection wants the RAW speed — the smoothed term below
        // lags by design and would put the whoosh after the blade.
        UpdateSwingWhoosh(rawSpeed);

        _loopModSmoothedSpeed = Mathf.Lerp(
            _loopModSmoothedSpeed,
            rawSpeed,
            1f - Mathf.Exp(-dt / LoopModulationSmoothingSeconds));

        // How close the cutting end is to THIS listener, resolved before the swell because the
        // swell now rides it. Purely local: every client computes it against its own listener.
        float bladeProximity = UpdateBladeProximity(dt);

        // #297: the bog envelope must resolve before the throttle reads it.
        UpdateSawBog(dt);

        // #294: the throttle that crossfades the engine layers. Fed by the same signals the
        // pitch/volume modulation already trusts; must run before the layer-mix writes below.
        if (_loopLayeredActive)
            UpdateLoopThrottle(dt, bladeProximity);

        // Attack swell: server pulses at swing begin; the loop revs into the hit and settles after.
        // Computed BEFORE the duck since #187 — the duck target now reads it.
        //
        // 2026-07-31: the window used to drive the swell to a flat 1. That made the saw jump the
        // frame the animation changed — with the blade still cocked behind him — and then hold,
        // which is loud but is not localised, and reads as a mix event rather than as a blade
        // arriving. Ride the open window on blade proximity instead, so the loudest instant of the
        // swing is the instant the blade is nearest your head. A distant swing still swells (the
        // floor), it just no longer swells like one at your face.
        float swellWindow = Time.time < _loopSwellUntil ? 1f : 0f;
        float swellTarget = BladeProximityModulationEnabled
            ? swellWindow * Mathf.Lerp(SwingSwellProximityFloor, 1f, bladeProximity)
            : swellWindow;
        _loopSwellCurrent = Mathf.Lerp(
            _loopSwellCurrent,
            swellTarget,
            1f - Mathf.Exp(-dt / LoopSwellLerpSeconds));

        // C2.2: ducking is speech-only now — contact transients swell instead (see PlayBundyAudioLocal).
        // #187: and the swell now OUTRANKS the duck. The attack vocal is queued at windup + 0.45s while
        // the blade lands at 1.17-1.55s, so a talking Bundy was always mid-line at contact and the duck
        // multiplied the swell straight back out — the loop sat at 35% for the one frame it was supposed
        // to be loudest. Speech still ducks the loop everywhere else (chatter, spots, finisher lines).
        float duckTarget = Time.time < _movementLoopSpeechDuckUntil ? LoopSpeechDuckGain : 1f;
        if (ContactSwellOverridesSpeechDuck)
            duckTarget = Mathf.Lerp(duckTarget, 1f, _loopSwellCurrent);
        _loopDuckGainCurrent = Mathf.Lerp(
            _loopDuckGainCurrent,
            duckTarget,
            1f - Mathf.Exp(-dt / LoopDuckLerpSeconds));

        bool swingTrackingEnabled = BundyPlugin.ModConfig?.ChainsawSwingAudioModulation?.Value ?? true;
        float pitch = 1f;
        float volumeBoost = 0f;
        if (swingTrackingEnabled && !_sawStalled)
        {
            pitch = Mathf.Min(LoopModulationMaxPitch, 1f + _loopModSmoothedSpeed * LoopModulationPitchPerSpeed);
            volumeBoost = Mathf.Min(LoopModulationMaxVolumeBoost, _loopModSmoothedSpeed * LoopModulationVolumePerSpeed);
            pitch = Mathf.Min(LoopModulationAbsoluteMaxPitch, pitch + LoopSwellPitchBoost * _loopSwellCurrent);
            volumeBoost += LoopSwellVolumeBoost * _loopSwellCurrent;

            // A blade genuinely at your head is brighter as well as louder — the pitch lift is what
            // separates "close" from "loud" when the two would otherwise be the same knob.
            if (BladeProximityModulationEnabled)
                pitch = Mathf.Min(LoopModulationAbsoluteMaxPitch, pitch + BladeProximityPitchBoost * bladeProximity);

            // Taunt: he performs it standing still, so _loopModSmoothedSpeed is ~0 and the motor
            // flatlines through the one animation that exists to show off. Idle oscillation keeps it
            // breathing; the stomp blip (armed locally by the same foot-plant detection that fires the
            // footstep, so it lands on the stomp every client can see) is the throttle being cracked.
            if (TauntLoopOscillationEnabled && IsTauntClipPlaying())
            {
                float idle = Mathf.Sin(Time.time * TauntLoopIdleOscillationHz * Mathf.PI * 2f) * 0.5f + 0.5f;
                pitch = Mathf.Min(LoopModulationAbsoluteMaxPitch, pitch + TauntLoopIdleOscillationPitch * idle);
                volumeBoost += TauntLoopIdleOscillationVolume * idle;

                float sinceBlip = Time.time - _tauntLoopStompBlipAt;
                if (sinceBlip >= 0f && sinceBlip < TauntLoopStompBlipSeconds)
                {
                    // Instant attack, squared decay: a rev, not a fade.
                    float blip = 1f - (sinceBlip / TauntLoopStompBlipSeconds);
                    blip *= blip;
                    pitch = Mathf.Min(LoopModulationAbsoluteMaxPitch, pitch + TauntLoopStompBlipPitch * blip);
                    volumeBoost += TauntLoopStompBlipVolume * blip;
                }
            }
        }

        // #298: the carry loop gets a direct proximity lift. It had NO proximity term — the near
        // layer was assumed to own closeness, but its budget (see below) couldn't deliver. Applied
        // OUTSIDE the swing-tracking gate on purpose: a stalled/idling saw at your face wants this
        // exactly as much as a running one, and a frozen Bundy has no speed/swell boost to spend
        // the Clamp01 headroom the 0.65 base deliberately leaves.
        if (BladeProximityModulationEnabled)
            volumeBoost += BladeProximityCarryVolumeBoost * bladeProximity;

        if (swingTrackingEnabled)
        {
            // Engine irregularity: a real motor never holds pitch perfectly. Applies while
            // running AND while stalled-idling (an idling saw wobbles the most).
            if (_loopWobbleSeed == 0f)
                _loopWobbleSeed = (GetInstanceID() & 1023) * 0.117f + 0.313f;
            float wobble = (Mathf.PerlinNoise(Time.time * LoopWobbleRate, _loopWobbleSeed) - 0.5f) * 2f * LoopWobbleDepth;
            pitch = Mathf.Clamp(pitch + wobble, 0.85f, LoopModulationAbsoluteMaxPitch);
        }

        // C4.2 engine-start ramp: multiplies the fully-modulated pitch/volume so the start still
        // carries speed/swell/wobble character, just scaled up from the sputtering entry. Applied
        // AFTER the wobble clamp — the 0.6 entry pitch is deliberately below that clamp's floor.
        float rampPitchMul = 1f;
        float rampVolumeMul = 1f;
        if (!float.IsNegativeInfinity(_loopStartRampBeginAt))
        {
            float rampT = (Time.time - _loopStartRampBeginAt) / LoopStartRampSeconds;
            if (rampT >= 1f)
            {
                _loopStartRampBeginAt = float.NegativeInfinity;
            }
            else
            {
                rampT = Mathf.Clamp01(rampT);
                rampPitchMul = Mathf.Lerp(LoopStartRampPitchStart, 1f, rampT);
                rampVolumeMul = Mathf.Lerp(LoopStartRampVolumeStart, 1f, rampT);
            }
        }

        // #293: the catch-tail entry fade — volume only, full pitch. Rides the same multiplier
        // slot as the ramp (the two are mutually exclusive by arming), so every layer downstream
        // inherits it identically.
        if (!float.IsNegativeInfinity(_loopEntryFadeBeginAt))
        {
            float fadeT = (Time.time - _loopEntryFadeBeginAt) / LoopEntryFadeSeconds;
            if (fadeT >= 1f)
                _loopEntryFadeBeginAt = float.NegativeInfinity;
            else
                rampVolumeMul *= Mathf.Lerp(LoopEntryFadeVolumeStart, 1f, Mathf.Clamp01(fadeT));
        }

        pitch *= rampPitchMul;
        // #297: RPM drop under load. Multiplicative and applied LAST so it overrides the swell's
        // pitch boost — the blade in a body is the one moment the engine must not scream higher.
        pitch *= 1f - SawBogPitchDrop * _sawBogCurrent;
        source.pitch = pitch;
        // C2.1: MovementLoopAudioVolume now defaults to 0.65, so the boost band (speed up to +0.30,
        // swell +0.25) actually lands inside Clamp01 instead of being flattened at 1.0.
        float carryEnvelope = Mathf.Clamp01(MovementLoopAudioVolume * (1f + volumeBoost)) * _loopDuckGainCurrent * rampVolumeMul;
        source.volume = carryEnvelope;

        // #294: chain/high layers share the carry envelope and pitch (one machine, one occlusion
        // answer) — only their MIX differs, and the throttle is what moves it. Low stays at the
        // full envelope: the engine body is the constant the other two layers get angry on top of.
        if (_loopLayeredActive)
        {
            AudioSource chainLayer = _loopChainLayerSource;
            if (chainLayer != null && chainLayer.isPlaying)
            {
                chainLayer.pitch = pitch;
                chainLayer.volume = carryEnvelope * ChainLayerMix();
            }

            AudioSource highLayer = _loopHighLayerSource;
            if (highLayer != null && highLayer.isPlaying)
            {
                highLayer.pitch = pitch;
                highLayer.volume = carryEnvelope * HighLayerMix();
            }
        }

        // C2.3: the near-field layer shares pitch (keeps the layers phase-locked) and the same
        // boost/duck envelope, scaled by its own gain — a different volume CURVE (steep, 7m)
        // does the near/far separation, not a different modulation.
        AudioSource near = _movementLoopNearAudioSource;
        if (near != null && near.isPlaying)
        {
            // The near layer's 7m curve already collapses with distance, but between 2.5m and 0.6m —
            // the whole span of a swing landing — it only moves ~4dB, which is not enough to read as
            // the blade arriving rather than as the saw being on. This gain is the difference.
            float nearVolume = MovementLoopAudioVolume * NearFieldLoopVolume * (1f + volumeBoost);
            if (BladeProximityModulationEnabled)
            {
                // #298: at the blade, this layer commands the FULL volume budget. The old purely
                // multiplicative gain (max 1.45x on a 0.39 base) capped the one layer that owns
                // close contrast at ~0.57 — point-blank could never dominate the mix by
                // construction. BladeProximityNearGain still shapes the mid-approach; the lerp
                // drives the ceiling itself to 1.0 as the blade reaches the listener.
                nearVolume = Mathf.Lerp(
                    nearVolume * (1f + BladeProximityNearGain * bladeProximity),
                    1f,
                    bladeProximity);
            }

            near.pitch = pitch; // ramp already folded in — identical pitch, no flanging
            float nearFinal = Mathf.Clamp01(nearVolume) * _loopDuckGainCurrent * rampVolumeMul;
            near.volume = nearFinal;

            // #294: the near emitter's chain layer. Created lazily here as well because the near
            // source itself can appear mid-loop (prop resolves late) — without this it would wait
            // silent until the next loop state edge.
            if (_loopLayeredActive)
            {
                if (_nearChainLayerSource == null)
                {
                    EnsureLoopLayerSource(ref _nearChainLayerSource, near, BundyAudioProfile.SawNear);
                    StartLoopLayer(_nearChainLayerSource, BundyAudioCatalog.ChainsawLayerChainClip, near.loop, restart: true);
                    if (_nearChainLayerSource != null && _loopChainLayerSource != null && _loopChainLayerSource.isPlaying)
                        _nearChainLayerSource.timeSamples = _loopChainLayerSource.timeSamples;
                }

                AudioSource nearChain = _nearChainLayerSource;
                if (nearChain != null && nearChain.isPlaying)
                {
                    nearChain.pitch = pitch;
                    nearChain.volume = nearFinal * ChainLayerMix();
                }
            }
        }

        // #297: the strain texture rides the bog envelope this frame just wrote.
        UpdateCutResistanceLayer();
    }

    // 0 at BladeProximityZeroMeters, 1 at BladeProximityFullMeters, squared so the term is
    // effectively inert across a room and does all its work in the final metre. Measured from the
    // blade-anchored emitter (which the prop carries bodily through every swing) to THIS client's
    // listener, so it is a real geometric fact rather than an animation timestamp.
    private float UpdateBladeProximity(float dt)
    {
        if (!BladeProximityModulationEnabled)
        {
            _bladeProximity01 = 0f;
            return 0f;
        }

        Transform listener = StartOfRound.Instance?.audioListener != null
            ? StartOfRound.Instance.audioListener.transform
            : null;
        AudioSource blade = _movementLoopNearAudioSource ?? _chainsawContactAudioSource ?? _movementLoopAudioSource;

        float target = 0f;
        if (listener != null && blade != null)
        {
            float distance = Vector3.Distance(listener.position, blade.transform.position);
            target = 1f - Mathf.InverseLerp(BladeProximityFullMeters, BladeProximityZeroMeters, distance);
            target *= target;
        }

        _bladeProximity01 = Mathf.Lerp(
            _bladeProximity01,
            target,
            1f - Mathf.Exp(-dt / BladeProximitySmoothingSeconds));
        return _bladeProximity01;
    }

    // Base-layer clip identity, sampled once per frame. Both the footstep driver and the loop
    // modulation need it, both run on every client, and GetCurrentAnimatorClipInfo allocates.
    private string GetBaseLayerClip(out float normalizedFraction)
    {
        if (_baseLayerClipFrame == Time.frameCount)
        {
            normalizedFraction = _baseLayerClipFraction;
            return _baseLayerClipName;
        }

        _baseLayerClipFrame = Time.frameCount;
        _baseLayerClipName = string.Empty;
        _baseLayerClipFraction = 0f;
        if (creatureAnimator != null &&
            creatureAnimator.runtimeAnimatorController != null &&
            creatureAnimator.layerCount > 0)
        {
            AnimatorClipInfo[] clipInfo = creatureAnimator.GetCurrentAnimatorClipInfo(0);
            if (clipInfo != null && clipInfo.Length > 0 && clipInfo[0].clip != null)
            {
                _baseLayerClipName = clipInfo[0].clip.name;
                float normalized = creatureAnimator.GetCurrentAnimatorStateInfo(0).normalizedTime;
                _baseLayerClipFraction = normalized - Mathf.Floor(normalized);
            }
        }

        normalizedFraction = _baseLayerClipFraction;
        return _baseLayerClipName;
    }

    // Clip name rather than _actionKind: the modulation and the stomp detection both run on every
    // client, and the action state is owner-side only.
    private bool IsTauntClipPlaying()
    {
        return GetBaseLayerClip(out _).IndexOf("Taunt", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Issue #213: the two gaits Lawson asked to announce themselves — "especially audible and travel
    /// further outside when bundy is performing his sprint and his taunt animations."
    ///
    /// Read off the base-layer CLIP for the same reason the taunt's own detection is: this has to be
    /// true on every client, and sprint phase (<c>_sprintPhase</c>) is owner-side state.
    /// </summary>
    private bool IsHeavyGaitFootstep()
    {
        string clip = GetBaseLayerClip(out _);
        return clip.IndexOf("Sprint_Overhead", StringComparison.OrdinalIgnoreCase) >= 0 ||
            clip.IndexOf("Taunt", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Issue #212/#213: a footstep's AI/mechanic noise propagation, in ONE place.
    ///
    /// The two playback paths disagreed about the same event — the vanilla-clip RPC hardcoded
    /// <c>PlayAudibleNoise(pos, 8f, 0.4f)</c> while <c>PlayBundyAudio</c>'s generic table pinned
    /// footsteps at range 8 / loudness 0.35.
    ///
    /// This is NOT the player-audibility dial. <c>PlayAudibleNoise</c> is what other enemies and the
    /// radar react to; what a player hears is the AudioSource's volume, maxDistance and rolloff.
    /// Turning this up produces no audible change while making the rest of the map notice him, which
    /// is exactly why #213's "travel further" is answered on both dials separately.
    /// </summary>
    private void EmitFootstepNoise(Vector3 position)
    {
        if (RoundManager.Instance == null)
            return;

        float gait = IsHeavyGaitFootstep() ? HeavyGaitFootstepNoiseScale : 1f;
        RoundManager.Instance.PlayAudibleNoise(
            position,
            FootstepNoiseRange * gait,
            Mathf.Clamp01(FootstepNoiseLoudness * gait));
    }

    /// <summary>
    /// Issue #213, the audible half: widen the footstep emitter's range while he sprints or taunts.
    ///
    /// maxDistance rather than volume, because the rolloff is a Custom curve evaluated over
    /// <c>d/maxDistance</c> — widening the range lifts the whole shape, so every distance gets louder
    /// rather than the silence line merely moving out. A volume scale cannot do this: Unity clamps
    /// <c>AudioSource.volume</c> at 1 and <c>BundyFootstepVolume</c> is already 0.9.
    ///
    /// Client-local, and driven off the clip rather than the sprint state, because each listener
    /// evaluates its own distance and remote clients do not have the owner's sprint phase.
    /// </summary>
    private void UpdateFootstepGaitRange()
    {
        if (!_audioMixerRoutingApplied || _footstepAudioSource == null)
            return;

        float target = GetConfiguredFootstepMaxDistance() * (IsHeavyGaitFootstep() ? HeavyGaitFootstepRangeScale : 1f);
        if (Mathf.Abs(_footstepAudioSource.maxDistance - target) < 0.1f)
            return;

        _footstepAudioSource.maxDistance = target;
    }

    /// <summary>
    /// Issue #210: the blood take, layered under an impact that has already started.
    ///
    /// Server-authored and fanned out through its own ClientRpc so it replicates like every other
    /// Bundy cue. The DELAY is not sent — each client derives it from the same measured clip data, so
    /// there is nothing to keep in sync and a client with a different clip set still lands the layer
    /// against the impact it is actually hearing.
    /// </summary>
    private void PlayBloodLayerForImpact(BundyAudioCue impactCue, int impactClipIndex)
    {
        if (!IsServer || !BloodLayerEnabled || impactCue != BundyAudioCue.PlayerHit || impactClipIndex < 0)
            return;

        if (!BundyAudioCatalog.HasAny(BundyAudioCue.Blood))
            return;

        int bloodIndex = BundyAudioCatalog.PickIndex(BundyAudioCue.Blood);
        if (bloodIndex < 0)
            return;

        PlayBundyBloodLayerClientRpc(impactClipIndex, bloodIndex);
    }

    [ClientRpc]
    public void PlayBundyBloodLayerClientRpc(int impactClipIndex, int bloodClipIndex)
    {
        // Cause and effect, not a thicker single hit: the blood starts a beat AFTER the impact's
        // loudest point, and that point is measured per clip because across the shipped pool it
        // ranges from 0.038 s to 0.301 s. Then each blood take's own audible onset is subtracted, so
        // blood_01 (audible 15 ms in) and blood_02 (61 ms in) land in the same place relative to the
        // impact instead of 46 ms apart.
        float delay = BundyAudioCatalog.GetPlayerHitPeakSeconds(impactClipIndex)
            + BloodLayerGapSeconds
            - BundyAudioCatalog.GetBloodOnsetSeconds(bloodClipIndex);
        StartCoroutine(PlayBloodLayerAfter(Mathf.Max(0f, delay), bloodClipIndex));
    }

    private System.Collections.IEnumerator PlayBloodLayerAfter(float delay, int bloodClipIndex)
    {
        if (delay > 0f)
            yield return new WaitForSeconds(delay);

        if (isEnemyDead)
            yield break;

        AudioClip clip = BundyAudioCatalog.GetClip(BundyAudioCue.Blood, bloodClipIndex);
        if (clip == null)
            yield break;

        ResolveRuntimeReferences();
        // The same blade-anchored emitter the hit itself rode, so the two are co-located and share
        // one occlusion/absorption answer — a bleed that arrives from a different point in space
        // reads as a second event somewhere else rather than as a consequence of this one.
        AudioSource source = ResolveAudioSourceForCue(BundyAudioCue.PlayerHit);
        if (source == null)
            yield break;

        float volume = Mathf.Clamp01(BasicAttackDamageAudioVolume * BloodLayerVolumeScale);
        source.PlayOneShot(clip, volume);
        WalkieTalkie.TransmitOneShotAudio(source, clip, volume);
    }

    private void ReleaseActivePairedBasicAttack(PlayerControllerB victim, string reason)
    {
        if (!_activeBasicVictimPaired)
            return;

        PlayerControllerB pairedVictim = victim ?? _activeBasicVictim;
        if (pairedVictim != null)
            ReleasePairedBasicAttack(pairedVictim, _activeAttackIndex, reason);

        _activeBasicVictimPaired = false;
        _activeBasicVictim = null;
    }

    private void ReleaseActivePairedFinisher(PlayerControllerB victim, string reason)
    {
        if (!_activeFinisherVictimPaired)
            return;

        PlayerControllerB pairedVictim = victim ?? _activeFinisherVictim;
        if (pairedVictim != null)
            ReleasePairedFinisher(pairedVictim, _activeFinisherEntryIndex, _activeFinisherBodyIndex, reason);

        _activeFinisherVictimPaired = false;
        _activeFinisherVictim = null;
    }

}
