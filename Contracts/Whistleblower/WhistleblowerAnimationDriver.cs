using System;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

using Y4NGZCompany.Contracts._Shared;

namespace Y4NGZCompany.Contracts.Whistleblower
{
    internal enum WhistleblowerAnimationMode
    {
        Hiding,
        Cornered,
        Escape,
        LastStand,
        // #963 S14 U1: appended, never renumbered. Every client maps the replicated behaviour
        // state to one of these, so a renumber would repose the four states that already ship.
        Custody,
    }

    internal enum WhistleblowerAnimationAction
    {
        PunchLeft,
        PunchRight,
        Evade,
        HeadHit,
        BodyHit,
        RibHit,
        KnockdownFront,
        KnockdownBack,
        StepBack,
        Stumble,
        Death,
    }

    internal sealed class WhistleblowerAnimationDriver : MonoBehaviour
    {
        private const int LocomotionNervousIdle = 0;
        private const int LocomotionCombatIdle = 1;
        private const int LocomotionBreathingIdle = 2;
        private const int LocomotionWalk = 3;
        private const int LocomotionRun = 4;
        private const int LocomotionBoxForward = 5;
        private const int LocomotionBoxBackward = 6;
        private const int LocomotionBoxLeft = 7;
        private const int LocomotionBoxRight = 8;
        private const int LocomotionCount = 9;

        private const int ActionPunchLeft = 0;
        private const int ActionPunchRight = 1;
        private const int ActionEvade = 2;
        private const int ActionHeadHit = 3;
        private const int ActionBodyHit = 4;
        private const int ActionRibHit = 5;
        private const int ActionFallFront = 6;
        private const int ActionFallBack = 7;
        private const int ActionCrawl = 8;
        private const int ActionGetUpFront = 9;
        private const int ActionGetUpBack = 10;
        private const int ActionStepBack = 11;
        private const int ActionStumble = 12;
        private const int ActionDeath = 13;
        private const int ActionCount = 14;

        private const float ActionFadeIn = 0.15f;
        private const float ActionFadeOut = 0.25f;
        // #983: the evade and both get-ups end in a crouch. Cross-fading that into a
        // standing idle in 250 ms read as a half-kneel hitch on the way up, so the
        // crouch-exit actions get their own longer window through FadeOutFor.
        private const float CrouchExitFadeOut = 0.45f;
        private const float ActionCrossFade = 0.22f;

        // Locomotion blend/selection tuning.
        private const float LocomotionBlendRate = 6f;
        // Combat bursts are short, so the boxing slots have to reach full weight
        // inside the burst instead of spending most of it mid-blend.
        private const float BoxingBlendRate = 10f;
        private const float LocomotionMoveThreshold = 0.08f;
        private const float LocomotionMinDwell = 0.25f;
        private const float LocomotionIdleDemoteDelay = 0.15f;
        // A new dominant axis must beat the held one by this factor before the
        // boxing shuffle switches between the forward/back and left/right pairs.
        private const float BoxingAxisMargin = 1.25f;
        private const float BoxingSignFlipThreshold = 0.25f;

        // Authored ground speed of each moving clip when played at its entry in
        // LocomotionPlaybackRates, in metres per second. The active moving clip's
        // playable speed is scaled by actualSpeed / reference so the feet track the
        // NavMeshAgent instead of floating. Zero means "never stride-scaled".
        private const float WalkReferenceSpeed = 1.5f;
        private const float RunReferenceSpeed = 4.4f;
        private const float BoxingReferenceSpeed = 0.85f;
        private const float StrideScaleMin = 0.6f;
        private const float StrideScaleMax = 1.6f;
        private const float StrideScaleRate = 3f;
        // Transform scale of the prefab the reference speeds above were measured
        // against. Root motion is off, so visual foot travel scales with the model
        // transform while the NavMeshAgent's world speed does not: a variant at a
        // different scale needs its references rescaled to match.
        private const float StrideReferenceModelScale = 1.52f;

        // WhistleblowerEnemyAI scales its punch impact delay by this, so the punch
        // entries in ActionPlaybackRates must stay expressed through the const.
        internal const float PunchPlaybackRate = 1.25f;

        // #765: closing on a target in the cornered state is a walk, not a boxing
        // shuffle stride-scaled past its authored pace. Ground speed at or above this
        // (moving forward) selects the walk cycle.
        internal const float AdvanceLocomotionSpeed = 1.7f;
        // Round 2: the advance jogs now; above this the run cycle carries it, since the
        // walk would hit StrideScaleMax and skate.
        internal const float AdvanceRunSpeed = 3f;

        // #765: the sanitized clips lock root translation INTO the pose (see
        // docs/assets/11-whistleblower-animation-inventory-and-selection.md), so a one-shot such as the
        // evade or the fall carries the hips away from the root and the fade back to
        // locomotion snaps them home again. The driver measures that hip travel in the
        // root frame every frame, holds the hips at their pre-action offset, and hands
        // the travel to the owner as RootDisplacement so the enemy actually moves where
        // the clip says he moved. Once the action is over the hold eases off over this
        // window so the locomotion pose takes over without a hop. #983: 0.2 s was
        // shorter than the crouch-exit blend that produces the offset it has to unwind.
        private const float DisplacementReleaseDuration = 0.5f;

        // #983: the crawl clip loops, so clamping it to an arbitrary wall-clock figure cut
        // it mid-reach with one arm in the air. The chain plays whole cycles instead: one,
        // or as many as it takes to cover this, so a short clip still reads as a crawl.
        private const float CrawlMinDuration = 1.5f;

        // Playback multipliers applied to the clip playables. Every weight-timing
        // duration derived from a clip length must be divided by the matching rate,
        // otherwise the blend windows drift away from what the graph actually plays.
        private static readonly float[] LocomotionPlaybackRates =
        {
            1f,     // LocomotionNervousIdle
            1f,     // LocomotionCombatIdle
            1f,     // LocomotionBreathingIdle
            1f,     // LocomotionWalk
            1.15f,  // LocomotionRun
            1.45f,  // LocomotionBoxForward
            1.45f,  // LocomotionBoxBackward
            1.45f,  // LocomotionBoxLeft
            1.45f,  // LocomotionBoxRight
        };

