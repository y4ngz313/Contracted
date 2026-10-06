using System;
using System.Reflection;
using UnityEngine;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Escalations.BlacksiteRetrieval
{
    /// <summary>
    /// Soft bridge onto <c>Y4NGZUpgrades.Weapons.WeaponHostingApi</c> (#316), which lets a blacksite
    /// soldier carry, fire and drop the MX16A4 when that pack is installed. The type keeps its
    /// namespace but not its assembly: it shipped in <c>Y4NGZUpgrades.dll</c> (Upgrades #168) and now
    /// lives in <c>BetterArmory.dll</c> (Upgrades #266), so it is found by scanning what is loaded,
    /// never by an assembly-qualified name.
    ///
    /// <b>Reflection is not a stylistic choice here.</b> BetterArmory and Y4NGZUpgrades both declare
    /// this plugin as a dependency, so we load FIRST: a <c>BepInDependency</c> would be a cycle, an
    /// assembly reference would make the pack mandatory, and a Chainloader probe in <c>Awake</c>
    /// would answer "absent" for a mod that is about to load. Detection is therefore lazy at call
    /// time, on the same retry-throttled shape as <see cref="ShipSystems.Layout.TechnicianUpgradeBridge"/>.
    ///
    /// Every member returns a neutral default on any failure and the bridge latches permanently inert
    /// once the pack is established as absent or as speaking a protocol we do not understand — a
    /// per-shot effects path cannot afford to keep paying for a type lookup that will never resolve.
    /// </summary>
    internal static class BlacksiteUpgradesWeaponBridge
    {
        /// <summary>
        /// Namespace-qualified only, never assembly-qualified. An earlier lookup pinned this name to
        /// <c>Y4NGZUpgrades</c>, and when the Better Armory split moved the type it resolved nothing,
        /// silently, for every soldier: the throttled budget below was rarely reached, so not even
        /// the inert line appeared. <see cref="FindApiType"/> now looks at what is actually loaded.
        /// </summary>
        private const string ApiTypeFullName = "Y4NGZUpgrades.Weapons.WeaponHostingApi";

        /// <summary>
        /// The assemblies known to have hosted the API, current home first. The scan prefers these
        /// so the ordinary install resolves on the first assembly it inspects, and their presence
        /// is what separates "the pack is installed but has no API" from "nothing is installed".
        /// </summary>
        private static readonly string[] CandidateAssemblyNames = { "BetterArmory", "Y4NGZUpgrades" };

        /// <summary>The one weapon a soldier is authored to carry.</summary>
        internal const string ConfigId = "mx16a4";

        /// <summary>The only protocol this bridge was written against. A different number means the
        /// meaning of a member changed under us, and guessing is worse than doing nothing.</summary>
        private const int SupportedProtocolVersion = 1;

        private const float ResolveRetryIntervalSeconds = 2f;

        /// <summary>
        /// How many throttled type lookups may miss, with neither candidate assembly loaded, before
        /// the bridge gives up for good. A candidate that is loaded without the type settles the
        /// answer at once and never spends this budget.
        ///
        /// This is a count of *calls*, not wall-clock time: the attempt counter only advances when
        /// something actually asks the bridge for something and the throttle window has elapsed, so
        /// fifteen attempts is at least thirty seconds and is longer whenever nothing is asking. The
        /// pack loads within a few seconds of us when it is installed at all, so a budget this size
        /// separates "still booting" from "absent" without leaving a lookup running all session.
        /// </summary>
        private const int MaximumResolveAttempts = 15;

        private static Type _apiType;
        private static MethodInfo _isWeaponAvailable;
        private static MethodInfo _getItemPrefab;
        private static MethodInfo _instantiateWorldModel;
        private static MethodInfo _tryGetMuzzle;
        private static MethodInfo _playMuzzleFlash;
        private static MethodInfo _getFireClips;
        private static MethodInfo _playImpact;
        private static MethodInfo _initializeDroppedWeapon;

        private static bool _inert;
        private static bool _inertLogged;
        private static float _nextResolveAttemptAt;
        private static int _resolveAttempts;
        private static bool _callFailureLogged;
        private static bool _scanFailureLogged;

        // Argument buffers, reused rather than allocated: the flash, audio and impact calls all run
        // on the per-shot path, and a fresh object[] per round per peer is exactly the kind of
        // garbage that shows up as a hitch during a firefight. This removes the array allocations,
        // not all of them - reflection still boxes every value-type argument on each Invoke.
        // Reference slots are cleared after each call so a buffer never keeps a destroyed GameObject
        // alive.
        private static readonly object[] OneStringArg = new object[1];
        private static readonly object[] InstantiateArgs = new object[2];
        private static readonly object[] MuzzleArgs = new object[3];
        private static readonly object[] FlashArgs = new object[4];
        private static readonly object[] ImpactArgs = new object[4];
        private static readonly object[] DropArgs = new object[3];

        /// <summary>
        /// #1209: optional, and deliberately outside <see cref="TryBindMethods"/>. The pack added it
        /// to protocol 1 after the eight hosting members shipped, so an older pack without it must
        /// keep every soldier's rifle; it only means Bullet Resistance does not reach their rounds.
        /// </summary>
        private static Func<int, int> _resolveIncomingFirearmDamage;
        private static readonly Func<int, int> SafeIncomingFirearmResistance = ResolveIncomingFirearmDamageSafely;
        private static bool _incomingFirearmCallFailureLogged;

        /// <summary>
        /// True once the bridge has given up permanently - the pack is absent, or speaks a protocol
        /// this build does not understand. A caller that failed to attach can use this to tell a
        /// settled "never" from a "not yet": only the former is worth latching on.
        /// </summary>
        internal static bool IsPermanentlyInert => _inert;

        /// <summary>
        /// Whether the pack is present, speaks protocol 1, and has a usable MX16A4. Cheap enough to
        /// ask at attach time; not cheap enough to ask per shot, so callers cache the answer.
        /// </summary>
        internal static bool IsAvailable()
        {
            if (!Resolve())
                return false;

            try
            {
                OneStringArg[0] = ConfigId;
                return _isWeaponAvailable.Invoke(null, OneStringArg) is bool available && available;
            }
            catch (Exception e)
            {
                ReportCallFailure("IsWeaponAvailable", e);
                return false;
            }
            finally
            {
                OneStringArg[0] = null;
            }
        }

        internal static bool TryInstantiateWorldModel(Transform parent, out GameObject model)
        {
            model = null;
            if (parent == null || !Resolve())
                return false;

            try
            {
                InstantiateArgs[0] = ConfigId;
                InstantiateArgs[1] = parent;
                model = _instantiateWorldModel.Invoke(null, InstantiateArgs) as GameObject;
                return model != null;
            }
            catch (Exception e)
            {
                ReportCallFailure("InstantiateWorldModel", e);
                return false;
            }
            finally
            {
                InstantiateArgs[0] = null;
                InstantiateArgs[1] = null;
            }
        }

        /// <summary>The muzzle in the model root's own local frame, per the API contract.</summary>
        internal static bool TryGetMuzzle(out Vector3 localPosition, out Vector3 localForward)
        {
            localPosition = Vector3.zero;
            localForward = Vector3.forward;
            if (!Resolve())
                return false;

            try
            {
                MuzzleArgs[0] = ConfigId;
                MuzzleArgs[1] = null;
                MuzzleArgs[2] = null;
                if (!(_tryGetMuzzle.Invoke(null, MuzzleArgs) is bool resolved) || !resolved)
                    return false;

                // Out parameters come back through the argument array.
                if (MuzzleArgs[1] is Vector3 position)
                    localPosition = position;
                if (MuzzleArgs[2] is Vector3 forward && forward.sqrMagnitude > 1e-6f)
                    localForward = forward.normalized;
                return true;
            }
            catch (Exception e)
            {
                ReportCallFailure("TryGetMuzzle", e);
                return false;
            }
            finally
            {
                MuzzleArgs[0] = null;
                MuzzleArgs[1] = null;
                MuzzleArgs[2] = null;
            }
        }

        /// <summary>
        /// Draw the weapon's own muzzle flash. Returns whether the call reached a live bridge and came
        /// back without throwing, which is what tells the caller whether it still needs to show its
        /// authored stand-in flash instead.
        /// </summary>
        internal static bool PlayMuzzleFlash(GameObject modelRoot, Vector3 worldForward, float scale)
        {
            if (modelRoot == null || !Resolve())
                return false;

            try
            {
                FlashArgs[0] = modelRoot;
                FlashArgs[1] = ConfigId;
                FlashArgs[2] = worldForward;
                FlashArgs[3] = scale;
                _playMuzzleFlash.Invoke(null, FlashArgs);
                return true;
            }
            catch (Exception e)
            {
                ReportCallFailure("PlayMuzzleFlash", e);
                return false;
            }
            finally
            {
                FlashArgs[0] = null;
                FlashArgs[1] = null;
                FlashArgs[2] = null;
                FlashArgs[3] = null;
            }
        }

        /// <summary>The weapon's authored fire variations. Asked once at attach and cached by the
        /// caller; never on the shot path.</summary>
        internal static bool TryGetFireClips(out AudioClip[] clips)
        {
            clips = null;
            if (!Resolve())
                return false;

            try
            {
                OneStringArg[0] = ConfigId;
                clips = _getFireClips.Invoke(null, OneStringArg) as AudioClip[];
                return clips != null && clips.Length > 0;
            }
            catch (Exception e)
            {
                ReportCallFailure("GetFireClips", e);
                clips = null;
                return false;
            }
            finally
            {
                OneStringArg[0] = null;
            }
        }

        internal static void PlayImpact(Vector3 point, Vector3 normal, Collider hitCollider)
        {
            if (hitCollider == null || !Resolve())
                return;

            try
            {
                ImpactArgs[0] = point;
                ImpactArgs[1] = normal;
                ImpactArgs[2] = hitCollider;
                ImpactArgs[3] = ConfigId;
                _playImpact.Invoke(null, ImpactArgs);
            }
            catch (Exception e)
            {
                ReportCallFailure("PlayImpact", e);
            }
            finally
            {
                ImpactArgs[0] = null;
                ImpactArgs[1] = null;
                ImpactArgs[2] = null;
                ImpactArgs[3] = null;
            }
        }

        internal static bool TryGetItemPrefab(out GameObject prefab)
        {
            prefab = null;
            if (!Resolve())
                return false;

            try
            {
                OneStringArg[0] = ConfigId;
                prefab = _getItemPrefab.Invoke(null, OneStringArg) as GameObject;
                return prefab != null;
            }
            catch (Exception e)
            {
                ReportCallFailure("GetItemPrefab", e);
                return false;
            }
            finally
            {
                OneStringArg[0] = null;
            }
        }

        internal static bool InitializeDroppedWeapon(GameObject spawnedInstance, int magazine, bool safetyOn)
        {
            if (spawnedInstance == null || !Resolve())
                return false;

            try
            {
                DropArgs[0] = spawnedInstance;
                DropArgs[1] = magazine;
                DropArgs[2] = safetyOn;
                return _initializeDroppedWeapon.Invoke(null, DropArgs) is bool initialized && initialized;
            }
            catch (Exception e)
            {
                ReportCallFailure("InitializeDroppedWeapon", e);
                return false;
            }
            finally
            {
                DropArgs[0] = null;
                DropArgs[1] = null;
                DropArgs[2] = null;
            }
        }

        /// <summary>
        /// #1209: the local player's Bullet Resistance as a function of raw gunshot damage, or null
        /// when the pack is absent, inert, or too old to expose it. Call only on the victim's own
        /// client: the pack reads the LOCAL player's rank. Never throws, and the returned function
        /// never throws either; a failing call answers with the damage it was given.
        /// </summary>
        internal static Func<int, int> IncomingFirearmResistanceOrNull()
        {
            return Resolve() && _resolveIncomingFirearmDamage != null ? SafeIncomingFirearmResistance : null;
        }

        private static int ResolveIncomingFirearmDamageSafely(int damage)
        {
            Func<int, int> resolve = _resolveIncomingFirearmDamage;
            if (resolve == null)
                return damage;

            try
            {
                return resolve(damage);
            }
            catch (Exception e)
            {
                if (!_incomingFirearmCallFailureLogged)
                {
                    _incomingFirearmCallFailureLogged = true;
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Blacksite] WeaponHostingApi.ResolveIncomingFirearmDamage threw: {e}. Gunshots apply unresisted. Logged once.");
                }
                return damage;
            }
        }

        private static bool Resolve()
        {
            if (_inert)
                return false;
            if (_apiType != null)
                return true;

            if (Time.realtimeSinceStartup < _nextResolveAttemptAt)
                return false;
            _nextResolveAttemptAt = Time.realtimeSinceStartup + ResolveRetryIntervalSeconds;

            Type apiType = FindApiType(out string loadedCandidates);
            if (apiType == null)
            {
                _resolveAttempts++;
                if (loadedCandidates != null)
                {
                    // A loaded assembly already carries every type it will ever have, so a candidate
                    // present without the API is a settled answer, not a race: the installed pack
                    // predates WeaponHostingApi, or has moved it somewhere this build does not know
                    // to look. Waiting out the budget would only delay the line.
                    GoInert($"{loadedCandidates} loaded, but no loaded assembly exposes {ApiTypeFullName}");
                }
                else if (_resolveAttempts >= MaximumResolveAttempts)
                {
                    GoInert("neither BetterArmory nor Y4NGZUpgrades loaded; soldiers keep their built-in weapon presentation");
                }
                else if (_resolveAttempts == 1)
                {
                    // Said once, at the first miss, so a session without the pack still records the
                    // fact even when no soldier lives long enough to exhaust the budget above.
                    ContractsBootstrap.Log?.LogInfo(
                        "[MoonContracts.Blacksite] Weapon hosting not bound yet: neither BetterArmory nor Y4NGZUpgrades is loaded; "
                        + $"it stays off after {MaximumResolveAttempts} throttled lookups.");
                }
                return false;
            }

            if (!TryReadProtocolVersion(apiType, out int protocol))
            {
                GoInert("WeaponHostingApi exposes no readable ProtocolVersion");
                return false;
            }

            if (protocol != SupportedProtocolVersion)
            {
                GoInert($"WeaponHostingApi speaks protocol {protocol}, this build understands only {SupportedProtocolVersion}");
                return false;
            }

            if (!TryBindMethods(apiType, out string missing))
            {
                GoInert($"WeaponHostingApi is missing '{missing}'");
                return false;
            }

            _apiType = apiType;
            BindIncomingFirearmResistance(apiType);
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Y4NGZUpgrades weapon hosting bound ({apiType.Assembly.GetName().Name}); soldiers will carry the MX16A4."
                + (_resolveIncomingFirearmDamage != null
                    ? " Soldier gunshots pass through the victim's own Bullet Resistance where that pack supports it."
                    : " This pack has no ResolveIncomingFirearmDamage, so soldier gunshots apply unresisted."));
            return true;
        }

        /// <summary>
        /// Locate the API among the assemblies actually loaded, never through the loader: the
        /// candidates by simple name first, then everything else in case the type moves again.
        /// <paramref name="loadedCandidates"/> names the candidate assemblies that are present, or
        /// is null when none is, whether or not one of them carried the type.
        /// </summary>
        private static Type FindApiType(out string loadedCandidates)
        {
            loadedCandidates = null;
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                Type found = null;
                for (int c = 0; c < CandidateAssemblyNames.Length; c++)
                {
                    string candidate = CandidateAssemblyNames[c];
                    for (int i = 0; i < assemblies.Length; i++)
                    {
                        if (!HasSimpleName(assemblies[i], candidate))
                            continue;
                        loadedCandidates = loadedCandidates == null ? candidate : loadedCandidates + " and " + candidate;
                        if (found == null)
                            found = assemblies[i].GetType(ApiTypeFullName, throwOnError: false);
                    }
                }
                if (found != null)
                    return found;

                for (int i = 0; i < assemblies.Length; i++)
                {
                    Type type = assemblies[i].GetType(ApiTypeFullName, throwOnError: false);
                    if (type != null)
                        return type;
                }
            }
            catch (Exception e)
            {
                if (!_scanFailureLogged)
                {
                    _scanFailureLogged = true;
                    ContractsBootstrap.Log?.LogWarning(
                        $"[MoonContracts.Blacksite] Scanning loaded assemblies for {ApiTypeFullName} threw: {e}. Logged once.");
                }
            }

            return null;
        }

        /// <summary>Simple-name match without allocating an <see cref="AssemblyName"/>: a full name
        /// is the simple name followed by a comma and the version tuple.</summary>
        private static bool HasSimpleName(Assembly assembly, string simpleName)
        {
            string fullName = assembly.FullName;
            return fullName != null
                && fullName.Length > simpleName.Length
                && fullName[simpleName.Length] == ','
                && string.CompareOrdinal(fullName, 0, simpleName, 0, simpleName.Length) == 0;
        }

        private static bool TryReadProtocolVersion(Type apiType, out int protocol)
        {
            protocol = 0;
            try
            {
                FieldInfo field = apiType.GetField(
                    "ProtocolVersion",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null)
                    return false;

                object value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
                if (!(value is int version))
                    return false;

                protocol = version;
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryBindMethods(Type apiType, out string missing)
        {
            missing = string.Empty;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            try
            {
                _isWeaponAvailable = apiType.GetMethod("IsWeaponAvailable", flags);
                _getItemPrefab = apiType.GetMethod("GetItemPrefab", flags);
                _instantiateWorldModel = apiType.GetMethod("InstantiateWorldModel", flags);
                _tryGetMuzzle = apiType.GetMethod("TryGetMuzzle", flags);
                _playMuzzleFlash = apiType.GetMethod("PlayMuzzleFlash", flags);
                _getFireClips = apiType.GetMethod("GetFireClips", flags);
                _playImpact = apiType.GetMethod("PlayImpact", flags);
                _initializeDroppedWeapon = apiType.GetMethod("InitializeDroppedWeapon", flags);
            }
            catch (Exception e)
            {
                missing = e.GetType().Name;
                return false;
            }

            if (_isWeaponAvailable == null)
                missing = "IsWeaponAvailable";
            else if (_getItemPrefab == null)
                missing = "GetItemPrefab";
            else if (_instantiateWorldModel == null)
                missing = "InstantiateWorldModel";
            else if (_tryGetMuzzle == null)
                missing = "TryGetMuzzle";
            else if (_playMuzzleFlash == null)
                missing = "PlayMuzzleFlash";
            else if (_getFireClips == null)
                missing = "GetFireClips";
            else if (_playImpact == null)
                missing = "PlayImpact";
            else if (_initializeDroppedWeapon == null)
                missing = "InitializeDroppedWeapon";

            return missing.Length == 0;
        }

        /// <summary>
        /// #1209: bound by exact signature, <c>static int (int)</c>, as a delegate so the per-hit
        /// call neither allocates an argument array nor boxes. Absence is an answer, not a failure.
        /// </summary>
        private static void BindIncomingFirearmResistance(Type apiType)
        {
            _resolveIncomingFirearmDamage = null;
            try
            {
                MethodInfo method = apiType.GetMethod(
                    "ResolveIncomingFirearmDamage",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(int) },
                    modifiers: null);
                if (method != null && method.ReturnType == typeof(int))
                {
                    _resolveIncomingFirearmDamage = Delegate.CreateDelegate(
                        typeof(Func<int, int>), method, throwOnBindFailure: false) as Func<int, int>;
                }
            }
            catch (Exception e)
            {
                _resolveIncomingFirearmDamage = null;
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.Blacksite] WeaponHostingApi.ResolveIncomingFirearmDamage could not be bound: {e.Message}. Gunshots apply unresisted.");
            }
        }

        private static void GoInert(string reason)
        {
            _inert = true;
            _apiType = null;
            _isWeaponAvailable = null;
            _getItemPrefab = null;
            _instantiateWorldModel = null;
            _tryGetMuzzle = null;
            _playMuzzleFlash = null;
            _getFireClips = null;
            _playImpact = null;
            _initializeDroppedWeapon = null;
            _resolveIncomingFirearmDamage = null;

            if (_inertLogged)
                return;
            _inertLogged = true;
            ContractsBootstrap.Log?.LogInfo(
                $"[MoonContracts.Blacksite] Weapon hosting stays off: {reason}.");
        }

        /// <summary>
        /// One line for the session. A call that fails once on a per-shot path fails every shot, and
        /// an unthrottled warning would bury the diagnosis it was meant to provide.
        /// </summary>
        private static void ReportCallFailure(string member, Exception e)
        {
            if (_callFailureLogged)
                return;
            _callFailureLogged = true;
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts.Blacksite] WeaponHostingApi.{member} threw: {e}. Logged once.");
        }
    }
}
