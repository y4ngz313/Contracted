using GameNetcodeStuff;
using Y4NGZCompany.Bootstrap;
using UnityEngine;

namespace Y4NGZCompany.Escalations.Bundy.AI;

// #321: compatibility alignment for victim packs predating pelvisMotionVersion 1.
// Corrected packs retain their authored pelvis motion and use the fixed root pins alone.
//
// The bug this exists to fix. Entry was already correct in the field: the 2026-08-06 run logged
// positionError=0.188m, yawError=4.8deg, appliedRootSnap=0.187m and a clean
// 'victim_sawmiddle' start — and the pair still ended up interpenetrating, with Bundy standing
// inside/behind the victim. Both ROOTS are in fact held perfectly for the whole action:
// MaintainPairedActionRootLock re-pins Bundy every Update frame, and BundyVictimInteractionsLock
// re-pins the victim every LateUpdate at the origin captured at begin. Neither of them holds the
// two ANIMATED bodies apart, because with applyRootMotion off the clips still translate each
// actor's PELVIS inside its own pinned root. A finisher clip authored with Bundy stepping into the
// victim therefore walks his mesh straight through them, and the root pins do not object: the
// entry frame is the only frame the 1.15m root offset describes.
//
// Why the authored anchor data cannot do this job. bundy_paired_actions.json carries per-time
// samples with an anchorDelta, and ApplyPairedPlayableRootCorrection consumes them for non-finisher
// paired actions — but for the three chainsaw finishers the data is unusable:
//   * attackerAnchor is the CONSTANT (0.000, 1.693, -0.018) at every sample of all three actions,
//     i.e. a static rest-pose bone, not a sampled curve. The attacker clips are not even in the
//     shipping bundle any more ("Registered paired attacker playable clips: 0").
//   * victimAnchor moves only in Y (0.93 -> 0.14, the body sinking); its XZ is zero throughout.
//   * every sample reports horizontalDistance = 0.018m, so the file carries no planar separation
//     information at all.
// The metadata was harvested 2026-06-23 from the legacy bundle whose victim clips
// ('chainsaw_Paired_Chainsaw_SawMiddle_Vic' and siblings) the log now reports as MISSING — what
// actually plays is the re-exported Interactions pack. So there is no authored curve to follow,
// which is why the finisher branch of ApplyPairedPlayableRootCorrection returns early and why this
// runs a MEASURED loop instead.
//
// The mechanism. One driver, chosen deliberately: Bundy stays hard-locked (he owns the navmesh, the
// ground resolve and the agent) and the VICTIM pin moves. Each frame the loop measures the real
// planar distance between the two pelvis bones, compares it to the configured separation, and steps
// the victim pin along the pair axis to close the error — rate-limited so it can never snap, and
// clamped in total so a bad measurement can never launch the body across the room. At the entry
// frame the error is ~0 (the entry pose was already right), so this is inert exactly where the
// evidence says nothing was wrong, and only acts once the clips start moving the bodies.
public abstract partial class BundyAIBase
{
    private bool _pairedFinisherAlignmentActive;
    private PlayerControllerB _pairedFinisherAlignmentVictim;
    private Vector3 _pairedFinisherAlignmentOrigin;       // the pin as begin established it
    private Vector3 _pairedFinisherAlignmentPin;          // the pin as this loop has moved it
    private Transform _pairedFinisherAttackerPelvis;
    private Transform _pairedFinisherVictimPelvis;
    // #716 G6: a failed pelvis lookup used to re-run two recursive whole-hierarchy searches on
    // every frame of the finisher. The retry is kept (a bone can appear late when a rig swaps
    // mid-action) but is rate limited; between retries the pair simply holds the fixed entry pin,
    // which is what the unresolved branch already did.
    private float _pairedFinisherPelvisRetryAt;
    private const float PairedFinisherPelvisRetryInterval = 2f;
    private float _pairedFinisherAlignmentStartedAt;
    private float _pairedFinisherAlignmentDuration;
    private int _pairedFinisherAlignmentStage;            // 0/25/50/75/100% diagnostic stages fired
    private bool _pairedFinisherAlignmentBonesWarned;
    private string _pairedFinisherAlignmentActionId;
    private bool _pairedFinisherAlignmentBaselineCaptured;
    private float _pairedFinisherAlignmentTargetForward;
    private float _pairedFinisherAlignmentTargetLateral;