        private static readonly float[] ActionPlaybackRates =
        {
            PunchPlaybackRate, // ActionPunchLeft
            PunchPlaybackRate, // ActionPunchRight
            // #983: movement is now locked for the whole evade (EvadeDuration), so the
            // rate is what decides how long discovery pins him. 1.2 meant 3.08 s of
            // standing still; 1.5 keeps the clip whole at 2.47 s.
            1.5f,              // ActionEvade
            1.15f,             // ActionHeadHit
            1.15f,             // ActionBodyHit
            1.15f,             // ActionRibHit
            1f,                // ActionFallFront
            1f,                // ActionFallBack
            // #983: one whole cycle of the ~3.97 s crawl at 1.35 is 2.94 s, close to the
            // 2.4 s the clamped cut used to run for, and a man scrambling back from the
            // boot that floored him is hurried.
            1.35f,             // ActionCrawl
            1.15f,             // ActionGetUpFront
            1.15f,             // ActionGetUpBack
            1.35f,             // ActionStepBack
            1.15f,             // ActionStumble
            1f,                // ActionDeath
        };

        private Animator _animator;
        private RuntimeAnimatorController _savedController;
        private PlayableGraph _graph;
        private AnimationMixerPlayable _rootMixer;
        private AnimationMixerPlayable _locomotionMixer;
        private AnimationMixerPlayable _actionMixer;
        private AnimationClipPlayable[] _actionPlayables = Array.Empty<AnimationClipPlayable>();
        private AnimationClipPlayable[] _locomotionPlayables = Array.Empty<AnimationClipPlayable>();
        private AnimationClip[] _actionClips = Array.Empty<AnimationClip>();
        private int _activeLocomotion = -1;
        private int _activeAction = -1;
        private int _previousAction = -1;
        private float _actionSwitchedAt;
        private int[] _actionSequence = Array.Empty<int>();
        private int _sequenceCursor;
        private float _actionStartedAt;
        private float _actionDuration;
        private float _lastLocomotionPhase;
        private float _movementSpeed;
        private float[] _locomotionWeights = Array.Empty<float>();
        private float _locomotionSelectedAt;
        private float _idleCandidateSince = -1f;
        private WhistleblowerAnimationMode _lastMode;
        private bool _hasMode;
        private int _strideLocomotion = -1;
        private float _strideScale = 1f;
        private float _strideModelScale = 1f;
        private bool _deathLocked;
        private bool _ready;

        private Transform _hips;
        private bool _displacementTracking;
        private bool _displacementPushing;
        private Vector3 _displacementBaseline;
        // #983: the hold offset inherited from the sequence steps that already finished.
        // The baseline re-bases per step so each clip measures its own travel, but the
        // root was already pushed by the earlier steps' travel, so the hold has to keep
        // carrying it - dropping it at a boundary would lurch him forward by the fall.
        private Vector3 _displacementCarry;
        private Vector3 _displacementApplied;
        private Vector3 _lastRawHipOffset;
        private float _displacementReleaseStartedAt = -1f;

        internal event Action<float> Footstep;

        /// <summary>World-space hip travel baked into the playing one-shot, reported per
        /// frame so the owner can move the root along with it. Only raised for actions
        /// that are meant to carry him (evade, stumble, hit staggers, fall, get up).</summary>
        internal event Action<Vector3> RootDisplacement;

        internal bool IsReady => _ready && _graph.IsValid();
        internal bool IsKnockdownPlaying =>
            IsReady
            && _actionSequence.Length > 0
            && !_deathLocked;
        internal bool IsCrawling => IsKnockdownPlaying && _activeAction == ActionCrawl;
        internal float StumbleDuration =>
            DurationFor(ContractAssetLoader.WhistleblowerStumbleClip, 0.8f) / ActionPlaybackRate(ActionStumble);
        /// <summary>#983: how long the evade one-shot actually plays. The owner locks
        /// movement for exactly this, so the combat ladder cannot drive the agent and the
        /// stride scaler underneath a crouched body.</summary>
        internal float EvadeDuration =>
            DurationFor(ContractAssetLoader.WhistleblowerEvadeClip, 1.6f) / ActionPlaybackRate(ActionEvade);

        internal void Configure(Animator animator)
        {
            if (animator != null)
                _animator = animator;

            EnsureGraph();
        }

        internal float PunchDuration(bool left)
        {
            return DurationFor(left ? ContractAssetLoader.WhistleblowerPunchLeftClip
                : ContractAssetLoader.WhistleblowerPunchRightClip, 1f) / PunchPlaybackRate;
        }

        internal float PunchContactDelay(bool left)
        {
            // #1023: measured maximum forward hand reach on the scavenger avatar,
            // at 55% of PunchLeft and 40% of PunchRight. Re-audit on clip replacement.
            float length = DurationFor(left ? ContractAssetLoader.WhistleblowerPunchLeftClip
                : ContractAssetLoader.WhistleblowerPunchRightClip, 1f);
            return WhistleblowerFugitivePolicy.PunchContactTime(left, length, PunchPlaybackRate);
        }

