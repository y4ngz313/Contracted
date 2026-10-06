using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;
using UnityEngine.Rendering;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Escalations.Bundy.AI;

namespace Y4NGZCompany.Escalations.Bundy
{
    internal enum BundyVictimClipId
    {
        PressUpThrow = 0,
        ChainsawNeckSlash = 100,
        ChainsawSawMiddle = 101,
        ChainsawStabLift = 102,
    }

    internal enum BundyVictimAnchorMode
    {
        HandContact = 0,
        SourcePelvis = 1,
        SourcePairRoot = 2,
    }

    internal sealed class BundyVictimPlayableController : MonoBehaviour
    {
        private enum ChainsawPresentationMode
        {
            ClonedPlayer = 0,
            AuthoredPlaceholder = 1,
            AuthoredWithIdentityOverlays = 2,
        }

        private const float CleanupGraceSeconds = 0.75f;
        private const float ProxyReleaseMaxDistance = 8f;
        private const float ProxyReleaseMinYOffset = -1.25f;
        private const float ProxyReleaseMaxYOffset = 4.5f;
        private const float SequenceTransitionStartNormalized = 0.95f;
        private const float SequenceTransitionDurationNormalized = 0.05f;
        private const float ChainsawAuthoredVictimYawOffsetDegrees = 180f;
        private const float ChainsawAuthoredVictimProxyScale = 1.75f;

        // #285: these are the LEGACY bundle-loader definitions. The three chainsaw finishers now ship
        // through the Interactions pack (y4ngz.bundy.victim_finishers), and the clips they name here
        // were intentionally dropped from bundy.bundle — so every one of them fails to resolve on a
        // normal session. The table is deliberately kept rather than deleted: #321 (C6) still consults
        // IsClipReady/IsClipSequenceReady as the per-variant fallback when an Interactions variant is
        // proven dead, and this controller is otherwise live (EndForBundy, the RootSync presentation
        // lift, the PressUpThrow standard-Bundy finisher). What is retired is the NOISE: a definition
        // whose clip is absent is a known, expected state and is reported as gated info, not a warning.
        private static readonly Dictionary<BundyVictimClipId, VictimClipDefinition> Definitions =
            new Dictionary<BundyVictimClipId, VictimClipDefinition>
            {
                {
                    BundyVictimClipId.PressUpThrow,
                    new VictimClipDefinition(
                        "megajam_Paired_PressUpThrow_Vic",
                        "megajam_Paired_PressUpThrow_Vic")
                },
                {
                    BundyVictimClipId.ChainsawNeckSlash,
                    new VictimClipDefinition(
                        "chainsaw_Paired_Chainsaw_NeckSlash_Vic",
                        "chainsaw_ChainsawVictimProxy")
                },
                {
                    BundyVictimClipId.ChainsawSawMiddle,
                    new VictimClipDefinition(
                        "chainsaw_Paired_Chainsaw_SawMiddle_Vic",
                        "chainsaw_ChainsawVictimProxy")
                },
                {
                    BundyVictimClipId.ChainsawStabLift,
                    new VictimClipDefinition(
                        "chainsaw_Paired_Chainsaw_StabLift_Vic",
                        "chainsaw_ChainsawVictimProxy")
                },
            };

        private static readonly Dictionary<BundyVictimClipId, AnimationClip> Clips =
            new Dictionary<BundyVictimClipId, AnimationClip>();
        private static readonly Dictionary<BundyVictimClipId, GameObject> ProxyPrefabs =
            new Dictionary<BundyVictimClipId, GameObject>();

        private static readonly BoneRetargetMap[] RealPlayerRetargetMap =
        {
            new BoneRetargetMap("pelvis", "spine", false),
            new BoneRetargetMap("spine_01", "spine.001", false),
            new BoneRetargetMap("spine_02", "spine.002", false),
            new BoneRetargetMap("spine_03", "spine.003", false),
            new BoneRetargetMap("neck_01", "spine.004", false),
            new BoneRetargetMap("head", "spine.004_end", false),

            new BoneRetargetMap("clavicle_l", "shoulder.L", false),
            new BoneRetargetMap("upperarm_l", "arm.L_upper", false),
            new BoneRetargetMap("lowerarm_l", "arm.L_lower", false),
            new BoneRetargetMap("hand_l", "hand.L", false),
            new BoneRetargetMap("clavicle_r", "shoulder.R", false),
            new BoneRetargetMap("upperarm_r", "arm.R_upper", false),
            new BoneRetargetMap("lowerarm_r", "arm.R_lower", false),
            new BoneRetargetMap("hand_r", "hand.R", false),

            new BoneRetargetMap("thigh_l", "thigh.L", false),
            new BoneRetargetMap("calf_l", "shin.L", false),
            new BoneRetargetMap("foot_l", "foot.L", false),
            new BoneRetargetMap("thigh_r", "thigh.R", false),
            new BoneRetargetMap("calf_r", "shin.R", false),
            new BoneRetargetMap("foot_r", "foot.R", false),
        };

        private static readonly BoneRetargetMap[] ChainsawRealPlayerRetargetMap =
        {
            new BoneRetargetMap("pelvis", "spine", true),
            new BoneRetargetMap("spine_01", "spine.001", false),
            new BoneRetargetMap("spine_02", "spine.002", false),
            new BoneRetargetMap("spine_03", "spine.003", false),
            new BoneRetargetMap("neck_01", "spine.004", false),
            new BoneRetargetMap("head", "spine.004_end", false),

            new BoneRetargetMap("clavicle_l", "shoulder.L", false),
            new BoneRetargetMap("upperarm_l", "arm.L_upper", false),
            new BoneRetargetMap("lowerarm_l", "arm.L_lower", false),
            new BoneRetargetMap("hand_l", "hand.L", false),
            new BoneRetargetMap("clavicle_r", "shoulder.R", false),
            new BoneRetargetMap("upperarm_r", "arm.R_upper", false),
            new BoneRetargetMap("lowerarm_r", "arm.R_lower", false),
            new BoneRetargetMap("hand_r", "hand.R", false),

            new BoneRetargetMap("thigh_l", "thigh.L", false),
            new BoneRetargetMap("calf_l", "shin.L", false),
            new BoneRetargetMap("foot_l", "foot.L", false),
            new BoneRetargetMap("thigh_r", "thigh.R", false),
            new BoneRetargetMap("calf_r", "shin.R", false),
            new BoneRetargetMap("foot_r", "foot.R", false),
        };

        private PlayerControllerB _player;
        private BundyAIBase _bundy;
        private BundyVictimClipId _clipId;
        private AnimationClip _clip;
        private BundyVictimClipId[] _clipSequence = Array.Empty<BundyVictimClipId>();
        private AnimationClip[] _clips = Array.Empty<AnimationClip>();
        private float[] _clipSequenceStartTimes = Array.Empty<float>();
        private float _clipSequenceLength;
        private int _currentClipSequenceIndex;
        private BundyVictimAnchorMode _anchorMode;
        private PlayableGraph _graph;
        private AnimationMixerPlayable _sequenceMixer;
        private AnimationClipPlayable[] _clipPlayables = Array.Empty<AnimationClipPlayable>();
        // #276: scratch weights buffer reused by every UpdateVictimSequencePlayback pass (it runs
        // twice per frame — LateUpdate + end-of-frame follow — and previously allocated a fresh
        // array each pass).
        private float[] _sequenceWeights = Array.Empty<float>();
        private bool _active;
        private readonly List<RendererState> _hiddenRendererStates = new List<RendererState>();
        private readonly List<BoneRetargetBinding> _boneRetargetBindings = new List<BoneRetargetBinding>();
        private readonly List<TransformState> _savedRetargetBoneStates = new List<TransformState>();
        private GameObject _victimPairRoot;
        private GameObject _victimVisualProxy;
        private GameObject _playerVisualProxy;
        private GameObject _playerIdentityOverlayRoot;
        private Animator _victimProxyAnimator;
        private Transform _proxyCameraTarget;
        private Transform _clonedPlayerTargetRoot;
        private string _clonedPlayerTargetRootReason;
        private bool _proxyHasVisibleRenderers;
        private bool _retargetRealPlayerBody;
        private bool _retargetVisiblePlayerProxy;
        private float _presentationLiftY;
        private Transform _realPlayerRetargetRootBone;
        private Transform _realPlayerRetargetAnchorBone;
        private Transform _sourceRetargetAnchorBone;
        private Transform _bundyLeftHand;
        private Transform _bundyRightHand;
        private Transform _bundyCarryReference;
        private Coroutine _endOfFrameFollowRoutine;
        // #716 G6: last frame the playable graph was actually evaluated (see UpdateVictimSequencePlayback).
        private int _lastSequenceEvaluationFrame = -1;
        private double _networkStartTime;
        private Vector3 _pairedOrigin;
        private Quaternion _pairedRotation;
        private float _cleanupAt;
        private bool _lockPlayerRootDuringPlayback;
        private Vector3 _startPosition;
        private Vector3 _startAnimatorPosition;
        private float _currentSequenceElapsed;
        private float _currentClipLocalTime;
        private float _currentClipWeight;
        private int _lastSyncDebugPhaseIndex = -1;
        private float _nextSyncDebugTickAt;
        private float _nextRetargetSyncDebugAt;
        private int _syncDebugTick;
        private bool _forceAuthoredChainsawProxyFallback;

        private bool _savedInSpecialInteractAnimation;
        private bool _savedSnapToServerPosition;
        private bool _savedDisableMoveInput;
        private bool _savedDisableLookInput;
        private bool _savedDisableSyncInAnimation;
        private bool _savedFreeRotationInInteractAnimation;
        private EnemyAI _savedInAnimationWithEnemy;
        private bool _savedAnimatorApplyRootMotion;
        private bool _savedAnimatorEnabled;

        internal static void RegisterClips(AssetBundle bundle)
        {
            Clips.Clear();
            ProxyPrefabs.Clear();

            if (bundle == null)
            {
                LogWarning("Victim playable registration skipped; bundle is null.");
                return;
            }

            AnimationClip[] bundleClips = bundle.LoadAllAssets<AnimationClip>();
            foreach (KeyValuePair<BundyVictimClipId, VictimClipDefinition> pair in Definitions)
            {
                AnimationClip clip = FindClip(bundleClips, pair.Value.ClipName);
                if (clip == null)
                {
                    // #285: expected on every clean session — the Interactions pack owns these clips
                    // now. Gated info so the legacy fallback state stays inspectable without the four
                    // stale warnings.
                    if (BundyLogGate.Allows(BundyLogCategory.Anim, 1))
                        LogInfo($"Legacy victim playable clip not in bundle (served by the Interactions pack): '{pair.Value.ClipName}'.");
                    continue;
                }

                Clips[pair.Key] = clip;

                GameObject proxyPrefab = FindPrefab(bundle, pair.Value.ProxyPrefabName);
                if (proxyPrefab == null)
                {
                    LogWarning(
                        $"Victim playable proxy prefab missing from bundle: '{pair.Value.ProxyPrefabName}'. " +
                        $"Clip '{pair.Value.ClipName}' will not be used.");
                    continue;
                }

                ProxyPrefabs[pair.Key] = proxyPrefab;
                LogInfo(
                    $"Registered victim playable clip '{clip.name}' ({clip.length:F2}s, humanMotion={clip.isHumanMotion}) " +
                    $"with proxy prefab '{proxyPrefab.name}'.");
            }
        }

        internal static bool IsClipReady(BundyVictimClipId clipId)
        {
            return Clips.TryGetValue(clipId, out AnimationClip clip) &&
                clip != null &&
                ProxyPrefabs.TryGetValue(clipId, out GameObject proxyPrefab) &&
                proxyPrefab != null;
        }

        internal static bool IsClipSequenceReady(params BundyVictimClipId[] clipIds)
        {
            if (clipIds == null || clipIds.Length == 0)
                return false;

            for (int i = 0; i < clipIds.Length; i++)
            {
                if (!IsClipReady(clipIds[i]))
                    return false;
            }

            return true;
        }

        internal static bool Begin(
            PlayerControllerB player,
            BundyAIBase bundy,
            BundyVictimClipId[] clipSequence,
            BundyVictimAnchorMode anchorMode,
            double networkStartTime,
            Vector3 pairedOrigin,
            Quaternion pairedRotation)
        {
            if (player == null || bundy == null || player.isPlayerDead)
                return false;

            if (clipSequence == null || clipSequence.Length == 0)
            {
                LogWarning("Victim playable begin skipped; empty clip sequence.");
                return false;
            }

            var resolvedClips = new AnimationClip[clipSequence.Length];
            for (int i = 0; i < clipSequence.Length; i++)
            {
                BundyVictimClipId clipId = clipSequence[i];
                if (!Clips.TryGetValue(clipId, out AnimationClip clip) || clip == null)
                {
                    LogWarning($"Victim playable begin skipped; clip '{clipId}' is not registered.");
                    return false;
                }

                if (!ProxyPrefabs.TryGetValue(clipId, out GameObject proxyPrefab) || proxyPrefab == null)
                {
                    LogWarning($"Victim playable begin skipped; proxy prefab '{clipId}' is not registered.");
                    return false;
                }

                resolvedClips[i] = clip;
            }

            var controller = player.GetComponent<BundyVictimPlayableController>();
            if (controller == null)
                controller = player.gameObject.AddComponent<BundyVictimPlayableController>();

            return controller.BeginInternal(
                player,
                bundy,
                clipSequence.ToArray(),
                resolvedClips,
                anchorMode,
                networkStartTime,
                pairedOrigin,
                pairedRotation);
        }

