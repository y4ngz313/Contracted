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
    private void LogFacingProbe(string context)
    {
        if (!ShouldLog(BundyLogCategory.Anim, 2))
            return;

        bool isOwnerPath = context.IndexOf("owner", StringComparison.OrdinalIgnoreCase) >= 0;
        float nextAt = isOwnerPath ? _nextFacingProbeAt : _nextFacingProbeClientAt;
        if (Time.time < nextAt)
            return;

        if (isOwnerPath)
            _nextFacingProbeAt = Time.time + 2f;
        else
            _nextFacingProbeClientAt = Time.time + 2f;

        try
        {
            Vector3 rootPos = transform.position;
            Vector3 rootFwd = transform.forward;
            float rootYaw = transform.eulerAngles.y;
            bool actionLocked = IsActionLocked();
            bool pairedRootLock = _pairedActionRootLockActive;

            string agentEnabled = "<n/a>", agentOnNavMesh = "<n/a>", agentUpdatePos = "<n/a>";
            string agentUpdateRot = "<n/a>", agentStopped = "<n/a>";
            string agentVel = "<n/a>", agentDesVel = "<n/a>";
            float agentVelMag = -1f;
            float agentVelYaw = 0f, agentDesVelYaw = 0f;
            if (agent != null)
            {
                agentEnabled = agent.enabled.ToString();
                agentOnNavMesh = (agent.enabled && agent.isOnNavMesh).ToString();
                agentUpdatePos = agent.updatePosition.ToString();
                agentUpdateRot = agent.updateRotation.ToString();
                agentStopped = agent.isStopped.ToString();
                agentVel = FormatVector(agent.velocity);
                agentVelMag = agent.velocity.magnitude;
                if (agent.velocity.sqrMagnitude > 0.0001f)
                    agentVelYaw = Quaternion.LookRotation(agent.velocity.normalized, Vector3.up).eulerAngles.y;
                agentDesVel = FormatVector(agent.desiredVelocity);
                if (agent.desiredVelocity.sqrMagnitude > 0.0001f)
                    agentDesVelYaw = Quaternion.LookRotation(agent.desiredVelocity.normalized, Vector3.up).eulerAngles.y;
            }

            string visualFwd = "<n/a>", visualLocalPos = "<n/a>", visualLocalRot = "<n/a>";
            float visualWorldYaw = 0f;
            if (creatureAnimator != null)
            {
                visualFwd = FormatVector(creatureAnimator.transform.forward);
                visualWorldYaw = creatureAnimator.transform.eulerAngles.y;
                visualLocalPos = FormatVector(creatureAnimator.transform.localPosition);
                visualLocalRot = FormatVector(creatureAnimator.transform.localEulerAngles);
            }

            string clip = "<n/a>", stateHash = "<n/a>", motionInt = "<n/a>";
            if (creatureAnimator != null && creatureAnimator.runtimeAnimatorController != null && creatureAnimator.layerCount > 0)
            {
                try
                {
                    AnimatorClipInfo[] ci = creatureAnimator.GetCurrentAnimatorClipInfo(0);
                    if (ci != null && ci.Length > 0 && ci[0].clip != null)
                        clip = ci[0].clip.name;
                    stateHash = creatureAnimator.GetCurrentAnimatorStateInfo(0).shortNameHash.ToString();
                    motionInt = ReadAnimatorInteger(creatureAnimator, BundyAnimatorParams.MotionState);
                }
                catch { }
            }

            float huntDist = -1f, huntAngle = 0f;
            try
            {
                PlayerControllerB huntTarget = GetCurrentHuntTarget();
                if (IsLivingPlayer(huntTarget))
                {
                    huntDist = Vector3.Distance(transform.position, huntTarget.transform.position);
                    Vector3 dir = (huntTarget.transform.position - transform.position).normalized;
                    huntAngle = Vector3.SignedAngle(transform.forward, dir, Vector3.up);
                }
            }
            catch { }

            float localDist = -1f, localAngle = 0f;
            try
            {
                PlayerControllerB localPlayer =
                    GameNetworkManager.Instance?.localPlayerController ??
                    StartOfRound.Instance?.localPlayerController;
                if (IsLivingPlayer(localPlayer))
                {
                    localDist = Vector3.Distance(transform.position, localPlayer.transform.position);
                    Vector3 dir = (localPlayer.transform.position - transform.position).normalized;
                    localAngle = Vector3.SignedAngle(transform.forward, dir, Vector3.up);
                }
            }
            catch { }

            LogDiagnostic(
                $"BundyFacingProbe[{context}] t={Time.time:F2}: IsOwner={IsOwner}, IsServer={IsServer}, isDead={isEnemyDead}, " +
                $"rootPos={FormatVector(rootPos)}, rootFwd={FormatVector(rootFwd)}, rootYaw={rootYaw:F1}, " +
                $"actionLocked={actionLocked}, pairedRootLock={pairedRootLock}, " +
                $"agentEnabled={agentEnabled}, agentOnNavMesh={agentOnNavMesh}, agentUpdatePos={agentUpdatePos}, " +
                $"agentUpdateRot={agentUpdateRot}, agentStopped={agentStopped}, " +
                $"agentVel={agentVel}, agentVelMag={agentVelMag:F2}, agentVelYaw={agentVelYaw:F1}, " +
                $"agentDesVel={agentDesVel}, agentDesVelYaw={agentDesVelYaw:F1}, " +
                $"visualFwd={visualFwd}, visualWorldYaw={visualWorldYaw:F1}, " +
                $"visualLocalPos={visualLocalPos}, visualLocalRot={visualLocalRot}, " +
                $"clip={clip}, stateHash={stateHash}, motionInt={motionInt}, " +
                $"huntDist={huntDist:F2}, huntAngle={huntAngle:F1}, " +
                $"localPlayerDist={localDist:F2}, localPlayerAngle={localAngle:F1}");
        }
        catch (Exception e)
        {
            LogDiagnostic($"BundyFacingProbe[{context}] exception: {e.GetType().Name}: {e.Message}");
        }
    }

    private void LogMotionStateChangeDiagnostics(int requestedMotionState)
    {
        // Job 1: animator-parameter readback on every locomotion state change (122 lines in run 5).
        if (!ShouldLog(BundyLogCategory.Anim, 2))
            return;

        if (creatureAnimator == null)
            return;

        try
        {
            Animator animator = creatureAnimator;
            string parameterValue = ReadAnimatorInteger(animator, BundyAnimatorParams.MotionState);
            string stateLine = "layer0=<unavailable>";
            string currentClips = "currentClips=<unavailable>";
            string nextClips = "nextClips=<not-in-transition>";

            if (animator.runtimeAnimatorController == null)
            {
                stateLine = "controller=<null>";
                currentClips = "currentClips=<no-controller>";
            }
            else if (animator.layerCount <= 0)
            {
                stateLine = $"layerCount={animator.layerCount}";
                currentClips = "currentClips=<no-layers>";
            }
            else
            {
                AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0);
                bool inTransition = animator.IsInTransition(0);
                stateLine =
                    $"currentShortHash={current.shortNameHash}, currentFullHash={current.fullPathHash}, " +
                    $"normalizedTime={current.normalizedTime:F3}, inTransition={inTransition}";

                if (inTransition)
                {
                    AnimatorStateInfo next = animator.GetNextAnimatorStateInfo(0);
                    stateLine += $", nextShortHash={next.shortNameHash}, nextFullHash={next.fullPathHash}";
                    nextClips = "nextClips=" + FormatClipInfo(animator.GetNextAnimatorClipInfo(0));
                }

                currentClips = "currentClips=" + FormatClipInfo(animator.GetCurrentAnimatorClipInfo(0));
            }

            LogDiagnostic(
                $"MotionState diagnostics requested={requestedMotionState}, parameterAfterSet={parameterValue}, " +
                $"{stateLine}, {currentClips}, {nextClips}, animatorSpeed={animator.speed:F3}, " +
                $"updateMode={animator.updateMode}, cullingMode={animator.cullingMode}, " +
                $"applyRootMotion={animator.applyRootMotion}");
        }
        catch (Exception e)
        {
            LogDiagnostic($"MotionState diagnostics exception: {e.GetType().Name}: {e.Message}");
        }
    }

    private static int PickNoRepeatIndex(int count, int lastValue)
    {
        if (count <= 1)
            return 0;

        int picked = UnityEngine.Random.Range(0, count);
        if (picked == lastValue)
            picked = (picked + 1) % count;

        return picked;
    }

    private static string ReadAnimatorInteger(Animator animator, string parameterName)
    {
        try
        {
            return animator.GetInteger(parameterName).ToString();
        }
        catch (Exception e)
        {
            return "<error:" + e.GetType().Name + ">";
        }
    }

    private static string FormatClipInfo(AnimatorClipInfo[] clipInfos)
    {
        if (clipInfos == null)
            return "<null>";

        if (clipInfos.Length == 0)
            return "count=0";

        var parts = new List<string>(clipInfos.Length);
        for (int i = 0; i < clipInfos.Length; i++)
        {
            AnimationClip clip = clipInfos[i].clip;
            parts.Add($"{i}:{ObjectName(clip)}@{clipInfos[i].weight:F3}");
        }

        return $"count={clipInfos.Length} [{string.Join(", ", parts)}]";
    }

    private static string FormatAnimatorIdentity(Animator animator)
    {
        if (animator == null)
            return "<null>";

        string controller = animator.runtimeAnimatorController != null
            ? animator.runtimeAnimatorController.name
            : "<null>";
        return
            $"'{animator.name}', controller='{controller}', controllerNull={animator.runtimeAnimatorController == null}, " +
            $"enabled={animator.enabled}, layers={animator.layerCount}, speed={animator.speed:F2}, " +
            $"updateMode={animator.updateMode}, cullingMode={animator.cullingMode}, applyRootMotion={animator.applyRootMotion}";
    }

    private static string FormatAnimatorSnapshot(Animator animator)
    {
        if (animator == null)
            return "<null>";

        try
        {
            string controller = animator.runtimeAnimatorController != null
                ? animator.runtimeAnimatorController.name
                : "<null>";
            if (animator.runtimeAnimatorController == null || animator.layerCount <= 0)
            {
                return
                    $"name='{animator.name}', controller='{controller}', controllerNull={animator.runtimeAnimatorController == null}, " +
                    $"enabled={animator.enabled}, layers={animator.layerCount}";
            }

            AnimatorStateInfo current = animator.GetCurrentAnimatorStateInfo(0);
            bool inTransition = animator.IsInTransition(0);
            string currentClips = FormatClipInfo(animator.GetCurrentAnimatorClipInfo(0));
            string nextClips = inTransition ? FormatClipInfo(animator.GetNextAnimatorClipInfo(0)) : "<not-in-transition>";
            return
                $"name='{animator.name}', controller='{controller}', controllerNull={animator.runtimeAnimatorController == null}, " +
                $"enabled={animator.enabled}, stateShortHash={current.shortNameHash}, stateFullHash={current.fullPathHash}, " +
                $"normalizedTime={current.normalizedTime:F3}, inTransition={inTransition}, currentClips={currentClips}, nextClips={nextClips}";
        }
        catch (Exception e)
        {
            return $"<snapshot-error:{e.GetType().Name}:{e.Message}>";
        }
    }

    private static string FormatPairedActionCatalogSummary(string actionId)
    {
        if (string.IsNullOrEmpty(actionId))
            return "action=<none>";

        if (!BundyPairedActionCatalog.TryGet(actionId, out BundyPairedActionDefinition action) || action == null)
            return $"action='{actionId}' missing";

        string attacker = action.attackerSequence != null && action.attackerSequence.Length > 0
            ? string.Join(" -> ", action.attackerSequence)
            : "<empty>";
        string victim = action.victimSequence != null && action.victimSequence.Length > 0
            ? string.Join(" -> ", action.victimSequence)
            : "<empty>";
        string victimIds = action.victimClipIds != null && action.victimClipIds.Length > 0
            ? string.Join(" -> ", action.victimClipIds)
            : "<empty>";

        return
            $"action='{actionId}', attackerSequence=[{attacker}], attackerDuration={action.attackerDuration:F2}s, " +
            $"victimSequence=[{victim}], victimClipIds=[{victimIds}], victimDuration={action.victimDuration:F2}s, " +
            $"duration={action.duration:F2}s, damageClipIndex={action.damageClipIndex}, damageNormalizedTime={action.damageNormalizedTime:F3}";
    }

    private string GetCurrentAnimatorStateName()
    {
        if (creatureAnimator == null || creatureAnimator.runtimeAnimatorController == null || creatureAnimator.layerCount <= 0)
            return "<no-animator>";
        try
        {
            AnimatorStateInfo state = creatureAnimator.GetCurrentAnimatorStateInfo(0);
            AnimatorClipInfo[] clips = creatureAnimator.GetCurrentAnimatorClipInfo(0);
            string clipName = clips != null && clips.Length > 0 && clips[0].clip != null ? clips[0].clip.name : "<no-clip>";
            string stateHash = state.shortNameHash.ToString();
            float normTime = state.normalizedTime;
            return $"{clipName} hash={stateHash} t={normTime:F2}";
        }
        catch { return "<error>"; }
    }

    private static string TurnIndexName(int turnIndex)
    {
        switch (turnIndex)
        {
            case BundyAnimatorParams.TurnLeft90: return "Left90";
            case BundyAnimatorParams.TurnRight90: return "Right90";
            case BundyAnimatorParams.Turn180: return "Turn180";
            default: return "Unknown";
        }
    }

    private float _nextTurnClipDiagnosticLogAt;

    private void LogTurnClipDiagnostics()
    {
        if (!ShouldLog(BundyLogCategory.Anim, 2))
            return;

        if (Time.time < _nextTurnClipDiagnosticLogAt)
            return;

        PlayerControllerB huntTarget = GetCurrentHuntTarget();
        if (!IsLivingPlayer(huntTarget) || IsActionLocked())
            return;

        float distance = Vector3.Distance(transform.position, huntTarget.transform.position);
        if (distance <= StopDistance || distance > TurnClipMaxDistance)
            return;

        _nextTurnClipDiagnosticLogAt = Time.time + 0.5f;

        Vector3 dir = huntTarget.transform.position - transform.position;
        dir.y = 0f;
        float yaw = Vector3.SignedAngle(transform.forward, dir.normalized, Vector3.up);
        string stateName = GetCurrentAnimatorStateName();
        int motionState = creatureAnimator != null ? creatureAnimator.GetInteger(BundyAnimatorParams.MotionState) : -1;
        bool manualFacing = UsesManualTargetFacing && agent != null && !agent.updateRotation;
        bool inTurnState = stateName.IndexOf("Turn", StringComparison.OrdinalIgnoreCase) >= 0;

        // #716 F3: through the Bundy writer (which now carries the DebugLogs master) instead of
        // UnityEngine.Debug; WriteInfoLine supplies the same [VariantName] prefix.
        WriteInfoLine(
            $"TURN_DIAG: yaw={yaw:F1}, dist={distance:F2}, " +
            $"animState={stateName}, motionState={motionState}({MotionStateName(motionState)}), " +
            $"manualFacing={manualFacing}, inTurnState={inTurnState}, " +
            $"nextTurnAllowed={(_nextTurnClipAllowedAt > Time.time ? _nextTurnClipAllowedAt - Time.time : 0f):F2}s");
    }

    private static string MotionStateName(int motionState)
    {
        switch (motionState)
        {
            case BundyAnimatorParams.MotionIdle:
                return "Idle";
            case BundyAnimatorParams.MotionWalk:
                return "Walk";
            case BundyAnimatorParams.MotionRun:
                return "Run";
            default:
                return "Unknown";
        }
    }

    private bool IsAnimatorInTurnState()
    {
        if (creatureAnimator == null || creatureAnimator.runtimeAnimatorController == null || creatureAnimator.layerCount <= 0)
            return false;

        try
        {
            // Check if the current animator clip name contains "Turn".
            // Turn clips are named Chainsaw_TurnLeft90, Chainsaw_TurnRight90, Chainsaw_Turn180.
            AnimatorClipInfo[] clips = creatureAnimator.GetCurrentAnimatorClipInfo(0);
            if (clips != null && clips.Length > 0 && clips[0].clip != null)
            {
                string clipName = clips[0].clip.name;
                if (clipName.IndexOf("Turn", StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
        }
        catch { /* Animator may be in transition or invalid state */ }

        return false;
    }

    private static void ApplyBasicAttackBlood(PlayerControllerB victim)
    {
        if (victim == null || victim.isPlayerDead)
            return;

        try { victim.AddBloodToBody(); } catch { /* signature change tolerated */ }
        try { victim.DropBlood(Vector3.down, true, false); } catch { /* signature change tolerated */ }
        try
        {
            if (victim.bloodParticle != null)
                victim.bloodParticle.Play(withChildren: true);
        }
        catch { /* field may not exist */ }
    }

    // Server-side only: spawns networked floor blood object.
    private static void ApplyFinisherBlood(PlayerControllerB victim)
    {
        if (victim == null || victim.isPlayerDead)
            return;

        try { victim.DropBlood(Vector3.down, true, false); } catch { }
    }

    // Runs on ALL clients (via KillFinisherVictimClientRpc → KillLocalVictim)
    // so every player sees the body decals + particle burst.
    private static void ApplyFinisherBloodVisuals(PlayerControllerB victim, BundyAIBase bundy, int bodyIndex)
    {
        if (victim == null || victim.isPlayerDead)
            return;

        try { victim.AddBloodToBody(); } catch { }
        try { victim.AddBloodToBody(); } catch { }
        try { victim.bleedingHeavily = true; } catch { }
        PlayBloodStreamEffect(victim, bundy, bodyIndex);
    }

    // #322: the during-cut gore is the BloodFX wound chute (HDRP decals + the authored pooled
    // particle prefabs), emitted OUTWARD from the blade contact point and tracking the pair for the
    // length of the cut. The legacy runtime-built particle rig is kept only as the fallback for a
    // disabled BloodFX or a missing bundle, behind the Masked stream and the victim's own particle.
    private static void PlayBloodStreamEffect(PlayerControllerB victim, BundyAIBase bundy, int bodyIndex)
    {
        if (victim == null)
            return;

        if (bundy != null)
        {
            try
            {
                if (bundy.TryStartFinisherGoreBloodFX(victim, bodyIndex))
                    return;
            }
            catch (Exception e)
            {
                if (BundyPlugin.Log != null)
                    BundyPlugin.Log.LogWarning($"[Bundy] BloodFX finisher gore chute failed; using fallback. {e.Message}");
            }

            try
            {
                if (BundyFinisherBloodVfx.Spawn(bundy, victim) != null)
                    return;
            }
            catch (Exception e)
            {
                if (BundyPlugin.Log != null)
                    BundyPlugin.Log.LogWarning($"[Bundy] Runtime directional finisher blood VFX failed; using fallback. {e.Message}");
            }
        }

        ParticleSystem stream = TryGetMaskedBloodStream();
        if (stream != null)
        {
            try
            {
                ParticleSystem instance = UnityEngine.Object.Instantiate(stream, victim.transform);
                instance.transform.localPosition = Vector3.up * 1.2f;
                instance.Play(withChildren: true);
                UnityEngine.Object.Destroy(instance.gameObject, 4f);
                return;
            }
            catch { }
        }

        // Fallback: victim's built-in blood particle
        try
        {
            if (victim.bloodParticle != null)
                victim.bloodParticle.Play(withChildren: true);
        }
        catch { }
    }

    private static ParticleSystem _cachedMaskedBloodStream;
    private static bool _searchedMaskedBloodStream;

    private static ParticleSystem TryGetMaskedBloodStream()
    {
        if (_cachedMaskedBloodStream != null)
            return _cachedMaskedBloodStream;
        if (_searchedMaskedBloodStream)
            return null;
        _searchedMaskedBloodStream = true;

        try
        {
            EnemyType maskedType = null;
            if (RoundManager.Instance != null && RoundManager.Instance.currentLevel != null)
            {
                foreach (SpawnableEnemyWithRarity enemy in RoundManager.Instance.currentLevel.Enemies)
                {
                    if (enemy != null && enemy.enemyType != null &&
                        enemy.enemyType.enemyName.IndexOf("Masked", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        maskedType = enemy.enemyType;
                        break;
                    }
                }
            }

            if (maskedType == null)
                return null;

            MaskedPlayerEnemy maskedPrefab = maskedType.enemyPrefab?.GetComponent<MaskedPlayerEnemy>();
            if (maskedPrefab != null && maskedPrefab.maskFloodParticle != null)
            {
                _cachedMaskedBloodStream = maskedPrefab.maskFloodParticle;
                return _cachedMaskedBloodStream;
            }
        }
        catch { }

        return null;
    }

    private static string PlayerLabel(PlayerControllerB player)
    {
        if (player == null)
            return "<null>";

        return $"{player.playerUsername} (clientId={player.actualClientId})";
    }

    protected static Transform FindChildRecursive(Transform parent, string childName)
    {
        if (parent == null)
            return null;

        for (int i = 0; i < parent.childCount; i++)
        {
            Transform child = parent.GetChild(i);
            if (child.name == childName)
                return child;

            Transform nested = FindChildRecursive(child, childName);
            if (nested != null)
                return nested;
        }

        return null;
    }

    private AudioSource FindAudioSource(string childName)
    {
        Transform child = FindChildRecursive(transform, childName);
        return child != null ? child.GetComponent<AudioSource>() : null;
    }

    private static string ObjectName(UnityEngine.Object value)
    {
        return value != null ? value.name : "<null>";
    }

    private static string FormatVector(Vector3 value)
    {
        return $"({value.x:F3}, {value.y:F3}, {value.z:F3})";
    }

    private static bool IsFinite(Vector3 value)
    {
        return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
    }

    private static bool IsFinite(float value)
    {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }

    // Job 1: will a diagnostic of this category and verbosity print right now? Call this directly at
    // sites where building the message is itself expensive (string interpolation, transform/animator
    // reads, coroutine starts) so the WORK is skipped, not just the write. Sites that only concatenate
    // can pass the message straight to the LogDiagnostic/LogInfo overloads below.
    private static bool ShouldLog(BundyLogCategory category, int verbosity)
    {
        return BundyLogGate.Allows(category, verbosity);
    }

    private void LogDiagnostic(BundyLogCategory category, int verbosity, string message)
    {
        if (!ShouldLog(category, verbosity))
            return;

        WriteInfoLine(message);
    }

    // Unclassified legacy call sites. They keep the shipped default (Core, verbosity 1), so an
    // untouched diagnostic still prints at the default LogVerbosity and goes quiet at 0. Reclassify a
    // site with the overload above rather than adding new callers here.
    private void LogDiagnostic(string message)
    {
        LogDiagnostic(BundyLogCategory.Core, BundyLogGate.DefaultVerbosity, message);
    }

    private void LogInfo(BundyLogCategory category, int verbosity, string message)
    {
        if (!ShouldLog(category, verbosity))
            return;

        WriteInfoLine(message);
    }

    private void LogInfo(string message)
    {
        LogInfo(BundyLogCategory.Core, BundyLogGate.DefaultVerbosity, message);
    }

    /// <summary>
    /// #716 F3. The raw writer. It used to bypass the DebugLogs master entirely, so the call sites
    /// that reach it directly (rather than through LogInfo/LogDiagnostic) put 8-12 Info lines per
    /// finisher per client into the log with diagnostics off. The master gate now applies here too;
    /// the category/verbosity axes stay with the LogInfo/LogDiagnostic overloads above.
    /// </summary>
    private void WriteInfoLine(string message)
    {
        if (!BundyLogGate.MasterEnabled)
            return;

        if (BundyPlugin.Log != null)
            BundyPlugin.Log.LogInfo($"[{VariantName}] {message}");
        else
            Debug.Log($"[{VariantName}] {message}", this);
    }

    private void LogError(string message)
    {
        if (BundyPlugin.Log != null)
            BundyPlugin.Log.LogError($"[{VariantName}] {message}");
        else
            Debug.LogError($"[{VariantName}] {message}", this);
    }

    private void LogWarning(string message)
    {
        if (BundyPlugin.Log != null)
            BundyPlugin.Log.LogWarning($"[{VariantName}] {message}");
        else
            Debug.LogWarning($"[{VariantName}] {message}", this);
    }

    private void WarnOnce(ref bool warned, string message)
    {
        if (warned)
            return;

        warned = true;
        if (BundyPlugin.Log != null)
            BundyPlugin.Log.LogWarning($"[{VariantName}] {message}");
        else
            Debug.LogWarning($"[{VariantName}] {message}", this);
    }

    private void SetAnimatorInteger(string parameterName, int value)
    {
        if (creatureAnimator == null)
        {
            WarnOnce(ref _warnedMissingAnimator, $"Missing creatureAnimator; cannot set int '{parameterName}' to {value}.");
            return;
        }

        creatureAnimator.SetInteger(parameterName, value);
        // Job 1 follow-up (run 6): this and the trigger writer below are raw Debug.Log calls that
        // bypass LogInfo/LogDiagnostic entirely, so the first Job 1 pass never reached them. Together
        // they were 239 of the 1,195 Bundy lines run 6 still printed — 20% of the remainder.
        if (ShouldLog(BundyLogCategory.Anim, 2)) WriteInfoLine($"Animator int {parameterName}={value}.");
    }

    private void SetAnimatorFloat(string parameterName, float value)
    {
        if (creatureAnimator == null)
        {
            WarnOnce(ref _warnedMissingAnimator, $"Missing creatureAnimator; cannot set float '{parameterName}' to {value}.");
            return;
        }

        // Set every frame while moving; no DebugLog here to avoid per-frame console spam.
        creatureAnimator.SetFloat(parameterName, value);
    }

    // #276: hash-id twin for the per-frame float params (Speed/MoveX/MoveY). The name rides along
    // for the missing-animator warning only.
    private void SetAnimatorFloat(int parameterId, string parameterName, float value)
    {
        if (creatureAnimator == null)
        {
            WarnOnce(ref _warnedMissingAnimator, $"Missing creatureAnimator; cannot set float '{parameterName}' to {value}.");
            return;
        }

        creatureAnimator.SetFloat(parameterId, value);
    }

    private bool HasAnimatorFloatParameter(string parameterName, ref int cached)
    {
        if (creatureAnimator == null)
            return false;

        if (cached >= 0)
            return cached == 1;

        cached = 0;
        try
        {
            AnimatorControllerParameter[] parameters = creatureAnimator.parameters;
            if (parameters == null)
                return false;

            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter parameter = parameters[i];
                if (parameter != null &&
                    parameter.type == AnimatorControllerParameterType.Float &&
                    string.Equals(parameter.name, parameterName, StringComparison.Ordinal))
                {
                    cached = 1;
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            if (DebugLogs)
                LogWarning($"Animator parameter probe failed for float '{parameterName}': {e.GetType().Name}: {e.Message}");
        }

        if (DebugLogs)
            LogWarning($"Animator is missing float parameter '{parameterName}'; skipping that locomotion channel.");
        return false;
    }

    // Bool twin of HasAnimatorFloatParameter (Job 5a). Same caching contract: `cached` is -1 until
    // probed, then 0/1. Callers use it to degrade gracefully on an older bundle whose controller predates
    // the parameter rather than spamming Animator warnings every frame.
    private bool HasAnimatorBoolParameter(string parameterName, ref int cached)
    {
        if (creatureAnimator == null)
            return false;

        if (cached >= 0)
            return cached == 1;

        cached = 0;
        try
        {
            AnimatorControllerParameter[] parameters = creatureAnimator.parameters;
            if (parameters == null)
                return false;

            for (int i = 0; i < parameters.Length; i++)
            {
                AnimatorControllerParameter parameter = parameters[i];
                if (parameter != null &&
                    parameter.type == AnimatorControllerParameterType.Bool &&
                    string.Equals(parameter.name, parameterName, StringComparison.Ordinal))
                {
                    cached = 1;
                    return true;
                }
            }
        }
        catch (Exception e)
        {
            if (DebugLogs)
                LogWarning($"Animator parameter probe failed for bool '{parameterName}': {e.GetType().Name}: {e.Message}");
        }

        return false;
    }

    private void SetAnimatorTrigger(string parameterName)
    {
        if (creatureAnimator == null)
        {
            WarnOnce(ref _warnedMissingAnimator, $"Missing creatureAnimator; cannot trigger '{parameterName}'.");
            return;
        }

        creatureAnimator.SetTrigger(parameterName);
        if (ShouldLog(BundyLogCategory.Anim, 2)) WriteInfoLine($"Animator trigger {parameterName}.");
    }
}