        internal void SetMovement(
            float normalizedSpeed,
            Vector3 worldVelocity,
            WhistleblowerAnimationMode mode,
            bool dead)
        {
            if (!EnsureGraph())
                return;

            if (dead && !_deathLocked)
                ApplyAction(WhistleblowerAnimationAction.Death);

            _movementSpeed = normalizedSpeed;
            float delta = Mathf.Max(Time.deltaTime, 0.001f);
            int locomotion = ChooseLocomotion(normalizedSpeed, worldVelocity, mode, dead);
            if (_activeLocomotion != locomotion)
            {
                // The stored phase belongs to the outgoing clip. Comparing it against
                // the incoming clip's phase can cross a footfall mark that neither
                // clip actually played, firing a footstep on the switch frame.
                _lastLocomotionPhase = -1f;
                _activeLocomotion = locomotion;
            }

            float blendRate = LocomotionBlendRateNow() * delta;
            float sum = 0f;
            for (int i = 0; i < LocomotionCount; i++)
            {
                float target = i == _activeLocomotion ? 1f : 0f;
                float weight = Mathf.MoveTowards(_locomotionMixer.GetInputWeight(i), target, blendRate);
                _locomotionWeights[i] = weight;
                sum += weight;
            }

            // AnimationMixerPlayable does not normalize its inputs. Retargeting a
            // blend mid-transition leaves the nine weights summing to less than one,
            // which bleeds the bind pose in and reads as a one-frame teleport.
            float scale = sum > 0.0001f && Mathf.Abs(sum - 1f) > 0.0001f ? 1f / sum : 1f;
            for (int i = 0; i < LocomotionCount; i++)
                _locomotionMixer.SetInputWeight(i, _locomotionWeights[i] * scale);

            UpdateStride(worldVelocity.magnitude, delta);
        }

        private float LocomotionBlendRateNow()
        {
            if (IsBoxingLocomotion(_activeLocomotion))
                return BoxingBlendRate;

            for (int i = LocomotionBoxForward; i <= LocomotionBoxRight; i++)
            {
                if (_locomotionMixer.GetInputWeight(i) > 0.001f)
                    return BoxingBlendRate;
            }

            return LocomotionBlendRate;
        }

        // Only the active moving clip is stride-scaled; the outgoing one returns to
        // its authored rate so a fading clip never drifts further out of phase.
        private void UpdateStride(float actualSpeed, float delta)
        {
            if (_strideLocomotion != _activeLocomotion)
            {
                RestoreLocomotionRate(_strideLocomotion);
                _strideLocomotion = _activeLocomotion;
                _strideScale = 1f;
                RestoreLocomotionRate(_activeLocomotion);
            }

            float reference = LocomotionReferenceSpeed(_activeLocomotion) * _strideModelScale;
            if (reference <= 0f
                || _activeLocomotion < 0
                || _activeLocomotion >= _locomotionPlayables.Length
                || !_locomotionPlayables[_activeLocomotion].IsValid())
                return;

            float target = Mathf.Clamp(actualSpeed / reference, StrideScaleMin, StrideScaleMax);
            _strideScale = Mathf.MoveTowards(_strideScale, target, StrideScaleRate * delta);
            _locomotionPlayables[_activeLocomotion].SetSpeed(
                LocomotionPlaybackRate(_activeLocomotion) * _strideScale);
        }

        private void RestoreLocomotionRate(int index)
        {
            if (index < 0 || index >= _locomotionPlayables.Length || !_locomotionPlayables[index].IsValid())
                return;

            _locomotionPlayables[index].SetSpeed(LocomotionPlaybackRate(index));
        }

        // A model authored at a different transform scale covers proportionally more
        // or less ground per cycle, so the measured references are rescaled to it
        // once per graph build. An unreadable or absurd scale falls back to the
        // baseline rather than driving the playback rate to an extreme.
        internal void RefreshStrideModelScale()
        {
            float scale = _animator != null ? Mathf.Abs(_animator.transform.lossyScale.y) : 0f;
            _strideModelScale = scale > 0.01f && scale < 100f
                ? scale / StrideReferenceModelScale
                : 1f;
        }

        private static float LocomotionReferenceSpeed(int locomotion)
        {
            switch (locomotion)
            {
                case LocomotionWalk:
                    return WalkReferenceSpeed;
                case LocomotionRun:
                    return RunReferenceSpeed;
                case LocomotionBoxForward:
                case LocomotionBoxBackward:
                case LocomotionBoxLeft:
                case LocomotionBoxRight:
                    return BoxingReferenceSpeed;
                default:
                    return 0f;
            }
        }

        private static bool IsBoxingLocomotion(int locomotion)
        {
            return locomotion >= LocomotionBoxForward && locomotion <= LocomotionBoxRight;
        }

        private static bool IsIdleLocomotion(int locomotion)
        {
            return locomotion >= LocomotionNervousIdle && locomotion <= LocomotionBreathingIdle;
        }

        internal void ApplyAction(WhistleblowerAnimationAction action)
        {
            if (!EnsureGraph())
                return;

            switch (action)
            {
                case WhistleblowerAnimationAction.PunchLeft:
                    PlaySingle(ActionPunchLeft);
                    break;
                case WhistleblowerAnimationAction.PunchRight:
                    PlaySingle(ActionPunchRight);
                    break;
                case WhistleblowerAnimationAction.Evade:
                    PlaySingle(ActionEvade);
                    break;
                case WhistleblowerAnimationAction.HeadHit:
                    PlaySingle(ActionHeadHit);
                    break;
                case WhistleblowerAnimationAction.BodyHit:
                    PlaySingle(ActionBodyHit);
                    break;
                case WhistleblowerAnimationAction.RibHit:
                    PlaySingle(ActionRibHit);
                    break;
                case WhistleblowerAnimationAction.KnockdownFront:
                    PlaySequence(ActionFallFront, ActionCrawl, ActionGetUpFront);
                    break;
                case WhistleblowerAnimationAction.KnockdownBack:
                    // The authored back fall/get-up poses do not join the mandatory crawl
                    // without a severe snap. Keep the hit-direction event distinct, but use
                    // the continuity-safe front chain for both directions.
                    PlaySequence(ActionFallFront, ActionCrawl, ActionGetUpFront);
                    break;
                case WhistleblowerAnimationAction.StepBack:
                    PlaySingle(ActionStepBack);
                    break;
                case WhistleblowerAnimationAction.Stumble:
                    PlaySingle(ActionStumble);
                    break;
                case WhistleblowerAnimationAction.Death:
                    PlayDeath();
                    break;
            }
        }