        internal static void End(PlayerControllerB player, BundyVictimClipId clipId, string reason)
        {
            var controller = player != null ? player.GetComponent<BundyVictimPlayableController>() : null;
            if (controller != null && controller._active && controller._clipId == clipId)
                controller.EndInternal(reason);
        }

        internal static bool IsActive(PlayerControllerB player, BundyVictimClipId clipId)
        {
            var controller = player != null ? player.GetComponent<BundyVictimPlayableController>() : null;
            return controller != null && controller._active && controller._clipId == clipId;
        }

        internal static void EndActive(PlayerControllerB player, string reason)
        {
            var controller = player != null ? player.GetComponent<BundyVictimPlayableController>() : null;
            if (controller != null && controller._active)
                controller.EndInternal(reason);
        }

        internal static void EndForBundy(BundyAIBase bundy, string reason)
        {
            PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
            if (players == null || bundy == null)
                return;

            foreach (PlayerControllerB player in players)
            {
                if (player == null)
                    continue;

                var controller = player.GetComponent<BundyVictimPlayableController>();
                if (controller != null && controller._active && controller._bundy == bundy)
                    controller.EndInternal(reason);
                else if (player.inAnimationWithEnemy == bundy)
                    ClearBundyLocks(player, bundy);
            }
        }

        internal static bool TryGetActiveSourceAnchor(
            PlayerControllerB player,
            BundyAIBase bundy,
            out Vector3 anchorPosition)
        {
            anchorPosition = Vector3.zero;

            var controller = player != null ? player.GetComponent<BundyVictimPlayableController>() : null;
            if (controller == null || !controller._active || controller._bundy != bundy)
                return false;

            controller.UpdateVictimSequencePlayback();
            controller.ApplyRealPlayerRetarget();
            return controller.TryGetCurrentSourceAnchor(out anchorPosition);
        }

        internal static bool TryGetActiveDebugState(
            PlayerControllerB player,
            BundyAIBase bundy,
            out Vector3 sourceAnchor,
            out Vector3 realPlayerAnchor,
            out Vector3 desiredAnchor,
            out string state)
        {
            sourceAnchor = Vector3.zero;
            realPlayerAnchor = Vector3.zero;
            desiredAnchor = Vector3.zero;
            state = "<inactive>";

            var controller = player != null ? player.GetComponent<BundyVictimPlayableController>() : null;
            if (controller == null || !controller._active || controller._bundy != bundy)
                return false;

            controller.UpdateVictimSequencePlayback();
            controller.ApplyRealPlayerRetarget();

            sourceAnchor = controller._sourceRetargetAnchorBone != null
                ? controller._sourceRetargetAnchorBone.position
                : (controller._victimProxyAnimator != null ? controller._victimProxyAnimator.transform.position : Vector3.zero);
            realPlayerAnchor = controller._realPlayerRetargetAnchorBone != null
                ? controller._realPlayerRetargetAnchorBone.position
                : (controller._player != null ? controller._player.transform.position : Vector3.zero);
            desiredAnchor = controller.ResolveDesiredRetargetAnchorPosition();
            state = controller.BuildSyncDebugState();
            return true;
        }

        private bool BeginInternal(
            PlayerControllerB player,
            BundyAIBase bundy,
            BundyVictimClipId[] clipSequence,
            AnimationClip[] clips,
            BundyVictimAnchorMode anchorMode,
            double networkStartTime,
            Vector3 pairedOrigin,
            Quaternion pairedRotation)
        {
            if (_active)
                EndInternal("replaced by new Bundy victim playable");

            if (player.playerBodyAnimator == null)
            {
                LogWarning($"Victim playable begin skipped; player '{player.playerUsername}' has no playerBodyAnimator.");
                return false;
            }

            _player = player;
            _bundy = bundy;
            _clipSequence = clipSequence ?? Array.Empty<BundyVictimClipId>();
            _clips = clips ?? Array.Empty<AnimationClip>();
            _clipId = _clipSequence.Length > 0 ? _clipSequence[0] : 0;
            _clip = _clips.Length > 0 ? _clips[0] : null;
            _anchorMode = anchorMode;
            BuildClipSequenceTiming();
            _networkStartTime = networkStartTime;
            // #716 G6: a new finisher must evaluate even if the previous one already stamped this
            // frame (an end and a begin can land in the same frame).
            _lastSequenceEvaluationFrame = -1;
            _pairedOrigin = pairedOrigin;
            _pairedRotation = FlattenRotation(pairedRotation);
            _cleanupAt = Time.time + Mathf.Max(_clipSequenceLength, 0.1f) + CleanupGraceSeconds;
            _lockPlayerRootDuringPlayback = player.playerBodyAnimator.transform != player.transform;
            _startPosition = pairedOrigin;
            _startAnimatorPosition = player.playerBodyAnimator.transform.position;
            _currentSequenceElapsed = 0f;
            _currentClipLocalTime = 0f;
            _currentClipWeight = 0f;
            _lastSyncDebugPhaseIndex = -1;
            _nextSyncDebugTickAt = 0f;
            _nextRetargetSyncDebugAt = 0f;
            _syncDebugTick = 0;
            _presentationLiftY = 0f;
            _forceAuthoredChainsawProxyFallback = false;

            SavePlayerState();
            PlaceAtPairedOrigin();
            RefreshPlayerLockState();

            LogInfo(
                $"Victim visual path selection for {PlayerLabel(player)}; " +
                $"clips={FormatClipSequence()}, anchorMode={_anchorMode}, " +
                $"chainsawMode={GetChainsawPresentationMode()}, " +
                $"preferChainsawRealPlayer={ShouldPreferChainsawRealPlayerVisuals()}, " +
                $"authoredProxyOnly={UsesAuthoredChainsawProxyOnly()}, " +
                $"chainsawBasisCorrection={UsesChainsawSourceRigBasisCorrection()}.");

            if (!CreateVictimVisualProxy())
            {
                RestorePlayerState();
                return false;
            }

            if (ShouldUseChainsawIdentityOverlays())
                ApplyChainsawIdentityOverlays();

            if (!_proxyHasVisibleRenderers && ShouldUseVisiblePlayerProxy())
                TryCreatePlayerVisualProxyFromClonedRig();

            if (!_proxyHasVisibleRenderers && ShouldUseRealPlayerRetarget())
                TryConfigureRealPlayerRetarget();

            if (!_proxyHasVisibleRenderers && !_retargetRealPlayerBody && ShouldPreferChainsawRealPlayerVisuals())
                TryActivateAuthoredChainsawProxyFallback("real-player visual proxy unavailable");

            if (!_proxyHasVisibleRenderers && UsesAuthoredChainsawProxyOnly())
            {
                LogWarning(
                    $"Chainsaw source-proxy-only finisher has no visible authored proxy for {PlayerLabel(_player)}; " +
                    "skipping cloned-player/real-player retarget fallback so the test does not mutate the player rig.");
            }

            if (_retargetRealPlayerBody && _player.playerBodyAnimator != null)
                _player.playerBodyAnimator.enabled = false;

            if (_proxyHasVisibleRenderers)
                HideRealPlayerVisuals();
            SyncSpecialAnimationFlag(true);
            BeginLocalThirdPersonCamera();

            try
            {
                CreateVictimSequenceGraph();
                UpdateVictimSequencePlayback();
                ApplyRealPlayerRetarget();
                LogVictimBranchSelection();

                _active = true;
                _endOfFrameFollowRoutine = StartCoroutine(EndOfFrameFollowLoop());
                string startMessage =
                    $"Started victim proxy playable '{FormatClipSequence()}' for {PlayerLabel(player)}; " +
                    $"origin={FormatVector(_pairedOrigin)}, yaw={_pairedRotation.eulerAngles.y:F1}, " +
                    $"victimYaw={ResolveVictimPlacementRotation().eulerAngles.y:F1}, " +
                    $"start={networkStartTime:F3}, proxyRootMotion={_victimProxyAnimator.applyRootMotion}, " +
                    $"anchorMode={_anchorMode}, " +
                    $"length={_clipSequenceLength:F2}, lockPlayerRoot={_lockPlayerRootDuringPlayback}, " +
                    $"sourceProxyOnly={UsesAuthoredChainsawProxyOnly()}.";
                // #277: routine success (the warning-level branch fired on every chainsaw
                // finisher), downgraded to gated info for both branches.
                if (BundyLogGate.Allows(BundyLogCategory.Anim, 1))
                    LogInfo(startMessage);
                return true;
            }
            catch (Exception e)
            {
                LogWarning($"Victim playable begin failed for {PlayerLabel(player)}: {e.GetType().Name}: {e.Message}");
                EndInternal("begin failed");
                return false;
            }
        }

        private void LateUpdate()
        {
            if (!_active)
                return;

            if (_player == null || _bundy == null || _bundy.isEnemyDead)
            {
                EndInternal("actor invalid");
                return;
            }

            if (_player.isPlayerDead)
            {
                if (IsChainsawVictimClip(_clipId) && ShouldEndChainsawProxyOnDeath())
                {
                    EndInternal("chainsaw victim died");
                    return;
                }

                if (!_proxyHasVisibleRenderers || Time.time >= _cleanupAt)
                {
                    EndInternal("actor invalid");
                    return;
                }

                UpdateVictimSequencePlayback();
                UpdateLocalThirdPersonCamera();
                return;
            }

            // A disconnected owner drops isPlayerControlled without ever setting isPlayerDead (the
            // kill RPC is lost with the owner), so without this check the pawn stays locked and
            // hidden until _cleanupAt. Checked after the dead branch: dead players also clear
            // isPlayerControlled, and the death-proxy display above must keep running.
            if (!_player.isPlayerControlled)
            {
                EndInternal("victim no longer player-controlled");
                return;
            }

            if (Time.time >= _cleanupAt)
            {
                EndInternal("clip timeout");
                return;
            }

            UpdateVictimSequencePlayback();

            RefreshPlayerLockState();
            ApplyRealPlayerRetarget();
            UpdateLocalThirdPersonCamera();
        }

        private void OnDestroy()
        {
            if (_active)
                EndInternal("controller destroyed");
        }

        private void SavePlayerState()
        {
            _savedInSpecialInteractAnimation = _player.inSpecialInteractAnimation;
            _savedSnapToServerPosition = _player.snapToServerPosition;
            _savedDisableMoveInput = _player.disableMoveInput;
            _savedDisableLookInput = _player.disableLookInput;
            _savedDisableSyncInAnimation = _player.disableSyncInAnimation;
            _savedFreeRotationInInteractAnimation = _player.freeRotationInInteractAnimation;
            _savedInAnimationWithEnemy = _player.inAnimationWithEnemy;
            _savedAnimatorApplyRootMotion = _player.playerBodyAnimator != null && _player.playerBodyAnimator.applyRootMotion;
            _savedAnimatorEnabled = _player.playerBodyAnimator == null || _player.playerBodyAnimator.enabled;

            if (_player.playerBodyAnimator != null)
            {
                _player.playerBodyAnimator.enabled = true;
            }
        }

        private void PlaceAtPairedOrigin()
        {
            if (_player == null)
                return;

            _player.transform.SetPositionAndRotation(_pairedOrigin, ResolveVictimPlacementRotation());
            _player.serverPlayerPosition = _player.transform.localPosition;
            _player.ResetFallGravity();
        }

        private void RefreshPlayerLockState()
        {
            if (_player == null)
                return;

            _player.inSpecialInteractAnimation = true;
            _player.snapToServerPosition = true;
            _player.disableMoveInput = true;
            _player.disableLookInput = false;
            _player.disableSyncInAnimation = true;
            _player.freeRotationInInteractAnimation = true;
            _player.inAnimationWithEnemy = _bundy;

            if (_lockPlayerRootDuringPlayback)
                _player.transform.SetPositionAndRotation(_pairedOrigin, ResolveVictimPlacementRotation());

            _player.serverPlayerPosition = _player.transform.localPosition;
            _player.ResetFallGravity();
        }