    // The target is the pose the ENTRY established, not a number from the config — the field report
    // is explicit that entry looked right and only the clips broke it, so the entry measurement is
    // the best available description of the authored relative pose. The config value is only a
    // FLOOR under it, for the case where entry itself is already too tight.
    //
    // Captured a beat after begin rather than on the begin frame: the victim has just been
    // teleported onto the pin and its animator has not evaluated the finisher clip yet, so a
    // same-frame measurement would sample the previous pose's bones.
    private const float PairedFinisherAlignmentBaselineDelay = 0.15f;

    // Deliberately small: this is a per-frame nudge, not a placement snap. 2.5 m/s closes the worst
    // observed clip translation inside a third of a second while staying invisible at 60fps.
    private const float PairedFinisherAlignmentMaxSpeed = 2.5f;

    // Below this the pair is already where it should be; correcting inside it would just add jitter
    // to a body being sawed in half.
    private const float PairedFinisherAlignmentDeadZone = 0.02f;

    // Hard ceiling on how far the pin may ever travel from where begin put it. Every input to the
    // loop is a bone position that a broken retarget could report anywhere, so the correction is
    // bounded by construction rather than by trusting the measurement.
    private const float PairedFinisherAlignmentMaxTotal = 1.25f;

    private static bool PairedFinisherAlignmentEnabled =>
        BundyPlugin.ModConfig?.ChainsawFinisherPairAlignment?.Value ?? true;

    private static float PairedFinisherMinPairSeparation =>
        Mathf.Clamp(BundyPlugin.ModConfig?.ChainsawFinisherPairSeparation?.Value ?? 1.25f, 0.6f, 2.5f);

    /// <summary>
    /// Armed from the chainsaw finisher begin — the local branch and the ClientRpc both, because the
    /// victim lock is client-local everywhere and each client must correct the pair it can see.
    /// </summary>
    private void BeginPairedFinisherAlignment(
        PlayerControllerB victim,
        Vector3 pairedOrigin,
        string pairedActionId,
        int bodyIndex)
    {
        EndPairedFinisherAlignment("re-entry");

        // Re-baked clips preserve the authored pelvis trajectory. Holding a constant pelvis
        // separation would counter-animate their approach/lift/drop. The two root pins still
        // remain active; retain this compatibility correction only for the older broken pack.
        if (BundyVictimInteractionsBridge.PreservesAuthoredPelvisMotion)
            return;

        if (!PairedFinisherAlignmentEnabled || victim == null)
            return;

        _pairedFinisherAlignmentActive = true;
        _pairedFinisherAlignmentVictim = victim;
        _pairedFinisherAlignmentOrigin = pairedOrigin;
        _pairedFinisherAlignmentPin = pairedOrigin;
        _pairedFinisherAlignmentStartedAt = Time.time;
        _pairedFinisherAlignmentDuration = ResolvePairedFinisherAlignmentDuration(bodyIndex, pairedActionId);
        _pairedFinisherAlignmentStage = 0;
        _pairedFinisherAlignmentBonesWarned = false;
        _pairedFinisherAlignmentActionId = pairedActionId;
        _pairedFinisherAlignmentBaselineCaptured = false;
        _pairedFinisherAlignmentTargetForward = PairedFinisherMinPairSeparation;
        _pairedFinisherAlignmentTargetLateral = 0f;
        _pairedFinisherAttackerPelvis = ResolveAttackerPelvis();
        _pairedFinisherVictimPelvis = ResolveVictimPelvis(victim);
        _pairedFinisherPelvisRetryAt = Time.time + PairedFinisherPelvisRetryInterval;

        LogPairedFinisherSeparation("begin", 0f);
    }

    private void EndPairedFinisherAlignment(string reason)
    {
        if (!_pairedFinisherAlignmentActive)
        {
            _pairedFinisherAlignmentVictim = null;
            _pairedFinisherVictimPelvis = null;
            return;
        }

        LogPairedFinisherSeparation($"end ({reason})", Vector3.Distance(
            FlattenPairedFinisherPoint(_pairedFinisherAlignmentPin),
            FlattenPairedFinisherPoint(_pairedFinisherAlignmentOrigin)));

        _pairedFinisherAlignmentActive = false;
        _pairedFinisherAlignmentVictim = null;
        _pairedFinisherVictimPelvis = null;
        _pairedFinisherAlignmentActionId = null;
        _pairedFinisherAlignmentStage = 0;
    }