        internal static float KnockdownDuration(bool hitFromFront)
        {
            _ = hitFromFront;
            return FallDuration()
                + CrawlDuration()
                + GetUpDuration();
        }

        internal static float CrawlStartTime(bool hitFromFront)
        {
            _ = hitFromFront;
            return FallDuration();
        }

        internal static float CrawlEndTime(bool hitFromFront)
        {
            return CrawlStartTime(hitFromFront) + CrawlDuration();
        }

        // The server-side knockdown timeline is built from these three helpers and the
        // knockdown sequence plays the exact same values, so both sides stay in sync.
        private static float FallDuration()
        {
            return DurationFor(ContractAssetLoader.WhistleblowerFallingDownClip, 1.1f)
                / ActionPlaybackRate(ActionFallFront);
        }

        private static float GetUpDuration()
        {
            return DurationFor(ContractAssetLoader.WhistleblowerGetUpFrontClip, 1.2f)
                / ActionPlaybackRate(ActionGetUpFront);
        }

        // #983: whole cycles only. The clip loops, so the previous 2.4 s clamp ended it
        // wherever the cycle happened to be and the get-up cross-faded out of a
        // half-finished reach. KnockdownDuration/CrawlStartTime/CrawlEndTime all read this
        // helper, so the server's _knockdownEndsAt follows the client chain automatically.
        private static float CrawlDuration()
        {
            float cycle = DurationFor(ContractAssetLoader.WhistleblowerCrawlBackwardsClip, 1.4f)
                / ActionPlaybackRate(ActionCrawl);
            if (cycle <= 0.05f)
                return CrawlMinDuration;

            int cycles = Mathf.Max(1, Mathf.CeilToInt(CrawlMinDuration / cycle));
            return cycle * cycles;
        }

        // #983: the blend window back to locomotion is per action. Everything that ends on
        // its feet can leave in 0.25 s; a crouch has further to travel. UpdateActionWeights
        // and the displacement push-off both read this, so the window the pose blends over
        // is the same one the root hold stops pushing through.
        private static float FadeOutFor(int action)
        {
            switch (action)
            {
                case ActionEvade:
                case ActionGetUpFront:
                case ActionGetUpBack:
                    return CrouchExitFadeOut;
                default:
                    return ActionFadeOut;
            }
        }

        private static float ActionPlaybackRate(int index)
        {
            return index >= 0 && index < ActionPlaybackRates.Length
                ? ActionPlaybackRates[index]
                : 1f;
        }

        private static float LocomotionPlaybackRate(int index)
        {
            return index >= 0 && index < LocomotionPlaybackRates.Length
                ? LocomotionPlaybackRates[index]
                : 1f;
        }

        private void Awake()
        {
            ResolveAnimator();
            EnsureGraph();
        }

        private void OnEnable()
        {
            ResolveAnimator();
            EnsureGraph();
        }

        private void LateUpdate()
        {
            UpdateActionWeights();
            ApplyDisplacementCompensation();
            UpdateFootsteps();
        }

        private static bool ActionPushesRoot(int action)
        {
            switch (action)
            {
                case ActionEvade:
                case ActionHeadHit:
                case ActionBodyHit:
                case ActionRibHit:
                case ActionFallFront:
                case ActionFallBack:
                case ActionGetUpFront:
                case ActionGetUpBack:
                case ActionStumble:
                    return true;
                default:
                    // Punches stay planted, the step-back and crawl are already moved by
                    // the agent, and the death pose settles where he stood.
                    return false;
            }
        }

        // Round 2: measured in the frame the tracking started in, not the live root
        // rotation. FaceTarget spins the root during evades and staggers, and a pure
        // turn used to read as hip travel and shove him sideways.
        private Quaternion _displacementFrame = Quaternion.identity;

        private Vector3 MeasureHipOffset()
        {
            Quaternion frame = _displacementTracking ? _displacementFrame : transform.rotation;
            Vector3 offset = Quaternion.Inverse(frame) * (_hips.position - transform.position);
            offset.y = 0f;
            return offset;
        }

        private void BeginDisplacementTracking(int action)
        {
            if (_hips == null)
                return;

            if (!_displacementTracking)
            {
                // The animator has not evaluated this action yet, so the last raw
                // offset is the pre-action pose: that is the offset to hold.
                _displacementFrame = transform.rotation;
                _displacementBaseline = _lastRawHipOffset;
                _displacementCarry = Vector3.zero;
                _displacementApplied = Vector3.zero;
                _displacementTracking = true;
            }
            else
            {
                // A sequence step or an interrupting one-shot. #983: the baseline re-bases
                // to the pose the outgoing clip actually ended on, so the release at the end
                // of a ~7 s chain unwinds the get-up's own travel rather than seven seconds
                // of stale pre-fall offset, and the push measures increments from here
                // instead of re-applying travel the previous action already handed over.
                // What the earlier steps are still holding moves into the carry, which keeps
                // the compensation continuous across the boundary frame.
                _displacementCarry += _lastRawHipOffset - _displacementBaseline;
                _displacementBaseline = _lastRawHipOffset;
                _displacementApplied = Vector3.zero;
            }

            _displacementReleaseStartedAt = -1f;
            _displacementPushing = ActionPushesRoot(action);
        }

        private void EndDisplacementTracking()
        {
            _displacementTracking = false;
            _displacementPushing = false;
            _displacementReleaseStartedAt = -1f;
            _displacementApplied = Vector3.zero;
            _displacementCarry = Vector3.zero;
        }