        private void EndInternal(string reason)
        {
            if (!_active && !_graph.IsValid() && _victimVisualProxy == null && _hiddenRendererStates.Count == 0)
                return;

            StopEndOfFrameFollow();
            AnimationClip endedClip = _clip;
            PlayerControllerB endedPlayer = _player;
            TryPlacePlayerAtProxyRelease(reason);
            DestroyGraph();
            RestorePlayerState();

            _active = false;
            _bundy = null;
            _clip = null;
            _clipSequence = Array.Empty<BundyVictimClipId>();
            _clips = Array.Empty<AnimationClip>();
            _clipSequenceStartTimes = Array.Empty<float>();
            _clipSequenceLength = 0f;
            _currentClipSequenceIndex = 0;
            _lastSequenceEvaluationFrame = -1;
            _anchorMode = BundyVictimAnchorMode.HandContact;
            _player = null;
            _victimPairRoot = null;
            _playerVisualProxy = null;
            _playerIdentityOverlayRoot = null;
            _victimProxyAnimator = null;
            _proxyCameraTarget = null;
            _clonedPlayerTargetRoot = null;
            _clonedPlayerTargetRootReason = null;
            _proxyHasVisibleRenderers = false;
            _retargetRealPlayerBody = false;
            _retargetVisiblePlayerProxy = false;
            _presentationLiftY = 0f;
            _realPlayerRetargetRootBone = null;
            _realPlayerRetargetAnchorBone = null;
            _sourceRetargetAnchorBone = null;
            _bundyLeftHand = null;
            _bundyRightHand = null;
            _bundyCarryReference = null;
            _lockPlayerRootDuringPlayback = false;
            _currentSequenceElapsed = 0f;
            _currentClipLocalTime = 0f;
            _currentClipWeight = 0f;
            _lastSyncDebugPhaseIndex = -1;
            _nextSyncDebugTickAt = 0f;
            _nextRetargetSyncDebugAt = 0f;
            _syncDebugTick = 0;
            _forceAuthoredChainsawProxyFallback = false;

            string rootDelta = endedPlayer != null ? FormatVector(endedPlayer.transform.position - _startPosition) : "<unknown>";
            string bodyDelta = endedPlayer != null && endedPlayer.playerBodyAnimator != null
                ? FormatVector(endedPlayer.playerBodyAnimator.transform.position - _startAnimatorPosition)
                : "<unknown>";
            LogInfo(
                $"Ended victim playable '{(endedClip != null ? endedClip.name : _clipId.ToString())}' " +
                $"for {PlayerLabel(endedPlayer)}; reason={reason}, rootDelta={rootDelta}, bodyDelta={bodyDelta}.");
        }

        private void DestroyGraph()
        {
            if (_graph.IsValid())
                _graph.Destroy();

            _sequenceMixer = default;
            _clipPlayables = Array.Empty<AnimationClipPlayable>();
            _sequenceWeights = Array.Empty<float>();
        }

        private void BuildClipSequenceTiming()
        {
            _clipSequenceStartTimes = new float[_clips.Length];
            _clipSequenceLength = 0f;
            _currentClipSequenceIndex = 0;

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

                AnimationClip sequenceClip = _clips[i];
                float sequenceLength = sequenceClip != null ? Mathf.Max(0.01f, sequenceClip.length) : 0.01f;
                _clipSequenceLength = _clipSequenceStartTimes[i] + sequenceLength;
            }
        }

        private void CreateVictimSequenceGraph()
        {
            if (_victimProxyAnimator == null || _clips == null || _clips.Length == 0)
                throw new InvalidOperationException("Victim sequence playable graph requires an animator and at least one clip.");

            _graph = PlayableGraph.Create($"BundyVictim_{_clipId}_{_player.playerClientId}");
            _graph.SetTimeUpdateMode(DirectorUpdateMode.GameTime);
            _sequenceMixer = AnimationMixerPlayable.Create(_graph, _clips.Length);
            _clipPlayables = new AnimationClipPlayable[_clips.Length];

            for (int i = 0; i < _clips.Length; i++)
            {
                AnimationClip sequenceClip = _clips[i];
                if (sequenceClip == null)
                    throw new InvalidOperationException($"Victim sequence clip {i} is null.");

                AnimationClipPlayable playable = AnimationClipPlayable.Create(_graph, sequenceClip);
                playable.SetApplyFootIK(false);
                playable.SetApplyPlayableIK(false);
                playable.SetDuration(sequenceClip.length);
                playable.SetTime(0d);
                _clipPlayables[i] = playable;
                _graph.Connect(playable, 0, _sequenceMixer, i);
                _sequenceMixer.SetInputWeight(i, i == 0 ? 1f : 0f);
            }

            AnimationPlayableOutput output = AnimationPlayableOutput.Create(
                _graph,
                "BundyVictimOutput",
                _victimProxyAnimator);
            output.SetSourcePlayable(_sequenceMixer);
            _graph.Play();
        }

        /// <summary>
        /// #716 G6 frame latch. The pose only changes once per frame, but this was reached two to
        /// four times a frame on the legacy path: LateUpdate, the end-of-frame follow loop, and the
        /// external anchor/bounds pulls (<see cref="TryGetActiveSourceAnchor"/> and friends) each
        /// forced a graph evaluation. The end-of-frame pass is the authoritative one - it runs
        /// after Unity's own animation update - so it passes <paramref name="force"/> and every
        /// other caller reads the pose that pass already produced.
        /// </summary>
        private void UpdateVictimSequencePlayback(bool force = false)
        {
            if (!_graph.IsValid() || !_sequenceMixer.IsValid() || _clipPlayables == null || _clipPlayables.Length == 0)
                return;

            if (!force &&
                (_lastSequenceEvaluationFrame == Time.frameCount || _endOfFrameFollowRoutine != null))
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
                AnimationClip sequenceClip = i < _clips.Length ? _clips[i] : null;
                if (sequenceClip == null || !_clipPlayables[i].IsValid())
                    continue;

                float clipStart = _clipSequenceStartTimes != null && i < _clipSequenceStartTimes.Length
                    ? _clipSequenceStartTimes[i]
                    : 0f;
                float clipLength = Mathf.Max(0.01f, sequenceClip.length);
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
            PinVictimProxyHorizontalRootIfNeeded();
            _currentClipSequenceIndex = Mathf.Clamp(strongestIndex, 0, _clipPlayables.Length - 1);
            _currentSequenceElapsed = elapsed;
            _currentClipLocalTime = GetClipLocalTime(_currentClipSequenceIndex, elapsed);
            _currentClipWeight = weights[_currentClipSequenceIndex] / totalWeight;
            LogSyncDebug();
        }

        private float GetSequenceTransitionDuration(int sourceClipIndex)
        {
            if (_clips == null || sourceClipIndex < 0 || sourceClipIndex >= _clips.Length)
                return 0f;

            AnimationClip sourceClip = _clips[sourceClipIndex];
            return sourceClip != null
                ? Mathf.Max(1f / 60f, sourceClip.length * SequenceTransitionDurationNormalized)
                : 0f;
        }