    /// <summary>
    /// Driven from Update on EVERY client (before the owner gate), immediately after
    /// MaintainPairedActionRootLock so it reads the same frame's locked attacker root. Frame rate,
    /// not AI interval: the clips translate the bodies every frame and a 5 Hz correction would read
    /// as the victim ratcheting.
    /// </summary>
    private void MaintainPairedFinisherAlignment()
    {
        if (!_pairedFinisherAlignmentActive)
            return;

        PlayerControllerB victim = _pairedFinisherAlignmentVictim;
        if (!_pairedActionRootLockActive || victim == null || victim.isPlayerDead || isEnemyDead)
        {
            EndPairedFinisherAlignment("lock or actor lost");
            return;
        }

        // The legacy proxy path moves the real body itself; there is no lock origin to retarget.
        if (_chainsawFinisherVictimFallbackLocal)
            return;

        // #716 G6: rate limit the recursive re-search instead of paying it every frame.
        if ((_pairedFinisherAttackerPelvis == null || _pairedFinisherVictimPelvis == null) &&
            Time.time >= _pairedFinisherPelvisRetryAt)
        {
            _pairedFinisherPelvisRetryAt = Time.time + PairedFinisherPelvisRetryInterval;
            if (_pairedFinisherAttackerPelvis == null)
                _pairedFinisherAttackerPelvis = ResolveAttackerPelvis();
            if (_pairedFinisherVictimPelvis == null)
                _pairedFinisherVictimPelvis = ResolveVictimPelvis(victim);
        }

        if (_pairedFinisherAttackerPelvis == null || _pairedFinisherVictimPelvis == null)
        {
            if (!_pairedFinisherAlignmentBonesWarned)
            {
                _pairedFinisherAlignmentBonesWarned = true;
                LogWarning(
                    "Paired finisher alignment disabled for this action: pelvis bones unresolved " +
                    $"(attacker={(_pairedFinisherAttackerPelvis != null)}, victim={(_pairedFinisherVictimPelvis != null)}), " +
                    $"victim={PlayerLabel(victim)}, action='{_pairedFinisherAlignmentActionId ?? "<none>"}'. " +
                    "The pair keeps the fixed entry pin.");
            }
            return;
        }

        Vector3 axis = _pairedActionRootRotation * Vector3.forward;
        axis.y = 0f;
        if (axis.sqrMagnitude < 0.0001f)
            return;
        axis.Normalize();
        Vector3 lateralAxis = Vector3.Cross(Vector3.up, axis);

        Vector3 attackerPelvis = FlattenPairedFinisherPoint(_pairedFinisherAttackerPelvis.position);
        Vector3 victimPelvis = FlattenPairedFinisherPoint(_pairedFinisherVictimPelvis.position);
        if (!IsFinite(attackerPelvis) || !IsFinite(victimPelvis))
            return;

        Vector3 measured = victimPelvis - attackerPelvis;
        float forwardSeparation = Vector3.Dot(measured, axis);
        float lateralSeparation = Vector3.Dot(measured, lateralAxis);

        if (!_pairedFinisherAlignmentBaselineCaptured)
        {
            if (Time.time - _pairedFinisherAlignmentStartedAt < PairedFinisherAlignmentBaselineDelay)
                return;

            _pairedFinisherAlignmentBaselineCaptured = true;
            _pairedFinisherAlignmentTargetForward = Mathf.Max(forwardSeparation, PairedFinisherMinPairSeparation);
            _pairedFinisherAlignmentTargetLateral = lateralSeparation;
            LogPairedFinisherSeparation("baseline", forwardSeparation);
        }

        Vector3 error = axis * (_pairedFinisherAlignmentTargetForward - forwardSeparation) +
            lateralAxis * (_pairedFinisherAlignmentTargetLateral - lateralSeparation);
        float errorMagnitude = error.magnitude;
        if (errorMagnitude > PairedFinisherAlignmentDeadZone)
        {
            float maxStep = PairedFinisherAlignmentMaxSpeed * Mathf.Max(0f, Time.deltaTime);
            Vector3 step = errorMagnitude > maxStep ? error * (maxStep / errorMagnitude) : error;
            Vector3 candidate = _pairedFinisherAlignmentPin + step;

            // Total travel is bounded from the origin begin established, never from the last frame:
            // a per-step clamp alone would let a persistent bad measurement integrate without limit.
            Vector3 fromOrigin = candidate - _pairedFinisherAlignmentOrigin;
            fromOrigin.y = 0f;
            if (fromOrigin.magnitude > PairedFinisherAlignmentMaxTotal)
                fromOrigin = fromOrigin.normalized * PairedFinisherAlignmentMaxTotal;

            _pairedFinisherAlignmentPin = new Vector3(
                _pairedFinisherAlignmentOrigin.x + fromOrigin.x,
                _pairedFinisherAlignmentOrigin.y,
                _pairedFinisherAlignmentOrigin.z + fromOrigin.z);
        }

        // Y is never corrected: the victim's height is the grounded resolve begin made and the
        // presentation lift owns any clearance above it.
        if (!BundyVictimInteractionsBridge.TryUpdateVictimPairedPose(victim, _pairedFinisherAlignmentPin))
        {
            EndPairedFinisherAlignment("victim lock gone");
            return;
        }

        UpdatePairedFinisherAlignmentDiagnostics(forwardSeparation);
    }