        private void ApplyDisplacementCompensation()
        {
            if (!IsReady || _hips == null)
                return;

            Vector3 raw = MeasureHipOffset();
            _lastRawHipOffset = raw;
            if (!_displacementTracking)
                return;

            Vector3 delta = raw - _displacementBaseline;
            float hold = 1f;
            if (_activeAction < 0)
            {
                _displacementPushing = false;
                if (_displacementReleaseStartedAt < 0f)
                    _displacementReleaseStartedAt = Time.time;
                float t = (Time.time - _displacementReleaseStartedAt) / DisplacementReleaseDuration;
                if (t >= 1f)
                {
                    EndDisplacementTracking();
                    return;
                }

                hold = 1f - t;
            }
            else if (_displacementPushing && !_deathLocked)
            {
                // The fade-out blends the hips back toward the locomotion pose; pushing
                // through it would drag the root back with them.
                bool hasFollowingAction = _actionSequence.Length > 0
                    && _sequenceCursor + 1 < _actionSequence.Length;
                float remaining = _actionDuration - (Time.time - _actionStartedAt);
                if (!hasFollowingAction && remaining <= FadeOutFor(_activeAction))
                    _displacementPushing = false;
            }

            if (_displacementPushing)
            {
                if (_previousAction >= 0)
                {
                    // A cross-fade between two clips with different baked offsets (the
                    // crawl into the get-up, a hit landing mid-evade) is not travel: the
                    // hold keeps it invisible, and the push re-bases so it never becomes one.
                    _displacementApplied = delta;
                }
                else
                {
                    Vector3 step = delta - _displacementApplied;
                    _displacementApplied = delta;
                    if (step.sqrMagnitude > 1e-8f)
                        RootDisplacement?.Invoke(_displacementFrame * step);
                }
            }

            // The hold is the whole offset the chain has accumulated, not just this step's.
            Vector3 worldDelta = _displacementFrame * (delta + _displacementCarry);
            if (worldDelta.sqrMagnitude > 1e-10f)
                _hips.position -= worldDelta * hold;
        }

        private void UpdateFootsteps()
        {
            if (!IsReady || _deathLocked || Footstep == null)
                return;

            // The gates here are animation-local: no locomotion intensity, effectively no
            // movement, the action layer (fall, crawl, get-up, staggers) holding the root, or
            // the picked locomotion clip barely blended in. #984: they stay exactly as they
            // are, and WhistleblowerEnemyAI.OnDriverFootstep adds the one thing they cannot
            // see — the replicated knockdown state, which outlives the blend back down under
            // 0.5 while he is still on the floor.
            float intensity = FootstepIntensity(_activeLocomotion);
            if (intensity <= 0f
                || _movementSpeed < 0.06f
                || _rootMixer.GetInputWeight(1) > 0.5f
                || _locomotionMixer.GetInputWeight(_activeLocomotion) < 0.35f)
            {
                _lastLocomotionPhase = -1f;
                return;
            }

            AnimationClipPlayable playable = _locomotionPlayables[_activeLocomotion];
            AnimationClip clip = playable.IsValid() ? playable.GetAnimationClip() : null;
            if (clip == null || clip.length <= 0.05f)
                return;

            float phase = (float)(playable.GetTime() % clip.length) / clip.length;
            if (_lastLocomotionPhase >= 0f && PhaseCrossedFootfall(_lastLocomotionPhase, phase))
                Footstep(intensity);
            _lastLocomotionPhase = phase;
        }

        // Mixamo walk/run cycles land one foot near the cycle start and the other
        // near the midpoint.
        private static bool PhaseCrossedFootfall(float previous, float current)
        {
            return PhaseCrossed(previous, current, 0.04f) || PhaseCrossed(previous, current, 0.54f);
        }

        private static bool PhaseCrossed(float previous, float current, float mark)
        {
            if (previous <= current)
                return previous < mark && current >= mark;
            return previous < mark || current >= mark;
        }

        // #984: these three values are the anchors WhistleblowerEnemyAI.FootstepVolume maps
        // onto vanilla's 0.6 walk / 0.9 sprint pair (WalkFootstepIntensity /
        // RunFootstepIntensity there). Changing them changes footstep loudness too.
        private static float FootstepIntensity(int locomotion)
        {
            switch (locomotion)
            {
                case LocomotionRun:
                    return 1f;
                case LocomotionWalk:
                    return 0.62f;
                case LocomotionBoxForward:
                case LocomotionBoxBackward:
                case LocomotionBoxLeft:
                case LocomotionBoxRight:
                    return 0.45f;
                default:
                    return 0f;
            }
        }

        private void OnDisable()
        {
            DestroyGraph();
        }

        private void OnDestroy()
        {
            DestroyGraph();
        }

        private bool EnsureGraph()
        {
            if (IsReady)
                return true;

            ResolveAnimator();
            if (_animator == null || !ContractAssetLoader.WhistleblowerAnimationClipsReady)
                return false;
            if (!gameObject.scene.IsValid())
                return false;

            DestroyGraph();

            if (_savedController == null)
                _savedController = _animator.runtimeAnimatorController;
            _animator.runtimeAnimatorController = null;
            _animator.applyRootMotion = false;
            _animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
            _animator.enabled = true;

            _graph = PlayableGraph.Create("WhistleblowerAnimationDriver");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);

            _rootMixer = AnimationMixerPlayable.Create(_graph, 2);
            _locomotionMixer = AnimationMixerPlayable.Create(_graph, LocomotionCount);
            _actionMixer = AnimationMixerPlayable.Create(_graph, ActionCount);