        private string FormatClipSequence()
        {
            if (_clips == null || _clips.Length == 0)
                return "<empty>";

            return string.Join(" -> ", _clips.Select(sequenceClip => sequenceClip != null ? sequenceClip.name : "<null>").ToArray());
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

        private void LogSyncDebug()
        {
            if (!SyncDebugEnabled || _clips == null || _clips.Length == 0)
                return;

            bool phaseChanged = _currentClipSequenceIndex != _lastSyncDebugPhaseIndex;
            if (!phaseChanged && Time.time < _nextSyncDebugTickAt)
                return;

            _nextSyncDebugTickAt = Time.time + 0.25f;
            string phase = phaseChanged ? "phase" : "tick";
            _lastSyncDebugPhaseIndex = _currentClipSequenceIndex;
            _syncDebugTick++;

            AnimationClip activeClip = _currentClipSequenceIndex >= 0 && _currentClipSequenceIndex < _clips.Length
                ? _clips[_currentClipSequenceIndex]
                : null;
            float activeClipLength = activeClip != null ? Mathf.Max(0.01f, activeClip.length) : 0.01f;

            LogInfo(
                $"BundySyncDebug victim {phase}: tick={_syncDebugTick}, frame={Time.frameCount}, " +
                $"player={PlayerLabel(_player)}, anchorMode={_anchorMode}, retargetRealPlayer={_retargetRealPlayerBody}, " +
                $"visibleProxyRetarget={_retargetVisiblePlayerProxy}, " +
                $"elapsed={_currentSequenceElapsed:F3}/{_clipSequenceLength:F3}, " +
                $"clipIndex={_currentClipSequenceIndex}, clip='{(activeClip != null ? activeClip.name : "<none>")}', " +
                $"clipLocal={_currentClipLocalTime:F3}/{activeClipLength:F3}, weight={_currentClipWeight:F2}, " +
                $"sourceAnchor={FormatVector(_sourceRetargetAnchorBone != null ? _sourceRetargetAnchorBone.position : Vector3.zero)}, " +
                $"playerAnchor={FormatVector(_realPlayerRetargetAnchorBone != null ? _realPlayerRetargetAnchorBone.position : Vector3.zero)}, " +
                $"desiredAnchor={FormatVector(ResolveDesiredRetargetAnchorPosition())}, " +
                $"playerRoot={FormatVector(_player != null ? _player.transform.position : Vector3.zero)}, " +
                $"retargetRoot={FormatVector(_realPlayerRetargetRootBone != null ? _realPlayerRetargetRootBone.position : Vector3.zero)}, " +
                $"proxyRoot={FormatVector(_victimProxyAnimator != null ? _victimProxyAnimator.transform.position : Vector3.zero)}.");
        }

        private string BuildSyncDebugState()
        {
            AnimationClip activeClip = _currentClipSequenceIndex >= 0 && _clips != null && _currentClipSequenceIndex < _clips.Length
                ? _clips[_currentClipSequenceIndex]
                : null;
            return
                $"victimClipIndex={_currentClipSequenceIndex}, victimClip='{(activeClip != null ? activeClip.name : "<none>")}', " +
                $"victimElapsed={_currentSequenceElapsed:F3}/{_clipSequenceLength:F3}, " +
                $"victimLocal={_currentClipLocalTime:F3}, victimWeight={_currentClipWeight:F2}, " +
                $"anchorMode={_anchorMode}, retarget={_retargetRealPlayerBody}, visibleProxyRetarget={_retargetVisiblePlayerProxy}, " +
                $"bindings={_boneRetargetBindings.Count}/{RealPlayerRetargetMap.Length}, " +
                $"sourceAnchor={FormatVector(_sourceRetargetAnchorBone != null ? _sourceRetargetAnchorBone.position : Vector3.zero)}, " +
                $"playerAnchor={FormatVector(_realPlayerRetargetAnchorBone != null ? _realPlayerRetargetAnchorBone.position : Vector3.zero)}, " +
                $"desiredAnchor={FormatVector(ResolveDesiredRetargetAnchorPosition())}";
        }

        private IEnumerator EndOfFrameFollowLoop()
        {
            var wait = new WaitForEndOfFrame();
            while (_active)
            {
                yield return wait;
                if (!_active)
                    yield break;

                // #716 G6: the authoritative pass of the frame - it runs after Unity's animation
                // update, so it re-evaluates even when LateUpdate already did.
                UpdateVictimSequencePlayback(force: true);

                ApplyRealPlayerRetarget();
                UpdateLocalThirdPersonCamera();
            }
        }

        private void StopEndOfFrameFollow()
        {
            if (_endOfFrameFollowRoutine == null)
                return;

            StopCoroutine(_endOfFrameFollowRoutine);
            _endOfFrameFollowRoutine = null;
        }

        private void RestorePlayerState()
        {
            if (_player == null)
                return;

            RestoreLocalThirdPersonCamera();
            SyncSpecialAnimationFlag(false);
            RestoreRealPlayerRetarget();
            DestroyVictimVisualProxy();
            RestoreRealPlayerVisuals();
            ClearBundyLocks(_player, _bundy);

            _player.freeRotationInInteractAnimation = _savedFreeRotationInInteractAnimation;
            _player.inSpecialInteractAnimation = _savedInSpecialInteractAnimation;
            _player.snapToServerPosition = _savedSnapToServerPosition;
            _player.disableMoveInput = _savedDisableMoveInput;
            _player.disableLookInput = _savedDisableLookInput;
            _player.disableSyncInAnimation = _savedDisableSyncInAnimation;

            if (_savedInAnimationWithEnemy != _bundy)
                _player.inAnimationWithEnemy = _savedInAnimationWithEnemy;
            else if (_player.inAnimationWithEnemy == _bundy)
                _player.inAnimationWithEnemy = null;

            if (_player.playerBodyAnimator != null)
            {
                _player.playerBodyAnimator.applyRootMotion = _savedAnimatorApplyRootMotion;
                _player.playerBodyAnimator.enabled = _savedAnimatorEnabled;
                _player.playerBodyAnimator.Update(0f);
            }

            _player.serverPlayerPosition = _player.transform.localPosition;
            _player.ResetFallGravity();
        }

        private bool CreateVictimVisualProxy()
        {
            if (_player == null)
                return false;

            DestroyVictimVisualProxy();

            if (!ProxyPrefabs.TryGetValue(_clipId, out GameObject source) || source == null)
            {
                LogWarning($"Victim proxy creation failed for {PlayerLabel(_player)}; no authored proxy prefab registered for {_clipId}.");
                return false;
            }

            try
            {
                _victimPairRoot = new GameObject($"BundyVictimPairRoot_{_clipId}_{_player.actualClientId}");
                PlaceVictimPairRoot();

                _victimVisualProxy = Instantiate(source, _victimPairRoot.transform, worldPositionStays: false);
                _victimVisualProxy.name = $"BundyVictimProxy_{_clipId}_{_player.actualClientId}";
                _victimVisualProxy.transform.localPosition = Vector3.zero;
                _victimVisualProxy.transform.localRotation = Quaternion.identity;
                _victimVisualProxy.transform.localScale = Vector3.one;

                StripProxyRuntimeComponents(_victimVisualProxy);
                _victimProxyAnimator = _victimVisualProxy.GetComponent<Animator>() ??
                    _victimVisualProxy.GetComponentInChildren<Animator>(true);
                if (_victimProxyAnimator == null)
                {
                    LogWarning($"Victim proxy creation failed for {PlayerLabel(_player)}; cloned visual has no Animator.");
                    DestroyVictimVisualProxy();
                    return false;
                }

                _victimProxyAnimator.enabled = true;
                _victimProxyAnimator.applyRootMotion = true;
                _victimProxyAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                _victimProxyAnimator.updateMode = AnimatorUpdateMode.Normal;
                PlaceProxyAnimatorAtPairedOrigin();

                Renderer[] proxyRenderers = _victimVisualProxy.GetComponentsInChildren<Renderer>(true);
                SkinnedMeshRenderer[] proxySkinnedRenderers = _victimVisualProxy.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                bool showAuthoredProxyRenderers = ShouldShowAuthoredProxyRenderers();
                _proxyHasVisibleRenderers = showAuthoredProxyRenderers && proxySkinnedRenderers.Any(renderer =>
                    renderer != null && renderer.sharedMesh != null && renderer.bones != null && renderer.bones.Length > 0);
                foreach (Renderer renderer in proxyRenderers)
                {
                    if (renderer == null)
                        continue;

                    bool isVisibleBody = renderer is SkinnedMeshRenderer skinnedRenderer &&
                        skinnedRenderer.sharedMesh != null &&
                        skinnedRenderer.bones != null &&
                        skinnedRenderer.bones.Length > 0;
                    bool enabled = showAuthoredProxyRenderers && isVisibleBody;
                    renderer.enabled = enabled;
                    renderer.shadowCastingMode = enabled ? ShadowCastingMode.On : ShadowCastingMode.Off;
                }

                _proxyCameraTarget = _proxyHasVisibleRenderers ? ResolveProxyCameraTarget() : ResolveRealPlayerCameraTarget();
                if (UsesAuthoredChainsawProxyOnly() && _proxyHasVisibleRenderers)
                    _sourceRetargetAnchorBone = _proxyCameraTarget;
                // #716 F4/F5: the authored-proxy branch is the routine path whenever the
                // Interactions presentation is not in use, so it warned on every finisher; and the
                // message (three hierarchy-path walks) was built even when nothing would print.
                // Both branches are now gated Info, matching the Begin receipt above.
                if (!BundyLogGate.Allows(BundyLogCategory.Anim, 1))
                    return true;

                string proxyMessage =
                    $"Created victim visual proxy for {PlayerLabel(_player)}; " +
                    $"source='{GetHierarchyPath(source.transform)}', proxy='{_victimVisualProxy.name}', " +
                    $"animator='{GetHierarchyPath(_victimProxyAnimator.transform)}', " +
                    $"avatar='{(_victimProxyAnimator.avatar != null ? _victimProxyAnimator.avatar.name : "<none>")}', " +
                    $"pairRootScale={FormatVector(_victimPairRoot.transform.localScale)}, " +
                    $"pairedYaw={_pairedRotation.eulerAngles.y:F1}, " +
                    $"victimYaw={ResolveVictimPlacementRotation().eulerAngles.y:F1}, " +
                    $"basis='{GetHierarchyPath(ResolveBundyVisualBasis())}', " +
                    $"cameraTarget='{GetHierarchyPath(_proxyCameraTarget)}', " +
                    $"sourceProxyVisible={showAuthoredProxyRenderers}, " +
                    $"visibleProxy={_proxyHasVisibleRenderers}, " +
                    $"renderers={proxyRenderers.Length}, " +
                    $"skinnedRenderers={proxySkinnedRenderers.Length}.";
                LogInfo(proxyMessage);
                return true;
            }
            catch (Exception e)
            {
                LogWarning($"Victim proxy creation failed for {PlayerLabel(_player)}: {e.GetType().Name}: {e.Message}");
                DestroyVictimVisualProxy();
                return false;
            }
        }

        private void PlaceProxyAnimatorAtPairedOrigin()
        {
            if (_victimProxyAnimator == null)
                return;

            if (_victimPairRoot != null)
            {
                PlaceVictimPairRoot();
            }
            else
            {
                _victimProxyAnimator.transform.SetPositionAndRotation(_pairedOrigin, ResolveVictimPlacementRotation());
                _victimProxyAnimator.transform.localScale = ResolvePairedRootScale();
            }
        }

        private void PlaceVictimPairRoot()
        {
            if (_victimPairRoot == null)
                return;

            _victimPairRoot.transform.SetPositionAndRotation(_pairedOrigin, ResolveVictimPlacementRotation());
            if (_presentationLiftY > 0f)
                _victimPairRoot.transform.position = _pairedOrigin + Vector3.up * _presentationLiftY;
            _victimPairRoot.transform.localScale = ResolvePairedRootScale();
        }

        private Quaternion ResolveVictimPlacementRotation()
        {
            if (!UsesChainsawSourceRigBasisCorrection())
                return _pairedRotation;

            return FlattenRotation(_pairedRotation * Quaternion.Euler(0f, ResolveChainsawVictimYawOffsetDegrees(), 0f));
        }

        private Vector3 ResolvePairedRootScale()
        {
            if (!UsesChainsawSourceRigBasisCorrection())
                return Vector3.one;

            return Vector3.one * ResolveChainsawVictimProxyScale();
        }

        private static float ResolveChainsawVictimYawOffsetDegrees()
        {
            return BundyPlugin.ModConfig?.ChainsawFinisherVictimYawOffset?.Value ??
                ChainsawAuthoredVictimYawOffsetDegrees;
        }

        private static float ResolveChainsawVictimProxyScale()
        {
            float scale = BundyPlugin.ModConfig?.ChainsawFinisherVictimProxyScale?.Value ??
                ChainsawAuthoredVictimProxyScale;
            return Mathf.Clamp(scale, 0.1f, 8f);
        }

        private Transform ResolveBundyVisualBasis()
        {
            if (_bundy == null)
                return null;

            Transform direct = _bundy.transform.Find("Visual");
            if (direct != null)
                return direct;

            Animator[] animators = _bundy.GetComponentsInChildren<Animator>(true);
            for (int i = 0; i < animators.Length; i++)
            {
                Animator animator = animators[i];
                if (animator != null && animator.transform != _bundy.transform)
                    return animator.transform;
            }

            return _bundy.transform;
        }

        private Transform ResolveProxyCameraTarget()
        {
            if (_victimProxyAnimator == null)
                return _victimVisualProxy != null ? _victimVisualProxy.transform : null;

            Transform target =
                FindChildByLeafName(_victimProxyAnimator.transform, "pelvis") ??
                FindChildByLeafName(_victimProxyAnimator.transform, "Hips") ??
                FindChildByLeafName(_victimProxyAnimator.transform, "spine_03") ??
                FindChildByLeafName(_victimProxyAnimator.transform, "spine_02") ??
                FindChildByLeafName(_victimProxyAnimator.transform, "root");

            return target != null ? target : _victimProxyAnimator.transform;
        }

        private Transform ResolveRealPlayerCameraTarget()
        {
            if (_player == null)
                return null;

            return _player.lowerSpine != null
                ? _player.lowerSpine
                : (_player.playerBodyAnimator != null ? _player.playerBodyAnimator.transform : _player.transform);
        }

        private bool TryCreatePlayerVisualProxyFromClonedRig()
        {
            if (_player == null || _victimProxyAnimator == null || _victimVisualProxy == null)
                return false;

            GameObject sourceVisualRoot = ResolvePlayerVisualProxySourceRoot();
            if (sourceVisualRoot == null)
                return false;

            _playerVisualProxy = new GameObject($"BundyPlayerVisualProxy_{_clipId}_{_player.actualClientId}");
            Transform proxyParent = _victimPairRoot != null ? _victimPairRoot.transform : _victimVisualProxy.transform;
            _playerVisualProxy.transform.SetParent(proxyParent, worldPositionStays: false);
            _playerVisualProxy.transform.localPosition = Vector3.zero;
            _playerVisualProxy.transform.localRotation = Quaternion.identity;
            _playerVisualProxy.transform.localScale = Vector3.one;

            GameObject visualClone = Instantiate(sourceVisualRoot, _playerVisualProxy.transform, worldPositionStays: true);
            visualClone.name = sourceVisualRoot.name + "_BundyVisualClone";
            visualClone.SetActive(true);
            StripPlayerVisualProxyRuntimeComponents(visualClone);

            int visibleRenderers = EnablePlayerVisualProxyRenderers(sourceVisualRoot.transform, visualClone.transform);
            Transform targetRoot = ResolvePlayerVisualProxyTargetRoot(sourceVisualRoot.transform, visualClone.transform, out string targetRootReason);
            if (visibleRenderers == 0 || targetRoot == null)
            {
                LogWarning(
                    $"Visible player victim proxy skipped for {PlayerLabel(_player)}; " +
                    $"source='{GetHierarchyPath(sourceVisualRoot.transform)}', renderers={visibleRenderers}, " +
                    $"targetRoot='{GetHierarchyPath(targetRoot)}'.");
                DestroyPlayerVisualProxyOnly();
                return false;
            }

            if (!TryConfigureVictimRetarget(targetRoot, "visible cloned-player", true))
            {
                DestroyPlayerVisualProxyOnly();
                return false;
            }

            _proxyHasVisibleRenderers = true;
            _clonedPlayerTargetRoot = targetRoot;
            _clonedPlayerTargetRootReason = targetRootReason;
            _proxyCameraTarget = _realPlayerRetargetAnchorBone != null ? _realPlayerRetargetAnchorBone : targetRoot;
            ApplyRealPlayerRetarget();
            LogInfo(
                $"Created visible cloned-player victim proxy for {PlayerLabel(_player)}; " +
                $"source='{GetHierarchyPath(sourceVisualRoot.transform)}', clone='{GetHierarchyPath(visualClone.transform)}', " +
                $"renderers={visibleRenderers}, targetRoot='{GetHierarchyPath(targetRoot)}', " +
                $"targetRootReason='{targetRootReason}', " +
                $"anchorBone='{GetHierarchyPath(_realPlayerRetargetAnchorBone)}', " +
                $"sourceAnchor='{GetHierarchyPath(_sourceRetargetAnchorBone)}', " +
                $"cloneLocalRot={FormatVector(_playerVisualProxy.transform.localEulerAngles)}, " +
                $"cloneWorldYaw={_playerVisualProxy.transform.eulerAngles.y:F1}.");
            return true;
        }

        private bool ShouldUseVisiblePlayerProxy()
        {
            return UsesSourceDrivenVictimPose() && !UsesAuthoredChainsawProxyOnly();
        }

        private bool ShouldUseRealPlayerRetarget()
        {
            if (ShouldPreferChainsawRealPlayerVisuals())
                return false;

            return !UsesAuthoredChainsawProxyOnly();
        }

        private bool ShouldShowAuthoredProxyRenderers()
        {
            return !ShouldPreferChainsawRealPlayerVisuals() || _forceAuthoredChainsawProxyFallback;
        }

        private bool UsesAuthoredChainsawProxyOnly()
        {
            return IsChainsawVictimClip(_clipId) &&
                (!ShouldPreferChainsawRealPlayerVisuals() || _forceAuthoredChainsawProxyFallback);
        }

        private bool ShouldPreferChainsawRealPlayerVisuals()
        {
            return IsChainsawVictimClip(_clipId) &&
                GetChainsawPresentationMode() == ChainsawPresentationMode.ClonedPlayer &&
                (BundyPlugin.ModConfig?.ExperimentalChainsawRealPlayerFinishers?.Value ?? true);
        }

        private bool ShouldUseChainsawIdentityOverlays()
        {
            return IsChainsawVictimClip(_clipId) &&
                GetChainsawPresentationMode() == ChainsawPresentationMode.AuthoredWithIdentityOverlays &&
                (BundyPlugin.ModConfig?.ExperimentalChainsawIdentityOverlays?.Value ?? true);
        }

        private ChainsawPresentationMode GetChainsawPresentationMode()
        {
            if (!IsChainsawVictimClip(_clipId))
                return ChainsawPresentationMode.AuthoredPlaceholder;

            int configured = BundyPlugin.ModConfig?.ChainsawFinisherPresentationMode?.Value ?? 0;
            if (configured < 0 || configured > 2)
                configured = 0;
            return (ChainsawPresentationMode)configured;
        }

        private bool UsesChainsawSourceRigBasisCorrection()
        {
            return IsChainsawVictimClip(_clipId);
        }

        private bool UsesSourceDrivenVictimPose()
        {
            return _anchorMode == BundyVictimAnchorMode.SourcePelvis ||
                _anchorMode == BundyVictimAnchorMode.SourcePairRoot;
        }

        private static bool IsChainsawVictimClip(BundyVictimClipId clipId)
        {
            return clipId == BundyVictimClipId.ChainsawNeckSlash ||
                clipId == BundyVictimClipId.ChainsawSawMiddle ||
                clipId == BundyVictimClipId.ChainsawStabLift;
        }

        private static void AddSkinnedRenderers(HashSet<SkinnedMeshRenderer> renderers, GameObject root)
        {
            if (renderers == null || root == null)
                return;

            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                AddSkinnedRenderer(renderers, renderer);
        }

        private static void AddSkinnedRenderer(HashSet<SkinnedMeshRenderer> renderers, SkinnedMeshRenderer renderer)
        {
            if (renderers != null && renderer != null && renderer.sharedMesh != null)
                renderers.Add(renderer);
        }

        private GameObject ResolvePlayerVisualProxySourceRoot()
        {
            if (_player == null)
                return null;

            var candidates = new List<GameObject>();
            AddGameObjectCandidate(candidates, _player.meshContainer != null ? _player.meshContainer.gameObject : null);
            AddGameObjectCandidate(candidates, _player.thisPlayerBody != null ? _player.thisPlayerBody.gameObject : null);
            AddGameObjectCandidate(candidates, _player.playerBodyAnimator != null ? _player.playerBodyAnimator.gameObject : null);
            AddGameObjectCandidate(candidates, _player.thisPlayerModel != null ? _player.thisPlayerModel.gameObject : null);
            AddGameObjectCandidate(candidates, _player.thisPlayerModelLOD1 != null ? _player.thisPlayerModelLOD1.gameObject : null);
            AddGameObjectCandidate(candidates, _player.thisPlayerModelLOD2 != null ? _player.thisPlayerModelLOD2.gameObject : null);

            for (int i = 0; i < candidates.Count; i++)
            {
                GameObject candidate = candidates[i];
                if (candidate != null && HasUsableSkinnedRenderer(candidate))
                    return candidate;
            }

            return candidates.Count > 0 ? candidates[0] : null;
        }

        private Transform ResolvePlayerVisualProxyTargetRoot(Transform sourceVisualRoot, Transform cloneRoot, out string reason)
        {
            reason = "<none>";
            if (cloneRoot == null)
                return null;

            BoneRetargetMap[] retargetMap = GetRetargetMapForCurrentClip();

            Transform realTargetRoot = ResolveRealPlayerRetargetRoot();
            Transform relativeTarget = null;
            string relativePath = string.Empty;
            if (sourceVisualRoot != null && realTargetRoot != null && realTargetRoot.IsChildOf(sourceVisualRoot))
            {
                relativePath = GetRelativePath(sourceVisualRoot, realTargetRoot);
                relativeTarget = FindChildByRelativePath(cloneRoot, relativePath);
            }

            var candidates = new List<RetargetRootCandidate>();
            AddRetargetRootCandidate(candidates, relativeTarget, $"relative:'{relativePath}'");
            AddRetargetRootCandidate(candidates, FindChildByLeafName(cloneRoot, "metarig"), "leaf:'metarig'");
            AddRetargetRootCandidate(candidates, FindChildByLeafName(cloneRoot, "root"), "leaf:'root'");
            AddRetargetRootCandidate(candidates, FindChildByLeafName(cloneRoot, "spine"), "leaf:'spine'");
            AddRetargetRootCandidate(candidates, cloneRoot, "fallback:cloneRoot");

            Transform best = null;
            int bestScore = -1;
            string bestReason = "<none>";
            var candidateSummary = new List<string>();
            for (int i = 0; i < candidates.Count; i++)
            {
                RetargetRootCandidate candidate = candidates[i];
                int score = CountResolvedRetargetTargets(candidate.Transform, retargetMap);
                candidateSummary.Add($"{candidate.Reason}=>'{GetHierarchyPath(candidate.Transform)}'[{score}]");
                if (score > bestScore)
                {
                    best = candidate.Transform;
                    bestScore = score;
                    bestReason = candidate.Reason;
                }
            }

            reason = $"{bestReason}, score={bestScore}, candidates=[{string.Join(", ", candidateSummary.ToArray())}]";
            return best;
        }

        private static void AddRetargetRootCandidate(List<RetargetRootCandidate> candidates, Transform candidate, string reason)
        {
            if (candidates == null || candidate == null || string.IsNullOrEmpty(reason))
                return;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Transform == candidate)
                    return;
            }

