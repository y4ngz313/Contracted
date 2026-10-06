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
    // #321 (C8): long enough for the DoFinisher transition to complete on a 0.25s crossfade, short
    // enough that the sample still lands inside the finisher rather than its exit.
    private const float FinisherAnimatorBranchSampleDelaySeconds = 0.4f;

    // #716 F4: both of these describe a steady state (Y4NGZInteractions absent, or a variant the
    // bundle cannot play), not an event. They fired at Warning on every single finisher; once per
    // Bundy is enough to diagnose either.
    private bool _warnedChainsawInteractionsUnavailable;
    private bool _warnedChainsawVariantSubstituted;

    protected bool BeginChainsawFinisherVictim(PlayerControllerB victim, int bodyIndex)
    {
        if (BundyVictimInteractionsBridge.ConfigEnabled)
        {
            if (BundyVictimInteractionsBridge.IsAvailable &&
                BeginChainsawFinisherVictimInteractions(victim, bodyIndex))
            {
                return true;
            }

            WarnOnce(
                ref _warnedChainsawInteractionsUnavailable,
                $"Chainsaw victim Interactions path unavailable for {PlayerLabel(victim)}; " +
                $"bodyIndex={bodyIndex}. Falling back to legacy victim presentation mode. " +
                "Logged once per Bundy.");
        }

        _activeChainsawFinisherUsesInteractions = false;
        return BeginPairedVictimSequence(
            victim,
            new[] { GetChainsawFinisherVictimClipId(bodyIndex) },
            BundyVictimAnchorMode.SourcePairRoot,
            ChainsawFinisherActionId(bodyIndex),
            startAttackerPlayable: false);
    }

    protected bool IsChainsawFinisherVictimReady(int bodyIndex)
    {
        if (BundyVictimInteractionsBridge.ConfigEnabled && BundyVictimInteractionsBridge.IsAvailable)
        {
            // #321 (C6): readiness used to be unconditionally true on the Interactions path, so a
            // body index whose clip never made it into the animationbundle was still selected and
            // then animated nothing. Preload gives a real per-variant verdict; fall back to the
            // legacy clip registry only when that variant is proven dead.
            if (BundyVictimInteractionsBridge.IsVariantAvailable(bodyIndex))
                return true;

            return BundyVictimPlayableController.IsClipReady(GetChainsawFinisherVictimClipId(bodyIndex));
        }

        return BundyVictimPlayableController.IsClipReady(GetChainsawFinisherVictimClipId(bodyIndex));
    }

    protected bool IsChainsawFinisherVictimPoolReady()
    {
        if (BundyVictimInteractionsBridge.ConfigEnabled &&
            BundyVictimInteractionsBridge.IsAvailable &&
            BundyVictimInteractionsBridge.IsAnyVariantAvailable())
        {
            return true;
        }

        return BundyVictimPlayableController.IsClipSequenceReady(
            BundyVictimClipId.ChainsawNeckSlash,
            BundyVictimClipId.ChainsawSawMiddle,
            BundyVictimClipId.ChainsawStabLift);
    }

    /// <summary>
    /// #321 (C6): the body indices this client can actually present, in selection order. Used by
    /// <see cref="BundyAIChainsaw.SelectFinisherBodyIndex"/> so a dead variant is skipped instead of
    /// being picked and playing nothing.
    /// </summary>
    protected bool IsChainsawFinisherBodyIndexSelectable(int bodyIndex)
    {
        return IsChainsawFinisherVictimReady(bodyIndex);
    }

    /// <summary>
    /// #321 (C6): the no-repeat pick assumed all three variants can play. When one cannot — its clip
    /// missing from the animationbundle, or its interaction refused by the API — picking it produced
    /// a finisher where the victim animated nothing, which reads in-game as "only one finisher ever
    /// plays". Skip proven-dead body indices, keeping the rotation across whatever is left.
    /// </summary>
    protected int SelectAvailableChainsawFinisherBodyIndex(int bodyIndex)
    {
        if (IsChainsawFinisherBodyIndexSelectable(bodyIndex))
            return bodyIndex;

        int bodyCount = Mathf.Max(1, FinisherBodyCount);
        for (int offset = 1; offset < bodyCount; offset++)
        {
            int candidate = (bodyIndex + offset) % bodyCount;
            if (!IsChainsawFinisherBodyIndexSelectable(candidate))
                continue;

            WarnOnce(
                ref _warnedChainsawVariantSubstituted,
                $"Chainsaw finisher variant bodyIndex={bodyIndex} is unavailable " +
                $"(interaction='{BundyVictimInteractionsBridge.DescribeInteractionId(bodyIndex)}'); " +
                $"substituting bodyIndex={candidate}. Logged once per Bundy.");
            return candidate;
        }

        LogWarning(
            $"No chainsaw finisher variant is available; keeping bodyIndex={bodyIndex} " +
            $"(interaction='{BundyVictimInteractionsBridge.DescribeInteractionId(bodyIndex)}'). " +
            "The victim will not animate.");
        return bodyIndex;
    }

    protected bool IsChainsawFinisherVictimActive(PlayerControllerB victim, int bodyIndex)
    {
        if (victim == null || victim.isPlayerDead)
            return false;

        if (_activeChainsawFinisherUsesInteractions)
        {
            // #321 (B3): this used to short-circuit on a local bool that was set before any client
            // reported back, so a failed TryStartInteraction still claimed an active victim
            // animation and the lethal impact fired over a T-pose. Consult what actually started:
            // the bridge's own tracking, or the legacy playable if this client fell back to it.
            // Tracking (not API liveness) is the question, so a session that reached its natural
            // end just before the release RPC does not abort the finisher.
            if (BundyVictimInteractionsBridge.IsVictimFinisherTracked(victim, bodyIndex))
                return true;

            if (_chainsawFinisherVictimFallbackLocal &&
                IsPairedVictimPlayableActive(victim, GetChainsawFinisherVictimClipId(bodyIndex)))
            {
                return true;
            }

            return false;
        }

        return BundyVictimInteractionsBridge.IsVictimFinisherActive(victim, bodyIndex) ||
            IsPairedVictimPlayableActive(victim, GetChainsawFinisherVictimClipId(bodyIndex));
    }

    protected void ReleaseChainsawFinisherVictim(PlayerControllerB victim, int bodyIndex, string reason)
    {
        if (_activeChainsawFinisherUsesInteractions)
        {
            ReleaseChainsawFinisherVictimInteractions(victim, bodyIndex, reason);
            return;
        }

        ReleasePairedVictimPlayable(victim, GetChainsawFinisherVictimClipId(bodyIndex), reason);
    }

    private bool BeginChainsawFinisherVictimInteractions(PlayerControllerB victim, int bodyIndex)
    {
        int playerIndex = GetPlayerIndex(victim);
        if (playerIndex < 0 || victim == null || victim.isPlayerDead)
            return false;

        BundyVictimClipId clipId = GetChainsawFinisherVictimClipId(bodyIndex);
        double startTime = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.ServerTime.Time
            : Time.timeAsDouble;
        Quaternion pairedRotation = GetPairedVictimRotation(victim, clipId);
        Vector3 pairedOrigin = GetPairedVictimOrigin(victim, clipId, pairedRotation);
        string pairedActionId = ChainsawFinisherActionId(bodyIndex);
        // The legacy proxy applied ChainsawFinisherVictimYawOffset (180 = face Bundy) inside the
        // playable controller; the Interactions lock positions the real player directly, so the
        // yaw offset must be folded into the lock rotation here or the victim faces away.
        float victimYawOffset = BundyPlugin.ModConfig?.ChainsawFinisherVictimYawOffset?.Value ?? 180f;
        Quaternion victimLockRotation = pairedRotation * Quaternion.Euler(0f, victimYawOffset, 0f);

        transform.rotation = pairedRotation;
        inSpecialAnimation = true;
        inSpecialAnimationWithPlayer = victim;
        _activePairedActionId = pairedActionId;
        _activeChainsawFinisherUsesInteractions = true;

        LogInfo(
            $"BundyFinisherFlow BeginChainsawFinisherVictimInteractions: player={PlayerLabel(victim)}, " +
            $"bodyIndex={Mathf.Clamp(bodyIndex, 0, 2)}, action='{pairedActionId}', " +
            $"origin={FormatVector(pairedOrigin)}, yaw={pairedRotation.eulerAngles.y:F1}, networkStart={startTime:F3}.");

        if (IsServer)
        {
            BeginChainsawVictimInteractionsClientRpc(
                playerIndex,
                Mathf.Clamp(bodyIndex, 0, 2),
                startTime,
                pairedOrigin,
                victimLockRotation,
                transform.position,
                pairedRotation,
                pairedActionId);
        }
        else
        {
            BeginPairedActionRootLock(transform.position, pairedRotation);
            // #321 (A1): suppress before the animation starts, so the very first pinned frame
            // cannot generate a contact impulse between Bundy's capsule and the victim controller.
            BundyFinisherCollisionSuppressor.Suppress(this, victim, $"chainsaw finisher begin ({pairedActionId})");
            if (!TryStartChainsawVictimInteractionsLocal(victim, bodyIndex, startTime, pairedOrigin, victimLockRotation, pairedActionId))
            {
                BundyFinisherCollisionSuppressor.RestoreAllForBundy(this, "chainsaw finisher local start failed");
                EndFinisherGoreBloodFX("chainsaw finisher local start failed"); // #322
                _activeChainsawFinisherUsesInteractions = false;
                return false;
            }

            // #321: the pair holds its authored separation for the whole action, not just the entry
            // frame. Armed after the victim lock exists so the first correction has a pin to move.
            BeginPairedFinisherAlignment(victim, pairedOrigin, pairedActionId, bodyIndex);

            // #297/#323: the server path arms this inside the ClientRpc instead.
            BeginSawCuttingLocal(Mathf.Clamp(bodyIndex, 0, 2), pairedActionId, victim);
        }

        return true;
    }

    private void ReleaseChainsawFinisherVictimInteractions(PlayerControllerB victim, int bodyIndex, string reason)
    {
        int playerIndex = GetPlayerIndex(victim);
        if (playerIndex < 0)
            return;

        string releaseReason = string.IsNullOrEmpty(reason) ? "Bundy chainsaw finisher release" : reason;
        bool interrupted = IsInterruptedReleaseReason(releaseReason);
        if (IsServer)
        {
            ReleaseChainsawVictimInteractionsClientRpc(
                playerIndex,
                Mathf.Clamp(bodyIndex, 0, 2),
                releaseReason,
                interrupted,
                endAttacker: true);
        }
        else
        {
            StopChainsawVictimInteractionsLocal(victim, bodyIndex, releaseReason, interrupted);
            if (_chainsawFinisherVictimFallbackLocal)
                BundyVictimPlayableController.End(victim, GetChainsawFinisherVictimClipId(bodyIndex), releaseReason);
            BundyFinisherCollisionSuppressor.RestoreAllForBundy(this, $"chainsaw finisher release: {releaseReason}");
            EndFinisherGoreBloodFX($"chainsaw finisher release: {releaseReason}"); // #322
            BundyAttackerPlayableController.EndActive(this, releaseReason);
            ClearPairedActionRootLock();
            // #297/#323: the server path disarms inside the ClientRpc instead.
            EndSawCuttingLocal($"chainsaw finisher release: {releaseReason}", interrupted);
        }

        _activeChainsawFinisherUsesInteractions = false;
        _chainsawFinisherVictimFallbackLocal = false;
        if (inSpecialAnimationWithPlayer == victim)
        {
            inSpecialAnimationWithPlayer = null;
            inSpecialAnimation = false;
        }
    }

    private static BundyVictimClipId GetChainsawFinisherVictimClipId(int bodyIndex)
    {
        switch (Mathf.Clamp(bodyIndex, 0, 2))
        {
            case 0:
                return BundyVictimClipId.ChainsawNeckSlash;
            case 1:
                return BundyVictimClipId.ChainsawSawMiddle;
            default:
                return BundyVictimClipId.ChainsawStabLift;
        }
    }

    private static string ChainsawFinisherActionId(int bodyIndex)
    {
        return $"chainsaw.finisher.body{Mathf.Clamp(bodyIndex, 0, 2)}";
    }

    private static bool IsChainsawFinisherActionId(string actionId)
    {
        return !string.IsNullOrEmpty(actionId) &&
            actionId.StartsWith("chainsaw.finisher.", StringComparison.Ordinal);
    }

    /// <summary>
    /// The finisher variant as THIS client can know it. <c>_activeFinisherBodyIndex</c> is set by
    /// the server-side action start only; the paired action id is replicated by every begin RPC, so
    /// it is the client-side source of truth. Falls back to the throat band when neither is known.
    /// </summary>
    private int ResolveLocalFinisherBodyIndex()
    {
        if (_activeFinisherBodyIndex >= 0)
            return Mathf.Clamp(_activeFinisherBodyIndex, 0, 2);

        return IsChainsawFinisherActionId(_activePairedActionId)
            ? GetChainsawFinisherBodyIndexFromActionId(_activePairedActionId)
            : 0;
    }

    private static int GetChainsawFinisherBodyIndexFromActionId(string actionId)
    {
        if (string.IsNullOrEmpty(actionId))
            return 0;

        int bodyMarker = actionId.LastIndexOf("body", StringComparison.Ordinal);
        if (bodyMarker < 0 || bodyMarker + 4 >= actionId.Length)
            return 0;

        return int.TryParse(actionId.Substring(bodyMarker + 4), out int bodyIndex)
            ? Mathf.Clamp(bodyIndex, 0, 2)
            : 0;
    }

    private bool BeginPairedVictimSequence(
        PlayerControllerB victim,
        BundyVictimClipId[] clipIds,
        BundyVictimAnchorMode anchorMode,
        string pairedActionId = null,
        bool startAttackerPlayable = true)
    {
        int playerIndex = GetPlayerIndex(victim);
        if (playerIndex < 0)
            return false;

        if (!BundyVictimPlayableController.IsClipSequenceReady(clipIds))
        {
            LogAnimEvent(nameof(BeginPairedVictimSequence), $"victim playable not registered; sequence={FormatClipSequence(clipIds)}");
            return false;
        }

        double startTime = NetworkManager.Singleton != null
            ? NetworkManager.Singleton.ServerTime.Time
            : Time.timeAsDouble;
        BundyVictimClipId firstClip = clipIds != null && clipIds.Length > 0 ? clipIds[0] : BundyVictimClipId.PressUpThrow;
        Quaternion pairedRotation = GetPairedVictimRotation(victim, firstClip);
        Vector3 pairedOrigin = GetPairedVictimOrigin(victim, firstClip, pairedRotation);
        transform.rotation = pairedRotation;
        inSpecialAnimation = true;
        inSpecialAnimationWithPlayer = victim;
        bool shouldStartAttackerPlayable = startAttackerPlayable && !IsChainsawFinisherActionId(pairedActionId);
        bool attackerPlayableStarted = shouldStartAttackerPlayable &&
            BundyAttackerPlayableController.Begin(this, pairedActionId, startTime);
        _activePairedActionId = !string.IsNullOrEmpty(pairedActionId) ? pairedActionId : null;
        // #716 F5: the arguments (clip-sequence formatting, the attacker debug-state dump) were
        // built before LogInfo could refuse them, so this ran in full with diagnostics off.
        if (ShouldLog(BundyLogCategory.Core, BundyLogGate.DefaultVerbosity))
            LogInfo(
            $"BundyFinisherFlow BeginPairedVictimSequence: player={PlayerLabel(victim)}, action='{pairedActionId ?? "<none>"}', " +
            $"clipIds={FormatClipSequence(clipIds)}, attackerPlayableRequested={shouldStartAttackerPlayable}, " +
            $"BundyAttackerPlayableController.Begin returned {attackerPlayableStarted}, " +
            $"attackerPlayable={BundyAttackerPlayableController.GetDebugState(this)}, " +
            $"attackerRoot={FormatVector(transform.position)}, attackerYaw={transform.eulerAngles.y:F1}, " +
            $"victimRoot={FormatVector(victim.transform.position)}, victimYaw={victim.transform.eulerAngles.y:F1}, " +
            $"pairedOrigin={FormatVector(pairedOrigin)}, pairedYaw={pairedRotation.eulerAngles.y:F1}, networkStart={startTime:F3}.");

        if (IsServer)
            BeginPairedVictimSequenceClientRpc(
                playerIndex,
                clipIds.Length > 0 ? (int)clipIds[0] : -1,
                clipIds.Length > 1 ? (int)clipIds[1] : -1,
                clipIds.Length > 2 ? (int)clipIds[2] : -1,
                Mathf.Clamp(clipIds.Length, 0, 3),
                (int)anchorMode,
                startTime,
                pairedOrigin,
                pairedRotation,
                transform.position,
                pairedRotation,
                pairedActionId ?? string.Empty,
                shouldStartAttackerPlayable);
        else
        {
            BeginPairedActionRootLock(transform.position, pairedRotation);
            BundyVictimPlayableController.Begin(victim, this, clipIds, anchorMode, startTime, pairedOrigin, pairedRotation);
        }

        LogInfo(
            $"Paired sequence begin: player={PlayerLabel(victim)}, action='{pairedActionId ?? "<none>"}', " +
            $"clips={FormatClipSequence(clipIds)}, attackerPlayableRequested={shouldStartAttackerPlayable}, " +
            $"attackerPlayable={attackerPlayableStarted}, " +
            $"attackerRoot={FormatVector(transform.position)}, victimOrigin={FormatVector(pairedOrigin)}, " +
            $"yaw={pairedRotation.eulerAngles.y:F1}.");
        if (SyncDebugLogs)
        {
            LogDiagnostic(
                $"BundySyncDebug paired begin: player={PlayerLabel(victim)}, action='{pairedActionId ?? "<none>"}', " +
                $"clips={FormatClipSequence(clipIds)}, anchorMode={anchorMode}, " +
                $"attackerPlayableRequested={shouldStartAttackerPlayable}, attackerPlayable={attackerPlayableStarted}, " +
                $"attackerRoot={FormatVector(transform.position)}, victimRoot={FormatVector(victim.transform.position)}, " +
                $"victimOrigin={FormatVector(pairedOrigin)}, yaw={pairedRotation.eulerAngles.y:F1}, " +
                $"networkStart={startTime:F3}.");
        }

        return true;
    }

    private static string FormatClipSequence(BundyVictimClipId[] clipIds)
    {
        if (clipIds == null || clipIds.Length == 0)
            return "<empty>";

        return string.Join(" -> ", clipIds.Select(clipId => clipId.ToString()).ToArray());
    }

    private Vector3 GetPairedVictimOrigin(PlayerControllerB victim, BundyVictimClipId clipId, Quaternion pairedRotation)
    {
        if (IsChainsawFinisherVictimClip(clipId))
            return ResolveGroundedPairedRoot(transform.position + pairedRotation * GetChainsawFinisherVictimLocalOffset(), victim);

        Vector3 localOffset = Vector3.zero;
        if (clipId == BundyVictimClipId.PressUpThrow)
            localOffset.z = PressUpThrowDefaultVictimForwardOffset;

        if (BundyPlugin.ModConfig != null && clipId == BundyVictimClipId.PressUpThrow)
        {
            localOffset += new Vector3(
                BundyPlugin.ModConfig.PressUpThrowVictimOffsetX.Value,
                BundyPlugin.ModConfig.PressUpThrowVictimOffsetY.Value,
                BundyPlugin.ModConfig.PressUpThrowVictimOffsetZ.Value);
        }

        return transform.position + pairedRotation * localOffset;
    }

    private static Vector3 GetChainsawFinisherVictimLocalOffset()
    {
        if (BundyPlugin.ModConfig == null)
            return new Vector3(0f, 0f, 1.15f);

        return new Vector3(
            BundyPlugin.ModConfig.ChainsawFinisherVictimOffsetX.Value,
            BundyPlugin.ModConfig.ChainsawFinisherVictimOffsetY.Value,
            BundyPlugin.ModConfig.ChainsawFinisherVictimOffsetZ.Value);
    }

    private Quaternion GetPairedVictimRotation(PlayerControllerB victim, BundyVictimClipId clipId)
    {
        Vector3 direction;
        if (IsLivingPlayer(victim))
            direction = victim.transform.position - transform.position;
        else
            direction = transform.forward;

        direction.y = 0f;
        if (direction.sqrMagnitude < 0.001f)
            direction = transform.forward;

        Quaternion rotation = Quaternion.LookRotation(direction.normalized, Vector3.up);
        if (BundyPlugin.ModConfig != null && clipId == BundyVictimClipId.PressUpThrow)
        {
            rotation *= Quaternion.Euler(
                0f,
                BundyPlugin.ModConfig.PressUpThrowVictimEulerY.Value,
                0f);
        }

        return rotation;
    }

    private static bool IsChainsawFinisherVictimClip(BundyVictimClipId clipId)
    {
        return clipId == BundyVictimClipId.ChainsawNeckSlash
            || clipId == BundyVictimClipId.ChainsawSawMiddle
            || clipId == BundyVictimClipId.ChainsawStabLift;
    }

    private static Vector3 SamplePairedAnchorDelta(
        BundyPairedActionDefinition action,
        float elapsed,
        float fallbackSequenceLength)
    {
        if (action == null || action.samples == null || action.samples.Length == 0)
            return Vector3.zero;

        BundyPairedActionSample[] samples = action.samples;
        float clampedElapsed = Mathf.Clamp(
            elapsed,
            0f,
            Mathf.Max(0.01f, action.duration > 0f ? action.duration : fallbackSequenceLength));

        BundyPairedActionSample first = samples[0];
        if (first == null || clampedElapsed <= first.time)
            return ToVector3(first?.anchorDelta);

        for (int i = 1; i < samples.Length; i++)
        {
            BundyPairedActionSample previous = samples[i - 1];
            BundyPairedActionSample current = samples[i];
            if (previous == null || current == null)
                continue;

            if (clampedElapsed <= current.time)
            {
                float t = Mathf.InverseLerp(previous.time, current.time, clampedElapsed);
                return Vector3.LerpUnclamped(
                    ToVector3(previous.anchorDelta),
                    ToVector3(current.anchorDelta),
                    t);
            }
        }

        return ToVector3(samples[samples.Length - 1]?.anchorDelta);
    }

    private static Vector3 ToVector3(BundyVector3Data value)
    {
        return value != null
            ? new Vector3(value.x, value.y, value.z)
            : Vector3.zero;
    }

    private void ReleasePairedVictimPlayable(PlayerControllerB victim, BundyVictimClipId clipId, string reason)
    {
        int playerIndex = GetPlayerIndex(victim);
        if (playerIndex < 0)
            return;

        string releaseReason = string.IsNullOrEmpty(reason) ? "Bundy action release" : reason;
        bool keepAttackerUntilRecover =
            _actionKind == ActionKind.Attack &&
            releaseReason.IndexOf("throw impact", StringComparison.OrdinalIgnoreCase) >= 0;
        if (IsServer)
        {
            ReleasePairedVictimPlayableClientRpc(playerIndex, (int)clipId, releaseReason, !keepAttackerUntilRecover);
        }
        else
        {
            BundyVictimPlayableController.End(victim, clipId, releaseReason);
            if (!keepAttackerUntilRecover)
            {
                BundyAttackerPlayableController.EndActive(this, releaseReason);
                ClearPairedActionRootLock();
            }
        }

        if (inSpecialAnimationWithPlayer == victim)
        {
            inSpecialAnimationWithPlayer = null;
            inSpecialAnimation = false;
        }
    }

    private bool IsPairedVictimPlayableActive(PlayerControllerB victim, BundyVictimClipId clipId)
    {
        int playerIndex = GetPlayerIndex(victim);
        return playerIndex >= 0 && BundyVictimPlayableController.IsActive(victim, clipId);
    }

    [ClientRpc]
    public void BeginPairedVictimSequenceClientRpc(
        int playerIndex,
        int clipId0,
        int clipId1,
        int clipId2,
        int clipCount,
        int anchorMode,
        double networkStartTime,
        Vector3 pairedOrigin,
        Quaternion pairedRotation,
        Vector3 attackerRootPosition,
        Quaternion attackerRootRotation,
        string pairedActionId,
        bool startAttackerPlayable)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null || playerIndex < 0 || playerIndex >= players.Length)
            return;

        int count = Mathf.Clamp(clipCount, 0, 3);
        if (count <= 0 || clipId0 < 0)
            return;

        var sequence = new List<BundyVictimClipId>(count)
        {
            (BundyVictimClipId)clipId0,
        };
        if (count > 1 && clipId1 >= 0)
            sequence.Add((BundyVictimClipId)clipId1);
        if (count > 2 && clipId2 >= 0)
            sequence.Add((BundyVictimClipId)clipId2);

        _activePairedActionId = !string.IsNullOrEmpty(pairedActionId) ? pairedActionId : null;
        BeginPairedActionRootLock(attackerRootPosition, attackerRootRotation);
        // #321 (A1): the legacy path pins the real body too whenever it retargets it, so the same
        // collider fight applies. Scoped to finishers — the throw/press actions want their contacts.
        if (IsChainsawFinisherActionId(pairedActionId))
        {
            BundyFinisherCollisionSuppressor.Suppress(this, players[playerIndex], $"legacy paired finisher begin ({pairedActionId})");
            // The legacy path arms no cut, so BeginSawCuttingLocal's backstop never covers it — but
            // it establishes the same client-local suppression, and the same lost release RPC would
            // strand it. Disarmed by the release's EndSawCuttingLocal like every other path.
            _finisherLocalTeardownAt = Time.time + FinisherLocalTeardownBackstopSeconds;
        }
        bool shouldStartAttackerPlayable = startAttackerPlayable && !IsChainsawFinisherActionId(pairedActionId);
        bool attackerPlayableStarted = shouldStartAttackerPlayable &&
            BundyAttackerPlayableController.Begin(this, pairedActionId, networkStartTime);
        if (!shouldStartAttackerPlayable && !IsServer && IsChainsawFinisherActionId(pairedActionId))
            PlayFinisherAnimation(0, GetChainsawFinisherBodyIndexFromActionId(pairedActionId));
        // #277: routine success, downgraded from warning to gated info.
        // #716 F5: gate the call site too - sequence.ToArray() and the attacker debug-state dump
        // were evaluated before LogInfo's own gate could drop the line.
        if (ShouldLog(BundyLogCategory.Anim, 1))
            LogInfo(BundyLogCategory.Anim, 1,
            $"Paired action client root lock: playerIndex={playerIndex}, sequence={FormatClipSequence(sequence.ToArray())}, " +
            $"attackerRoot={FormatVector(attackerRootPosition)}, victimOrigin={FormatVector(pairedOrigin)}, " +
            $"yaw={attackerRootRotation.eulerAngles.y:F1}, action='{pairedActionId}', " +
            $"attackerPlayableRequested={shouldStartAttackerPlayable}, attackerPlayable={attackerPlayableStarted}, " +
            $"attackerPlayableState={BundyAttackerPlayableController.GetDebugState(this)}.");
        if (SyncDebugLogs)
        {
            LogDiagnostic(
                $"BundySyncDebug paired client-begin: player={PlayerLabel(players[playerIndex])}, action='{pairedActionId}', " +
                $"sequence={FormatClipSequence(sequence.ToArray())}, anchorMode={(BundyVictimAnchorMode)anchorMode}, " +
                $"attackerPlayableRequested={shouldStartAttackerPlayable}, attackerPlayable={attackerPlayableStarted}, " +
                $"attackerRoot={FormatVector(attackerRootPosition)}, " +
                $"victimRoot={FormatVector(players[playerIndex].transform.position)}, victimOrigin={FormatVector(pairedOrigin)}, " +
                $"yaw={attackerRootRotation.eulerAngles.y:F1}, networkStart={networkStartTime:F3}.");
        }

        BundyVictimPlayableController.Begin(
            players[playerIndex],
            this,
            sequence.ToArray(),
            (BundyVictimAnchorMode)anchorMode,
            networkStartTime,
            pairedOrigin,
            pairedRotation);
    }

    [ClientRpc]
    public void BeginChainsawVictimInteractionsClientRpc(
        int playerIndex,
        int bodyIndex,
        double networkStartTime,
        Vector3 pairedOrigin,
        Quaternion pairedRotation,
        Vector3 attackerRootPosition,
        Quaternion attackerRootRotation,
        string pairedActionId)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null || playerIndex < 0 || playerIndex >= players.Length)
            return;

        PlayerControllerB victim = players[playerIndex];
        _activePairedActionId = !string.IsNullOrEmpty(pairedActionId) ? pairedActionId : null;
        _activeChainsawFinisherUsesInteractions = true;
        _chainsawFinisherVictimFallbackLocal = false;
        BeginPairedActionRootLock(attackerRootPosition, attackerRootRotation);
        // #321 (A1): every client owns its own physics, so the ignore-pairs must be established on
        // every client, not just the server. This is the begin half; ReleaseChainsawVictimInteractions
        // ClientRpc / KillFinisherVictimClientRpc / action cleanup are the restore halves.
        BundyFinisherCollisionSuppressor.Suppress(this, victim, $"chainsaw finisher begin ({pairedActionId})");
        // #297: the blade is about to be in a body — every client arms the saw's cutting state
        // (engine bog + resistance texture) off the same RPC that starts the choreography.
        // #323 rides the same call: the RPC carries the action id and bodyIndex, and every client
        // has the paired-action catalog, so each one schedules the gore phases itself from the
        // exact frame its own copy of the choreography starts on. No extra traffic, no drift.
        BeginSawCuttingLocal(Mathf.Clamp(bodyIndex, 0, 2), pairedActionId, victim);
        if (!IsServer)
            PlayFinisherAnimation(0, Mathf.Clamp(bodyIndex, 0, 2));

        bool started = TryStartChainsawVictimInteractionsLocal(
            victim,
            bodyIndex,
            networkStartTime,
            pairedOrigin,
            pairedRotation,
            pairedActionId);
        if (!started)
        {
            // #321 (B3): the fallback engages per client. _activeChainsawFinisherUsesInteractions
            // must NOT be cleared here — on the host it also selects the release path for the whole
            // lobby, and clearing it would send remote clients that DID start the Interactions
            // choreography down the legacy release instead. ReleaseChainsawVictimInteractionsClientRpc
            // already ends both presentations, so a per-client flag is all the fallback needs.
            _chainsawFinisherVictimFallbackLocal = true;
            EndPairedFinisherAlignment("legacy victim playable fallback");
            BundyVictimClipId fallbackClip = GetChainsawFinisherVictimClipId(bodyIndex);
            LogWarning(
                $"Chainsaw victim Interactions client start failed for {PlayerLabel(victim)}; " +
                $"bodyIndex={Mathf.Clamp(bodyIndex, 0, 2)}, action='{pairedActionId}', " +
                $"interaction='{BundyVictimInteractionsBridge.DescribeInteractionId(bodyIndex)}'. " +
                "Engaging legacy victim playable fallback on this client.");
            // The proxy path moves the real body itself; a lock left over from an earlier finisher
            // would fight it every LateUpdate and keep inSpecialInteractAnimation asserted, so drop
            // ours before handing the body over.
            BundyVictimInteractionsBridge.ReleaseStaleVictimLock(victim, "legacy victim playable fallback");
            BundyVictimPlayableController.Begin(
                victim,
                this,
                new[] { fallbackClip },
                BundyVictimAnchorMode.SourcePairRoot,
                networkStartTime,
                pairedOrigin,
                pairedRotation);
        }

        // #321: arm the per-frame separation hold once the lock this client will retarget exists.
        if (started)
            BeginPairedFinisherAlignment(victim, pairedOrigin, pairedActionId, bodyIndex);

        // #321 (C7): one line per finisher per client. Together with the bridge's start-rejection
        // warning this is the acceptance evidence that all three variants play.
        LogInfo(
            $"BundyFinisherVariant: bodyIndex={Mathf.Clamp(bodyIndex, 0, 2)}, " +
            $"interaction='{BundyVictimInteractionsBridge.DescribeInteractionId(bodyIndex)}', " +
            $"clip={GetChainsawFinisherVictimClipId(bodyIndex)}, action='{pairedActionId}', " +
            $"apiStarted={started}, legacyFallback={_chainsawFinisherVictimFallbackLocal}, " +
            $"victim={PlayerLabel(victim)}, isServer={IsServer}.");
        VerifyFinisherAnimatorBranch(Mathf.Clamp(bodyIndex, 0, 2));
    }

    [ClientRpc]
    public void ReleasePairedVictimPlayableClientRpc(int playerIndex, int clipId, string reason, bool endAttacker)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null || playerIndex < 0 || playerIndex >= players.Length)
            return;

        BundyVictimPlayableController.End(players[playerIndex], (BundyVictimClipId)clipId, reason);
        BundyFinisherCollisionSuppressor.RestoreAllForBundy(this, $"legacy paired release: {reason}");
        EndFinisherGoreBloodFX($"legacy paired release: {reason}"); // #322
        // #323: the legacy playable path is a real finisher release on every client, so the gore
        // phases and the engine muffle end here too — otherwise a fallback finisher would leave a
        // muffled saw and a ringing bed behind until the max-duration backstop caught it.
        EndSawCuttingLocal($"legacy paired release: {reason}", IsInterruptedReleaseReason(reason));
        if (endAttacker)
        {
            BundyAttackerPlayableController.EndActive(this, reason);
            ClearPairedActionRootLock();
        }
    }

    [ClientRpc]
    public void ReleaseChainsawVictimInteractionsClientRpc(
        int playerIndex,
        int bodyIndex,
        string reason,
        bool interrupted,
        bool endAttacker)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null || playerIndex < 0 || playerIndex >= players.Length)
            return;

        PlayerControllerB victim = players[playerIndex];
        StopChainsawVictimInteractionsLocal(victim, bodyIndex, reason, interrupted);
        BundyVictimPlayableController.End(victim, GetChainsawFinisherVictimClipId(bodyIndex), reason);
        // #321 (A1): restore on every client, and use RestoreAllForBundy rather than the single
        // pair so a victim who died and respawned (or whose index moved) cannot strand an entry.
        BundyFinisherCollisionSuppressor.RestoreAllForBundy(this, $"chainsaw finisher release: {reason}");
        EndFinisherGoreBloodFX($"chainsaw finisher release: {reason}"); // #322
        if (endAttacker)
        {
            BundyAttackerPlayableController.EndActive(this, reason);
            ClearPairedActionRootLock();
        }

        _activeChainsawFinisherUsesInteractions = false;
        _chainsawFinisherVictimFallbackLocal = false;
        EndSawCuttingLocal($"chainsaw finisher release: {reason}", interrupted); // #297/#323
    }

    private bool TryStartChainsawVictimInteractionsLocal(
        PlayerControllerB victim,
        int bodyIndex,
        double networkStartTime,
        Vector3 pairedOrigin,
        Quaternion pairedRotation,
        string pairedActionId)
    {
        if (victim == null)
            return false;

        bool started = BundyVictimInteractionsBridge.TryStartVictimFinisher(
            victim,
            this,
            bodyIndex,
            pairedOrigin,
            pairedRotation,
            networkStartTime,
            out object handle);
        LogInfo(
            $"Chainsaw victim Interactions local start: player={PlayerLabel(victim)}, " +
            $"bodyIndex={Mathf.Clamp(bodyIndex, 0, 2)}, action='{pairedActionId}', started={started}, " +
            $"networkStart={networkStartTime:F3}, origin={FormatVector(pairedOrigin)}, " +
            $"yaw={pairedRotation.eulerAngles.y:F1}, handle={(handle != null ? handle.ToString() : "<null>")}.");
        return started;
    }

    private void StopChainsawVictimInteractionsLocal(
        PlayerControllerB victim,
        int bodyIndex,
        string reason,
        bool interrupted)
    {
        bool stopped = BundyVictimInteractionsBridge.TryStopVictimFinisher(victim, bodyIndex, interrupted);
        BundyVictimDeathCam.End(victim, this, reason);
        LogInfo(
            $"Chainsaw victim Interactions local stop: player={PlayerLabel(victim)}, " +
            $"bodyIndex={Mathf.Clamp(bodyIndex, 0, 2)}, stopped={stopped}, interrupted={interrupted}, reason={reason}.");
    }

    private static bool IsInterruptedReleaseReason(string reason)
    {
        if (string.IsNullOrEmpty(reason))
            return false;

        return reason.IndexOf("interrupt", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("cancel", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("invalid", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("destroy", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("death", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("cleanup", StringComparison.OrdinalIgnoreCase) >= 0 ||
            reason.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    [ClientRpc]
    public void KillFinisherVictimClientRpc(int playerIndex, Vector3 force)
    {
        KillLocalVictim(playerIndex, force);
    }

    /// <summary>
    /// #322: <paramref name="bodyIndex"/> rides along so every client picks the same wound band and
    /// chute weight as the server did. It is not read from <c>_activeFinisherBodyIndex</c> on the
    /// receiving end because a remote client can see the blood RPC before (or after) its own paired
    /// action state has settled.
    /// </summary>
    [ClientRpc]
    public void PlayFinisherBloodClientRpc(int playerIndex, int bodyIndex)
    {
        PlayFinisherBloodLocal(playerIndex, bodyIndex);
    }

    private bool TryStartFinisherBlood(PlayerControllerB victim, string reason)
    {
        if (_finisherBloodVisualStarted || victim == null)
            return false;

        int playerIndex = GetPlayerIndex(victim);
        int bodyIndex = Mathf.Clamp(_activeFinisherBodyIndex, 0, 2);
        _finisherBloodVisualStarted = true;

        if (IsServer && playerIndex >= 0)
        {
            PlayFinisherBloodClientRpc(playerIndex, bodyIndex);
            if (IsHost)
                PlayFinisherBloodLocal(playerIndex, bodyIndex);
        }
        else if (playerIndex >= 0)
        {
            PlayFinisherBloodLocal(playerIndex, bodyIndex);
        }
        else
        {
            _finisherBloodVisualPlayedLocal = true;
            ApplyFinisherBloodVisuals(victim, this, bodyIndex);
        }

        LogFinisherSyncPhase("blood-start", victim, reason);
        return true;
    }

    private void PlayFinisherBloodLocal(int playerIndex, int bodyIndex)
    {
        if (_finisherBloodVisualPlayedLocal)
            return;

        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null || playerIndex < 0 || playerIndex >= players.Length)
            return;

        PlayerControllerB victim = players[playerIndex];
        if (victim == null || victim.isPlayerDead)
            return;

        _finisherBloodVisualStarted = true;
        _finisherBloodVisualPlayedLocal = true;
        ApplyFinisherBloodVisuals(victim, this, bodyIndex);
    }

    // ----------------------------------------------------------------- #322 gore chute

    /// <summary>
    /// Default-on switch for the BloodFX wound chute. Off falls back to the legacy runtime particle
    /// rig (<see cref="BundyFinisherBloodVfx"/>) rather than to no gore.
    /// </summary>
    private static bool ChainsawFinisherUsesBloodFX =>
        BundyPlugin.ModConfig?.ChainsawFinisherUsesBloodFX?.Value ?? true;

    /// <summary>
    /// Open the sustained BloodFX chute at the blade contact point (#322). Returns false when
    /// BloodFX is disabled or its bundle is missing, which is the caller's cue to run the legacy
    /// particle rig instead.
    /// </summary>
    internal bool TryStartFinisherGoreBloodFX(PlayerControllerB victim, int bodyIndex)
    {
        if (victim == null || !ChainsawFinisherUsesBloodFX || !BundyBloodFXBridge.FinisherGoreReady)
            return false;

        // #276: start each finisher with a fresh victim renderer scan; the pose provider below
        // re-reads the cached set every pulse (see BundyAIBase.GetPlayerBloodRenderers).
        InvalidatePlayerBloodRendererCache();

        int clampedBodyIndex = Mathf.Clamp(bodyIndex, 0, 2);
        PlayerControllerB pinned = victim;
        bool started = BundyBloodFXBridge.BeginFinisherGore(
            GetInstanceID(),
            victim.transform,
            delegate (out Vector3 origin, out Vector3 direction)
            {
                return ResolveFinisherWoundPose(pinned, clampedBodyIndex, out origin, out direction);
            },
            victim,
            ResolveFinisherBodyAnchor(victim, clampedBodyIndex),
            clampedBodyIndex,
            GetFinisherGoreScale(clampedBodyIndex),
            $"bundy finisher body{clampedBodyIndex} on {PlayerLabel(victim)}");

        if (started)
            ApplyFinisherBloodConsequencesLocal(victim, clampedBodyIndex);

        LogInfo(
            $"Finisher gore chute start: player={PlayerLabel(victim)}, bodyIndex={clampedBodyIndex}, " +
            $"started={started}, source=BloodFX.");
        return started;
    }

    private static Transform ResolveFinisherBodyAnchor(PlayerControllerB victim, int bodyIndex)
    {
        if (victim == null)
            return null;

        if (Mathf.Clamp(bodyIndex, 0, 2) == 2)
        {
            if (victim.lowerSpine != null)
                return victim.lowerSpine;
            if (victim.upperSpine != null)
                return victim.upperSpine;
        }
        else
        {
            if (victim.upperSpine != null)
                return victim.upperSpine;
            if (victim.lowerSpine != null)
                return victim.lowerSpine;
        }

        return victim.playerBodyAnimator != null
            ? victim.playerBodyAnimator.transform
            : victim.transform;
    }

    /// <summary>Close this Bundy's chute. Safe to call when none is open, and called from every
    /// finisher exit path for the same reason #321's collision suppressor is.</summary>
    private void EndFinisherGoreBloodFX(string reason)
    {
        BundyBloodFXBridge.EndFinisherGore(GetInstanceID(), reason);
    }

    // ---- client-local finisher teardown ---------------------------------------------------------
    //
    // #321/#322/#323 all establish per-client state from the begin RPC (ignored collider pairs, the
    // gore chute, the cut's audio phases + engine muffle) and all three used to be undone only by a
    // release RPC or by the server's own CompleteAction. Neither reaches a client whose release was
    // dropped, and neither reaches ANY client when Bundy dies mid-finisher — Update early-returns on
    // isEnemyDead and CompleteAction never runs. This is the one trio every such path calls.

    /// <summary>Time this client gives up waiting for a release RPC. Armed from
    /// <c>BeginSawCuttingLocal</c>, disarmed by <c>EndSawCuttingLocal</c>.</summary>
    private float _finisherLocalTeardownAt = float.PositiveInfinity;

    // One second past the saw-cutting bog's own 12s ceiling, so the ordinary audio backstops resolve
    // first and this only ever catches what they do not own.
    private const float FinisherLocalTeardownBackstopSeconds = SawCuttingMaxSeconds + 1f;

    /// <summary>
    /// Undo everything the finisher established on THIS client. A no-op when nothing is in flight,
    /// so every teardown site can call it unconditionally.
    /// </summary>
    /// <param name="stopAudioImmediately">Destruction paths get no further Update, so the bed cannot
    /// be left to fade out under the per-frame driver.</param>
    private void RunFinisherLocalTeardown(string reason, bool stopAudioImmediately = false)
    {
        _finisherLocalTeardownAt = float.PositiveInfinity;
        BundyFinisherCollisionSuppressor.RestoreAllForBundy(this, reason); // #321
        EndFinisherGoreBloodFX(reason);                                    // #322
        EndSawCuttingLocal(reason, interrupted: true);                     // #297/#323
        if (stopAudioImmediately)
            StopFinisherGoreAudioNow(reason);
    }

    /// <summary>
    /// Backstop for a lost release RPC on a remote client: without it the suppression, the chute and
    /// the muffle stay armed until the next action cleanup — which, on a client, never comes.
    /// </summary>
    private void UpdateFinisherLocalTeardownBackstop()
    {
        if (Time.time < _finisherLocalTeardownAt)
            return;

        LogWarning(
            $"Finisher local teardown backstop fired after {FinisherLocalTeardownBackstopSeconds:F0}s; " +
            $"no release reached this client (action='{_activePairedActionId ?? "<none>"}').");
        RunFinisherLocalTeardown("finisher local teardown backstop");
    }

    /// <summary>Live wound pose for the chute. False ends it.</summary>
    private bool ResolveFinisherWoundPose(
        PlayerControllerB victim, int bodyIndex, out Vector3 origin, out Vector3 direction)
    {
        origin = Vector3.zero;
        direction = Vector3.forward;

        if (victim == null || this == null)
            return false;

        origin = ResolveFinisherBloodContactPoint(victim, bodyIndex);
        if (!IsFinite(origin))
            return false;

        direction = ResolveFinisherBloodDirection(origin, victim);
        return true;
    }

    /// <summary>Chute size per variant. A throat opens wider than a belly.</summary>
    private static float GetFinisherGoreScale(int bodyIndex)
    {
        switch (Mathf.Clamp(bodyIndex, 0, 2))
        {
            case 0: return 1.2f;
            case 2: return 0.95f;
            default: return 1.05f;
        }
    }

    private void KillLocalVictim(int playerIndex, Vector3 force)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null || playerIndex < 0 || playerIndex >= players.Length)
            return;

        PlayerControllerB victim = players[playerIndex];
        if (victim == null || victim.isPlayerDead)
            return;

        // Blood visuals: run on ALL clients so every player sees the burst.
        // #322: _activeFinisherBodyIndex is server-only state (the action start sets it), so a
        // remote client reaching this fallback would pass -1 and clamp every variant to the throat
        // band. The action id IS replicated — by both begin RPCs — so derive from it there.
        if (!_finisherBloodVisualPlayedLocal)
            PlayFinisherBloodLocal(playerIndex, ResolveLocalFinisherBodyIndex());

        BundyVictimInteractionsBridge.TryStopVictimFinisher(victim, _activeFinisherBodyIndex, interrupted: false);
        BundyVictimDeathCam.End(victim, this, "finisher victim death");
        // #321 (A1): the ragdoll swap destroys the player's colliders, so the ignore-pairs must be
        // dropped here as well as on release — otherwise a finisher that ends in death (the normal
        // case) leaves Bundy's registry holding a dead pair until the next action cleanup.
        BundyFinisherCollisionSuppressor.RestoreAllForBundy(this, "finisher victim death");
        // #322: the chute ends here and BloodFX's own KillPlayerClientRpc patch takes over with the
        // death burst and floor puddle.
        EndFinisherGoreBloodFX("finisher victim death");
        // #321 (A2): the lock asserts inSpecialInteractAnimation every LateUpdate; dying with it
        // set survives the ragdoll swap and leaves the respawn unable to interact. Clear it (and
        // any lock still re-asserting it) before KillPlayer rather than after.
        BundyVictimInteractionsBridge.ClearVictimSpecialAnimationForDeath(victim, "finisher victim death");

        if (!victim.IsOwner)
            return;

        victim.KillPlayer(force, true, CauseOfDeath.Mauling, 0, Vector3.zero);
    }

    private IEnumerator GuardPlayerKnockback(
        PlayerControllerB victim,
        Vector3 startRootPosition,
        Vector3 knockbackDirection,
        float duration,
        int wallImpactDamage)
    {
        if (!IsLivingPlayer(victim) || !victim.IsOwner)
            yield break;

        knockbackDirection.y = 0f;
        if (knockbackDirection.sqrMagnitude < 0.001f)
            knockbackDirection = victim.transform.position - startRootPosition;
        knockbackDirection.y = 0f;
        if (knockbackDirection.sqrMagnitude < 0.001f)
            knockbackDirection = Vector3.forward;
        knockbackDirection.Normalize();

        Vector3 previousProbe = ThrowImpactProbePosition(victim);
        float startedAt = Time.time;
        bool wallDamageApplied = false;

        while (Time.time - startedAt <= Mathf.Max(0.05f, duration))
        {
            yield return null;

            if (!IsLivingPlayer(victim) || !victim.IsOwner)
                yield break;

            Vector3 currentProbe = ThrowImpactProbePosition(victim);
            if (TryClampPlayerKnockbackSegment(victim, previousProbe, currentProbe, knockbackDirection, out RaycastHit hit, out Vector3 safeRoot))
            {
                PlacePlayerRoot(victim, safeRoot);
                victim.externalForceAutoFade = Vector3.zero;
                if (!wallDamageApplied && wallImpactDamage > 0)
                {
                    victim.DamagePlayer(wallImpactDamage, true, true, CauseOfDeath.Bludgeoning);
                    wallDamageApplied = true;
                }

                LogInfo(
                    $"Knockback guard clamped {PlayerLabel(victim)} before wall; " +
                    $"collider={ColliderLabel(hit.collider)}.");
                yield break;
            }

            previousProbe = currentProbe;
        }
    }

    private bool TryClampPlayerKnockbackSegment(
        PlayerControllerB victim,
        Vector3 previousProbe,
        Vector3 currentProbe,
        Vector3 knockbackDirection,
        out RaycastHit hit,
        out Vector3 safeRoot)
    {
        hit = default;
        safeRoot = victim != null ? victim.transform.position : Vector3.zero;

        Vector3 sweep = currentProbe - previousProbe;
        float sweepDistance = sweep.magnitude;
        if (sweepDistance <= 0.025f)
            return false;

        if (!Physics.SphereCast(
                previousProbe,
                KnockbackGuardSphereRadius,
                sweep / sweepDistance,
                out hit,
                sweepDistance + KnockbackGuardWallPadding,
                GetWorldCollisionMask(),
                QueryTriggerInteraction.Ignore) ||
            !IsThrowImpactDamageSurface(victim, hit.collider))
        {
            return false;
        }

        Vector3 safeProbe = previousProbe + (sweep / sweepDistance) * Mathf.Max(0f, hit.distance - KnockbackGuardWallPadding);
        safeRoot = safeProbe - Vector3.up * 0.8f;
        safeRoot -= knockbackDirection.normalized * 0.03f;
        return true;
    }

    private static void PlacePlayerRoot(PlayerControllerB victim, Vector3 rootPosition)
    {
        if (victim == null)
            return;

        victim.transform.position = rootPosition;
        victim.serverPlayerPosition = victim.transform.localPosition;
        victim.ResetFallGravity();
    }

    private static Vector3 ThrowImpactProbePosition(PlayerControllerB victim)
    {
        return victim != null ? victim.transform.position + Vector3.up * 0.8f : Vector3.zero;
    }

    private bool IsThrowImpactDamageSurface(PlayerControllerB victim, Collider collider)
    {
        if (collider == null)
            return false;

        Transform hitTransform = collider.transform;
        if (hitTransform == null)
            return false;

        if (victim != null && hitTransform.IsChildOf(victim.transform))
            return false;

        if (hitTransform.IsChildOf(transform))
            return false;

        return true;
    }

    private static int GetWorldCollisionMask()
    {
        return StartOfRound.Instance != null
            ? StartOfRound.Instance.collidersAndRoomMaskAndDefault
            : Physics.DefaultRaycastLayers;
    }

    private static string ColliderLabel(Collider collider)
    {
        return collider != null ? collider.name : "<null>";
    }

    // #321 (C8): body index -> the Base Layer state the shipped controller actually entered after
    // SetInteger(FinisherBody, n) + SetTrigger(DoFinisher). The controller lives inside
    // runtime-assets/bundy.bundle and cannot be edited here, but it CAN be proven at runtime: if two
    // different body indices land in the same state with the same clip, the controller has a single
    // finisher state and "only one finisher ever plays" is an authoring defect, not a code defect.
    private static readonly Dictionary<int, string> ObservedFinisherStates = new Dictionary<int, string>();
    private static bool _finisherAnimatorBranchVerdictLogged;

    private void VerifyFinisherAnimatorBranch(int bodyIndex)
    {
        if (creatureAnimator == null || !isActiveAndEnabled || !gameObject.activeInHierarchy)
            return;

        StartCoroutine(VerifyFinisherAnimatorBranchRoutine(Mathf.Clamp(bodyIndex, 0, 2)));
    }

    private IEnumerator VerifyFinisherAnimatorBranchRoutine(int bodyIndex)
    {
        // The trigger is consumed on the next animator evaluation and the transition needs a beat to
        // land, so sampling on the trigger frame would only ever report the outgoing state.
        yield return null;
        yield return new WaitForSeconds(FinisherAnimatorBranchSampleDelaySeconds);

        Animator animator = creatureAnimator;
        if (animator == null || animator.runtimeAnimatorController == null)
            yield break;

        AnimatorStateInfo state = animator.GetCurrentAnimatorStateInfo(0);
        string clipName = "<none>";
        AnimatorClipInfo[] clips = animator.GetCurrentAnimatorClipInfo(0);
        if (clips != null && clips.Length > 0 && clips[0].clip != null)
            clipName = clips[0].clip.name;

        int authoredBody;
        try
        {
            authoredBody = animator.GetInteger(BundyAnimatorParams.FinisherBody);
        }
        catch
        {
            // A controller built before FinisherBody existed throws rather than returning 0.
            authoredBody = int.MinValue;
        }

        string signature = $"{state.shortNameHash}|{clipName}";
        LogInfo(
            $"BundyFinisherAnimatorBranch: bodyIndex={bodyIndex}, " +
            $"FinisherBody={(authoredBody == int.MinValue ? "<parameter missing>" : authoredBody.ToString())}, " +
            $"stateHash={state.shortNameHash}, clip='{clipName}', stateLength={state.length:F2}s.");

        if (authoredBody == int.MinValue)
        {
            LogWarning(
                "Bundy's shipped animator controller has no FinisherBody integer parameter; every finisher " +
                "will play the same state regardless of the selected body index. The controller ships inside " +
                "runtime-assets/bundy.bundle and must be re-authored to branch on it.");
            yield break;
        }

        foreach (KeyValuePair<int, string> observed in ObservedFinisherStates)
        {
            if (observed.Key == bodyIndex || !string.Equals(observed.Value, signature, StringComparison.Ordinal))
                continue;

            if (_finisherAnimatorBranchVerdictLogged)
                yield break;

            _finisherAnimatorBranchVerdictLogged = true;
            LogWarning(
                $"Bundy's shipped animator controller does NOT branch on FinisherBody: bodyIndex={observed.Key} " +
                $"and bodyIndex={bodyIndex} both entered stateHash={state.shortNameHash} clip='{clipName}'. " +
                "The attacker side plays one finisher for every variant; only the victim side varies. " +
                "Fixing this requires re-authoring runtime-assets/bundy.bundle.");
            yield break;
        }

        ObservedFinisherStates[bodyIndex] = signature;
    }

    private static int GetPlayerIndex(PlayerControllerB player)
    {
        PlayerControllerB[] players = StartOfRound.Instance?.allPlayerScripts;
        if (players == null || player == null)
            return -1;

        for (int i = 0; i < players.Length; i++)
        {
            if (players[i] == player)
                return i;
        }

        return -1;
    }

}