            _locomotionWeights = new float[LocomotionCount];
            _locomotionPlayables = new AnimationClipPlayable[LocomotionCount];
            _locomotionPlayables[LocomotionNervousIdle] = ConnectClip(_locomotionMixer, LocomotionNervousIdle, ContractAssetLoader.WhistleblowerNervousIdleClip, loop: true);
            _locomotionPlayables[LocomotionCombatIdle] = ConnectClip(_locomotionMixer, LocomotionCombatIdle, ContractAssetLoader.WhistleblowerCombatIdleClip, loop: true);
            _locomotionPlayables[LocomotionBreathingIdle] = ConnectClip(_locomotionMixer, LocomotionBreathingIdle, ContractAssetLoader.WhistleblowerBreathingIdleClip, loop: true);
            _locomotionPlayables[LocomotionWalk] = ConnectClip(_locomotionMixer, LocomotionWalk, ContractAssetLoader.WhistleblowerWalkingClip, loop: true);
            _locomotionPlayables[LocomotionRun] = ConnectClip(_locomotionMixer, LocomotionRun, ContractAssetLoader.WhistleblowerRunningClip, loop: true);
            _locomotionPlayables[LocomotionBoxForward] = ConnectClip(_locomotionMixer, LocomotionBoxForward, ContractAssetLoader.WhistleblowerBoxingForwardClip, loop: true);
            _locomotionPlayables[LocomotionBoxBackward] = ConnectClip(_locomotionMixer, LocomotionBoxBackward, ContractAssetLoader.WhistleblowerBoxingBackwardClip, loop: true);
            _locomotionPlayables[LocomotionBoxLeft] = ConnectClip(_locomotionMixer, LocomotionBoxLeft, ContractAssetLoader.WhistleblowerBoxingLeftClip, loop: true);
            _locomotionPlayables[LocomotionBoxRight] = ConnectClip(_locomotionMixer, LocomotionBoxRight, ContractAssetLoader.WhistleblowerBoxingRightClip, loop: true);

            // Locomotion playables are never restarted, so their rate is applied once
            // here. Action playables get theirs on every StartAction instead.
            for (int i = 0; i < LocomotionCount; i++)
            {
                if (_locomotionPlayables[i].IsValid())
                    _locomotionPlayables[i].SetSpeed(LocomotionPlaybackRate(i));
            }

            _actionClips = new[]
            {
                ContractAssetLoader.WhistleblowerPunchLeftClip,
                ContractAssetLoader.WhistleblowerPunchRightClip,
                ContractAssetLoader.WhistleblowerEvadeClip,
                ContractAssetLoader.WhistleblowerHeadHitClip,
                ContractAssetLoader.WhistleblowerBodyHitClip,
                ContractAssetLoader.WhistleblowerRibHitClip,
                ContractAssetLoader.WhistleblowerFallingDownClip,
                ContractAssetLoader.WhistleblowerFallFlatClip,
                ContractAssetLoader.WhistleblowerCrawlBackwardsClip,
                ContractAssetLoader.WhistleblowerGetUpFrontClip,
                ContractAssetLoader.WhistleblowerGetUpBackClip,
                ContractAssetLoader.WhistleblowerStepBackClip,
                ContractAssetLoader.WhistleblowerStumbleClip,
                ContractAssetLoader.WhistleblowerDeathClip,
            };

            _actionPlayables = new AnimationClipPlayable[ActionCount];
            for (int i = 0; i < ActionCount; i++)
                _actionPlayables[i] = ConnectClip(_actionMixer, i, _actionClips[i], loop: i == ActionCrawl);

            _graph.Connect(_locomotionMixer, 0, _rootMixer, 0);
            _graph.Connect(_actionMixer, 0, _rootMixer, 1);
            _rootMixer.SetInputWeight(0, 1f);
            _rootMixer.SetInputWeight(1, 0f);
            _activeLocomotion = LocomotionNervousIdle;
            _locomotionSelectedAt = Time.time;
            _idleCandidateSince = -1f;
            _hasMode = false;
            _strideLocomotion = _activeLocomotion;
            _strideScale = 1f;
            RefreshStrideModelScale();
            for (int i = 0; i < LocomotionCount; i++)
                _locomotionMixer.SetInputWeight(i, i == _activeLocomotion ? 1f : 0f);
            ClearActionWeights();

            var output = AnimationPlayableOutput.Create(_graph, "WhistleblowerAnimationOutput", _animator);
            output.SetSourcePlayable(_rootMixer);
            _graph.Play();
            _ready = true;

            _hips = _animator.isHuman ? _animator.GetBoneTransform(HumanBodyBones.Hips) : null;
            EndDisplacementTracking();
            _lastRawHipOffset = _hips != null ? MeasureHipOffset() : Vector3.zero;
            RestoreDeathPose();
            return true;
        }

        // #983: DestroyGraph keeps the death lock, so a rebuilt graph must come back holding
        // the pose he died in instead of standing the corpse up in a breathing idle. The
        // death clip is parked on its last frame at full weight; nothing replays.
        private void RestoreDeathPose()
        {
            if (!_deathLocked
                || ActionDeath >= _actionPlayables.Length
                || !_actionPlayables[ActionDeath].IsValid())
                return;

            _previousAction = -1;
            _activeAction = ActionDeath;
            _actionSwitchedAt = Time.time;
            _actionStartedAt = Time.time;
            _actionDuration = 999f;

            AnimationClipPlayable playable = _actionPlayables[ActionDeath];
            playable.SetDone(false);
            playable.SetTime(Mathf.Max(0f, DurationFor(ActionClip(ActionDeath), 1f) - 0.02f));
            playable.SetSpeed(0f);
            playable.Play();
            UpdateActionWeights(force: true);
        }

        private AnimationClipPlayable ConnectClip(AnimationMixerPlayable mixer, int input, AnimationClip clip, bool loop)
        {
            var playable = AnimationClipPlayable.Create(_graph, clip);
            playable.SetApplyFootIK(false);
            playable.SetApplyPlayableIK(false);
            // A finite duration marks the playable done (and pauses it) after one pass,
            // freezing looping locomotion mid-cycle and making one-shots unplayable a
            // second time. Looping comes from the clip's import settings; one-shots are
            // faded out by weight, so the playables themselves must never expire.
            playable.SetDuration(double.MaxValue);
            playable.SetTime(0);
            playable.SetSpeed(1);
            _ = loop;

            _graph.Connect(playable, 0, mixer, input);
            mixer.SetInputWeight(input, 0f);
            return playable;
        }

