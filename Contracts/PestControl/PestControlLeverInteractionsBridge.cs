using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using BepInEx;
using GameNetcodeStuff;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.PestControl
{
    /// <summary>
    /// Reflection-only consumer of the optional Y4NGZInteractions live-body API, used to play the
    /// authored lever throw on the operator's real body.
    ///
    /// The pack itself is built by <c>Assets/Editor/PestControlLeverAnimBuilder.cs</c> in
    /// <c>C:\Unity\Projects\LethalModAssets_v2</c> (menu Y4NGZ / Pest Control / Build Lever
    /// Animation Pack). That builder names this class as its counterpart, and the parameter, layer
    /// and trigger names below are a contract with it — changing one means changing both.
    ///
    /// Nothing here is required for the trap to work. Every failure path falls back to the legacy
    /// vanilla <c>SA_PullLever</c> route in <see cref="PestControlLeverPlayerAnimation"/>, and says
    /// why exactly once, because a bridge that silently animates nothing is indistinguishable from
    /// a bridge that is working.
    /// </summary>
    internal static class PestControlLeverInteractionsBridge
    {
        private const string PackId = "y4ngz.pestcontrol";

        /// <summary>
        /// The controller selects its clip from an Any-State transition gated on the
        /// <c>Y4NGZ_PestLever_Pushing</c> bool, and the presenter fires the enter trigger and then
        /// calls <c>Animator.Update(0)</c> inside the start call — so the branch is already chosen
        /// by the time <c>TryStartInteraction</c> returns and a bool set afterwards is too late.
        /// Registering the two directions as separate interactions is what makes the choice at
        /// start time: push declares the bool as its <c>activeBool</c> (the presenter raises it
        /// before firing enter), pull declares none and rides the parameter's default of false,
        /// which a fresh controller assignment restores at every session start.
        /// </summary>
        private const string PushInteractionId = "y4ngz.pestcontrol.lever.push";
        private const string PullInteractionId = "y4ngz.pestcontrol.lever.pull";

        private const string BundleFileName = "y4ngz-pestlever.animationbundle";
        private const string ManifestFileName = "y4ngz-pestlever.manifest.json";
        private const string AuthoredInteractionId = "y4ngz.pestcontrol.lever";
        private const string BundleInternalName = "y4ngzpestleveranimations";
        private const string ControllerAssetName = "Y4NGZ_PestLever_PlayerMetarig";
        private const string EnterTrigger = "Y4NGZ_PestLever_Enter";
        private const string ExitTrigger = "Y4NGZ_PestLever_Exit";
        private const string PushingBool = "Y4NGZ_PestLever_Pushing";
        private const string FullBodyLayer = "PestLeverFullBody";

        /// <summary>
        /// The authored first-person arms layer, and the three states inside it. Internal because
        /// this layer has to be driven directly rather than through the manifest's enter/exit
        /// triggers — see <see cref="PestControlLeverPlayerAnimation.CrossFadeFirstPersonArms"/>.
        /// These names are part of the same contract with <c>PestControlLeverAnimBuilder</c> as
        /// the trigger and parameter names above; changing one means changing both.
        /// </summary>
        internal const string FirstPersonArmsLayer = "PestLeverFirstPersonArms";

        internal const string PushState = "Push";
        internal const string PullState = "Pull";
        internal const string NeutralState = "Neutral";

        private static readonly BindingFlags ApiFlags = BindingFlags.Public | BindingFlags.Static;

        private static bool _typesResolved;
        private static bool _resolutionFailureLogged;
        private static bool _packRegistered;
        private static bool _packFailureLogged;
        private static bool _disabledByPackFailure;

        // The Interactions API initializes after this plugin, so an early registration attempt
        // fails with interaction_animation_api_not_initialized. That failure is transient: retry
        // (throttled) instead of permanently falling back to the legacy path.
        private static float _nextRegistrationRetryAt;
        private static bool _transientFailureLogged;

        private static bool _preloadAttempted;
        private static bool? _pushAvailable;
        private static bool? _pullAvailable;
        private static float _exitSeconds = DefaultExitSeconds;

        private const float DefaultExitSeconds = 0.1f;

        /// <summary>
        /// The authored graceful-exit crossfade. The caller has to keep the operator planted and
        /// <c>inSpecialInteractAnimation</c> asserted across it — dropping either the moment the
        /// exit is requested puts vanilla locomotion back on the rig mid-crossfade, which is the
        /// hard cut the exit exists to remove.
        /// </summary>
        internal static float ExitSeconds => _exitSeconds;

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
        private static MethodInfo _tryBeginInteractionExit;
        private static MethodInfo _isInteractionActive;
        private static MethodInfo _tryPreloadInteractionAssets;

        internal static bool IsAvailable
        {
            get
            {
                if (!_packRegistered && !_disabledByPackFailure)
                    TryRegisterLeverPack();

                return _typesResolved && _packRegistered && !_disabledByPackFailure;
            }
        }

        internal static bool TryInitialize()
        {
            if (_typesResolved)
                return true;

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

                // Optional. The graceful exit is what fires Y4NGZ_PestLever_Exit and crossfades back
                // to Neutral instead of snapping; without it the throw still ends, just hard.
                _tryBeginInteractionExit = _apiType.GetMethod(
                    "TryBeginInteractionExit",
                    ApiFlags,
                    null,
                    new[] { _handleType, typeof(string).MakeByRefType() },
                    null);
                _tryPreloadInteractionAssets = _apiType.GetMethod(
                    "TryPreloadInteractionAssets",
                    ApiFlags,
                    null,
                    new[] { typeof(string), typeof(string), typeof(string).MakeByRefType() },
                    null);

                if (_tryRegisterInteractionPack == null ||
                    _tryStartInteraction == null ||
                    _tryStopInteraction == null ||
                    _isInteractionActive == null)
                {
                    LogResolutionFailureOnce("required Y4NGZInteractions API methods were not found");
                    return false;
                }

                _typesResolved = true;
                Log(
                    "Pest lever Interactions bridge resolved LCInteractionAnimationAPI: " +
                    $"api='{_apiType.Assembly.GetName().Name}', gracefulExit=" +
                    $"{(_tryBeginInteractionExit != null ? "available" : "absent")}.");
                return true;
            }
            catch (Exception e)
            {
                LogResolutionFailureOnce($"{e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        internal static bool TryRegisterLeverPack()
        {
            if (_packRegistered)
                return true;

            if (_disabledByPackFailure || Time.realtimeSinceStartup < _nextRegistrationRetryAt)
                return false;

            if (!TryInitialize())
                return false;

            string resolvedBundlePath = ResolveRuntimeAssetPath(BundleFileName);
            if (string.IsNullOrWhiteSpace(resolvedBundlePath) || !File.Exists(resolvedBundlePath))
            {
                MarkPackUnavailableOnce(
                    "lever animation pack missing",
                    $"file='{BundleFileName}' resolvedPath='{resolvedBundlePath}'");
                return false;
            }

            try
            {
                LeverManifest authored = LoadAuthoredManifest();
                _exitSeconds = Value(authored?.body?.exitSeconds, DefaultExitSeconds);
                object pack = CreatePackDefinition(authored, resolvedBundlePath);
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
                            Log(
                                "Pest lever Interactions pack registration deferred; the Interactions API is " +
                                $"not initialized yet (reason={reason}). Will retry until it comes up.");
                        }
                        return false;
                    }

                    MarkPackUnavailableOnce("lever interaction pack registration failed", reason);
                    return false;
                }

                _packRegistered = true;
                _disabledByPackFailure = false;
                Log(
                    $"Registered pest lever Interactions pack: pack='{PackId}', " +
                    $"bundle='{resolvedBundlePath}', interactions=[{PushInteractionId}, {PullInteractionId}].");
                TryPreloadLeverInteractions();
                return true;
            }
            catch (Exception e)
            {
                MarkPackUnavailableOnce("lever interaction pack registration exception", Unwrap(e));
                return false;
            }
        }

        /// <summary>
        /// Warms both directions once the pack registers. Without this the first throw of a session
        /// performs a synchronous bundle load inside the start call; when that stalls or fails the
        /// start is refused and the operator plays nothing. The verdict also doubles as the only
        /// per-direction availability proof the API exposes.
        /// </summary>
        private static void TryPreloadLeverInteractions()
        {
            if (_preloadAttempted || !_packRegistered)
                return;

            _preloadAttempted = true;
            if (_tryPreloadInteractionAssets == null)
            {
                Log(
                    "Pest lever Interactions preload skipped; the resolved Y4NGZInteractions build has no " +
                    "TryPreloadInteractionAssets. Per-direction availability stays unverified.");
                return;
            }

            _pushAvailable = Preload(PushInteractionId);
            _pullAvailable = Preload(PullInteractionId);
        }

        private static bool? Preload(string interactionId)
        {
            try
            {
                object[] args = { PackId, interactionId, null };
                bool preloaded = (bool)_tryPreloadInteractionAssets.Invoke(null, args);
                string reason = args.Length > 2 ? args[2] as string : string.Empty;
                if (preloaded)
                {
                    Log($"Preloaded pest lever interaction '{interactionId}'.");
                    return true;
                }

                if (string.IsNullOrEmpty(reason))
                {
                    // A refusal with no reason is not proof the assets are broken, and banning the
                    // direction on it would disable half the lever for the session.
                    Warn(
                        $"Pest lever interaction preload returned false without a reason: '{interactionId}'. " +
                        "Leaving the direction selectable.");
                    return null;
                }

                Warn(
                    $"Pest lever interaction unavailable: '{interactionId}', reason={reason}. " +
                    "That direction will use the legacy SA_PullLever path.");
                return false;
            }
            catch (Exception e)
            {
                // An exception can be a reflection or type mismatch rather than a broken clip, so
                // leave the verdict unknown rather than banning the direction.
                Warn($"Pest lever interaction preload threw for '{interactionId}': {Unwrap(e)}");
                return null;
            }
        }

        /// <summary>False only when preload positively proved this direction cannot load.</summary>
        internal static bool IsDirectionAvailable(bool pushingDown)
        {
            return (pushingDown ? _pushAvailable : _pullAvailable) ?? true;
        }

        /// <summary>
        /// Starts the authored throw on <paramref name="player"/>. Returns false whenever the legacy
        /// path should run instead; the caller owns that decision and this never falls back itself.
        /// </summary>
        internal static bool TryStartLeverThrow(PlayerControllerB player, bool pushingDown, out object handle)
        {
            handle = null;
            if (player == null || player.isPlayerDead)
                return false;

            if (!TryRegisterLeverPack() || !IsDirectionAvailable(pushingDown))
                return false;

            string interactionId = pushingDown ? PushInteractionId : PullInteractionId;
            try
            {
                object request = Activator.CreateInstance(_requestType);
                SetProperty(request, "Player", player);
                SetProperty(request, "PackId", PackId);
                SetProperty(request, "InteractionId", interactionId);

                object[] args = { request, null, null };
                bool started = (bool)_tryStartInteraction.Invoke(null, args);
                handle = args.Length > 1 ? args[1] : null;
                string reason = args.Length > 2 ? args[2] as string : string.Empty;
                if (!started)
                {
                    // Always logged, naming the interaction and the API's own reason. This line is
                    // the acceptance evidence for a throw that never plays.
                    Warn(
                        $"Pest lever Interactions start rejected for {PlayerLabel(player)}; " +
                        $"interaction='{interactionId}', reason={reason}. Using the legacy path.");
                    if (ShouldDisableAfterStartFailure(reason))
                        MarkPackUnavailableOnce("lever interaction start proved the pack unavailable", reason);

                    handle = null;
                    return false;
                }

                Log(
                    $"Started pest lever throw for {PlayerLabel(player)}; " +
                    $"interaction='{interactionId}', handle={handle}.");
                return true;
            }
            catch (Exception e)
            {
                Warn(
                    $"Pest lever Interactions start exception for {PlayerLabel(player)}; " +
                    $"interaction='{interactionId}': {Unwrap(e)}");
                handle = null;
                return false;
            }
        }

        /// <summary>
        /// Ends a throw. The graceful path fires the authored exit trigger and crossfades back to
        /// Neutral over the manifest's exitSeconds, which is the whole reason the exit no longer
        /// reads as the arm vanishing; a hard stop is only for interruption.
        /// </summary>
        internal static bool TryEndLeverThrow(object handle, bool interrupted)
        {
            if (handle == null || !_typesResolved)
                return false;

            if (!interrupted && _tryBeginInteractionExit != null)
            {
                try
                {
                    object[] args = { handle, null };
                    if ((bool)_tryBeginInteractionExit.Invoke(null, args))
                        return true;

                    string reason = args.Length > 1 ? args[1] as string : string.Empty;
                    Warn($"Pest lever graceful exit refused (reason={reason}); stopping the session instead.");
                }
                catch (Exception e)
                {
                    Warn($"Pest lever graceful exit threw: {Unwrap(e)}. Stopping the session instead.");
                }
            }

            try
            {
                object stopReason = Enum.Parse(_stopReasonType, interrupted ? "Interrupted" : "Requested");
                return (bool)_tryStopInteraction.Invoke(null, new[] { handle, stopReason });
            }
            catch (Exception e)
            {
                Warn($"Pest lever Interactions stop failed for handle={handle}: {Unwrap(e)}");
                return false;
            }
        }

        internal static bool IsActive(object handle)
        {
            if (handle == null || !_typesResolved || _isInteractionActive == null)
                return false;

            try
            {
                return (bool)_isInteractionActive.Invoke(null, new[] { handle });
            }
            catch (Exception e)
            {
                Warn($"Pest lever Interactions active check failed for handle={handle}: {Unwrap(e)}");
                return false;
            }
        }

        // ------------------------------------------------------------ registration payload

        private static object CreatePackDefinition(LeverManifest authored, string resolvedBundlePath)
        {
            object pack = Activator.CreateInstance(_packDefinitionType);
            SetProperty(pack, "PackId", PackId);
            SetProperty(pack, "Version", ContractsBootstrap.PLUGIN_VERSION);
            SetProperty(pack, "AssetRootPath", Path.GetDirectoryName(resolvedBundlePath) ?? string.Empty);

            Array interactions = Array.CreateInstance(_interactionDefinitionType, 2);
            interactions.SetValue(CreateInteractionDefinition(PushInteractionId, true, authored, resolvedBundlePath), 0);
            interactions.SetValue(CreateInteractionDefinition(PullInteractionId, false, authored, resolvedBundlePath), 1);
            SetProperty(pack, "Interactions", interactions);
            return pack;
        }

        private static object CreateInteractionDefinition(
            string interactionId,
            bool pushingDown,
            LeverManifest authored,
            string resolvedBundlePath)
        {
            object definition = Activator.CreateInstance(_interactionDefinitionType);
            SetProperty(definition, "InteractionId", interactionId);
            SetProperty(definition, "PresentationKind", Enum.Parse(_presentationKindType, "BodyWorld"));
            SetProperty(definition, "ManifestJson", BuildManifestJson(interactionId, pushingDown, authored, resolvedBundlePath));
            return definition;
        }

        /// <summary>
        /// Emits the schema-2 payload the API registers. The authored manifest supplies the values;
        /// this rewrites the two fields the runtime contract owns rather than the art pipeline:
        ///
        /// 1. <c>interactionId</c>, because one authored file backs both registered directions.
        /// 2. <c>stopOnVanillaSpecialAnimation</c>, which the builder hardcodes to true. The
        ///    presenter auto-stops a live-body session the instant inSpecialInteractAnimation is
        ///    set, and this interaction deliberately holds that flag for the whole throw — the
        ///    operator is planted and movement is surrendered. Left true, the session would stop on
        ///    its own first tick and the throw would silently play nothing.
        /// </summary>
        private static string BuildManifestJson(
            string interactionId,
            bool pushingDown,
            LeverManifest authored,
            string resolvedBundlePath)
        {
            LeverBodyManifest body = authored?.body;
            string bundleFile = !string.IsNullOrWhiteSpace(resolvedBundlePath)
                ? Path.GetFileName(resolvedBundlePath)
                : BundleFileName;

            return "{" +
                "\"schemaVersion\":2," +
                $"\"interactionId\":\"{JsonString(interactionId)}\"," +
                $"\"durationSeconds\":{JsonFloat(Value(authored?.durationSeconds, PestControlTrapPreview.LeverAnimationSeconds))}," +
                $"\"bundleInternalName\":\"{JsonString(Value(authored?.bundleInternalName, BundleInternalName))}\"," +
                "\"body\":{" +
                    "\"enabled\":true," +
                    $"\"bundleFileName\":\"{JsonString(bundleFile)}\"," +
                    $"\"controllerAssetName\":\"{JsonString(Value(body?.controllerAssetName, ControllerAssetName))}\"," +
                    $"\"activeBool\":\"{JsonString(pushingDown ? PushingBool : string.Empty)}\"," +
                    $"\"enterTrigger\":\"{JsonString(Value(body?.enterTrigger, EnterTrigger))}\"," +
                    $"\"exitTrigger\":\"{JsonString(Value(body?.exitTrigger, ExitTrigger))}\"," +
                    $"\"fullBodyLayer\":\"{JsonString(Value(body?.fullBodyLayer, FullBodyLayer))}\"," +
                    $"\"firstPersonArmsLayer\":\"{JsonString(Value(body?.firstPersonArmsLayer, FirstPersonArmsLayer))}\"," +
                    $"\"startLayerWeight\":{JsonFloat(Value(body?.startLayerWeight, 1f))}," +
                    $"\"exitSeconds\":{JsonFloat(Value(body?.exitSeconds, DefaultExitSeconds))}," +
                    "\"rebuildRigBuilders\":true," +
                    "\"preserveGameplayCamera\":true," +
                    "\"stopOnVanillaSpecialAnimation\":false" +
                "}" +
            "}";
        }

        /// <summary>
        /// Reads the authored manifest if it is present. Absence is not fatal — every field it
        /// supplies has a compiled default that matches the builder — but it is worth saying,
        /// because a manifest that failed to deploy means the bundle beside it probably did too.
        /// </summary>
        private static LeverManifest LoadAuthoredManifest()
        {
            try
            {
                string manifestPath = ResolveRuntimeAssetPath(ManifestFileName);
                if (string.IsNullOrWhiteSpace(manifestPath) || !File.Exists(manifestPath))
                {
                    Warn(
                        $"Pest lever manifest not found (file='{ManifestFileName}', resolvedPath='{manifestPath}'); " +
                        "registering from compiled defaults.");
                    return null;
                }

                LeverManifest manifest = JsonUtility.FromJson<LeverManifest>(File.ReadAllText(manifestPath));
                if (manifest == null)
                {
                    Warn($"Pest lever manifest '{ManifestFileName}' parsed to null; registering from compiled defaults.");
                    return null;
                }

                if (!string.IsNullOrWhiteSpace(manifest.interactionId) &&
                    !string.Equals(manifest.interactionId, AuthoredInteractionId, StringComparison.OrdinalIgnoreCase))
                {
                    Warn(
                        $"Pest lever manifest declares interactionId='{manifest.interactionId}', expected " +
                        $"'{AuthoredInteractionId}'. The pack may have been rebuilt against a different contract.");
                }

                Log(
                    $"Parsed pest lever manifest: file='{ManifestFileName}', controller=" +
                    $"'{manifest.body?.controllerAssetName}', duration={manifest.durationSeconds:0.###}s.");
                return manifest;
            }
            catch (Exception e)
            {
                Warn($"Pest lever manifest read failed: {Unwrap(e)}. Registering from compiled defaults.");
                return null;
            }
        }

        // ------------------------------------------------------------------------- helpers

        private static string Value(string authored, string fallback)
        {
            return !string.IsNullOrWhiteSpace(authored) ? authored : fallback;
        }

        private static float Value(float? authored, float fallback)
        {
            return authored.HasValue && authored.Value > 0f ? authored.Value : fallback;
        }

        private static bool ShouldDisableAfterStartFailure(string reason)
        {
            if (string.IsNullOrEmpty(reason))
                return false;

            return reason.IndexOf("bundle_missing", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("bundle_load_failed", StringComparison.OrdinalIgnoreCase) >= 0 ||
                reason.IndexOf("controller_missing", StringComparison.OrdinalIgnoreCase) >= 0;
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

            string assemblyDirectory = Path.GetDirectoryName(
                typeof(PestControlLeverInteractionsBridge).Assembly.Location);
            if (!string.IsNullOrWhiteSpace(assemblyDirectory))
            {
                string assemblyPath = Path.Combine(assemblyDirectory, fileName);
                if (File.Exists(assemblyPath))
                    return assemblyPath;
            }

            string interactionsPluginPath = Path.Combine(Paths.PluginPath, "y4ngz-Y4NGZInteractions", fileName);
            if (File.Exists(interactionsPluginPath))
                return interactionsPluginPath;

            string pluginRootPath = Path.Combine(Paths.PluginPath, fileName);
            if (File.Exists(pluginRootPath))
                return pluginRootPath;

            string baseDirectoryPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, fileName);
            if (File.Exists(baseDirectoryPath))
                return baseDirectoryPath;

            return companyPluginPath;
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

        private static void LogResolutionFailureOnce(string reason)
        {
            if (_resolutionFailureLogged)
                return;

            _resolutionFailureLogged = true;
            Warn($"Pest lever Interactions bridge unavailable; {reason}. The legacy SA_PullLever path will be used.");
        }

        private static void MarkPackUnavailableOnce(string summary, string detail)
        {
            _packRegistered = false;
            _disabledByPackFailure = true;
            if (_packFailureLogged)
                return;

            _packFailureLogged = true;
            Warn(
                $"Pest lever Interactions bridge unavailable; {summary}. {detail}. " +
                "The legacy SA_PullLever path will be used.");
        }

        private static void Log(string message)
        {
            ContractsBootstrap.Log?.LogInfo("[MoonContracts.PestControl] " + message);
        }

        private static void Warn(string message)
        {
            ContractsBootstrap.Log?.LogWarning("[MoonContracts.PestControl] " + message);
        }

        // Mirrors of the authored schema-2 manifest. Deliberately local: the Company assembly has no
        // compile-time reference to Y4NGZInteractions, and must keep working without it installed.

        [Serializable]
        private sealed class LeverManifest
        {
            public int schemaVersion = 2;
            public string interactionId = AuthoredInteractionId;
            public float durationSeconds = PestControlTrapPreview.LeverAnimationSeconds;
            public string bundleInternalName = BundleInternalName;
            public LeverBodyManifest body = new LeverBodyManifest();
        }

        [Serializable]
        private sealed class LeverBodyManifest
        {
            public bool enabled = true;
            public string bundleFileName = BundleFileName;
            public string controllerAssetName = ControllerAssetName;
            public string enterTrigger = EnterTrigger;
            public string exitTrigger = ExitTrigger;
            public string fullBodyLayer = FullBodyLayer;
            public string firstPersonArmsLayer = FirstPersonArmsLayer;
            public float startLayerWeight = 1f;
            public float exitSeconds = DefaultExitSeconds;
        }
    }
}