    // Five always-on lines per finisher (begin / 25 / 50 / 75 / end). This is the evidence the next
    // playtest needs: whether the pair ever actually got closer than the configured separation, and
    // how much correction it took to hold it.
    private void UpdatePairedFinisherAlignmentDiagnostics(float forwardSeparation)
    {
        if (_pairedFinisherAlignmentDuration <= 0.01f)
            return;

        float fraction = (Time.time - _pairedFinisherAlignmentStartedAt) / _pairedFinisherAlignmentDuration;
        int stage = Mathf.Clamp(Mathf.FloorToInt(fraction * 4f), 0, 3);
        if (stage <= _pairedFinisherAlignmentStage)
            return;

        _pairedFinisherAlignmentStage = stage;
        LogPairedFinisherSeparation($"{stage * 25}%", forwardSeparation);
    }

    private void LogPairedFinisherSeparation(string phase, float measuredForwardSeparation)
    {
        PlayerControllerB victim = _pairedFinisherAlignmentVictim;
        float rootDistance = victim != null
            ? Vector3.Distance(
                FlattenPairedFinisherPoint(transform.position),
                FlattenPairedFinisherPoint(victim.transform.position))
            : -1f;
        float pelvisDistance = _pairedFinisherAttackerPelvis != null && _pairedFinisherVictimPelvis != null
            ? Vector3.Distance(
                FlattenPairedFinisherPoint(_pairedFinisherAttackerPelvis.position),
                FlattenPairedFinisherPoint(_pairedFinisherVictimPelvis.position))
            : -1f;
        Vector3 pinTravel = _pairedFinisherAlignmentPin - _pairedFinisherAlignmentOrigin;
        pinTravel.y = 0f;

        LogInfo(
            $"BundyFinisherSeparation {phase}: victim={PlayerLabel(victim)}, " +
            $"action='{_pairedFinisherAlignmentActionId ?? "<none>"}', " +
            $"target={_pairedFinisherAlignmentTargetForward:F3}m (floor={PairedFinisherMinPairSeparation:F2}m, " +
            $"baselined={_pairedFinisherAlignmentBaselineCaptured}), pelvisPlanar={pelvisDistance:F3}m, " +
            $"forwardComponent={measuredForwardSeparation:F3}m, rootPlanar={rootDistance:F3}m, " +
            $"pinTravel={pinTravel.magnitude:F3}m {FormatVector(pinTravel)}, " +
            $"enabled={PairedFinisherAlignmentEnabled}, fallback={_chainsawFinisherVictimFallbackLocal}.");
    }

    private float ResolvePairedFinisherAlignmentDuration(int bodyIndex, string pairedActionId)
    {
        if (TryResolveFinisherGoreAction(pairedActionId, out BundyPairedActionDefinition action) ||
            TryResolveFinisherGoreAction($"chainsaw.finisher.body{Mathf.Clamp(bodyIndex, 0, 2)}", out action))
        {
            if (action.duration > 0.1f)
                return action.duration;
        }

        return 4f;
    }

    // Bundy's rig calls it 'pelvis' (the same name BundyChainsawHandIK resolves off the canonical
    // Armature). Searched from the animator's own transform first so a stale duplicate under the
    // enemy root cannot win.
    private Transform ResolveAttackerPelvis()
    {
        Transform searchRoot = creatureAnimator != null ? creatureAnimator.transform : transform;
        return FindChildRecursive(searchRoot, "pelvis") ?? FindChildRecursive(transform, "pelvis");
    }

    // Vanilla's player metarig names the hips bone 'spine' — the same mapping
    // BundyVictimPlayableController's retarget table uses ("pelvis" -> "spine").
    private static Transform ResolveVictimPelvis(PlayerControllerB victim)
    {
        return victim != null ? FindChildRecursive(victim.transform, "spine") : null;
    }

    private static Vector3 FlattenPairedFinisherPoint(Vector3 point)
    {
        point.y = 0f;
        return point;
    }
}