        private int ChooseLocomotion(
            float normalizedSpeed,
            Vector3 worldVelocity,
            WhistleblowerAnimationMode mode,
            bool dead)
        {
            int desired = PickLocomotion(normalizedSpeed, worldVelocity, mode, dead);
            bool forced = dead || !_hasMode || mode != _lastMode || _activeLocomotion < 0;
            _lastMode = mode;
            _hasMode = true;

            if (forced)
            {
                _locomotionSelectedAt = Time.time;
                _idleCandidateSince = -1f;
                return desired;
            }

            if (desired == _activeLocomotion)
            {
                _idleCandidateSince = -1f;
                return desired;
            }

            // Dropping to an idle has to hold: a burst ending or a one-frame steering
            // stutter would otherwise flash idle in the middle of combat footwork.
            if (IsIdleLocomotion(desired) && !IsIdleLocomotion(_activeLocomotion))
            {
                if (_idleCandidateSince < 0f)
                    _idleCandidateSince = Time.time;
                if (Time.time - _idleCandidateSince < LocomotionIdleDemoteDelay)
                    return _activeLocomotion;
            }
            else
            {
                _idleCandidateSince = -1f;
            }

            if (Time.time - _locomotionSelectedAt < LocomotionMinDwell)
                return _activeLocomotion;

            _locomotionSelectedAt = Time.time;
            return desired;
        }

        private int PickLocomotion(
            float normalizedSpeed,
            Vector3 worldVelocity,
            WhistleblowerAnimationMode mode,
            bool dead)
        {
            if (dead)
                return LocomotionBreathingIdle;

            switch (mode)
            {
                case WhistleblowerAnimationMode.Hiding:
                    // #765: he roams before discovery, so hiding has a walk.
                    return normalizedSpeed > LocomotionMoveThreshold ? LocomotionWalk : LocomotionNervousIdle;
                case WhistleblowerAnimationMode.Custody:
                    // #963 S14 U1, decision 6: a man being marched out walks, and stands nervous
                    // when his escort stops. Same branch as hiding on purpose; a bound-hands clip
                    // is a later, purely additive change (one locomotion slot, one ConnectClip).
                    return normalizedSpeed > LocomotionMoveThreshold ? LocomotionWalk : LocomotionNervousIdle;
                case WhistleblowerAnimationMode.Escape:
                    return normalizedSpeed > LocomotionMoveThreshold ? LocomotionRun : LocomotionNervousIdle;
                case WhistleblowerAnimationMode.LastStand:
                    if (normalizedSpeed <= LocomotionMoveThreshold)
                        return LocomotionBreathingIdle;
                    // The final stand closes at 5 m/s; a capped walk cycle cannot
                    // cover that distance without skating.
                    return worldVelocity.magnitude >= AdvanceRunSpeed ? LocomotionRun : LocomotionWalk;
                case WhistleblowerAnimationMode.Cornered:
                    if (normalizedSpeed <= LocomotionMoveThreshold || worldVelocity.sqrMagnitude <= 0.01f)
                        // Existing nervous idle until a frightened guard is authored.
                        return LocomotionNervousIdle;

                    Vector3 local = transform.InverseTransformDirection(worldVelocity.normalized);
                    // #765: an advance on the target is a walk; the shuffle only covers
                    // the short in-range bursts it was measured for.
                    if (worldVelocity.magnitude >= AdvanceLocomotionSpeed && local.z > 0.5f)
                        return worldVelocity.magnitude >= AdvanceRunSpeed ? LocomotionRun : LocomotionWalk;
                    return PickBoxingLocomotion(local);
                default:
                    return LocomotionNervousIdle;
            }
        }

        private int PickBoxingLocomotion(Vector3 local)
        {
            float lateral = Mathf.Abs(local.x);
            float forward = Mathf.Abs(local.z);

            bool useLateral;
            if (_activeLocomotion == LocomotionBoxLeft || _activeLocomotion == LocomotionBoxRight)
                useLateral = forward <= lateral * BoxingAxisMargin;
            else if (_activeLocomotion == LocomotionBoxForward || _activeLocomotion == LocomotionBoxBackward)
                useLateral = lateral > forward * BoxingAxisMargin;
            else
                useLateral = lateral > forward;

            // Near the axis the sign is noise, so a shuffle already on this axis holds
            // its direction until the component is unambiguous.
            if (useLateral)
            {
                if ((_activeLocomotion == LocomotionBoxLeft || _activeLocomotion == LocomotionBoxRight)
                    && lateral < BoxingSignFlipThreshold)
                    return _activeLocomotion;
                return local.x < 0f ? LocomotionBoxLeft : LocomotionBoxRight;
            }

            if ((_activeLocomotion == LocomotionBoxForward || _activeLocomotion == LocomotionBoxBackward)
                && forward < BoxingSignFlipThreshold)
                return _activeLocomotion;
            return local.z < 0f ? LocomotionBoxBackward : LocomotionBoxForward;
        }

        private void PlaySingle(int action)
        {
            if (_deathLocked)
                return;
            if (IsKnockdownPlaying && action != ActionDeath)
                return;

            _actionSequence = Array.Empty<int>();
            _sequenceCursor = 0;
            StartAction(action, DurationFor(ActionClip(action), 0.7f) / ActionPlaybackRate(action), lockAction: false);
        }

        private void PlaySequence(params int[] actions)
        {
            if (_deathLocked || actions == null || actions.Length == 0)
                return;
            // Round 2: a second knockdown request while the chain plays restarted it from
            // the fall at full weight; the server never sends one, but nothing stopped it.
            if (IsKnockdownPlaying)
                return;

            _actionSequence = actions;
            _sequenceCursor = 0;
            StartSequenceAction();
        }

        private void StartSequenceAction()
        {
            if (_sequenceCursor < 0 || _sequenceCursor >= _actionSequence.Length)
            {
                _actionSequence = Array.Empty<int>();
                _sequenceCursor = 0;
                _activeAction = -1;
                return;
            }

            int action = _actionSequence[_sequenceCursor];
            float duration = action == ActionCrawl
                ? CrawlDuration()
                : DurationFor(ActionClip(action), 1f) / ActionPlaybackRate(action);
            StartAction(action, duration, lockAction: false);
        }

