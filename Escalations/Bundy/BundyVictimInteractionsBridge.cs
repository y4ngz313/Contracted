using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using GameNetcodeStuff;
using UnityEngine;
using Unity.Netcode;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.Bundy
{
    internal static class BundyVictimInteractionsBridge
    {
        internal static bool PreservesAuthoredPelvisMotion { get; private set; }

        private const string PackId = "y4ngz.bundy.victim_finishers";
        private const string BodyBundleFileName = "y4ngz-bundy-victim-finishers.animationbundle";
        private const string PackManifestFileName = "y4ngz-bundy-victim-finishers.manifest.json";
        private const string BodyBundleInternalName = "y4ngz-bundy-victim-finishers";
        private const string BodyControllerName = "Y4NGZ_BundyVictim_PlayerMetarig";
        private const string FullBodyLayerName = "BundyVictimFullBody";
        private const string ActiveBoolName = "BundyVictimFinisherActive";
        private const string ExitTriggerName = "BundyVictim_Exit";

        private static readonly BindingFlags ApiFlags = BindingFlags.Public | BindingFlags.Static;
        private static readonly Dictionary<int, ActiveVictimInteraction> ActiveInteractions =
            new Dictionary<int, ActiveVictimInteraction>();

        private static bool _typesResolved;
        private static bool _resolutionFailureLogged;
        private static bool _packRegistered;
        private static bool _packFailureLogged;
        private static bool _disabledByPackFailure;
        // The Interactions API initializes after this plugin, so an early registration attempt
        // fails with interaction_animation_api_not_initialized. That failure is transient: retry
        // (throttled) instead of permanently falling back to the legacy proxy path.
        private static float _nextRegistrationRetryAt;
        private static bool _transientFailureLogged;
        /// <summary>#716 G6. Set once type resolution fails for a reason that cannot change while
        /// the process runs (the Interactions assembly is absent, or the build that is loaded does
        /// not expose the API surface). Without it every <c>IsAvailable</c> read re-entered
        /// <see cref="TryInitialize"/> and walked every loaded assembly seven times - up to three
        /// times per <c>DoAIInterval</c> on installs without Y4NGZInteractions, because only the
        /// "not_initialized" registration branch was throttled.</summary>
        private static bool _typeResolutionAbandoned;
        private static bool _assemblyLoadHookInstalled;
        // #716 G6: retry budget for the exception arm only (see the catch in TryInitialize).
        private const int MaxTypeResolutionExceptionAttempts = 3;
        private static int _typeResolutionExceptionAttempts;

        private static Type _apiType;
        private static Type _packDefinitionType;
        private static Type _interactionDefinitionType;
        private static Type _presentationKindType;
        private static Type _requestType;
        private static Type _handleType;
        private static Type _stopReasonType;
        private static MethodInfo _tryRegisterInteractionPack;
        private static MethodInfo _tryStartInteraction;
        private static MethodInfo _tryStopInteraction;
        private static MethodInfo _isInteractionActive;
        // Preload is an optional optimization. Active-session lookup is required because
        // Interactions 1.0 exposes player cleanup through presentation-scoped handles.
        private static MethodInfo _tryPreloadInteractionAssets;
        private static MethodInfo _tryGetActiveInteraction;

        // #321 (C6): per-body-index preload verdict. Unknown (null entry) means "never probed" and
        // is treated as available; false means preload proved the clip/controller cannot load, so
        // that variant must not be selected — picking it is exactly the "only one finisher ever
        // plays" symptom, because the chosen body index silently animates nothing.
        private static readonly bool?[] VariantAvailable = new bool?[3];
        private static bool _preloadAttempted;

        internal static bool IsAvailable
        {
            get
            {
                if (!ConfigEnabled)
                    return false;

                if (!_packRegistered && !_disabledByPackFailure)
                    TryRegisterVictimPack();

                return _typesResolved && _packRegistered && !_disabledByPackFailure;
            }
        }

        internal static bool ConfigEnabled =>
            BundyPlugin.ModConfig?.ChainsawFinisherVictimUsesInteractionsApi?.Value ?? true;

        internal static bool TryInitialize()
        {
            if (_typesResolved)
                return true;

            if (!ConfigEnabled)
                return false;

            // #716 G6: a definitive miss stays latched for the session; only an Y4NGZInteractions
            // assembly load lifts it (see AbandonTypeResolution).
            if (_typeResolutionAbandoned)
                return false;

            try
            {
                _apiType = FindType("Y4NGZInteractions.InteractionAnimationApi.LCInteractionAnimationAPI");
                _packDefinitionType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationPackDefinition");
                _interactionDefinitionType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationDefinition");
                _presentationKindType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationPresentationKind");
                _requestType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationRequest");
                _handleType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationHandle");
                _stopReasonType = FindType("Y4NGZInteractions.InteractionAnimationApi.InteractionAnimationStopReason");

                if (_apiType == null ||
                    _packDefinitionType == null ||
                    _interactionDefinitionType == null ||
                    _presentationKindType == null ||
                    _requestType == null ||
                    _handleType == null ||
                    _stopReasonType == null)
                {
                    LogResolutionFailureOnce("required Y4NGZInteractions API types were not found");
                    // The assembly is not loaded (or not the expected build); that can still change
                    // if Y4NGZInteractions loads later, so re-arm on AssemblyLoad rather than poll.
                    AbandonTypeResolution(rearmOnAssemblyLoad: true);
                    return false;
                }

                _tryRegisterInteractionPack = _apiType.GetMethod(
                    "TryRegisterInteractionPack",
                    ApiFlags,
                    null,
                    new[] { _packDefinitionType, typeof(string).MakeByRefType() },
                    null);
                _tryStartInteraction = _apiType.GetMethod(
                    "TryStartInteraction",
                    ApiFlags,
                    null,
                    new[] { _requestType, _handleType.MakeByRefType(), typeof(string).MakeByRefType() },
                    null);
                _tryStopInteraction = _apiType.GetMethod(
                    "TryStopInteraction",
                    ApiFlags,
                    null,
                    new[] { _handleType, _stopReasonType },
                    null);
                _isInteractionActive = _apiType.GetMethod(
                    "IsInteractionActive",
                    ApiFlags,
                    null,
                    new[] { _handleType },
                    null);

                _tryPreloadInteractionAssets = _apiType.GetMethod(
                    "TryPreloadInteractionAssets",
                    ApiFlags,
                    null,
                    new[] { typeof(string), typeof(string), typeof(string).MakeByRefType() },
                    null);
                _tryGetActiveInteraction = _apiType.GetMethod(
                    "TryGetActiveInteraction",
                    ApiFlags,
                    null,
                    new[]
                    {
                        typeof(PlayerControllerB),
                        _presentationKindType,
                        _handleType.MakeByRefType()
                    },
                    null);

                if (_tryRegisterInteractionPack == null ||
                    _tryStartInteraction == null ||
                    _tryStopInteraction == null ||
                    _isInteractionActive == null ||
                    _tryGetActiveInteraction == null)
                {
                    LogResolutionFailureOnce("required Y4NGZInteractions API methods were not found");
                    // The types resolved, so the assembly is present but exposes a different API
                    // surface. A second Y4NGZInteractions build will not appear mid-session.
                    AbandonTypeResolution(rearmOnAssemblyLoad: false);
                    return false;
                }

                _typesResolved = true;
                LogInfo(
                    "Bundy victim Interactions bridge resolved LCInteractionAnimationAPI: " +
                    $"api='{_apiType.Assembly.GetName().Name}', packType='{_packDefinitionType.FullName}', " +
                    $"requestType='{_requestType.FullName}', handleType='{_handleType.FullName}'.");
                return true;
            }
            catch (Exception e)
            {
                LogResolutionFailureOnce($"{e.GetType().Name}: {e.Message}");
                // A throw is not proof of a permanent miss the way "the type is not there" is - it
                // can be a half-initialized assembly or a transient reflection failure - but an
                // AssemblyLoad re-arm cannot rescue it either, because the assembly that threw is
                // already loaded and no further load event is coming. So it gets a small retry
                // budget and then latches for good.
                _typeResolutionExceptionAttempts++;
                if (_typeResolutionExceptionAttempts >= MaxTypeResolutionExceptionAttempts)
                    AbandonTypeResolution(rearmOnAssemblyLoad: false);

                return false;
            }
        }

        internal static bool TryRegisterVictimPack()
        {
            if (_packRegistered)
                return true;

            if (!ConfigEnabled || _disabledByPackFailure)
                return false;

            if (Time.realtimeSinceStartup < _nextRegistrationRetryAt)
                return false;

            if (!TryInitialize())
                return false;

            string resolvedBundlePath = ResolveRuntimeAssetPath(BodyBundleFileName);
            if (string.IsNullOrWhiteSpace(resolvedBundlePath) || !File.Exists(resolvedBundlePath))
            {
                MarkPackUnavailableOnce(
                    "victim animation pack missing",
                    $"file='{BodyBundleFileName}' resolvedPath='{resolvedBundlePath}'");
                return false;
            }

            try
            {
                if (!TryLoadPackManifest(out BundyVictimPackManifest manifest, out string manifestReason))
                {
                    MarkPackUnavailableOnce("victim interaction pack manifest unavailable", manifestReason);
                    return false;
                }

                object pack = CreatePackDefinition(manifest, resolvedBundlePath);
                object[] args = { pack, null };
                bool registered = (bool)_tryRegisterInteractionPack.Invoke(null, args);
                string reason = args.Length > 1 ? args[1] as string : string.Empty;
                if (!registered &&
                    string.Equals(reason, "pack_already_registered", StringComparison.OrdinalIgnoreCase))
                {
                    registered = true;
                }

                if (!registered)
                {
                    if (!string.IsNullOrEmpty(reason) &&
                        reason.IndexOf("not_initialized", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        _nextRegistrationRetryAt = Time.realtimeSinceStartup + 5f;
                        if (!_transientFailureLogged)
                        {
                            _transientFailureLogged = true;
                            LogInfo(
                                "Victim Interactions pack registration deferred; the Interactions API is not " +
                                $"initialized yet (reason={reason}). Will retry until it comes up.");
                        }
                        return false;
                    }

                    MarkPackUnavailableOnce("victim interaction pack registration failed", reason);
                    return false;
                }

                _packRegistered = true;
                PreservesAuthoredPelvisMotion = manifest.pelvisMotionVersion >= 1;
                _disabledByPackFailure = false;
                LogInfo(
                    "Registered Bundy victim Interactions pack: " +
                    $"pack='{PackId}', bundle='{resolvedBundlePath}', manifest='{PackManifestFileName}', " +
                    "interactions=[victim_neckslash, victim_sawmiddle, victim_stablift].");
                TryPreloadVictimInteractions();
                return true;
            }
            catch (Exception e)
            {
                MarkPackUnavailableOnce("victim interaction pack registration exception", Unwrap(e));
                return false;
            }
        }

        /// <summary>
        /// #321 (B4): warm every variant once the pack registers. Without this the first finisher of
        /// a session performs a synchronous bundle load inside <c>TryStartInteraction</c>; when that
        /// stalls or fails the start is refused and the victim T-poses for the whole animation.
        /// #321 (C6): the per-interaction verdict doubles as the only per-variant availability proof
        /// the API exposes, so a body index whose clip is missing from the animationbundle can be
        /// excluded from selection instead of being chosen and playing nothing.
        /// </summary>
        internal static void TryPreloadVictimInteractions()
        {
            if (_preloadAttempted || !_packRegistered)
                return;

            _preloadAttempted = true;
            if (_tryPreloadInteractionAssets == null)
            {
                LogInfo(
                    "Bundy victim Interactions preload skipped; the resolved Y4NGZInteractions build has no " +
                    "TryPreloadInteractionAssets. Per-variant availability stays unverified.");
                return;
            }

            for (int bodyIndex = 0; bodyIndex < 3; bodyIndex++)
            {
                string interactionId = GetInteractionId(bodyIndex);
                try
                {
                    object[] args = { PackId, interactionId, null };
                    bool preloaded = (bool)_tryPreloadInteractionAssets.Invoke(null, args);
                    string reason = args.Length > 2 ? args[2] as string : string.Empty;
                    if (preloaded)
                    {
                        VariantAvailable[bodyIndex] = true;
                        LogInfo(
                            $"Preloaded victim finisher variant bodyIndex={bodyIndex}, interaction='{interactionId}'.");
                    }
                    else if (string.IsNullOrEmpty(reason))
                    {
                        // A refusal with no reason is not proof the assets are broken; banning the
                        // variant on it could silently disable all three finishers.
                        VariantAvailable[bodyIndex] = null;
                        LogWarning(
                            $"Victim finisher variant preload returned false without a reason: bodyIndex={bodyIndex}, " +
                            $"interaction='{interactionId}'. Leaving the variant selectable.");
                    }
                    else
                    {
                        VariantAvailable[bodyIndex] = false;
                        LogWarning(
                            $"Victim finisher variant unavailable: bodyIndex={bodyIndex}, " +
                            $"interaction='{interactionId}', reason={reason}. That body index will not be selected.");
                    }
                }
                catch (Exception e)
                {
                    // An exception is not proof the clip is missing (it can be a reflection/type
                    // mismatch), so leave the verdict unknown rather than banning the variant.
                    VariantAvailable[bodyIndex] = null;
                    LogWarning(
                        $"Victim finisher variant preload threw for bodyIndex={bodyIndex}, " +
                        $"interaction='{interactionId}': {Unwrap(e)}");
                }
            }
        }

        /// <summary>#321 (C6): false only when preload positively proved this variant cannot load.</summary>
        internal static bool IsVariantAvailable(int bodyIndex)
        {
            int clamped = Mathf.Clamp(bodyIndex, 0, 2);
            return VariantAvailable[clamped] ?? true;
        }

        /// <summary>#321 (C6): true when at least one body index can actually play.</summary>
        internal static bool IsAnyVariantAvailable()
        {
            for (int i = 0; i < VariantAvailable.Length; i++)
            {
                if (VariantAvailable[i] ?? true)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// #321 (B3): "did the local client actually start this finisher", without the liveness
        /// side effect of <see cref="IsVictimFinisherActive"/>. The impact gate needs the honest
        /// answer to the start question — a client whose <c>TryStartInteraction</c> failed must not
        /// report an active victim animation — but must not abort the finisher merely because the
        /// API session already reached its natural end before the release RPC arrived.
        /// </summary>
        internal static bool IsVictimFinisherTracked(PlayerControllerB victim, int bodyIndex)
        {
            int playerIndex = GetPlayerIndex(victim);
            return playerIndex >= 0 &&
                ActiveInteractions.TryGetValue(playerIndex, out ActiveVictimInteraction active) &&
                active.BodyIndex == Mathf.Clamp(bodyIndex, 0, 2);
        }

        /// <summary>
        /// #321 (alignment): retarget a live victim lock's pinned world pose. The lock captures a
        /// FIXED origin at begin and re-asserts it every LateUpdate, which holds the pair's relative
        /// root transform but nothing about the two ANIMATED bodies — the finisher clips translate
        /// each actor's pelvis inside its own pinned root, and the pair interpenetrates as they do.
        /// The AI's per-frame alignment loop owns that correction and pushes the new pin through
        /// here. Returns false when this player has no Bundy lock (legacy proxy path, or already
        /// released), so the caller can stand down instead of retrying every frame.
        /// </summary>
        internal static bool TryUpdateVictimPairedPose(PlayerControllerB victim, Vector3 pairedOrigin)
        {
            if (victim == null)
                return false;

            var victimLock = victim.GetComponent<BundyVictimInteractionsLock>();
            if (victimLock == null || !victimLock.IsConfigured)
                return false;

            victimLock.UpdatePairedOrigin(pairedOrigin);
            return true;
        }

        /// <summary>
        /// #321 (B3/A2): release any Bundy victim lock still pinning this player. The legacy proxy
        /// fallback moves the real body itself, so a lock left over from a previous finisher would
        /// fight it every LateUpdate and keep <c>inSpecialInteractAnimation</c> asserted.
        /// </summary>
        internal static void ReleaseStaleVictimLock(PlayerControllerB victim, string reason)
        {
            if (victim == null)
                return;

            var victimLock = victim.GetComponent<BundyVictimInteractionsLock>();
            if (victimLock != null)
                victimLock.ReleaseNow(reason);
        }

        /// <summary>
        /// #321 (A2): vanilla's death path is sensitive to a player dying while
        /// <c>inSpecialInteractAnimation</c> is asserted (the flag survives the ragdoll swap and
        /// leaves the respawned body unable to interact). Clear the finisher's assertion — and the
        /// lock that keeps re-asserting it — immediately before <c>KillPlayer</c>.
        /// </summary>
        internal static void ClearVictimSpecialAnimationForDeath(PlayerControllerB victim, string reason)
        {
            if (victim == null)
                return;

            ReleaseStaleVictimLock(victim, reason);

            // The flag is shared with every other Interactions consumer. The API can resolve
            // the player's BodyWorld handle, but the handle does not expose its pack owner. Our own
            // session is always stopped before this runs, so a session still active here belongs to
            // somebody else, and zeroing the flag under it would break their animation. Leave it to
            // its owner and say so; the assertion we are responsible for is the lock, already gone.
            if (IsApiBodyInteractionActive(victim))
            {
                LogWarning(
                    $"Leaving inSpecialInteractAnimation set for {PlayerLabel(victim)} on death ({reason}); " +
                    "another Interactions consumer owns this player's live-body session. Bundy's own " +
                    "victim lock has been released.");
                return;
            }

            victim.inSpecialInteractAnimation = false;
        }

        internal static bool TryStartVictimFinisher(
            PlayerControllerB victim,
            int bodyIndex,
            out object handle)
        {
            handle = null;
            if (victim == null || victim.isPlayerDead)
                return false;

            if (!TryRegisterVictimPack())
                return false;

            int playerIndex = GetPlayerIndex(victim);
            // Remembered because it is the only ownership evidence available below: stopping our own
            // tracked session removes it from ActiveInteractions, after which "is this API session
            // ours" can no longer be answered from our side.
            bool stoppedOwnSession = playerIndex >= 0 &&
                TryStopVictimFinisher(victim, bodyIndex, interrupted: true);

            // #321 (B5): the old code refused outright when inSpecialInteractAnimation was already
            // set. That is a silent T-pose whenever a previous finisher was interrupted without its
            // release reaching this client, and — now that the lock asserts the flag itself (A2) —
            // it would also self-block on a legitimate restart. Recover the state we own before
            // judging it: drop any lock we left behind and stop any BodyWorld session the API still
            // owns on this player. Only a flag we cannot explain still refuses the start.
            if (victim.inSpecialInteractAnimation)
            {
                ReleaseStaleVictimLock(victim, "stale victim lock before finisher start");
                if (IsApiBodyInteractionActive(victim))
                {
                    // TryGetActiveInteraction resolves the player's current BodyWorld handle,
                    // but the API offers no pack-scoped ownership query. Stopping that handle is
                    // still correct — the direct-controller contract requires it before we take the
                    // animator — but a session we did not just stop belongs to another consumer,
                    // and that must not be silent.
                    if (!stoppedOwnSession)
                    {
                        LogWarning(
                            $"Stopping a live-body Interactions session on {PlayerLabel(victim)} that this pack " +
                            $"('{PackId}') does not own, to start a Bundy finisher; the API exposes no " +
                            "pack-scoped stop. Another consumer's animation may be cut short.");
                    }

                    TryStopApiBodyInteraction(victim, interrupted: true);
                }

                if (victim.inSpecialInteractAnimation)
                {
                    LogWarning(
                        $"Victim Interactions finisher start skipped for {PlayerLabel(victim)}; player is in a " +
                        $"special animation this bridge does not own (inAnimationWithEnemy=" +
                        $"{(victim.inAnimationWithEnemy != null ? victim.inAnimationWithEnemy.name : "<null>")}).");
                    return false;
                }

                LogInfo(
                    $"Recovered stale special-animation state for {PlayerLabel(victim)} before finisher start; " +
                    $"bodyIndex={Mathf.Clamp(bodyIndex, 0, 2)}.");
            }

            string interactionId = GetInteractionId(bodyIndex);
            try
            {
                object request = Activator.CreateInstance(_requestType);
                SetProperty(request, "Player", victim);
                SetProperty(request, "PackId", PackId);
                SetProperty(request, "InteractionId", interactionId);

                object[] args = { request, null, null };
                bool started = (bool)_tryStartInteraction.Invoke(null, args);
                handle = args.Length > 1 ? args[1] : null;
                string reason = args.Length > 2 ? args[2] as string : string.Empty;
                if (!started)
                {
                    // #321 (C6/C7): one line, always logged, naming the interaction id and the
                    // API's own reason — this is the acceptance evidence for a variant that never
                    // plays.
                    LogWarning(
                        $"Victim Interactions finisher start rejected for {PlayerLabel(victim)}; " +
                        $"bodyIndex={Mathf.Clamp(bodyIndex, 0, 2)}, interaction='{interactionId}', reason={reason}.");
                    if (IsVariantSpecificStartFailure(reason))
                    {
                        VariantAvailable[Mathf.Clamp(bodyIndex, 0, 2)] = false;
                        LogWarning(
                            $"Victim finisher variant bodyIndex={Mathf.Clamp(bodyIndex, 0, 2)} " +
                            $"('{interactionId}') marked unavailable for the rest of the session; reason={reason}.");
                    }
                    else if (ShouldDisableAfterStartFailure(reason))
                    {
                        MarkPackUnavailableOnce("victim interaction start proved pack unavailable", reason);
                    }

                    handle = null;
                    return false;
                }

                if (playerIndex >= 0)
                {
                    ActiveInteractions[playerIndex] = new ActiveVictimInteraction(
                        victim,
                        null,
                        bodyIndex,
                        handle);
                }

                LogInfo(
                    $"Started victim Interactions finisher for {PlayerLabel(victim)}; " +
                    $"bodyIndex={Mathf.Clamp(bodyIndex, 0, 2)}, interaction='{interactionId}', handle={FormatHandle(handle)}.");
                return true;
            }
            catch (Exception e)
            {
                LogWarning(
                    $"Victim Interactions finisher start exception for {PlayerLabel(victim)}; " +
                    $"bodyIndex={bodyIndex}: {Unwrap(e)}");
                handle = null;
                return false;
            }
        }

        internal static bool TryStartVictimFinisher(
            PlayerControllerB victim,
            EnemyAI bundy,
            int bodyIndex,
            Vector3 pairedOrigin,
            Quaternion pairedRotation,
            double networkStartTime,
            out object handle)
        {
            if (!TryStartVictimFinisher(victim, bodyIndex, out handle))
                return false;

            int playerIndex = GetPlayerIndex(victim);
            if (playerIndex >= 0 && ActiveInteractions.TryGetValue(playerIndex, out ActiveVictimInteraction active))
            {
                BundyVictimInteractionsLock victimLock = victim.GetComponent<BundyVictimInteractionsLock>();
                if (victimLock == null)
                    victimLock = victim.gameObject.AddComponent<BundyVictimInteractionsLock>();

                victimLock.Configure(victim, bundy, bodyIndex, pairedOrigin, pairedRotation, networkStartTime);
                ActiveInteractions[playerIndex] = active.WithLock(victimLock);
            }

            BundyVictimDeathCam.Begin(victim, bundy, ResolveCameraTarget(victim), pairedOrigin, pairedRotation);
            return true;
        }

        internal static bool TryStopVictimFinisher(object handle, bool interrupted)
        {
            if (handle == null)
                return false;

            int playerIndex = FindActiveIndex(handle);
            if (playerIndex >= 0 && ActiveInteractions.TryGetValue(playerIndex, out ActiveVictimInteraction active))
            {
                StopActiveInteraction(playerIndex, active, interrupted, "handle stop");
                return true;
            }

            return TryStopApiHandle(handle, interrupted);
        }

        internal static bool TryStopVictimFinisher(PlayerControllerB victim, int bodyIndex, bool interrupted)
        {
            int playerIndex = GetPlayerIndex(victim);
            if (playerIndex < 0 || !ActiveInteractions.TryGetValue(playerIndex, out ActiveVictimInteraction active))
                return false;

            StopActiveInteraction(playerIndex, active, interrupted, "victim stop");
            return true;
        }

        internal static bool IsVictimFinisherActive(PlayerControllerB victim, int bodyIndex)
        {
            int playerIndex = GetPlayerIndex(victim);
            if (playerIndex < 0 || !ActiveInteractions.TryGetValue(playerIndex, out ActiveVictimInteraction active))
                return false;

            if (active.BodyIndex != Mathf.Clamp(bodyIndex, 0, 2))
                return false;

            if (!IsApiHandleActive(active.Handle))
            {
                StopActiveInteraction(playerIndex, active, interrupted: true, reason: "api inactive");
                return false;
            }

            return true;
        }

        internal static void EndForBundy(EnemyAI bundy, string reason)
        {
            if (bundy == null || ActiveInteractions.Count == 0)
                return;

            var toStop = new List<int>();
            foreach (KeyValuePair<int, ActiveVictimInteraction> pair in ActiveInteractions)
            {
                if (pair.Value.Lock != null && pair.Value.Lock.Bundy == bundy)
                    toStop.Add(pair.Key);
            }

            for (int i = 0; i < toStop.Count; i++)
            {
                if (ActiveInteractions.TryGetValue(toStop[i], out ActiveVictimInteraction active))
                    StopActiveInteraction(toStop[i], active, interrupted: true, reason);
            }
        }

        private static void StopActiveInteraction(
            int playerIndex,
            ActiveVictimInteraction active,
            bool interrupted,
            string reason)
        {
            TryStopApiHandle(active.Handle, interrupted);
            if (active.Lock != null)
                active.Lock.ReleaseNow(reason);
            BundyVictimDeathCam.End(active.Victim, active.Lock != null ? active.Lock.Bundy : null, reason);
            ActiveInteractions.Remove(playerIndex);
            LogInfo(
                $"Stopped victim Interactions finisher for {PlayerLabel(active.Victim)}; " +
                $"bodyIndex={active.BodyIndex}, interrupted={interrupted}, reason={reason}, handle={FormatHandle(active.Handle)}.");
        }

        private static bool TryStopApiHandle(object handle, bool interrupted)
        {
            if (handle == null || !_typesResolved || _tryStopInteraction == null)
                return false;

            try
            {
                object stopReason = Enum.Parse(_stopReasonType, interrupted ? "Interrupted" : "Requested");
                return (bool)_tryStopInteraction.Invoke(null, new[] { handle, stopReason });
            }
            catch (Exception e)
            {
                LogWarning($"Victim Interactions finisher stop failed for handle={FormatHandle(handle)}: {Unwrap(e)}");
                return false;
            }
        }

        private static bool IsApiHandleActive(object handle)
        {
            if (handle == null || !_typesResolved || _isInteractionActive == null)
                return false;

            try
            {
                return (bool)_isInteractionActive.Invoke(null, new[] { handle });
            }
            catch (Exception e)
            {
                LogWarning($"Victim Interactions active check failed for handle={FormatHandle(handle)}: {Unwrap(e)}");
                return false;
            }
        }

        private static bool TryGetApiBodyInteraction(PlayerControllerB victim, out object handle)
        {
            handle = null;
            if (victim == null || !_typesResolved || _tryGetActiveInteraction == null)
                return false;

            try
            {
                object presentationKind = Enum.Parse(_presentationKindType, "BodyWorld");
                object[] args = { victim, presentationKind, null };
                bool active = (bool)_tryGetActiveInteraction.Invoke(null, args);
                handle = args[2];
                return active && handle != null;
            }
            catch (Exception e)
            {
                LogWarning($"Victim Interactions player-active check failed for {PlayerLabel(victim)}: {Unwrap(e)}");
                return false;
            }
        }

        private static bool IsApiBodyInteractionActive(PlayerControllerB victim)
        {
            return TryGetApiBodyInteraction(victim, out _);
        }

        private static bool TryStopApiBodyInteraction(PlayerControllerB victim, bool interrupted)
        {
            if (!TryGetApiBodyInteraction(victim, out object handle))
                return false;

            try
            {
                object stopReason = Enum.Parse(_stopReasonType, interrupted ? "Interrupted" : "Requested");
                return (bool)_tryStopInteraction.Invoke(null, new[] { handle, stopReason });
            }
            catch (Exception e)
            {
                LogWarning($"Victim Interactions player stop failed for {PlayerLabel(victim)}: {Unwrap(e)}");
                return false;
            }
        }

        /// <summary>
        /// #321 (C6): distinguishes "this one interaction's asset is broken" from "the whole pack is
        /// gone". Only the former should ban a single body index; the latter still disables the
        /// bridge so Bundy drops to the legacy proxy path as before.
        /// </summary>
        private static bool IsVariantSpecificStartFailure(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return false;

            return reason.IndexOf("interaction_not_found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("clip_missing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("clip_not_found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("override_clip", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("manifest_invalid", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static int FindActiveIndex(object handle)
        {
            foreach (KeyValuePair<int, ActiveVictimInteraction> pair in ActiveInteractions)
            {
                if (Equals(pair.Value.Handle, handle))
                    return pair.Key;
            }

            return -1;
        }

        private static object CreatePackDefinition(BundyVictimPackManifest manifest, string resolvedBundlePath)
        {
            object pack = Activator.CreateInstance(_packDefinitionType);
            SetProperty(pack, "PackId", PackId);
            SetProperty(pack, "Version", !string.IsNullOrWhiteSpace(manifest?.version) ? manifest.version : BundyPluginInfo.PLUGIN_VERSION);
            SetProperty(pack, "AssetRootPath", Path.GetDirectoryName(resolvedBundlePath) ?? string.Empty);

            Array interactions = Array.CreateInstance(_interactionDefinitionType, 3);
            interactions.SetValue(CreateInteractionDefinition(0, manifest, resolvedBundlePath), 0);
            interactions.SetValue(CreateInteractionDefinition(1, manifest, resolvedBundlePath), 1);
            interactions.SetValue(CreateInteractionDefinition(2, manifest, resolvedBundlePath), 2);
            SetProperty(pack, "Interactions", interactions);
            return pack;
        }

        private static object CreateInteractionDefinition(
            int bodyIndex,
            BundyVictimPackManifest manifest,
            string resolvedBundlePath)
        {
            object definition = Activator.CreateInstance(_interactionDefinitionType);
            string interactionId = GetInteractionId(bodyIndex);
            BundyVictimInteractionManifest entry = FindInteractionEntry(manifest, interactionId);
            SetProperty(definition, "InteractionId", interactionId);
            SetProperty(definition, "PresentationKind", Enum.Parse(_presentationKindType, "BodyWorld"));
            SetProperty(definition, "ManifestJson", BuildManifestJson(bodyIndex, manifest, entry, resolvedBundlePath));
            return definition;
        }

        private static string BuildManifestJson(
            int bodyIndex,
            BundyVictimPackManifest manifest,
            BundyVictimInteractionManifest entry,
            string resolvedBundlePath)
        {
            string interactionId = GetInteractionId(bodyIndex);
            string bundlePath = !string.IsNullOrWhiteSpace(resolvedBundlePath)
                ? Path.GetFileName(resolvedBundlePath)
                : BodyBundleFileName;
            string bundleInternalName = !string.IsNullOrWhiteSpace(manifest?.bundleInternalName)
                ? manifest.bundleInternalName
                : BodyBundleInternalName;
            string slotName = !string.IsNullOrWhiteSpace(entry?.slot) ? entry.slot : GetSlotName(bodyIndex);
            string triggerName = !string.IsNullOrWhiteSpace(entry?.enterTrigger) ? entry.enterTrigger : GetEnterTriggerName(bodyIndex);
            string clipName = !string.IsNullOrWhiteSpace(entry?.clip) ? entry.clip : GetClipName(bodyIndex);
            return "{" +
                "\"schemaVersion\":2," +
                $"\"interactionId\":\"{JsonString(interactionId)}\"," +

                $"\"durationSeconds\":{JsonFloat(GetExpectedDuration(bodyIndex, entry))}," +
                $"\"bundleInternalName\":\"{JsonString(bundleInternalName)}\"," +
                "\"body\":{" +
                    "\"enabled\":true," +
                    $"\"bundleFileName\":\"{JsonString(bundlePath)}\"," +
                    $"\"controllerAssetName\":\"{JsonString(BodyControllerName)}\"," +
                    $"\"activeBool\":\"{JsonString(ActiveBoolName)}\"," +
                    $"\"enterTrigger\":\"{JsonString(triggerName)}\"," +
                    $"\"exitTrigger\":\"{JsonString(ExitTriggerName)}\"," +
                    $"\"fullBodyLayer\":\"{JsonString(FullBodyLayerName)}\"," +
                    "\"firstPersonArmsLayer\":\"\"," +
                    "\"startLayerWeight\":1," +
                    "\"layerWeightRampSeconds\":0," +
                    "\"fullBodyLayerWeight\":1," +
                    "\"rebuildRigBuilders\":true," +
                    "\"preserveGameplayCamera\":true," +
                    "\"stopOnVanillaSpecialAnimation\":false," +
                    "\"clipPack\":{" +
                        "\"enabled\":true," +
                        $"\"bundleFileName\":\"{JsonString(bundlePath)}\"," +
                        $"\"bundleInternalName\":\"{JsonString(bundleInternalName)}\"," +
                        "\"overrides\":[{" +
                            $"\"slot\":\"{JsonString(slotName)}\"," +
                            $"\"clip\":\"{JsonString(clipName)}\"" +
                        "}]" +
                    "}" +
                "}" +
            "}";
        }

        /// <summary>#321 (C7): the interaction id for a body index, for diagnostics outside the bridge.</summary>
        internal static string DescribeInteractionId(int bodyIndex)
        {
            return GetInteractionId(bodyIndex);
        }

        private static string GetInteractionId(int bodyIndex)
        {
            switch (Mathf.Clamp(bodyIndex, 0, 2))
            {
                case 0:
                    return "victim_neckslash";
                case 1:
                    return "victim_sawmiddle";
                default:
                    return "victim_stablift";
            }
        }

        private static string GetSlotName(int bodyIndex)
        {
            switch (Mathf.Clamp(bodyIndex, 0, 2))
            {
                case 0:
                    return "BundyVictim_NeckSlash_Slot";
                case 1:
                    return "BundyVictim_SawMiddle_Slot";
                default:
                    return "BundyVictim_StabLift_Slot";
            }
        }

        private static string GetEnterTriggerName(int bodyIndex)
        {
            switch (Mathf.Clamp(bodyIndex, 0, 2))
            {
                case 0:
                    return "BundyVictim_NeckSlash";
                case 1:
                    return "BundyVictim_SawMiddle";
                default:
                    return "BundyVictim_StabLift";
            }
        }

        private static string GetClipName(int bodyIndex)
        {
            switch (Mathf.Clamp(bodyIndex, 0, 2))
            {
                case 0:
                    return "BundyVictim_NeckSlash";
                case 1:
                    return "BundyVictim_SawMiddle";
                default:
                    return "BundyVictim_StabLift";
            }
        }

        private static float GetExpectedDuration(int bodyIndex, BundyVictimInteractionManifest entry = null)
        {
            if (entry != null && entry.durationSeconds > 0f)
                return entry.durationSeconds;

            switch (Mathf.Clamp(bodyIndex, 0, 2))
            {
                case 0:
                    return 4.0f;
                case 1:
                    return 4.2f;
                default:
                    return 4.4f;
            }
        }

        private static string JsonFloat(float value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        private static string JsonString(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            return value
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t");
        }

        private static bool TryLoadPackManifest(out BundyVictimPackManifest manifest, out string reason)
        {
            manifest = null;
            reason = string.Empty;

            try
            {
                string manifestPath = ResolveRuntimeAssetPath(PackManifestFileName);
                if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                {
                    reason = $"file='{PackManifestFileName}' resolvedPath='{manifestPath}'";
                    return false;
                }

                string json = File.ReadAllText(manifestPath);
                manifest = JsonUtility.FromJson<BundyVictimPackManifest>(json);
                if (manifest == null)
                {
                    reason = "manifest_json_returned_null";
                    return false;
                }

                if (!string.Equals(manifest.packId, PackId, StringComparison.OrdinalIgnoreCase))
                {
                    reason = $"manifest_pack_id_mismatch:'{manifest.packId}'";
                    return false;
                }

                if (manifest.interactions == null || manifest.interactions.Length < 3)
                {
                    reason = "manifest_missing_required_interactions";
                    return false;
                }

                for (int i = 0; i < 3; i++)
                {
                    string interactionId = GetInteractionId(i);
                    BundyVictimInteractionManifest entry = FindInteractionEntry(manifest, interactionId);
                    if (entry == null)
                    {
                        reason = $"manifest_missing_interaction:'{interactionId}'";
                        return false;
                    }
                }

                LogInfo(
                    "Parsed Bundy victim Interactions manifest: " +
                    $"file='{PackManifestFileName}', pack='{manifest.packId}', bundle='{manifest.bundleFileName}', " +
                    $"internalName='{manifest.bundleInternalName}'.");
                return true;
            }
            catch (Exception e)
            {
                reason = Unwrap(e);
                return false;
            }
        }

        private static BundyVictimInteractionManifest FindInteractionEntry(
            BundyVictimPackManifest manifest,
            string interactionId)
        {
            BundyVictimInteractionManifest[] interactions = manifest?.interactions;
            if (interactions == null)
                return null;

            for (int i = 0; i < interactions.Length; i++)
            {
                BundyVictimInteractionManifest entry = interactions[i];
                if (entry != null &&
                    string.Equals(entry.interactionId, interactionId, StringComparison.OrdinalIgnoreCase))
                {
                    return entry;
                }
            }

            return null;
        }

        private static Transform ResolveCameraTarget(PlayerControllerB victim)
        {
            if (victim == null)
                return null;

            return victim.lowerSpine != null
                ? victim.lowerSpine
                : (victim.playerBodyAnimator != null ? victim.playerBodyAnimator.transform : victim.transform);
        }

        private static Type FindType(string fullName)
        {
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            for (int i = 0; i < assemblies.Length; i++)
            {
                Type type = assemblies[i].GetType(fullName, throwOnError: false);
                if (type != null)
                    return type;
            }

            return null;
        }

        private static void SetProperty(object target, string propertyName, object value)
        {
            PropertyInfo property = target?.GetType().GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public);
            if (property != null && property.CanWrite)
                property.SetValue(target, value, null);
        }

        private static string ResolveRuntimeAssetPath(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
                return string.Empty;

            if (Path.IsPathRooted(fileName))
                return fileName;

            string companyPluginPath = Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZCompany", fileName);
            if (File.Exists(companyPluginPath))
                return companyPluginPath;

            string assemblyDirectory = Path.GetDirectoryName(typeof(BundyVictimInteractionsBridge).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                string assemblyPath = Path.Combine(assemblyDirectory, fileName);
                if (File.Exists(assemblyPath))
                    return assemblyPath;
            }

            string pluginPath = Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZInteractions", fileName);
            if (File.Exists(pluginPath))
                return pluginPath;

            string pluginRootPath = Path.Combine(Paths.PluginPath, fileName);
            if (File.Exists(pluginRootPath))
                return pluginRootPath;

            string baseDirectoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (File.Exists(baseDirectoryPath))
                return baseDirectoryPath;

            return companyPluginPath;
        }

        private static bool ShouldDisableAfterStartFailure(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return false;

            return reason.IndexOf("bundle_missing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("bundle_load_failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("controller_missing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("clip_pack", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>#716 G6. Stops the per-evaluation assembly walk for a resolution failure that
        /// cannot resolve itself. <paramref name="rearmOnAssemblyLoad"/> is true only for "the
        /// Interactions assembly is not loaded yet": a single AppDomain.AssemblyLoad subscription
        /// (installed once, never per attempt) lifts the latch instead of polling. Mirrors
        /// CCTVOperatorInteractionsBridge.AbandonRegistration.</summary>
        private static void AbandonTypeResolution(bool rearmOnAssemblyLoad)
        {
            _typeResolutionAbandoned = true;
            if (!rearmOnAssemblyLoad || _assemblyLoadHookInstalled)
                return;

            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoadedWhileAbandoned;
                _assemblyLoadHookInstalled = true;
            }
            catch
            {
                // Best effort: without the hook the bridge simply stays on the legacy path for the
                // session, which is where the un-latched retry loop ended up anyway.
            }
        }

        private static void OnAssemblyLoadedWhileAbandoned(object sender, AssemblyLoadEventArgs args)
        {
            if (!_typeResolutionAbandoned || _typesResolved)
                return;

            string name = args?.LoadedAssembly?.GetName()?.Name;
            if (name == null || name.IndexOf("Y4NGZInteractions", StringComparison.OrdinalIgnoreCase) < 0)
                return;

            _typeResolutionAbandoned = false;
            _typeResolutionExceptionAttempts = 0;
            _resolutionFailureLogged = false;
            _nextRegistrationRetryAt = 0f;
        }

        private static void LogResolutionFailureOnce(string reason)
        {
            if (_resolutionFailureLogged)
                return;

            _resolutionFailureLogged = true;
            LogWarning($"Bundy victim Interactions bridge unavailable; {reason}.");
        }

        private static void MarkPackUnavailableOnce(string summary, string detail)
        {
            _packRegistered = false;
            _disabledByPackFailure = true;
            if (_packFailureLogged)
                return;

            _packFailureLogged = true;
            LogWarning(
                $"Bundy victim Interactions bridge unavailable; {summary}. " +
                $"{detail}. Legacy ChainsawFinisherPresentationMode path will be used.");
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

        private static string FormatHandle(object handle)
        {
            return handle != null ? handle.ToString() : "<null>";
        }

        private static string PlayerLabel(PlayerControllerB player)
        {
            return player == null
                ? "<null>"
                : $"{player.playerUsername} (clientId={player.actualClientId})";
        }

        private static string Unwrap(Exception e)
        {
            Exception inner = e is TargetInvocationException && e.InnerException != null ? e.InnerException : e;
            return $"{inner.GetType().Name}: {inner.Message}";
        }

        private static void LogInfo(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogInfo("[Bundy] " + message);
        }

        private static void LogWarning(string message)
        {
            if (BundyPlugin.Log != null)
                BundyPlugin.Log.LogWarning("[Bundy] " + message);
        }

        private sealed class ActiveVictimInteraction
        {
            internal readonly PlayerControllerB Victim;
            internal readonly BundyVictimInteractionsLock Lock;
            internal readonly int BodyIndex;
            internal readonly object Handle;

            internal ActiveVictimInteraction(
                PlayerControllerB victim,
                BundyVictimInteractionsLock victimLock,
                int bodyIndex,
                object handle)
            {
                Victim = victim;
                Lock = victimLock;
                BodyIndex = Mathf.Clamp(bodyIndex, 0, 2);
                Handle = handle;
            }

            internal ActiveVictimInteraction WithLock(BundyVictimInteractionsLock victimLock)
            {
                return new ActiveVictimInteraction(Victim, victimLock, BodyIndex, Handle);
            }
        }

        [Serializable]
        private sealed class BundyVictimPackManifest
        {
            public string packId = string.Empty;
            public string version = string.Empty;
            public int pelvisMotionVersion = 0;
            public string bundleFileName = BodyBundleFileName;
            public string bundleInternalName = BodyBundleInternalName;
            public BundyVictimInteractionManifest[] interactions = new BundyVictimInteractionManifest[0];
        }

        [Serializable]
        private sealed class BundyVictimInteractionManifest
        {
            public string interactionId = string.Empty;
            public float durationSeconds = 0f;
            public string clip = string.Empty;
            public string slot = string.Empty;
            public string enterTrigger = string.Empty;
        }

        [DefaultExecutionOrder(10000)]
        private sealed class BundyVictimInteractionsLock : MonoBehaviour
        {
            private PlayerControllerB _player;
            private EnemyAI _bundy;
            private int _bodyIndex;
            private Vector3 _pairedOrigin;
            private Quaternion _pairedRotation;
            private bool _configured;
            private BundyFinisherPlaybackSync _playback;
            private bool _savedInSpecialInteractAnimation;
            private bool _savedSnapToServerPosition;
            private bool _savedDisableMoveInput;
            private bool _savedDisableLookInput;
            private bool _savedDisableSyncInAnimation;
            private bool _savedFreeRotationInInteractAnimation;
            private EnemyAI _savedInAnimationWithEnemy;

            internal EnemyAI Bundy => _bundy;

            internal bool IsConfigured => _configured && _player != null;

            /// <summary>
            /// #321 (alignment): move the pinned origin without touching any of the saved vanilla
            /// player state. Rotation is deliberately NOT retargetable — the victim's facing is the
            /// authored 180-degree lock and re-solving it per frame would make the body swivel.
            /// </summary>
            internal void UpdatePairedOrigin(Vector3 pairedOrigin)
            {
                if (!_configured)
                    return;

                _pairedOrigin = pairedOrigin;
            }

            internal void Configure(
                PlayerControllerB player,
                EnemyAI bundy,
                int bodyIndex,
                Vector3 pairedOrigin,
                Quaternion pairedRotation,
                double networkStartTime)
            {
                if (_configured)
                    RestorePlayer("reconfigured");

                _player = player;
                _bundy = bundy;
                _bodyIndex = Mathf.Clamp(bodyIndex, 0, 2);
                _pairedOrigin = pairedOrigin;
                _pairedRotation = FlattenRotation(pairedRotation);
                if (_player == null)
                    return;

                _savedInSpecialInteractAnimation = _player.inSpecialInteractAnimation;
                _savedSnapToServerPosition = _player.snapToServerPosition;
                _savedDisableMoveInput = _player.disableMoveInput;
                _savedDisableLookInput = _player.disableLookInput;
                _savedDisableSyncInAnimation = _player.disableSyncInAnimation;
                _savedFreeRotationInInteractAnimation = _player.freeRotationInInteractAnimation;
                _savedInAnimationWithEnemy = _player.inAnimationWithEnemy;
                _configured = true;
                LockNow();
                if (BundyFinisherPlaybackSync.TryCreate(bundy.creatureAnimator, player.playerBodyAnimator,
                    bodyIndex, networkStartTime, out _playback, out string syncReason))
                    LogInfo("BundyFinisherClock bound: " + syncReason);
                else
                    LogWarning("BundyFinisherClock unavailable; retaining trigger playback: " + syncReason);
            }

            internal void ReleaseNow(string reason)
            {
                RestorePlayer(reason);
            }

            private void Update()
            {
                if (!_configured || _playback == null)
                    return;
                if (_player == null || _bundy == null || _player.isPlayerDead || _bundy.isEnemyDead)
                {
                    _playback = null;
                    return;
                }
                double now = NetworkManager.Singleton != null
                    ? NetworkManager.Singleton.ServerTime.Time : Time.timeAsDouble;
                if (!_playback.Tick(now))
                    _playback = null;
            }

            private void LateUpdate()
            {
                if (!_configured)
                    return;

                if (_player == null || _bundy == null || _bundy.isEnemyDead || _player.isPlayerDead)
                {
                    RestorePlayer("actor invalid");
                    return;
                }

                LockNow();
            }

            private void OnDestroy()
            {
                RestorePlayer("lock destroyed");
            }

            private void LockNow()
            {
                if (!_configured || _player == null)
                    return;

                // #321 (A2): the lock pinned the transform every LateUpdate but never asserted the
                // flag vanilla uses to stand down, so PlayerControllerB.Update kept running gravity
                // and CharacterController.Move against the pin — the pair visibly shoving each
                // other. Asserting it also gates interact/weapons/item-drops for the execution,
                // which is exactly the wanted behaviour while the victim is being cut in half. It
                // is asserted here rather than before the API start so it can never self-block the
                // bridge's own stale-state check (B5).
                _player.inSpecialInteractAnimation = true;
                _player.snapToServerPosition = true;
                _player.disableMoveInput = true;
                _player.disableLookInput = false;
                _player.disableSyncInAnimation = true;
                _player.freeRotationInInteractAnimation = true;
                _player.inAnimationWithEnemy = _bundy;
                _player.transform.SetPositionAndRotation(_pairedOrigin, _pairedRotation);
                _player.serverPlayerPosition = _player.transform.localPosition;
                _player.ResetFallGravity();
            }

            private void RestorePlayer(string reason)
            {
                _playback = null;
                if (!_configured || _player == null)
                    return;

                try
                {
                    _player.inSpecialInteractAnimation = _savedInSpecialInteractAnimation;
                    _player.snapToServerPosition = _savedSnapToServerPosition;
                    _player.disableMoveInput = _savedDisableMoveInput;
                    _player.disableLookInput = _savedDisableLookInput;
                    _player.disableSyncInAnimation = _savedDisableSyncInAnimation;
                    _player.freeRotationInInteractAnimation = _savedFreeRotationInInteractAnimation;
                    if (_savedInAnimationWithEnemy != _bundy)
                        _player.inAnimationWithEnemy = _savedInAnimationWithEnemy;
                    else if (_player.inAnimationWithEnemy == _bundy)
                        _player.inAnimationWithEnemy = null;

                    _player.serverPlayerPosition = _player.transform.localPosition;
                    _player.ResetFallGravity();
                    LogInfo(
                        $"Restored victim Interactions lock for {PlayerLabel(_player)}; " +
                        $"bodyIndex={_bodyIndex}, reason={reason}.");
                }
                catch (Exception e)
                {
                    LogWarning($"Victim Interactions lock restore failed for {PlayerLabel(_player)}: {e.GetType().Name}: {e.Message}");
                }
                finally
                {
                    _configured = false;
                    _player = null;
                    _bundy = null;
                }
            }

            private static Quaternion FlattenRotation(Quaternion rotation)
            {
                return Quaternion.Euler(0f, rotation.eulerAngles.y, 0f);
            }
        }
    }
}
