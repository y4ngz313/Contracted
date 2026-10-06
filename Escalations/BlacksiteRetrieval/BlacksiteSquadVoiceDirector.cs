using System;
using System.Collections.Generic;
using UnityEngine;

using Y4NGZCompany.Contracts._Shared;
namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    internal sealed class BlacksiteSquadVoiceDirector
    {
        private const float ChannelTailSeconds = 0.4f;
        private const float OrdinaryIntervalMin = 10f;
        private const float OrdinaryIntervalMax = 18f;
        private const float AmbientIntervalMin = 30f;
        private const float AmbientIntervalMax = 55f;
        private const float NoncriticalWindowSeconds = 20f;
        private const int MaxNoncriticalLinesPerWindow = 2;
        private const int RecentClipHistorySize = 10;

        private sealed class PendingVoice
        {
            internal BlacksiteVoiceEvent Event;
            internal BlacksiteSoldierAI PreferredSpeaker;
            internal bool Critical;
            internal bool Ambient;
            internal bool Transition;
            internal long TransitionKey;
            internal float Volume;
            internal float ExpiresAt;
            // #1271 S8: a response is part of the call that queued it: it skips the lock and cap,
            // moves no cadence, never draws a response of its own and is never spoken by the
            // caller. Aboard lets a boarded member speak a boarding-time line.
            internal BlacksiteSoldierAI ExcludedSpeaker;
            internal bool Response;
            internal bool Aboard;
        }

        private readonly BlacksiteSquadDirector _squad;
        private readonly List<BlacksiteSoldierAI> _speakerScratch = new List<BlacksiteSoldierAI>(8);
        private readonly List<int> _clipIndexScratch = new List<int>(8);
        private readonly Queue<string> _recentClipIds = new Queue<string>(RecentClipHistorySize);
        private readonly HashSet<string> _recentClipSet = new HashSet<string>(StringComparer.Ordinal);
        private readonly Queue<float> _noncriticalStarts = new Queue<float>(MaxNoncriticalLinesPerWindow + 1);
        private readonly HashSet<long> _scheduledTransitions = new HashSet<long>();

        private readonly Dictionary<(ulong Speaker, BlacksiteVoiceEvent Event), HashSet<string>> _voiceBags = new Dictionary<(ulong, BlacksiteVoiceEvent), HashSet<string>>();
        private PendingVoice _queued;
        private float _channelBusyUntil;
        private float _nextOrdinaryAt;
        private float _nextAmbientAt;
        // #1271 S8: one stored member, not a timer - an unforced pick skips whoever spoke last so
        // the six voices spread across the radio.
        private BlacksiteSoldierAI _lastSpeaker;

        internal BlacksiteSquadVoiceDirector(BlacksiteSquadDirector squad)
        {
            _squad = squad ?? throw new ArgumentNullException(nameof(squad));
            ResetCadence();
        }

        internal bool IsChannelBusy => Time.time < _channelBusyUntil;

        internal void Reset()
        {
            _queued = null;
            _lastSpeaker = null;
            _channelBusyUntil = 0f;
            _voiceBags.Clear();
            _recentClipIds.Clear();
            _recentClipSet.Clear();
            _noncriticalStarts.Clear();
            _scheduledTransitions.Clear();
            // #1066 U8: the reassurance gate's memory dies with the deployment, exactly as the
            // BlacksiteSquadDirector.ProgressToken it is compared against does. Leaving it behind
            // would let the first line of a new deployment be judged against the last one's score.
            _progressAtLastReassurance = int.MinValue;
            ResetCadence();
        }

        internal void UpdateServer()
        {
            if (!_squad.IsServerVoiceAuthority)
                return;

            PruneNoncriticalWindow();
            if (IsChannelBusy)
                return;

            if (_queued != null)
            {
                PendingVoice pending = _queued;
                if (Time.time > pending.ExpiresAt)
                {
                    LogSuppression(pending, "queued request expired");
                    _queued = null;
                }
                else
                {
                    // #1271 S8: the slot is emptied before the play, because the play itself may
                    // queue the response to the line it just spoke.
                    _queued = null;
                    if (TryPlay(pending))
                        return;
                    _queued = pending;
                }
            }

            if (_queued == null && Time.time >= _nextAmbientAt)
            {
                _nextAmbientAt = Time.time + UnityEngine.Random.Range(AmbientIntervalMin, AmbientIntervalMax);
                Request(
                    BlacksiteVoiceEvent.Patrol,
                    preferredSpeaker: null,
                    critical: false,
                    transition: false,
                    transitionToken: 0,
                    volume: 0.56f,
                    ambient: true);
            }
        }

        internal void Request(
            BlacksiteVoiceEvent voiceEvent,
            BlacksiteSoldierAI preferredSpeaker,
            bool critical = false,
            bool transition = false,
            int transitionToken = 0,
            float volume = 0.76f,
            bool ambient = false,
            bool immediate = false)
        {
            if (!_squad.IsServerVoiceAuthority)
                return;

            critical |= BlacksiteVoiceEventRules.IsCritical(voiceEvent);
            long transitionKey = transition ? ComposeTransitionKey(voiceEvent, transitionToken) : 0L;
            if (transition && _scheduledTransitions.Contains(transitionKey))
            {
                if (BlacksiteDiagnostics.Enabled)
                {
                    BlacksiteDiagnostics.LogRateLimited(
                        $"voice.duplicate.{transitionKey}",
                        5f,
                        "Voice",
                        $"event={voiceEvent} suppressed: transition {transitionToken} was already scheduled.");
                }
                return;
            }

            var pending = new PendingVoice
            {
                Event = voiceEvent,
                PreferredSpeaker = preferredSpeaker,
                Critical = critical,
                Ambient = ambient,
                Transition = transition,
                TransitionKey = transitionKey,
                Volume = Mathf.Clamp01(volume),
                ExpiresAt = Time.time + (critical ? 12f : transition ? 25f : 8f),
                Aboard = BlacksiteVoiceEventRules.IsSpokenAboard(voiceEvent),
            };

            if (transition)
                _scheduledTransitions.Add(transitionKey);

            // #314 D5: an immediate request is one whose meaning expires -- a casualty report must
            // reach the squad immediately -- so it cuts the channel instead of queueing behind it.
            // #1271 S8: a queued response holds the channel like a line still playing, so an
            // ordinary request cannot talk over the answer; a critical one still replaces it.
            // A transition replaces it too: its key is recorded above before queueing, so
            // dropping it would silence that line for the deployment, and an answer is worth
            // less than a once-only call.
            if ((IsChannelBusy || (_queued != null && _queued.Response)) && !immediate)
            {
                if (critical || _queued == null || (transition && _queued.Response))
                {
                    if (BlacksiteDiagnostics.Enabled)
                    {
                        string action = _queued == null ? "queued" : "replaced queued request";
                        BlacksiteDiagnostics.Log(
                            "Voice",
                            $"event={voiceEvent} {action}; critical={critical}, channelRemaining={Mathf.Max(0f, _channelBusyUntil - Time.time):0.00}s.");
                    }
                    _queued = pending;
                }
                else
                {
                    LogSuppression(pending, "channel busy with another queued request");
                }
                return;
            }

            if (TryPlay(pending))
                return;

            if (critical || transition)
                _queued = pending;
        }

        private bool TryPlay(PendingVoice pending)
        {
            if (pending == null || Time.time > pending.ExpiresAt)
                return LogSuppression(pending, "request expired");
            if (!pending.Critical && !pending.Response)
            {
                if (!pending.Ambient && Time.time < _nextOrdinaryAt)
                    return LogSuppression(pending, "ordinary cadence lock");
                PruneNoncriticalWindow();
                if (_noncriticalStarts.Count >= MaxNoncriticalLinesPerWindow)
                    return LogSuppression(pending, "noncritical 20-second cap");
            }

            AudioClip[] clips = ContractAssetLoader.GetBlacksiteVoiceClips(pending.Event);
            if (clips == null || clips.Length == 0)
                return LogSuppression(pending, "no bundled clips");
            BlacksiteSoldierAI speaker = SelectSpeaker(pending);
            if (speaker == null) return LogSuppression(pending, pending.Response ? "no eligible responder" : "no eligible speaker");
            int clipIndex = SelectClipIndex(pending.Event, clips, pending.Critical, speaker.NetworkObjectId);
            if (clipIndex < 0)
                return LogSuppression(pending, "recent-clip exclusion left no candidate");


            float pitch = UnityEngine.Random.Range(0.98f, 1.02f);
            AudioClip clip = clips[clipIndex];
            speaker.PresentSquadVoiceServer(pending.Event, clipIndex, pending.Volume, pitch);
            // Never shorten the channel: a line starting while a longer one is still playing must
            // extend the busy window, not stomp it and let a third line talk over both.
            _channelBusyUntil = Mathf.Max(
                _channelBusyUntil,
                Time.time + Mathf.Max(0.1f, clip.length / pitch) + ChannelTailSeconds);
            RememberClip(BuildClipId(pending.Event, clip));

            if (!pending.Critical && !pending.Response)
                _noncriticalStarts.Enqueue(Time.time);
            if (!pending.Ambient && !pending.Response)
                _nextOrdinaryAt = Time.time + UnityEngine.Random.Range(OrdinaryIntervalMin, OrdinaryIntervalMax);
            if (pending.Ambient)
                _nextAmbientAt = Time.time + UnityEngine.Random.Range(AmbientIntervalMin, AmbientIntervalMax);

            // #1271 S8: the answer rides the existing one-slot queue and plays when UpdateServer
            // sees the channel free, so it starts one channel tail after the call. It never
            // displaces a waiting call: an occupied slot means no answer.
            if (!pending.Response && _queued == null
                && BlacksiteVoiceEventRules.TryGetResponse(pending.Event, pending.Ambient, out BlacksiteVoiceEvent response))
            {
                _queued = new PendingVoice
                {
                    Event = response,
                    Response = true,
                    ExcludedSpeaker = speaker,
                    Critical = pending.Critical,
                    Aboard = pending.Aboard,
                    Volume = pending.Volume,
                    ExpiresAt = _channelBusyUntil + 1f,
                };
            }
            _lastSpeaker = speaker;

            if (BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.Log(
                    "Voice",
                    $"event={pending.Event}, speaker={speaker.MemberIndex}, clip={clip.name}, duration={clip.length:0.00}s, critical={pending.Critical}, ambient={pending.Ambient}, response={pending.Response}.");
            }
            return true;
        }

        /// <summary>
        /// #1271 S8: who speaks a line, in order. A response goes to the Leader when a member
        /// reported, otherwise to anyone but the caller; a leader call goes to the Leader; then
        /// the member who saw or did the thing; then anyone, skipping the last speaker. Roles are
        /// reassigned over survivors, so "the Leader" is whoever holds the role now.
        /// </summary>
        private BlacksiteSoldierAI SelectSpeaker(PendingVoice pending)
        {
            _speakerScratch.Clear();
            _squad.CollectEligibleVoiceSpeakers(_speakerScratch, includeBoarded: pending.Aboard);
            if (pending.ExcludedSpeaker != null)
                _speakerScratch.Remove(pending.ExcludedSpeaker);
            if (_speakerScratch.Count == 0)
                return null;
            if (pending.Response)
            {
                BlacksiteSoldierAI caller = pending.ExcludedSpeaker;
                bool callerLeads = caller != null && caller.TacticalRole == BlacksiteSoldierTacticalRole.Leader;
                BlacksiteSoldierAI leader = callerLeads ? null : EligibleLeader();
                return leader != null ? leader : PickAvoidingLastSpeaker();
            }
            if (BlacksiteVoiceEventRules.IsLeaderCall(pending.Event))
            {
                BlacksiteSoldierAI leader = EligibleLeader();
                if (leader != null)
                    return leader;
            }
            BlacksiteSoldierAI preferred = pending.PreferredSpeaker;
            if (preferred != null && _speakerScratch.Contains(preferred))
                return preferred;
            return PickAvoidingLastSpeaker();
        }

        private BlacksiteSoldierAI EligibleLeader()
        {
            for (int i = 0; i < _speakerScratch.Count; i++)
                if (_speakerScratch[i].TacticalRole == BlacksiteSoldierTacticalRole.Leader)
                    return _speakerScratch[i];
            return null;
        }

        private BlacksiteSoldierAI PickAvoidingLastSpeaker()
        {
            int count = _speakerScratch.Count;
            int last = count > 1 && _lastSpeaker != null ? _speakerScratch.IndexOf(_lastSpeaker) : -1;
            if (last < 0)
                return _speakerScratch[UnityEngine.Random.Range(0, count)];
            // Draw from the other count - 1 members by stepping over the last speaker's slot.
            int pick = UnityEngine.Random.Range(0, count - 1);
            return _speakerScratch[pick >= last ? pick + 1 : pick];
        }

        /// <summary>
        /// #1066 U8: the lines that CLAIM the squad is on top of the situation. They are the only
        /// voice content that is falsifiable by the mission state, so they are gated on the squad
        /// having actually achieved something since one was last spoken
        /// (<see cref="BlacksiteSquadDirector.ProgressToken"/>). Everything else in the Patrol and
        /// Searching pools is chatter and stays on its timer.
        /// </summary>
        private static readonly HashSet<string> ReassuranceClipNames =
            new HashSet<string>(StringComparer.Ordinal) { "sectorissecurenovison" };

        private int _progressAtLastReassurance = int.MinValue;

        private bool ReassuranceAllowed =>
            _squad.ProgressToken != _progressAtLastReassurance;

        private int SelectClipIndex(BlacksiteVoiceEvent voiceEvent, AudioClip[] clips, bool critical, ulong speaker)
        {
            // #1066 U8: a reassurance line is eligible only when the squad has progressed since the
            // last one. The clip is never removed from the bag or the history, so the rotation is
            // unchanged; it simply cannot be SELECTED while the squad is wedged.
            bool reassuranceAllowed = ReassuranceAllowed;
            var bagKey = (speaker, voiceEvent);
            if (!_voiceBags.TryGetValue(bagKey, out var played)) _voiceBags[bagKey] = played = new HashSet<string>();
            bool exhausted = true;
            foreach (var clip in clips) if (clip != null && !played.Contains(clip.name.ToLowerInvariant())) exhausted = false;
            if (exhausted) played.Clear();
            _clipIndexScratch.Clear();
            for (int i = 0; i < clips.Length; i++)
            {
                AudioClip clip = clips[i];
                if (clip != null && !played.Contains(clip.name.ToLowerInvariant()) && !_recentClipSet.Contains(BuildClipId(voiceEvent, clip)))
                    _clipIndexScratch.Add(i);
            }

            // #1066 U8: drop the reassurance clips from the primary pool while the squad has nothing
            // to be reassuring about. Done after the pool is built so the bag and history rotation
            // above are untouched, and only when something else is available - a pool that is ALL
            // reassurance keeps its old answer rather than going silent.
            if (!reassuranceAllowed)
                FilterOutReassurance(clips);

            if (_clipIndexScratch.Count > 0)
            { int selected = _clipIndexScratch[UnityEngine.Random.Range(0, _clipIndexScratch.Count)]; played.Add(clips[selected].name.ToLowerInvariant()); NoteIfReassurance(clips[selected]); return selected; }
            // A small semantic pool can be entirely in the global history. Pick its least
            // recently used clip instead of silencing that event or repeating index zero.
            int oldest = -1, oldestRank = int.MaxValue;
            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i] == null || played.Contains(clips[i].name.ToLowerInvariant())) continue;
                if (!reassuranceAllowed && IsReassurance(clips[i])) continue;
                string key = BuildClipId(voiceEvent, clips[i]); int rank = 0;
                foreach (string recent in _recentClipIds) { if (recent == key) break; rank++; }
                if (rank < oldestRank) { oldestRank = rank; oldest = i; }
            }
            if (oldest >= 0) { played.Add(clips[oldest].name.ToLowerInvariant()); NoteIfReassurance(clips[oldest]); return oldest; }

            string mostRecent = _recentClipIds.Count > 0 ? LastQueuedValue(_recentClipIds) : string.Empty;
            for (int i = 0; i < clips.Length; i++)
            {
                if (clips[i] == null || BuildClipId(voiceEvent, clips[i]) == mostRecent) continue;
                if (!reassuranceAllowed && IsReassurance(clips[i])) continue;
                NoteIfReassurance(clips[i]);
                return i;
            }
            if (clips.Length > 0 && clips[0] != null)
            {
                NoteIfReassurance(clips[0]);
                return 0;
            }
            return -1;
        }

        private static bool IsReassurance(AudioClip clip)
            => clip != null && ReassuranceClipNames.Contains(clip.name.ToLowerInvariant());

        /// <summary>
        /// #1066 U8: a reassurance line spends the progress it was granted for, so the next one has
        /// to wait for the squad to achieve something else.
        /// </summary>
        private void NoteIfReassurance(AudioClip clip)
        {
            if (IsReassurance(clip))
                _progressAtLastReassurance = _squad.ProgressToken;
        }

        private void FilterOutReassurance(AudioClip[] clips)
        {
            int kept = 0;
            for (int i = 0; i < _clipIndexScratch.Count; i++)
                if (!IsReassurance(clips[_clipIndexScratch[i]]))
                    _clipIndexScratch[kept++] = _clipIndexScratch[i];
            if (kept > 0 && kept < _clipIndexScratch.Count)
                _clipIndexScratch.RemoveRange(kept, _clipIndexScratch.Count - kept);
        }

        private static bool LogSuppression(PendingVoice pending, string reason)
        {
            if (pending != null && BlacksiteDiagnostics.Enabled)
            {
                BlacksiteDiagnostics.LogRateLimited(
                    $"voice.suppressed.{pending.Event}.{reason}",
                    2f,
                    "Voice",
                    $"event={pending.Event} suppressed: {reason}; critical={pending.Critical}, ambient={pending.Ambient}.");
            }
            return false;
        }

        private void RememberClip(string clipId)
        {
            if (string.IsNullOrEmpty(clipId))
                return;
            // Move a repeat to the tail; duplicate queue entries would make the set forget it too early.
            int count = _recentClipIds.Count;
            for (int i = 0; i < count; i++) { string old = _recentClipIds.Dequeue(); if (old != clipId) _recentClipIds.Enqueue(old); }
            while (_recentClipIds.Count >= RecentClipHistorySize)
            {
                string removed = _recentClipIds.Dequeue();
                _recentClipSet.Remove(removed);
            }
            _recentClipIds.Enqueue(clipId);
            _recentClipSet.Add(clipId);
        }

        private void PruneNoncriticalWindow()
        {
            float cutoff = Time.time - NoncriticalWindowSeconds;
            while (_noncriticalStarts.Count > 0 && _noncriticalStarts.Peek() <= cutoff)
                _noncriticalStarts.Dequeue();
        }

        private void ResetCadence()
        {
            _nextOrdinaryAt = Time.time + UnityEngine.Random.Range(OrdinaryIntervalMin, OrdinaryIntervalMax);
            _nextAmbientAt = Time.time + UnityEngine.Random.Range(AmbientIntervalMin, AmbientIntervalMax);
        }

        private static long ComposeTransitionKey(BlacksiteVoiceEvent voiceEvent, int transitionToken)
        {
            return ((long)(int)voiceEvent << 32) | (uint)transitionToken;
        }

        private static string BuildClipId(BlacksiteVoiceEvent voiceEvent, AudioClip clip)
        {
            return clip == null ? string.Empty : clip.name.ToLowerInvariant();
        }

        private static string LastQueuedValue(Queue<string> values)
        {
            string last = string.Empty;
            foreach (string value in values)
                last = value;
            return last;
        }
    }
}