        private void PlayDeath()
        {
            // Dying mid-knockdown must not snap him upright into the standing death
            // clip: freeze the fall/crawl pose he is already in instead.
            bool downed = IsKnockdownPlaying
                && (_activeAction == ActionFallFront
                    || _activeAction == ActionFallBack
                    || _activeAction == ActionCrawl);
            _actionSequence = Array.Empty<int>();
            _sequenceCursor = 0;

            if (downed && _activeAction >= 0 && _activeAction < _actionPlayables.Length)
            {
                _deathLocked = true;
                _displacementPushing = false;
                AnimationClipPlayable playable = _actionPlayables[_activeAction];
                if (playable.IsValid())
                    playable.SetSpeed(0);
                UpdateActionWeights(force: true);
                return;
            }

            StartAction(ActionDeath, 999f, lockAction: true);
        }

        private void StartAction(int index, float duration, bool lockAction)
        {
            if (!EnsureGraph() || index < 0 || index >= _actionPlayables.Length)
                return;

            _previousAction = _activeAction;
            _actionSwitchedAt = Time.time;
            _activeAction = index;
            _deathLocked = lockAction;
            _actionStartedAt = Time.time;
            _actionDuration = Mathf.Max(0.2f, duration);
            BeginDisplacementTracking(index);

            AnimationClipPlayable playable = _actionPlayables[index];
            if (playable.IsValid())
            {
                playable.SetDone(false);
                playable.SetTime(0);
                // Re-setting the rate here also un-freezes a playable that PlayDeath
                // previously parked at speed 0.
                playable.SetSpeed(ActionPlaybackRate(index));
                playable.Play();
            }

            UpdateActionWeights(force: true);
        }

        private void UpdateActionWeights(bool force = false)
        {
            if (!IsReady)
                return;

            if (_activeAction < 0)
            {
                _rootMixer.SetInputWeight(0, 1f);
                _rootMixer.SetInputWeight(1, 0f);
                ClearActionWeights();
                return;
            }

            float elapsed = Mathf.Max(0f, Time.time - _actionStartedAt);
            if (!_deathLocked && !force && elapsed >= _actionDuration)
            {
                if (_actionSequence.Length > 0 && _sequenceCursor + 1 < _actionSequence.Length)
                {
                    _sequenceCursor++;
                    StartSequenceAction();
                    return;
                }

                _actionSequence = Array.Empty<int>();
                _sequenceCursor = 0;
                _activeAction = -1;
                UpdateActionWeights(force: true);
                return;
            }

            float weight;
            if (_deathLocked)
            {
                weight = 1f;
            }
            else
            {
                float fadeIn = Mathf.Clamp01(elapsed / ActionFadeIn);
                bool hasFollowingAction = _actionSequence.Length > 0
                    && _sequenceCursor + 1 < _actionSequence.Length;
                float fadeOut = hasFollowingAction
                    ? 1f
                    : Mathf.Clamp01((_actionDuration - elapsed) / FadeOutFor(_activeAction));
                weight = Mathf.Min(fadeIn, fadeOut);
            }

            _rootMixer.SetInputWeight(0, 1f - weight);
            _rootMixer.SetInputWeight(1, weight);

            // Cross-fade inside the action mixer so sequence steps (fall -> crawl ->
            // get up) and interrupting one-shots blend instead of snapping.
            float crossFade = _previousAction >= 0
                ? Mathf.Clamp01((Time.time - _actionSwitchedAt) / ActionCrossFade)
                : 1f;
            for (int i = 0; i < _actionPlayables.Length; i++)
            {
                float target = 0f;
                if (i == _activeAction)
                    target = crossFade;
                else if (i == _previousAction)
                    target = 1f - crossFade;
                _actionMixer.SetInputWeight(i, target);
            }
            if (crossFade >= 1f)
                _previousAction = -1;
        }

        private void ClearActionWeights()
        {
            if (!_actionMixer.IsValid())
                return;

            for (int i = 0; i < ActionCount; i++)
                _actionMixer.SetInputWeight(i, 0f);
        }

        private AnimationClip ActionClip(int index)
        {
            return index >= 0 && index < _actionClips.Length
                ? _actionClips[index]
                : null;
        }

        private static float DurationFor(AnimationClip clip, float fallback)
        {
            return clip != null && clip.length > 0.05f
                ? clip.length
                : fallback;
        }

        private void ResolveAnimator()
        {
            if (_animator == null)
                _animator = GetComponentInChildren<Animator>(true);
        }

        private void DestroyGraph()
        {
            if (_graph.IsValid())
                _graph.Destroy();

            _ready = false;
            _activeLocomotion = -1;
            _activeAction = -1;
            _previousAction = -1;
            _lastLocomotionPhase = -1f;
            _actionSequence = Array.Empty<int>();
            _sequenceCursor = 0;
            // #983: _deathLocked is deliberately kept. A graph rebuild after he died (a
            // disable/enable, a late Configure) used to clear the lock and replay the death
            // clip from the top, standing a corpse up to fall over again.
            _idleCandidateSince = -1f;
            _hasMode = false;
            _strideLocomotion = -1;
            _strideScale = 1f;
            _strideModelScale = 1f;
            _hips = null;
            EndDisplacementTracking();
            _lastRawHipOffset = Vector3.zero;
            _locomotionWeights = Array.Empty<float>();
            _actionPlayables = Array.Empty<AnimationClipPlayable>();
            _locomotionPlayables = Array.Empty<AnimationClipPlayable>();
            _actionClips = Array.Empty<AnimationClip>();

            if (_animator != null && _savedController != null && _animator.runtimeAnimatorController == null)
                _animator.runtimeAnimatorController = _savedController;
        }
    }
}
