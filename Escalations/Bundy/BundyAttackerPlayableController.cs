using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Escalations.Bundy
{
    internal sealed class BundyAttackerPlayableController : MonoBehaviour
    {
        private const float CleanupGraceSeconds = 1.25f;
        private const float SequenceTransitionStartNormalized = 0.95f;
        private const float SequenceTransitionDurationNormalized = 0.05f;

        private static readonly Dictionary<string, AnimationClip> ClipsByName =
            new Dictionary<string, AnimationClip>(StringComparer.Ordinal);

        private BundyAIBase _bundy;
        private Animator _animator;
        private string _actionId;
        private AnimationClip[] _clips = Array.Empty<AnimationClip>();
        private float[] _clipSequenceStartTimes = Array.Empty<float>();
        private float _clipSequenceLength;
        private PlayableGraph _graph;
        private AnimationMixerPlayable _sequenceMixer;
        private AnimationClipPlayable[] _clipPlayables = Array.Empty<AnimationClipPlayable>();
        // #276: scratch weights buffer reused by every UpdateSequencePlayback pass (it runs twice
        // per frame — LateUpdate + end-of-frame — and previously allocated a fresh array each pass).
        private float[] _sequenceWeights = Array.Empty<float>();
        private RuntimeAnimatorController _savedController;
        private bool _savedAnimatorEnabled;
        private bool _savedApplyRootMotion;
        private AnimatorUpdateMode _savedUpdateMode;
        private AnimatorCullingMode _savedCullingMode;
        private Transform _attackerAnchor;
        private double _networkStartTime;
        private float _cleanupAt;
        private bool _active;
        private Coroutine _endOfFrameRoutine;
        // #716 G6: last frame the playable graph was actually evaluated (see UpdateSequencePlayback).
        private int _lastSequenceEvaluationFrame = -1;
        // #716 F4: "no attacker sequence for this paired action" is a normal outcome with the
        // shipped bundle; report it once per session instead of per action.
        private static bool _missingAttackerMetadataLogged;
        private int _currentStrongestIndex = -1;
        private float _currentStrongestLocalTime;
        private float _currentStrongestWeight;
        private int _lastSyncDebugPhaseIndex = -1;
        private float _nextSyncDebugTickAt;
        private int _syncDebugTick;

        internal static void RegisterClips(AssetBundle bundle)
        {
            ClipsByName.Clear();

            if (bundle == null)
            {
                LogWarning("Attacker playable clip registration skipped; bundle is null.");
                return;
            }

            AnimationClip[] clips = bundle.LoadAllAssets<AnimationClip>();
            foreach (AnimationClip clip in clips)
            {
                if (clip == null || string.IsNullOrEmpty(clip.name))
                    continue;

                if (clip.name.EndsWith("_Att", StringComparison.Ordinal) ||
                    clip.name.StartsWith("megajam_Paired_", StringComparison.Ordinal))
                {
                    ClipsByName[clip.name] = clip;
                }
            }

            LogInfo($"Registered paired attacker playable clips: {ClipsByName.Count}.");
        }

        internal static bool Begin(BundyAIBase bundy, string actionId, double networkStartTime)
        {
            if (bundy == null || string.IsNullOrEmpty(actionId))
                return false;

            if (!BundyPairedActionCatalog.TryGet(actionId, out BundyPairedActionDefinition action) ||
                action == null ||
                action.attackerSequence == null ||
                action.attackerSequence.Length == 0)
            {
                // #716 F4: the shipped bundle carries no attacker sequences for the non-chainsaw
                // paired actions, so this fired at Warning on every one of them for the whole
                // session. It is a normal-path outcome (the attacker keeps its Animator-driven
                // pose), so it is one Info line per session, not a warning per action.
                // The gate is checked here, not inside LogInfo, so the once-flag is only spent on a
                // line that actually printed - otherwise the first finisher with diagnostics off
                // would consume the notice for the whole session.
                if (!_missingAttackerMetadataLogged && BundyLogGate.Allows(BundyLogCategory.Anim, 1))
                {
                    _missingAttackerMetadataLogged = true;
                    LogInfo(
                        $"Attacker playable not used for paired action '{actionId}' (no attacker sequence in the " +
                        "bundle); the attacker keeps its Animator pose. Logged once per session.");
                }

                return false;
            }

            var clips = new AnimationClip[action.attackerSequence.Length];
            for (int i = 0; i < action.attackerSequence.Length; i++)
            {
                string clipName = action.attackerSequence[i];
                if (!TryResolveClipForAction(actionId, clipName, out AnimationClip clip, out string resolvedClipName) || clip == null)
                {
                    LogWarning($"Attacker playable begin skipped; clip '{clipName}' is not registered for action '{actionId}'.");
                    return false;
                }

                if (!string.Equals(resolvedClipName, clipName, StringComparison.Ordinal))
                    LogWarning($"Attacker playable resolved '{clipName}' -> source-pack clip '{resolvedClipName}' for action '{actionId}'.");

                clips[i] = clip;
            }

            var controller = bundy.GetComponent<BundyAttackerPlayableController>();
            if (controller == null)
                controller = bundy.gameObject.AddComponent<BundyAttackerPlayableController>();

            return controller.BeginInternal(bundy, actionId, clips, networkStartTime);
        }

        private static bool TryResolveClipForAction(
            string actionId,
            string clipName,
            out AnimationClip clip,
            out string resolvedClipName)
        {
            clip = null;
            resolvedClipName = clipName;

            if (IsChainsawPairedAttackerAction(actionId) &&
                !string.IsNullOrEmpty(clipName) &&
                clipName.StartsWith("chainsaw_Paired_Chainsaw_", StringComparison.Ordinal))
            {
                string sourcePackClipName = clipName.Substring("chainsaw_".Length);
                if (ClipsByName.TryGetValue(sourcePackClipName, out clip) && clip != null)
                {
                    resolvedClipName = sourcePackClipName;
                    return true;
                }
            }

            return !string.IsNullOrEmpty(clipName) &&
                ClipsByName.TryGetValue(clipName, out clip) &&
                clip != null;
        }

        private static bool IsChainsawPairedAttackerAction(string actionId)
        {
            return !string.IsNullOrEmpty(actionId) &&
                actionId.StartsWith("chainsaw.finisher.", StringComparison.Ordinal);
        }

        internal static void EndActive(BundyAIBase bundy, string reason)
        {
            var controller = bundy != null ? bundy.GetComponent<BundyAttackerPlayableController>() : null;
            if (controller != null && controller._active)
                controller.EndInternal(reason);
        }

        internal static bool IsActive(BundyAIBase bundy)
        {
            var controller = bundy != null ? bundy.GetComponent<BundyAttackerPlayableController>() : null;
            return controller != null && controller._active;
        }

        internal static string GetDebugState(BundyAIBase bundy)
        {
            var controller = bundy != null ? bundy.GetComponent<BundyAttackerPlayableController>() : null;
            if (controller == null)
                return "attackerPlayableController=<missing>";

            return controller.FormatDebugState();
        }

        private bool BeginInternal(
            BundyAIBase bundy,
            string actionId,
            AnimationClip[] clips,
            double networkStartTime)
        {
            if (_active)
            {
                if (string.Equals(_actionId, actionId, StringComparison.Ordinal) &&
                    Math.Abs(_networkStartTime - networkStartTime) < 0.05d)
                {
                    return true;
                }

                EndInternal("replaced by new paired attacker playable");
            }

            _bundy = bundy;
            _animator = bundy.creatureAnimator != null
                ? bundy.creatureAnimator
                : bundy.GetComponentInChildren<Animator>(true);
            if (_animator == null)
            {
                LogWarning($"Attacker playable begin skipped for action '{actionId}'; Bundy animator missing.");
                return false;
            }

            _attackerAnchor =
                FindChildByLeafName(_animator.transform, "CC_Base_Hip", "CC_Base_Pelvis", "pelvis", "mixamorig:Hips", "Hips") ??
                _animator.transform;
            _actionId = actionId;
            _clips = clips ?? Array.Empty<AnimationClip>();
            _networkStartTime = networkStartTime;
            BuildClipSequenceTiming();
            _cleanupAt = Time.time + Mathf.Max(_clipSequenceLength, 0.1f) + CleanupGraceSeconds;
            _currentStrongestIndex = -1;
            _currentStrongestLocalTime = 0f;
            _currentStrongestWeight = 0f;
            // #716 G6: a new action must evaluate even if the previous one already stamped this
            // frame (an end and a begin can land in the same frame).
            _lastSequenceEvaluationFrame = -1;
            _lastSyncDebugPhaseIndex = -1;
            _nextSyncDebugTickAt = 0f;
            _syncDebugTick = 0;

            _savedController = _animator.runtimeAnimatorController;
            _savedAnimatorEnabled = _animator.enabled;
            _savedApplyRootMotion = _animator.applyRootMotion;
            _savedUpdateMode = _animator.updateMode;
            _savedCullingMode = _animator.cullingMode;

            try
            {
                _animator.enabled = true;
                _animator.applyRootMotion = false;
                _animator.updateMode = AnimatorUpdateMode.Normal;
                _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _animator.runtimeAnimatorController = null;

                CreateGraph();
                UpdateSequencePlayback();
                _active = true;
                _endOfFrameRoutine = StartCoroutine(EndOfFrameLoop());

                // #277: routine success, downgraded from warning to gated info.
                if (BundyLogGate.Allows(BundyLogCategory.Anim, 1))
                    LogInfo(
                    $"Started paired attacker playable '{actionId}' on '{_animator.name}'; " +
                    $"savedController='{(_savedController != null ? _savedController.name : "<null>")}', " +
                    $"controllerNowNull={_animator.runtimeAnimatorController == null}, animEnabled={_animator.enabled}, " +
                    $"graphValid={_graph.IsValid()}, graphPlaying={(_graph.IsValid() && _graph.IsPlaying())}, " +
                    $"sequence={FormatSequence(_clips)}, start={networkStartTime:F3}, length={_clipSequenceLength:F2}.");
                return true;
            }
            catch (Exception e)
            {
                LogWarning($"Attacker playable begin failed for action '{actionId}': {e.GetType().Name}: {e.Message}");
                EndInternal("begin failed");
                return false;
            }
        }

        private void LateUpdate()
        {
            if (!_active)
                return;

            if (_bundy == null || _bundy.isEnemyDead || _animator == null)
            {
                EndInternal("actor invalid");
                return;
            }

            if (Time.time >= _cleanupAt)
            {
                EndInternal("clip timeout");
                return;
            }

            UpdateSequencePlayback();
        }

        private void OnDestroy()
        {
            if (_active)
                EndInternal("controller destroyed");
        }

        private IEnumerator EndOfFrameLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (_active)
            {
                yield return wait;
                // #716 G6: the authoritative pass of the frame; forces past the frame latch.
                if (_active)
                    UpdateSequencePlayback(force: true);
            }
        }

        private void EndInternal(string reason)
        {
            if (!_active && !_graph.IsValid())
                return;

            if (_endOfFrameRoutine != null)
            {
                StopCoroutine(_endOfFrameRoutine);
                _endOfFrameRoutine = null;
            }

            string endedAction = _actionId;
            DestroyGraph();

            if (_animator != null)
            {
                _animator.runtimeAnimatorController = _savedController;
                _animator.applyRootMotion = _savedApplyRootMotion;
                _animator.updateMode = _savedUpdateMode;
                _animator.cullingMode = _savedCullingMode;
                _animator.enabled = _savedAnimatorEnabled;
                if (_animator.enabled)
                    _animator.Update(0f);
            }

            _active = false;
            _bundy = null;
            _animator = null;
            _attackerAnchor = null;
            _actionId = null;
            _clips = Array.Empty<AnimationClip>();
            _clipSequenceStartTimes = Array.Empty<float>();
            _clipSequenceLength = 0f;
            _networkStartTime = 0d;
            _cleanupAt = 0f;
            _currentStrongestIndex = -1;
            _currentStrongestLocalTime = 0f;
            _currentStrongestWeight = 0f;
            _lastSequenceEvaluationFrame = -1;
            _lastSyncDebugPhaseIndex = -1;
            _nextSyncDebugTickAt = 0f;
            _syncDebugTick = 0;

            // #277: routine success, downgraded from warning to gated info.
            if (BundyLogGate.Allows(BundyLogCategory.Anim, 1))
                LogInfo($"Ended paired attacker playable '{endedAction}'; reason={reason}.");
        }

        private void CreateGraph()
        {
            if (_animator == null || _clips == null || _clips.Length == 0)
                throw new InvalidOperationException("Paired attacker playable requires an animator and clips.");

            _graph = PlayableGraph.Create($"BundyAttacker_{_actionId}");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _sequenceMixer = AnimationMixerPlayable.Create(_graph, _clips.Length);
            _clipPlayables = new AnimationClipPlayable[_clips.Length];

            for (int i = 0; i < _clips.Length; i++)
            {
                AnimationClip clip = _clips[i];
                if (clip == null)
                    throw new InvalidOperationException($"Paired attacker clip {i} is null.");

                AnimationClipPlayable playable = AnimationClipPlayable.Create(_graph, clip);
                playable.SetApplyFootIK(false);
                playable.SetApplyPlayableIK(false);
                playable.SetDuration(clip.length);
                playable.SetTime(0d);
                _clipPlayables[i] = playable;
                _graph.Connect(playable, 0, _sequenceMixer, i);
                _sequenceMixer.SetInputWeight(i, i == 0 ? 1f : 0f);
            }

            AnimationPlayableOutput output = AnimationPlayableOutput.Create(_graph, "BundyAttackerOutput", _animator);
            output.SetSourcePlayable(_sequenceMixer);
            _graph.Play();
        }

        private void BuildClipSequenceTiming()
        {
            _clipSequenceStartTimes = new float[_clips.Length];
            _clipSequenceLength = 0f;

            for (int i = 0; i < _clips.Length; i++)
            {
                if (i == 0)
                {
                    _clipSequenceStartTimes[i] = 0f;
                }
                else
                {
                    AnimationClip previousClip = _clips[i - 1];
                    float previousLength = previousClip != null ? Mathf.Max(0.01f, previousClip.length) : 0.01f;
                    _clipSequenceStartTimes[i] =
                        _clipSequenceStartTimes[i - 1] + previousLength * SequenceTransitionStartNormalized;
                }

                AnimationClip clip = _clips[i];
                float clipLength = clip != null ? Mathf.Max(0.01f, clip.length) : 0.01f;
                _clipSequenceLength = _clipSequenceStartTimes[i] + clipLength;
            }
        }

        /// <summary>
        /// #716 G6 frame latch. LateUpdate and the end-of-frame loop both reached this every frame,
        /// so the attacker graph evaluated twice for one pose. The end-of-frame pass is the
        /// authoritative one (it runs after Unity's animation update) and passes
        /// <paramref name="force"/>; while that loop is running the earlier callers reuse its pose.
        /// </summary>
        private void UpdateSequencePlayback(bool force = false)
        {
            if (!_graph.IsValid() || !_sequenceMixer.IsValid() || _clipPlayables == null || _clipPlayables.Length == 0)
                return;

            if (!force &&
                (_lastSequenceEvaluationFrame == Time.frameCount || _endOfFrameRoutine != null))
            {
                return;
            }

            float elapsed = Mathf.Clamp((float)GetElapsedSeconds(), 0f, Mathf.Max(0f, _clipSequenceLength));
            float[] weights = _sequenceWeights;
            if (weights == null || weights.Length != _clipPlayables.Length)
            {
                weights = new float[_clipPlayables.Length];
                _sequenceWeights = weights;
            }
            else
            {
                Array.Clear(weights, 0, weights.Length);
            }
            float totalWeight = 0f;
            int strongestIndex = 0;
            float strongestWeight = 0f;

            for (int i = 0; i < _clipPlayables.Length; i++)
            {
                AnimationClip clip = i < _clips.Length ? _clips[i] : null;
                if (clip == null || !_clipPlayables[i].IsValid())
                    continue;

                float clipStart = i < _clipSequenceStartTimes.Length ? _clipSequenceStartTimes[i] : 0f;
                float clipLength = Mathf.Max(0.01f, clip.length);
                float localTime = elapsed - clipStart;
                if (elapsed >= _clipSequenceLength && i == _clipPlayables.Length - 1)
                    localTime = clipLength;

                if (localTime < 0f || localTime > clipLength)
                {
                    _sequenceMixer.SetInputWeight(i, 0f);
                    continue;
                }

                float weight = 1f;
                if (i > 0)
                {
                    float fadeInDuration = GetSequenceTransitionDuration(i - 1);
                    if (fadeInDuration > 0f && localTime < fadeInDuration)
                        weight = Mathf.Min(weight, Mathf.Clamp01(localTime / fadeInDuration));
                }

                if (i + 1 < _clipPlayables.Length)
                {
                    float fadeOutDuration = GetSequenceTransitionDuration(i);
                    float fadeOutStart = clipLength * SequenceTransitionStartNormalized;
                    if (fadeOutDuration > 0f && localTime >= fadeOutStart)
                        weight = Mathf.Min(weight, Mathf.Clamp01(1f - ((localTime - fadeOutStart) / fadeOutDuration)));
                }

                _clipPlayables[i].SetTime(Mathf.Clamp(localTime, 0f, clipLength));
                weights[i] = weight;
                totalWeight += weight;
                if (weight > strongestWeight)
                {
                    strongestWeight = weight;
                    strongestIndex = i;
                }
            }

            if (totalWeight <= 0f)
            {
                strongestIndex = elapsed >= _clipSequenceLength ? _clipPlayables.Length - 1 : 0;
                totalWeight = 1f;
                weights[strongestIndex] = 1f;
            }

            for (int i = 0; i < _clipPlayables.Length; i++)
                _sequenceMixer.SetInputWeight(i, weights[i] / totalWeight);

            _lastSequenceEvaluationFrame = Time.frameCount;
            _graph.Evaluate(0f);
            if (!_active || _clipPlayables == null || _clipPlayables.Length == 0 || weights.Length == 0)
                return;

            int maxIndex = Mathf.Min(_clipPlayables.Length, weights.Length) - 1;
            if (maxIndex < 0)
                return;

            _currentStrongestIndex = Mathf.Clamp(strongestIndex, 0, maxIndex);
            _currentStrongestLocalTime = GetClipLocalTime(_currentStrongestIndex, elapsed);
            _currentStrongestWeight = weights[_currentStrongestIndex] / totalWeight;
            LogSyncDebug(elapsed);
            _bundy?.ApplyPairedPlayableRootCorrection(
                _actionId,
                _attackerAnchor,
                elapsed,
                _clipSequenceLength);
        }

        private float GetSequenceTransitionDuration(int sourceClipIndex)
        {
            if (_clips == null || sourceClipIndex < 0 || sourceClipIndex >= _clips.Length)
                return 0f;

            AnimationClip clip = _clips[sourceClipIndex];
            return clip != null
                ? Mathf.Max(1f / 60f, clip.length * SequenceTransitionDurationNormalized)
                : 0f;
        }

        private double GetElapsedSeconds()
        {
            double now = Unity.Netcode.NetworkManager.Singleton != null
                ? Unity.Netcode.NetworkManager.Singleton.ServerTime.Time
                : Time.timeAsDouble;

            return Math.Max(0d, now - _networkStartTime);
        }

        private float GetClipLocalTime(int clipIndex, float elapsed)
        {
            if (_clips == null || clipIndex < 0 || clipIndex >= _clips.Length || _clips[clipIndex] == null)
                return 0f;

            float clipStart = _clipSequenceStartTimes != null && clipIndex < _clipSequenceStartTimes.Length
                ? _clipSequenceStartTimes[clipIndex]
                : 0f;
            return Mathf.Clamp(elapsed - clipStart, 0f, Mathf.Max(0.01f, _clips[clipIndex].length));
        }

        private void LogSyncDebug(float elapsed)
        {
            if (!SyncDebugEnabled || _clips == null || _clips.Length == 0)
                return;

            bool phaseChanged = _currentStrongestIndex != _lastSyncDebugPhaseIndex;
            if (!phaseChanged && Time.time < _nextSyncDebugTickAt)
                return;

            _nextSyncDebugTickAt = Time.time + 0.25f;
            string phase = phaseChanged ? "phase" : "tick";
            _lastSyncDebugPhaseIndex = _currentStrongestIndex;
            _syncDebugTick++;

            AnimationClip activeClip = _currentStrongestIndex >= 0 && _currentStrongestIndex < _clips.Length
                ? _clips[_currentStrongestIndex]
                : null;
            string activeClipName = activeClip != null ? activeClip.name : "<none>";
            float activeClipLength = activeClip != null ? Mathf.Max(0.01f, activeClip.length) : 0.01f;
            Vector3 bundyRoot = _bundy != null ? _bundy.transform.position : Vector3.zero;
            Vector3 anchor = _attackerAnchor != null ? _attackerAnchor.position : Vector3.zero;

            LogInfo(
                $"BundySyncDebug attacker {phase}: tick={_syncDebugTick}, frame={Time.frameCount}, " +
                $"action='{_actionId}', elapsed={elapsed:F3}/{_clipSequenceLength:F3}, " +
                $"clipIndex={_currentStrongestIndex}, clip='{activeClipName}', " +
                $"clipLocal={_currentStrongestLocalTime:F3}/{activeClipLength:F3}, weight={_currentStrongestWeight:F2}, " +
                $"bundyRoot={FormatVector(bundyRoot)}, attackerAnchor={FormatVector(anchor)}.");
        }

        private void DestroyGraph()
        {
            if (_graph.IsValid())
                _graph.Destroy();

            _sequenceMixer = default;
            _clipPlayables = Array.Empty<AnimationClipPlayable>();
            _sequenceWeights = Array.Empty<float>();
        }

        private static string FormatSequence(AnimationClip[] clips)
        {
            if (clips == null || clips.Length == 0)
                return "<empty>";

            return string.Join(" -> ", clips.Select(FormatClip).ToArray());
        }

        private string FormatDebugState()
        {
            string savedController = _savedController != null ? _savedController.name : "<null>";
            string currentController = _animator != null && _animator.runtimeAnimatorController != null
                ? _animator.runtimeAnimatorController.name
                : "<null>";
            string activeClip = _currentStrongestIndex >= 0 && _currentStrongestIndex < _clips.Length && _clips[_currentStrongestIndex] != null
                ? _clips[_currentStrongestIndex].name
                : "<none>";

            return
                $"attackerPlayableActive={_active}, action='{_actionId ?? "<none>"}', " +
                $"animator='{(_animator != null ? _animator.name : "<null>")}', savedController='{savedController}', " +
                $"runtimeController='{currentController}', controllerNull={(_animator != null && _animator.runtimeAnimatorController == null)}, " +
                $"animEnabled={(_animator != null && _animator.enabled)}, graphValid={_graph.IsValid()}, " +
                $"graphPlaying={(_graph.IsValid() && _graph.IsPlaying())}, sequence={FormatSequence(_clips)}, " +
                $"activeClip='{activeClip}', activeLocal={_currentStrongestLocalTime:F3}, activeWeight={_currentStrongestWeight:F2}";
        }

        private static string FormatClip(AnimationClip clip)
        {
            return clip != null ? $"{clip.name}({clip.length:F2}s)" : "<null>";
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3}, {value.y:F3}, {value.z:F3})";
        }

        // #277: was hardcoded true — every finisher emitted 0.25s-interval multi-hundred-char sync
        // logs on every client in release. Anim category, per-tick tier.
        private static bool SyncDebugEnabled => BundyLogGate.Allows(BundyLogCategory.Anim, 2);

        /// <summary>#716 F3. Same as the victim controller: attacker Info lines are diagnostics and
        /// now sit under the DebugLogs master + Anim/verbosity gate.</summary>
        private static void LogInfo(string message)
        {
            if (!BundyLogGate.Allows(BundyLogCategory.Anim, 1))
                return;

            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogInfo("[Bundy] " + message);
        }

        private static void LogWarning(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogWarning("[Bundy] " + message);
        }

        private static Transform FindChildByLeafName(Transform parent, params string[] childNames)
        {
            if (parent == null || childNames == null)
                return null;

            foreach (string childName in childNames)
            {
                Transform match = FindChildByLeafName(parent, childName);
                if (match != null)
                    return match;
            }

            return null;
        }

        private static Transform FindChildByLeafName(Transform parent, string childName)
        {
            if (parent == null || string.IsNullOrEmpty(childName))
                return null;

            if (string.Equals(parent.name, childName, StringComparison.OrdinalIgnoreCase) ||
                parent.name.EndsWith(":" + childName, StringComparison.OrdinalIgnoreCase))
            {
                return parent;
            }

            for (int i = 0; i < parent.childCount; i++)
            {
                Transform child = parent.GetChild(i);
                Transform nested = FindChildByLeafName(child, childName);
                if (nested != null)
                    return nested;
            }

            return null;
        }
    }
}