            candidates.Add(new RetargetRootCandidate(candidate, reason));
        }

        private static int CountResolvedRetargetTargets(Transform targetRoot, BoneRetargetMap[] retargetMap)
        {
            if (targetRoot == null || retargetMap == null)
                return -1;

            int resolved = 0;
            for (int i = 0; i < retargetMap.Length; i++)
            {
                if (FindChildByLeafName(targetRoot, retargetMap[i].TargetBoneName) != null)
                    resolved++;
            }

            return resolved;
        }

        private BoneRetargetMap[] GetRetargetMapForCurrentClip()
        {
            return IsChainsawVictimClip(_clipId)
                ? ChainsawRealPlayerRetargetMap
                : RealPlayerRetargetMap;
        }

        private static void AddGameObjectCandidate(List<GameObject> candidates, GameObject candidate)
        {
            if (candidates == null || candidate == null)
                return;

            if (!candidates.Contains(candidate))
                candidates.Add(candidate);
        }

        private static bool HasUsableSkinnedRenderer(GameObject root)
        {
            if (root == null)
                return false;

            SkinnedMeshRenderer[] renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                SkinnedMeshRenderer renderer = renderers[i];
                if (renderer != null && renderer.sharedMesh != null && renderer.bones != null && renderer.bones.Length > 0)
                    return true;
            }

            return false;
        }

        private int EnablePlayerVisualProxyRenderers(Transform sourceRoot, Transform cloneRoot)
        {
            if (sourceRoot == null || cloneRoot == null)
                return 0;

            var allowedSourceRenderers = new List<Renderer>();
            AddAllowedPlayerBodyRenderer(allowedSourceRenderers, _player != null ? _player.thisPlayerModel : null, sourceRoot);
            if (allowedSourceRenderers.Count == 0)
                AddAllowedPlayerBodyRenderer(allowedSourceRenderers, _player != null ? _player.thisPlayerModelLOD1 : null, sourceRoot);
            if (allowedSourceRenderers.Count == 0)
                AddAllowedPlayerBodyRenderer(allowedSourceRenderers, _player != null ? _player.thisPlayerModelLOD2 : null, sourceRoot);

            var allowedCloneRenderers = new HashSet<Renderer>();
            for (int i = 0; i < allowedSourceRenderers.Count; i++)
            {
                Renderer sourceRenderer = allowedSourceRenderers[i];
                string relativePath = GetRelativePath(sourceRoot, sourceRenderer.transform);
                Transform cloneRendererTransform = FindChildByRelativePath(cloneRoot, relativePath);
                Renderer cloneRenderer = cloneRendererTransform != null ? cloneRendererTransform.GetComponent<Renderer>() : null;
                if (cloneRenderer != null)
                    allowedCloneRenderers.Add(cloneRenderer);
            }

            if (allowedCloneRenderers.Count == 0)
            {
                foreach (SkinnedMeshRenderer fallback in cloneRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    if (fallback == null || fallback.sharedMesh == null)
                        continue;

                    string name = fallback.name ?? string.Empty;
                    if (name.IndexOf("arm", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        name.IndexOf("hand", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    allowedCloneRenderers.Add(fallback);
                    break;
                }
            }

            int visibleRenderers = 0;
            Renderer[] renderers = cloneRoot.GetComponentsInChildren<Renderer>(true);
            for (int i = 0; i < renderers.Length; i++)
            {
                Renderer renderer = renderers[i];
                if (renderer == null)
                    continue;

                bool enabled = allowedCloneRenderers.Contains(renderer);
                renderer.enabled = enabled;
                renderer.shadowCastingMode = enabled ? ShadowCastingMode.On : ShadowCastingMode.Off;
                if (renderer is SkinnedMeshRenderer skinnedRenderer)
                {
                    if (!enabled || skinnedRenderer.sharedMesh == null)
                        continue;

                    skinnedRenderer.updateWhenOffscreen = true;
                }

                if (enabled)
                    visibleRenderers++;
            }

            LogInfo(
                $"Prepared cloned-player victim renderers for {PlayerLabel(_player)}; " +
                $"enabled={visibleRenderers}, disabled={Mathf.Max(0, renderers.Length - visibleRenderers)}, " +
                $"sourceRoot='{GetHierarchyPath(sourceRoot)}', cloneRoot='{GetHierarchyPath(cloneRoot)}'.");
            return visibleRenderers;
        }

        private static void AddAllowedPlayerBodyRenderer(List<Renderer> renderers, Renderer renderer, Transform sourceRoot)
        {
            if (renderers == null || renderer == null || sourceRoot == null)
                return;

            if (!renderer.transform.IsChildOf(sourceRoot))
                return;

            if (!renderers.Contains(renderer))
                renderers.Add(renderer);
        }

        private static string GetRelativePath(Transform root, Transform child)
        {
            if (root == null || child == null || child == root)
                return string.Empty;

            var stack = new Stack<string>();
            Transform current = child;
            while (current != null && current != root)
            {
                stack.Push(current.name);
                current = current.parent;
            }

            return current == root ? string.Join("/", stack.ToArray()) : string.Empty;
        }

        private static Transform FindChildByRelativePath(Transform root, string relativePath)
        {
            if (root == null)
                return null;

            if (string.IsNullOrEmpty(relativePath))
                return root;

            Transform current = root;
            string[] parts = relativePath.Split('/');
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                if (string.IsNullOrEmpty(part))
                    continue;

                Transform next = null;
                for (int childIndex = 0; childIndex < current.childCount; childIndex++)
                {
                    Transform child = current.GetChild(childIndex);
                    if (string.Equals(child.name, part, StringComparison.Ordinal))
                    {
                        next = child;
                        break;
                    }
                }

                if (next == null)
                    return null;

                current = next;
            }

            return current;
        }

        private void DestroyPlayerVisualProxyOnly()
        {
            if (_playerVisualProxy != null)
                Destroy(_playerVisualProxy);

            _playerVisualProxy = null;
            _clonedPlayerTargetRoot = null;
            _clonedPlayerTargetRootReason = null;
            _retargetVisiblePlayerProxy = false;
            _proxyHasVisibleRenderers = false;
        }

        private void ApplyChainsawIdentityOverlays()
        {
            bool suitApplied = ApplyPlayerSuitMaterialToAuthoredVictim();
            bool headMaskApplied = TryAttachPlayerHeadMaskOverlay();
            LogInfo(
                $"Applied chainsaw identity overlays for {PlayerLabel(_player)}; " +
                $"suitApplied={suitApplied}, headMaskApplied={headMaskApplied}, " +
                $"overlayRoot='{GetHierarchyPath(_playerIdentityOverlayRoot != null ? _playerIdentityOverlayRoot.transform : null)}'.");
        }

        private bool ApplyPlayerSuitMaterialToAuthoredVictim()
        {
            if (_player == null || _victimVisualProxy == null)
                return false;

            Material suitMaterial = _player.thisPlayerModel != null
                ? _player.thisPlayerModel.sharedMaterial
                : null;
            if (suitMaterial == null)
                return false;

            int applied = 0;
            foreach (SkinnedMeshRenderer renderer in _victimVisualProxy.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer == null || renderer.sharedMesh == null)
                    continue;

                Material[] materials = renderer.sharedMaterials;
                if (materials == null || materials.Length == 0)
                    continue;

                int suitIndex = materials.Length > 1 ? 1 : 0;
                materials[suitIndex] = suitMaterial;
                renderer.sharedMaterials = materials;
                applied++;
            }

            if (applied > 0)
            {
                LogInfo(
                    $"Applied player suit material to authored chainsaw victim for {PlayerLabel(_player)}; " +
                    $"material='{suitMaterial.name}', renderers={applied}." );
                return true;
            }

            return false;
        }

        private bool TryAttachPlayerHeadMaskOverlay()
        {
            if (_player == null || _victimProxyAnimator == null)
                return false;

            Transform sourceMask = ResolvePlayerHeadMaskSource();
            if (sourceMask == null)
                return false;

            Transform sourceHead = _player.playerGlobalHead != null ? _player.playerGlobalHead : _player.transform;
            Transform targetHead =
                FindChildByLeafName(_victimProxyAnimator.transform, "spine.004_end") ??
                FindChildByLeafName(_victimProxyAnimator.transform, "head") ??
                FindChildByLeafName(_victimProxyAnimator.transform, "Head");
            if (targetHead == null)
            {
                LogWarning($"Chainsaw head-mask overlay skipped for {PlayerLabel(_player)}; authored head anchor missing.");
                return false;
            }

            EnsurePlayerIdentityOverlayRoot(targetHead);
            GameObject overlay = Instantiate(sourceMask.gameObject, _playerIdentityOverlayRoot.transform, worldPositionStays: false);
            overlay.name = sourceMask.name + "_BundyHeadMaskOverlay";
            StripIdentityOverlayRuntimeComponents(overlay);

            Transform overlayTransform = overlay.transform;
            overlayTransform.localPosition = Quaternion.Inverse(sourceHead.rotation) * (sourceMask.position - sourceHead.position);
            overlayTransform.localRotation = Quaternion.Inverse(sourceHead.rotation) * sourceMask.rotation;
            overlayTransform.localScale = sourceMask.localScale;

            int enabledRenderers = 0;
            foreach (Renderer renderer in overlay.GetComponentsInChildren<Renderer>(true))
            {
                if (renderer == null)
                    continue;

                renderer.enabled = true;
                renderer.shadowCastingMode = ShadowCastingMode.On;
                enabledRenderers++;
            }

            LogInfo(
                $"Attached chainsaw head-mask overlay for {PlayerLabel(_player)}; " +
                $"source='{GetHierarchyPath(sourceMask)}', targetHead='{GetHierarchyPath(targetHead)}', " +
                $"localPos={FormatVector(overlayTransform.localPosition)}, localRot={FormatVector(overlayTransform.localEulerAngles)}, " +
                $"renderers={enabledRenderers}." );
            return enabledRenderers > 0;
        }

        private Transform ResolvePlayerHeadMaskSource()
        {
            if (_player == null)
                return null;

            Transform searchRoot = _player.playerBodyAnimator != null
                ? _player.playerBodyAnimator.transform
                : _player.transform;
            string[] candidateNames =
            {
                "HeadMaskComedy",
                "HeadMaskTragedy",
            };

            for (int i = 0; i < candidateNames.Length; i++)
            {
                Transform candidate = FindChildByLeafName(searchRoot, candidateNames[i]);
                if (candidate == null)
                    continue;

                Renderer[] renderers = candidate.GetComponentsInChildren<Renderer>(true);
                for (int rendererIndex = 0; rendererIndex < renderers.Length; rendererIndex++)
                {
                    if (renderers[rendererIndex] != null && renderers[rendererIndex].enabled)
                        return candidate;
                }
            }

            return null;
        }

        private void EnsurePlayerIdentityOverlayRoot(Transform targetParent)
        {
            if (_playerIdentityOverlayRoot != null)
                return;

            _playerIdentityOverlayRoot = new GameObject($"BundyPlayerIdentityOverlay_{_clipId}_{_player.actualClientId}");
            _playerIdentityOverlayRoot.transform.SetParent(targetParent, worldPositionStays: false);
            _playerIdentityOverlayRoot.transform.localPosition = Vector3.zero;
            _playerIdentityOverlayRoot.transform.localRotation = Quaternion.identity;
            _playerIdentityOverlayRoot.transform.localScale = Vector3.one;
        }

        private static void StripIdentityOverlayRuntimeComponents(GameObject overlay)
        {
            if (overlay == null)
                return;

            foreach (Collider collider in overlay.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            foreach (Rigidbody rigidbody in overlay.GetComponentsInChildren<Rigidbody>(true))
            {
                rigidbody.isKinematic = true;
                rigidbody.detectCollisions = false;
            }

            foreach (AudioSource audioSource in overlay.GetComponentsInChildren<AudioSource>(true))
                audioSource.enabled = false;

            foreach (Animator animator in overlay.GetComponentsInChildren<Animator>(true))
                animator.enabled = false;

            foreach (Behaviour behaviour in overlay.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == null)
                    continue;

                behaviour.enabled = false;
            }
        }

        private bool TryActivateAuthoredChainsawProxyFallback(string reason)
        {
            if (!IsChainsawVictimClip(_clipId) || _victimVisualProxy == null)
                return false;

            _forceAuthoredChainsawProxyFallback = true;
            PlaceProxyAnimatorAtPairedOrigin();

            Renderer[] proxyRenderers = _victimVisualProxy.GetComponentsInChildren<Renderer>(true);
            int visibleRendererCount = 0;
            for (int i = 0; i < proxyRenderers.Length; i++)
            {
                Renderer renderer = proxyRenderers[i];
                if (renderer == null)
                    continue;

                bool enabled = renderer is SkinnedMeshRenderer skinnedRenderer &&
                    skinnedRenderer.sharedMesh != null &&
                    skinnedRenderer.bones != null &&
                    skinnedRenderer.bones.Length > 0;
                renderer.enabled = enabled;
                renderer.shadowCastingMode = enabled ? ShadowCastingMode.On : ShadowCastingMode.Off;
                if (enabled)
                    visibleRendererCount++;
            }

            _proxyHasVisibleRenderers = visibleRendererCount > 0;
            _proxyCameraTarget = _proxyHasVisibleRenderers ? ResolveProxyCameraTarget() : ResolveRealPlayerCameraTarget();
            if (_proxyHasVisibleRenderers)
                _sourceRetargetAnchorBone = _proxyCameraTarget;

            LogWarning(
                $"Falling back to authored chainsaw victim proxy for {PlayerLabel(_player)}; " +
                $"reason={reason}, visibleRenderers={visibleRendererCount}, " +
                $"pairRootScale={FormatVector(_victimPairRoot != null ? _victimPairRoot.transform.localScale : Vector3.one)}, " +
                $"victimYaw={ResolveVictimPlacementRotation().eulerAngles.y:F1}.");
            return _proxyHasVisibleRenderers;
        }

        internal static bool TryGetActiveVisibleBoundsMinY(
            PlayerControllerB player,
            BundyAIBase bundy,
            out float minY)
        {
            minY = 0f;
            var controller = player != null ? player.GetComponent<BundyVictimPlayableController>() : null;
            if (controller == null || !controller._active || controller._bundy != bundy)
                return false;

            controller.UpdateVictimSequencePlayback();
            controller.ApplyRealPlayerRetarget();
            return controller.TryGetVisibleBoundsMinY(out minY);
        }

        internal static void ApplyActivePresentationLift(PlayerControllerB player, BundyAIBase bundy, float liftY)
        {
            var controller = player != null ? player.GetComponent<BundyVictimPlayableController>() : null;
            if (controller == null || !controller._active || controller._bundy != bundy)
                return;

            controller._presentationLiftY = Mathf.Max(0f, liftY);
            controller.ApplyPresentationLift();
            controller.ApplyRealPlayerRetarget();
        }

        private bool TryGetVisibleBoundsMinY(out float minY)
        {
            minY = 0f;
            bool found = false;
            float best = float.PositiveInfinity;

            AccumulateSkinnedBoundsMinY(_playerVisualProxy, ref found, ref best);
            if (!found && _proxyHasVisibleRenderers)
                AccumulateSkinnedBoundsMinY(_victimVisualProxy, ref found, ref best);

            if (!found || !IsFinite(best))
                return false;

            minY = best;
            return true;
        }

        private static void AccumulateSkinnedBoundsMinY(GameObject root, ref bool found, ref float minY)
        {
            if (root == null)
                return;

            foreach (SkinnedMeshRenderer renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (renderer == null || renderer.sharedMesh == null || !renderer.enabled)
                    continue;

                minY = Mathf.Min(minY, renderer.bounds.min.y);
                found = true;
            }
        }

        private void ApplyPresentationLift()
        {
            if (_victimPairRoot != null)
            {
                _victimPairRoot.transform.SetPositionAndRotation(
                    _pairedOrigin + Vector3.up * _presentationLiftY,
                    ResolveVictimPlacementRotation());
                _victimPairRoot.transform.localScale = ResolvePairedRootScale();
                PinVictimProxyHorizontalRootIfNeeded();
                return;
            }

            if (_victimProxyAnimator != null)
            {
                _victimProxyAnimator.transform.SetPositionAndRotation(
                    _pairedOrigin + Vector3.up * _presentationLiftY,
                    ResolveVictimPlacementRotation());
                _victimProxyAnimator.transform.localScale = ResolvePairedRootScale();
                PinVictimProxyHorizontalRootIfNeeded();
            }
        }

        private void PinVictimProxyHorizontalRootIfNeeded()
        {
            if (!UsesAuthoredChainsawProxyOnly() ||
                _anchorMode != BundyVictimAnchorMode.SourcePairRoot ||
                _victimProxyAnimator == null)
            {
                return;
            }

            Transform proxyRoot = _victimProxyAnimator.transform;
            Vector3 position = proxyRoot.position;
            proxyRoot.SetPositionAndRotation(
                new Vector3(_pairedOrigin.x, position.y, _pairedOrigin.z),
                ResolveVictimPlacementRotation());
        }

        private static void CollectSourceBonesByName(Transform transform, Dictionary<string, Transform> bonesByName)
        {
            if (transform == null || bonesByName == null)
                return;

            string normalized = NormalizeBoneName(transform.name);
            if (!string.IsNullOrEmpty(normalized) && !bonesByName.ContainsKey(normalized))
                bonesByName.Add(normalized, transform);

            for (int i = 0; i < transform.childCount; i++)
                CollectSourceBonesByName(transform.GetChild(i), bonesByName);
        }

        private static Transform ResolveProxyBoneForPlayerBone(Transform playerBone, Dictionary<string, Transform> sourceBonesByName)
        {
            if (playerBone == null || sourceBonesByName == null)
                return null;

            string sourceName = ResolveSourceBoneNameForPlayerBone(playerBone.name);
            if (!string.IsNullOrEmpty(sourceName) &&
                sourceBonesByName.TryGetValue(NormalizeBoneName(sourceName), out Transform mapped))
            {
                return mapped;
            }

            if (sourceBonesByName.TryGetValue(NormalizeBoneName(playerBone.name), out mapped))
                return mapped;

            return null;
        }

        private static string ResolveSourceBoneNameForPlayerBone(string playerBoneName)
        {
            string normalizedPlayer = NormalizeBoneName(playerBoneName);
            for (int i = 0; i < RealPlayerRetargetMap.Length; i++)
            {
                BoneRetargetMap map = RealPlayerRetargetMap[i];
                if (string.Equals(NormalizeBoneName(map.TargetBoneName), normalizedPlayer, StringComparison.OrdinalIgnoreCase))
                    return map.SourceBoneName;
            }

            return playerBoneName;
        }

        private static string NormalizeBoneName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;

            int colonIndex = name.LastIndexOf(':');
            if (colonIndex >= 0 && colonIndex < name.Length - 1)
                name = name.Substring(colonIndex + 1);

            return name.Replace(".", string.Empty)
                .Replace("_", string.Empty)
                .Replace("-", string.Empty)
                .ToLowerInvariant();
        }

        private bool TryConfigureRealPlayerRetarget()
        {
            if (_player == null || _victimProxyAnimator == null || _player.playerBodyAnimator == null)
                return false;

            Transform targetRoot = ResolveRealPlayerRetargetRoot();
            return TryConfigureVictimRetarget(targetRoot, "real-player", false);
        }

        private bool TryConfigureVictimRetarget(Transform targetRoot, string targetLabel, bool visibleProxyTarget)
        {
            _boneRetargetBindings.Clear();
            _savedRetargetBoneStates.Clear();
            _retargetRealPlayerBody = false;
            _retargetVisiblePlayerProxy = false;
            _realPlayerRetargetRootBone = null;
            _realPlayerRetargetAnchorBone = null;
            _sourceRetargetAnchorBone = null;
            _bundyLeftHand = null;
            _bundyRightHand = null;
            _bundyCarryReference = null;

            Transform sourceRoot = _victimProxyAnimator != null ? _victimProxyAnimator.transform : null;
            if (sourceRoot == null || targetRoot == null)
            {
                LogWarning(
                    $"{targetLabel} victim retarget skipped for {PlayerLabel(_player)}; " +
                    $"sourceRoot='{GetHierarchyPath(sourceRoot)}', targetRoot='{GetHierarchyPath(targetRoot)}'.");
                return false;
            }

            _realPlayerRetargetRootBone = targetRoot;
            _sourceRetargetAnchorBone =
                FindChildByLeafName(sourceRoot, "pelvis") ??
                FindChildByLeafName(sourceRoot, "Hips") ??
                sourceRoot;
            _realPlayerRetargetAnchorBone = ResolveRealPlayerAnchorBone(targetRoot);
            _bundyLeftHand = ResolveBundyHand("CC_Base_L_Hand", "mixamorig:LeftHand", "LeftHand", "hand_l", "hand.L");
            _bundyRightHand = ResolveBundyHand("CC_Base_R_Hand", "mixamorig:RightHand", "RightHand", "hand_r", "hand.R");
            _bundyCarryReference = ResolveBundyCarryReference();
            BoneRetargetMap[] retargetMap = GetRetargetMapForCurrentClip();

            var missing = new List<string>();
            for (int i = 0; i < retargetMap.Length; i++)
            {
                BoneRetargetMap map = retargetMap[i];
                Transform source = FindChildByLeafName(sourceRoot, map.SourceBoneName);
                Transform target = FindChildByLeafName(targetRoot, map.TargetBoneName);
                if (source == null || target == null)
                {
                    missing.Add($"{map.SourceBoneName}->{map.TargetBoneName}");
                    continue;
                }

                SaveRetargetBoneState(target);
                _boneRetargetBindings.Add(new BoneRetargetBinding(source, target, map.CopyWorldPosition));
            }

            _retargetRealPlayerBody = _boneRetargetBindings.Count >= 10;
            _retargetVisiblePlayerProxy = visibleProxyTarget && _retargetRealPlayerBody;
            if (!_retargetRealPlayerBody)
            {
                LogWarning(
                    $"{targetLabel} victim retarget skipped for {PlayerLabel(_player)}; " +
                    $"only {_boneRetargetBindings.Count}/{retargetMap.Length} bones resolved. " +
                    $"Missing: {string.Join(", ", missing.Take(8).ToArray())}");
                RestoreRealPlayerRetarget();
                return false;
            }

            _proxyCameraTarget = _realPlayerRetargetAnchorBone;

            LogInfo(
                $"Configured {targetLabel} victim retarget for {PlayerLabel(_player)}; " +
                $"sourceRoot='{GetHierarchyPath(sourceRoot)}', targetRoot='{GetHierarchyPath(targetRoot)}', " +
                $"sourceAnchor='{GetHierarchyPath(_sourceRetargetAnchorBone)}', " +
                $"anchorBone='{GetHierarchyPath(_realPlayerRetargetAnchorBone)}', " +
                $"leftHand='{GetHierarchyPath(_bundyLeftHand)}', rightHand='{GetHierarchyPath(_bundyRightHand)}', " +
                $"carryRef='{GetHierarchyPath(_bundyCarryReference)}', " +
                $"contactOffset={FormatVector(GetCarryAnchorContactOffset())}, " +
                $"bindings={_boneRetargetBindings.Count}/{retargetMap.Length}, " +
                $"retargetProfile={(IsChainsawVictimClip(_clipId) ? "chainsaw" : "default")}, " +
                $"cameraTarget='{GetHierarchyPath(_proxyCameraTarget)}', " +
                $"visibleProxyTarget={_retargetVisiblePlayerProxy}, " +
                $"missing={missing.Count}.");
            return true;
        }

        private Transform ResolveRealPlayerAnchorBone(Transform targetRoot)
        {
            if (targetRoot == null)
                return null;

            if (UsesSourceDrivenVictimPose())
            {
                return FindChildByLeafName(targetRoot, "pelvis") ??
                    FindChildByLeafName(targetRoot, "spine") ??
                    targetRoot;
            }

            return FindChildByLeafName(targetRoot, "spine.002") ??
                FindChildByLeafName(targetRoot, "spine.003") ??
                FindChildByLeafName(targetRoot, "spine.001") ??
                targetRoot;
        }

        private Transform ResolveRealPlayerRetargetRoot()
        {
            if (_player == null)
                return null;

            if (_player.thisPlayerModel != null && _player.thisPlayerModel.rootBone != null)
                return _player.thisPlayerModel.rootBone;

            if (_player.playerBodyAnimator != null)
                return _player.playerBodyAnimator.transform;

            return _player.transform;
        }

        private void ApplyRealPlayerRetarget()
        {
            if (!_retargetRealPlayerBody || _boneRetargetBindings.Count == 0)
                return;

            for (int i = 0; i < _boneRetargetBindings.Count; i++)
                _boneRetargetBindings[i].Apply();

            ApplyRealPlayerCarryAnchor();
        }

        private void ApplyRealPlayerCarryAnchor()
        {
            if (_realPlayerRetargetRootBone == null || _realPlayerRetargetAnchorBone == null)
                return;

            Vector3 desiredAnchor = ResolveDesiredRetargetAnchorPosition();
            Vector3 anchorBefore = _realPlayerRetargetAnchorBone.position;
            Vector3 rootBefore = _realPlayerRetargetRootBone.position;
            Vector3 delta = desiredAnchor - _realPlayerRetargetAnchorBone.position;
            if (!IsFinite(delta) || delta.sqrMagnitude > 64f)
            {
                LogRetargetSyncDebug(desiredAnchor, anchorBefore, anchorBefore, Vector3.zero, true);
                return;
            }

            _realPlayerRetargetRootBone.position += delta;
            LogRetargetSyncDebug(
                desiredAnchor,
                anchorBefore,
                _realPlayerRetargetAnchorBone.position,
                _realPlayerRetargetRootBone.position - rootBefore,
                false);
        }

        private Vector3 ResolveDesiredRetargetAnchorPosition()
        {
            switch (_anchorMode)
            {
                case BundyVictimAnchorMode.SourcePelvis:
                case BundyVictimAnchorMode.SourcePairRoot:
                    if (_sourceRetargetAnchorBone != null)
                        return _sourceRetargetAnchorBone.position;
                    break;
                case BundyVictimAnchorMode.HandContact:
                default:
                    return ResolveBundyCarryAnchorPosition();
            }

            return ResolveBundyCarryAnchorPosition();
        }

        private bool TryGetCurrentSourceAnchor(out Vector3 anchorPosition)
        {
            if (_sourceRetargetAnchorBone != null)
            {
                anchorPosition = _sourceRetargetAnchorBone.position;
                return true;
            }

            if (_realPlayerRetargetAnchorBone != null)
            {
                anchorPosition = _realPlayerRetargetAnchorBone.position;
                return true;
            }

            if (_victimProxyAnimator != null)
            {
                anchorPosition = _victimProxyAnimator.transform.position;
                return true;
            }

            if (_player != null)
            {
                anchorPosition = _player.transform.position;
                return true;
            }

            anchorPosition = Vector3.zero;
            return false;
        }

        private void LogRetargetSyncDebug(
            Vector3 desiredAnchor,
            Vector3 anchorBefore,
            Vector3 anchorAfter,
            Vector3 rootDelta,
            bool skipped)
        {
            if (!SyncDebugEnabled || Time.time < _nextRetargetSyncDebugAt)
                return;

            _nextRetargetSyncDebugAt = Time.time + 0.25f;
            LogInfo(
                $"BundySyncDebug victim retarget tick: frame={Time.frameCount}, player={PlayerLabel(_player)}, " +
                $"skipped={skipped}, anchorMode={_anchorMode}, retargetRealPlayer={_retargetRealPlayerBody}, " +
                $"visibleProxyRetarget={_retargetVisiblePlayerProxy}, " +
                $"clipIndex={_currentClipSequenceIndex}, elapsed={_currentSequenceElapsed:F3}/{_clipSequenceLength:F3}, " +
                $"desiredAnchor={FormatVector(desiredAnchor)}, playerAnchorBefore={FormatVector(anchorBefore)}, " +
                $"playerAnchorAfter={FormatVector(anchorAfter)}, rootDelta={FormatVector(rootDelta)}, " +
                $"sourceAnchor={FormatVector(_sourceRetargetAnchorBone != null ? _sourceRetargetAnchorBone.position : Vector3.zero)}, " +
                $"retargetRoot={FormatVector(_realPlayerRetargetRootBone != null ? _realPlayerRetargetRootBone.position : Vector3.zero)}, " +
                $"bindings={_boneRetargetBindings.Count}/{RealPlayerRetargetMap.Length}.");
        }

        private Vector3 ResolveBundyCarryAnchorPosition()
        {
            Vector3 basePosition;
            if (_bundyLeftHand != null && _bundyRightHand != null)
                basePosition = (_bundyLeftHand.position + _bundyRightHand.position) * 0.5f;
            else if (_bundyLeftHand != null)
                basePosition = _bundyLeftHand.position;
            else if (_bundyRightHand != null)
                basePosition = _bundyRightHand.position;
            else
                basePosition = _pairedOrigin + _pairedRotation * new Vector3(0f, 1.65f, 0.2f);

            Vector3 contactOffset = GetCarryAnchorContactOffset();
            Vector3 forward = ResolveCarryAwayDirection(basePosition);
            Vector3 right = Vector3.Cross(Vector3.up, forward);
            if (right.sqrMagnitude < 0.001f)
                right = _pairedRotation * Vector3.right;
            else
                right.Normalize();

            return basePosition +
                right * contactOffset.x +
                Vector3.up * contactOffset.y +
                forward * contactOffset.z;
        }

        private Vector3 ResolveCarryAwayDirection(Vector3 handMidpoint)
        {
            Vector3 away = Vector3.zero;
            if (_bundyCarryReference != null)
                away = handMidpoint - _bundyCarryReference.position;

            if (!IsFinite(away) || away.sqrMagnitude < 0.01f)
            {
                away = _bundy != null ? _bundy.transform.forward : (_pairedRotation * Vector3.forward);
                away.y = 0f;
            }

            if (!IsFinite(away) || away.sqrMagnitude < 0.01f)
                away = _pairedRotation * Vector3.forward;

            return away.normalized;
        }

        private static Vector3 GetCarryAnchorContactOffset()
        {
            if (BundyPlugin.ModConfig == null)
                return new Vector3(0f, 0.32f, 0.38f);

            return new Vector3(
                BundyPlugin.ModConfig.PressUpThrowCarryAnchorOffsetX.Value,
                BundyPlugin.ModConfig.PressUpThrowCarryAnchorOffsetY.Value,
                BundyPlugin.ModConfig.PressUpThrowCarryAnchorOffsetZ.Value);
        }

        private Transform ResolveBundyHand(params string[] names)
        {
            if (_bundy == null || names == null)
                return null;

            for (int i = 0; i < names.Length; i++)
            {
                Transform hand = FindChildByLeafName(_bundy.transform, names[i]);
                if (hand != null)
                    return hand;
            }

            Transform visual = ResolveBundyVisualBasis();
            if (visual != null && visual != _bundy.transform)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    Transform hand = FindChildByLeafName(visual, names[i]);
                    if (hand != null)
                        return hand;
                }
            }

            return null;
        }

        private Transform ResolveBundyCarryReference()
        {
            if (_bundy == null)
                return null;

            string[] names =
            {
                "mixamorig:Spine2",
                "mixamorig:Spine1",
                "mixamorig:Spine",
                "CC_Base_Spine02",
                "CC_Base_Spine01",
                "CC_Base_Waist",
                "Spine2",
                "Spine1",
                "Spine",
                "spine_03",
                "spine_02",
                "spine_01",
                "mixamorig:Head",
                "CC_Base_Head",
                "Head",
                "head",
            };

            for (int i = 0; i < names.Length; i++)
            {
                Transform reference = FindChildByLeafName(_bundy.transform, names[i]);
                if (reference != null)
                    return reference;
            }

            Transform visual = ResolveBundyVisualBasis();
            if (visual != null && visual != _bundy.transform)
            {
                for (int i = 0; i < names.Length; i++)
                {
                    Transform reference = FindChildByLeafName(visual, names[i]);
                    if (reference != null)
                        return reference;
                }
            }

            return _bundy.transform;
        }

        private void SaveRetargetBoneState(Transform target)
        {
            if (target == null)
                return;

            for (int i = 0; i < _savedRetargetBoneStates.Count; i++)
            {
                if (_savedRetargetBoneStates[i].Transform == target)
                    return;
            }

            _savedRetargetBoneStates.Add(new TransformState(target));
        }

        private void RestoreRealPlayerRetarget()
        {
            for (int i = 0; i < _savedRetargetBoneStates.Count; i++)
                _savedRetargetBoneStates[i].Restore();

            _savedRetargetBoneStates.Clear();
            _boneRetargetBindings.Clear();
            _retargetRealPlayerBody = false;
            _retargetVisiblePlayerProxy = false;
            _realPlayerRetargetRootBone = null;
            _realPlayerRetargetAnchorBone = null;
            _sourceRetargetAnchorBone = null;
            _bundyLeftHand = null;
            _bundyRightHand = null;
            _bundyCarryReference = null;
        }

        private static void StripProxyRuntimeComponents(GameObject proxy)
        {
            if (proxy == null)
                return;

            foreach (Collider collider in proxy.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            foreach (Rigidbody rigidbody in proxy.GetComponentsInChildren<Rigidbody>(true))
            {
                rigidbody.isKinematic = true;
                rigidbody.detectCollisions = false;
            }

            foreach (AudioSource audioSource in proxy.GetComponentsInChildren<AudioSource>(true))
                audioSource.enabled = false;

            foreach (Behaviour behaviour in proxy.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == null || behaviour is Animator)
                    continue;

                behaviour.enabled = false;
            }
        }

        private static void StripPlayerVisualProxyRuntimeComponents(GameObject proxy)
        {
            if (proxy == null)
                return;

            foreach (Collider collider in proxy.GetComponentsInChildren<Collider>(true))
                collider.enabled = false;

            foreach (Rigidbody rigidbody in proxy.GetComponentsInChildren<Rigidbody>(true))
            {
                rigidbody.isKinematic = true;
                rigidbody.detectCollisions = false;
            }

            foreach (AudioSource audioSource in proxy.GetComponentsInChildren<AudioSource>(true))
                audioSource.enabled = false;

            foreach (Animator animator in proxy.GetComponentsInChildren<Animator>(true))
            {
                animator.applyRootMotion = false;
                animator.enabled = false;
            }

            foreach (Behaviour behaviour in proxy.GetComponentsInChildren<Behaviour>(true))
            {
                if (behaviour == null)
                    continue;

                behaviour.enabled = false;
            }
        }

        private void HideRealPlayerVisuals()
        {
            _hiddenRendererStates.Clear();
            if (_player == null)
                return;

            var renderers = new HashSet<Renderer>();
            AddRenderers(renderers, _player.meshContainer != null ? _player.meshContainer.gameObject : null);
            AddRenderers(renderers, _player.thisPlayerBody != null ? _player.thisPlayerBody.gameObject : null);
            AddRenderers(renderers, _player.playerBodyAnimator != null ? _player.playerBodyAnimator.gameObject : null);
            AddRenderer(renderers, _player.thisPlayerModel);
            AddRenderer(renderers, _player.thisPlayerModelLOD1);
            AddRenderer(renderers, _player.thisPlayerModelLOD2);
            AddRenderer(renderers, _player.thisPlayerModelArms);

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                    continue;

                _hiddenRendererStates.Add(new RendererState(renderer, renderer.enabled, renderer.shadowCastingMode));
                renderer.enabled = false;
            }

            LogInfo($"Hid real player visual for {PlayerLabel(_player)}; renderers={_hiddenRendererStates.Count}.");
        }

        private void RestoreRealPlayerVisuals()
        {
            foreach (RendererState state in _hiddenRendererStates)
            {
                if (state.Renderer == null)
                    continue;

                state.Renderer.enabled = state.Enabled;
                state.Renderer.shadowCastingMode = state.ShadowCastingMode;
            }

            if (_hiddenRendererStates.Count > 0)
                LogInfo($"Restored real player visual for {PlayerLabel(_player)}; renderers={_hiddenRendererStates.Count}.");

            _hiddenRendererStates.Clear();
        }

        private void DestroyVictimVisualProxy()
        {
            if (_victimPairRoot != null)
                Destroy(_victimPairRoot);
            else if (_victimVisualProxy != null)
                Destroy(_victimVisualProxy);

            _victimPairRoot = null;
            _victimVisualProxy = null;
            _playerVisualProxy = null;
            _playerIdentityOverlayRoot = null;
            _victimProxyAnimator = null;
            _proxyCameraTarget = null;
            _clonedPlayerTargetRoot = null;
            _clonedPlayerTargetRootReason = null;
            _proxyHasVisibleRenderers = false;
            _retargetVisiblePlayerProxy = false;
            _realPlayerRetargetRootBone = null;
            _realPlayerRetargetAnchorBone = null;
            _sourceRetargetAnchorBone = null;
            _bundyLeftHand = null;
            _bundyRightHand = null;
            _bundyCarryReference = null;
        }

        private void TryPlacePlayerAtProxyRelease(string reason)
        {
            if (_player == null || _player.isPlayerDead || _victimVisualProxy == null || !ShouldUseProxyRelease(reason))
                return;

            Transform releaseTransform = ResolveProxyReleaseTransform();
            Vector3 releasePosition = releaseTransform.position;
            Quaternion releaseRotation = FlattenRotation(releaseTransform.rotation);
            bool usingFallback = false;

            if (!IsFinite(releasePosition) ||
                Vector3.Distance(FlattenPosition(releasePosition), FlattenPosition(_pairedOrigin)) > ProxyReleaseMaxDistance)
            {
                releasePosition = _pairedOrigin + _pairedRotation * (Vector3.forward * 1.1f);
                releaseRotation = _pairedRotation;
                usingFallback = true;
            }

            releasePosition.y = Mathf.Clamp(
                releasePosition.y,
                _pairedOrigin.y + ProxyReleaseMinYOffset,
                _pairedOrigin.y + ProxyReleaseMaxYOffset);

            _player.transform.SetPositionAndRotation(releasePosition, releaseRotation);
            _player.serverPlayerPosition = _player.transform.localPosition;
            _player.ResetFallGravity();

            LogInfo(
                $"Placed real player at victim proxy release for {PlayerLabel(_player)}; " +
                $"reason={reason}, pos={FormatVector(releasePosition)}, fallback={usingFallback}.");
        }

        private Transform ResolveProxyReleaseTransform()
        {
            if (_sourceRetargetAnchorBone != null)
                return _sourceRetargetAnchorBone;

            if (_proxyCameraTarget != null)
                return _proxyCameraTarget;

            if (_victimProxyAnimator != null)
            {
                Transform sourceRoot = _victimProxyAnimator.transform;
                return FindChildByLeafName(sourceRoot, "pelvis") ??
                    FindChildByLeafName(sourceRoot, "Hips") ??
                    FindChildByLeafName(sourceRoot, "spine_02") ??
                    FindChildByLeafName(sourceRoot, "spine_03") ??
                    sourceRoot;
            }

            return _victimVisualProxy != null ? _victimVisualProxy.transform : _player.transform;
        }

        private static void AddRenderers(HashSet<Renderer> renderers, GameObject root)
        {
            if (renderers == null || root == null)
                return;

            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(true))
                AddRenderer(renderers, renderer);
        }

        private static void AddRenderer(HashSet<Renderer> renderers, Renderer renderer)
        {
            if (renderers != null && renderer != null)
                renderers.Add(renderer);
        }

        private static bool ShouldUseProxyRelease(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return false;

            return reason.IndexOf("release", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("impact", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("recover", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private bool ShouldEndChainsawProxyOnDeath()
        {
            return _proxyHasVisibleRenderers && !_retargetRealPlayerBody;
        }

        private void LogVictimBranchSelection()
        {
            string branch = UsesAuthoredChainsawProxyOnly()
                ? "authored-proxy"
                : (_retargetVisiblePlayerProxy ? "cloned-player-proxy" : (_retargetRealPlayerBody ? "real-player-retarget" : "unknown"));
            float pairRootYaw = _victimPairRoot != null ? _victimPairRoot.transform.eulerAngles.y : 0f;
            float sourceYaw = _victimProxyAnimator != null ? _victimProxyAnimator.transform.eulerAngles.y : 0f;
            float cloneLocalYaw = _playerVisualProxy != null ? _playerVisualProxy.transform.localEulerAngles.y : 0f;
            float cloneWorldYaw = _playerVisualProxy != null ? _playerVisualProxy.transform.eulerAngles.y : 0f;
            LogInfo(
                $"Victim branch selected for {PlayerLabel(_player)}; branch={branch}, " +
                $"chainsawMode={GetChainsawPresentationMode()}, " +
                $"chainsawBasisCorrection={UsesChainsawSourceRigBasisCorrection()}, " +
                $"pairRootYaw={pairRootYaw:F1}, sourceRootYaw={sourceYaw:F1}, " +
                $"cloneLocalYaw={cloneLocalYaw:F1}, cloneWorldYaw={cloneWorldYaw:F1}, " +
                $"pairRoot='{GetHierarchyPath(_victimPairRoot != null ? _victimPairRoot.transform : null)}', " +
                $"sourceRoot='{GetHierarchyPath(_victimProxyAnimator != null ? _victimProxyAnimator.transform : null)}', " +
                $"cloneTargetRoot='{GetHierarchyPath(_clonedPlayerTargetRoot)}', " +
                $"cloneTargetRootReason='{_clonedPlayerTargetRootReason ?? "<none>"}'.");
        }

        private static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static Vector3 FlattenPosition(Vector3 value)
        {
            value.y = 0f;
            return value;
        }

        private void BeginLocalThirdPersonCamera()
        {
            BundyVictimDeathCam.Begin(
                _player,
                _bundy,
                ResolveDeathCamTarget(),
                _pairedOrigin,
                _pairedRotation);
        }

        private void UpdateLocalThirdPersonCamera()
        {
            BundyVictimDeathCam.UpdateTarget(
                _player,
                _bundy,
                ResolveDeathCamTarget(),
                _pairedOrigin,
                _pairedRotation);
        }

        private void RestoreLocalThirdPersonCamera()
        {
            BundyVictimDeathCam.End(_player, _bundy, "legacy victim playable restore");
        }

        private Transform ResolveDeathCamTarget()
        {
            return _proxyCameraTarget != null ? _proxyCameraTarget : ResolveRealPlayerCameraTarget();
        }

        private void SyncSpecialAnimationFlag(bool active)
        {
            if (!IsLocalPlayer(_player))
                return;

            try
            {
                _player.UpdateSpecialAnimationValue(active, active ? (short)_pairedRotation.eulerAngles.y : (short)0);
            }
            catch (Exception e)
            {
                LogWarning($"Special animation sync {(active ? "start" : "stop")} failed for {PlayerLabel(_player)}: {e.GetType().Name}: {e.Message}");
            }
        }

        private static bool IsLocalPlayer(PlayerControllerB player)
        {
            return player != null
                && GameNetworkManager.Instance != null
                && GameNetworkManager.Instance.localPlayerController == player;
        }

        private static void ClearBundyLocks(PlayerControllerB player, EnemyAI bundy)
        {
            if (player == null)
                return;

            player.inSpecialInteractAnimation = false;
            player.snapToServerPosition = false;
            player.disableMoveInput = false;
            player.disableLookInput = false;
            player.disableSyncInAnimation = false;
            if (player.inAnimationWithEnemy == bundy)
                player.inAnimationWithEnemy = null;
            player.serverPlayerPosition = player.transform.localPosition;
            player.ResetFallGravity();
        }

        private double GetElapsedSeconds()
        {
            double now = NetworkManager.Singleton != null
                ? NetworkManager.Singleton.ServerTime.Time
                : Time.timeAsDouble;
            return Math.Max(0.0, now - _networkStartTime);
        }

        private static AnimationClip FindClip(AnimationClip[] clips, string clipName)
        {
            if (clips == null)
                return null;

            foreach (AnimationClip clip in clips)
            {
                if (clip != null && string.Equals(clip.name, clipName, StringComparison.Ordinal))
                    return clip;
            }

            return null;
        }

        private static GameObject FindPrefab(AssetBundle bundle, string prefabName)
        {
            if (bundle == null || string.IsNullOrEmpty(prefabName))
                return null;

            GameObject prefab = bundle.LoadAsset<GameObject>(prefabName);
            if (prefab != null)
                return prefab;

            GameObject[] prefabs = bundle.LoadAllAssets<GameObject>();
            foreach (GameObject candidate in prefabs)
            {
                if (candidate != null && string.Equals(candidate.name, prefabName, StringComparison.Ordinal))
                    return candidate;
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
                if (string.Equals(child.name, childName, StringComparison.OrdinalIgnoreCase) ||
                    child.name.EndsWith(":" + childName, StringComparison.OrdinalIgnoreCase))
                {
                    return child;
                }

                Transform nested = FindChildByLeafName(child, childName);
                if (nested != null)
                    return nested;
            }

            return null;
        }

        private static Quaternion FlattenRotation(Quaternion rotation)
        {
            return Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
        }

        private static string PlayerLabel(PlayerControllerB player)
        {
            return player == null
                ? "<null>"
                : $"{player.playerUsername} (clientId={player.actualClientId})";
        }

        private static string GetHierarchyPath(Transform transform)
        {
            if (transform == null)
                return "<null>";

            var stack = new Stack<string>();
            Transform current = transform;
            while (current != null)
            {
                stack.Push(current.name);
                current = current.parent;
            }

            return string.Join("/", stack.ToArray());
        }

        private static string FormatVector(Vector3 value)
        {
            return $"({value.x:F3}, {value.y:F3}, {value.z:F3})";
        }

        // #277: was hardcoded true — every finisher emitted 0.25s-interval multi-hundred-char sync
        // logs on every client in release. Anim category, per-tick tier.
        private static bool SyncDebugEnabled => BundyLogGate.Allows(BundyLogCategory.Anim, 2);

        /// <summary>#716 F3. Every Info line from the victim controller is a diagnostic, and the
        /// controller writes a dozen of them per finisher per client. They now sit under the same
        /// DebugLogs master + Anim/verbosity gate as the rest of Bundy, so a client with
        /// diagnostics off writes none of them; with diagnostics on the output is unchanged.</summary>
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

        private struct VictimClipDefinition
        {
            internal readonly string ClipName;
            internal readonly string ProxyPrefabName;

            internal VictimClipDefinition(string clipName, string proxyPrefabName)
            {
                ClipName = clipName;
                ProxyPrefabName = proxyPrefabName;
            }
        }

        private struct BoneRetargetMap
        {
            internal readonly string SourceBoneName;
            internal readonly string TargetBoneName;
            internal readonly bool CopyWorldPosition;

            internal BoneRetargetMap(string sourceBoneName, string targetBoneName, bool copyWorldPosition)
            {
                SourceBoneName = sourceBoneName;
                TargetBoneName = targetBoneName;
                CopyWorldPosition = copyWorldPosition;
            }
        }

        private struct RetargetRootCandidate
        {
            internal readonly Transform Transform;
            internal readonly string Reason;

            internal RetargetRootCandidate(Transform transform, string reason)
            {
                Transform = transform;
                Reason = reason;
            }
        }

        private struct BoneRetargetBinding
        {
            private readonly Transform _source;
            private readonly Transform _target;
            private readonly Quaternion _sourceBindLocalRotation;
            private readonly Quaternion _targetBindLocalRotation;
            private readonly Vector3 _sourceBindWorldPosition;
            private readonly Vector3 _targetBindWorldPosition;
            private readonly bool _copyWorldPosition;

            internal BoneRetargetBinding(Transform source, Transform target, bool copyWorldPosition)
            {
                _source = source;
                _target = target;
                _copyWorldPosition = copyWorldPosition;
                _sourceBindLocalRotation = source != null ? source.localRotation : Quaternion.identity;
                _targetBindLocalRotation = target != null ? target.localRotation : Quaternion.identity;
                _sourceBindWorldPosition = source != null ? source.position : Vector3.zero;
                _targetBindWorldPosition = target != null ? target.position : Vector3.zero;
            }

            internal void Apply()
            {
                if (_source == null || _target == null)
                    return;

                Quaternion sourceDelta = Quaternion.Inverse(_sourceBindLocalRotation) * _source.localRotation;
                _target.localRotation = _targetBindLocalRotation * sourceDelta;

                if (_copyWorldPosition)
                    _target.position = _targetBindWorldPosition + (_source.position - _sourceBindWorldPosition);
            }
        }

        private struct TransformState
        {
            internal readonly Transform Transform;
            private readonly Vector3 _localPosition;
            private readonly Quaternion _localRotation;
            private readonly Vector3 _localScale;

            internal TransformState(Transform transform)
            {
                Transform = transform;
                _localPosition = transform != null ? transform.localPosition : Vector3.zero;
                _localRotation = transform != null ? transform.localRotation : Quaternion.identity;
                _localScale = transform != null ? transform.localScale : Vector3.one;
            }

            internal void Restore()
            {
                if (Transform == null)
                    return;

                Transform.localPosition = _localPosition;
                Transform.localRotation = _localRotation;
                Transform.localScale = _localScale;
            }
        }

        private struct RendererState
        {
            internal readonly Renderer Renderer;
            internal readonly bool Enabled;
            internal readonly ShadowCastingMode ShadowCastingMode;

            internal RendererState(Renderer renderer, bool enabled, ShadowCastingMode shadowCastingMode)
            {
                Renderer = renderer;
                Enabled = enabled;
                ShadowCastingMode = shadowCastingMode;
            }
        }
    }
}
