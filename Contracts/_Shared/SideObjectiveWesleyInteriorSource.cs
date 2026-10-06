using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using GameNetcodeStuff;
using Unity.Netcode;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compatibility;
namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1195. Wesley's Interiors as a side-objective source: the host's controller poll, the host's
    /// attributed progression credits, and every peer's sight targets for the published entry.
    ///
    /// The controller is found by a bounded scene search at the start of each round and cached; a
    /// round without one stops looking after <see cref="MaxControllerSearches"/>. Its props come off
    /// the controller's own <c>Prefabs</c> list, and the target set is rebuilt only when the entry
    /// or that list changes. Clients never read the host-only fields: they look for whatever entry
    /// the host published.
    ///
    /// Credits (host only): each step, the completion and the reward claim are published once per
    /// round as <see cref="ContractProgressionEventKind"/> side-objective events. The actor comes
    /// from the sender of Wesley's own ServerRpc (see <see cref="ResolveActor"/>); a step the poll
    /// sees that no RPC credited - attribution patches missing - is credited to the crew.
    /// </summary>
    internal static class SideObjectiveWesleyInteriorSource
    {
        /// <summary>The source token in progression event ids.</summary>
        private const string CreditSource = "wesley";
        private const int MaxControllerSearches = 8;
        private const float ControllerSearchIntervalSeconds = 2f;
        private const int MaxRewardSearches = 6;
        private const float RewardSearchIntervalSeconds = 1f;
        private const float RewardSearchRadiusMeters = 4f;

        private static NetworkBehaviour _controller;
        private static bool _controllerFound;
        private static int _controllerSearches;
        private static float _nextControllerSearchAt;

        private static readonly string[] EntryNames = new string[8];
        private static string _scopeEntry;
        private static string _scope = string.Empty;

        private static bool _triggerSeenEnabled;
        private static GrabbableObject _reward;
        private static bool _rewardFound;
        private static bool _rewardTaken;
        private static ulong _rewardHolder = ContractProgressionEventIds.UnknownPlayerClientId;
        private static int _rewardSearches;
        private static float _nextRewardSearchAt;

        private static readonly SideObjectiveCreditLedger Credits = new SideObjectiveCreditLedger();
        private static ulong _lastStepActor = ContractProgressionEventIds.UnknownPlayerClientId;
        private static ulong _caseOpener = ContractProgressionEventIds.UnknownPlayerClientId;
        private static readonly List<Transform> ActorRoots = new List<Transform>(16);

        private static readonly List<Component> PropScratch = new List<Component>(16);
        private static readonly List<MonoBehaviour> BehaviourScratch = new List<MonoBehaviour>(16);
        private static readonly Dictionary<Type, SpawnerFields> SpawnerFieldCache = new Dictionary<Type, SpawnerFields>();
        private static NetworkBehaviour _targetsController;
        private static string _targetsEntry;
        private static int _targetsPropCount = -2;

        /// <summary>Per component type: where its EnemyType references are, found by field type so
        /// no spawner mod (JLL's EnemySpawner) is named.</summary>
        private sealed class SpawnerFields
        {
            internal FieldInfo[] Direct;
            internal FieldInfo RandomFlag;
            internal FieldInfo Pool;
            internal FieldInfo PoolEnemy;
        }

        internal static void ResetRunState()
        {
            _controller = null;
            _controllerFound = false;
            _controllerSearches = 0;
            _nextControllerSearchAt = 0f;
            Array.Clear(EntryNames, 0, EntryNames.Length);
            _scopeEntry = null;
            _scope = string.Empty;
            _triggerSeenEnabled = false;
            _reward = null;
            _rewardFound = false;
            _rewardTaken = false;
            _rewardHolder = ContractProgressionEventIds.UnknownPlayerClientId;
            _rewardSearches = 0;
            _nextRewardSearchAt = 0f;
            Credits.Reset();
            _lastStepActor = ContractProgressionEventIds.UnknownPlayerClientId;
            _caseOpener = ContractProgressionEventIds.UnknownPlayerClientId;
            ActorRoots.Clear();
            PropScratch.Clear();
            BehaviourScratch.Clear();
            _targetsController = null;
            _targetsEntry = null;
            _targetsPropCount = -2;
        }

        /// <summary>Host-only: reads the controller, backfills credits, and returns the reading for
        /// <see cref="SideObjectiveRuntime"/> to publish (#1198: one source at a time).</summary>
        internal static SideObjectiveSnapshot PollHost(float now)
        {
            EnsureAttribution();
            NetworkBehaviour controller = EnsureController(now);
            SideObjectiveSnapshot reading = SideObjectiveSnapshot.None;
            var host = default(WesleysInteriorToolsCompat.HostReading);
            if (controller != null
                && WesleysInteriorToolsCompat.TryReadHost(controller, ref host)
                && host.Chosen
                && !string.IsNullOrEmpty(host.EntryName))
            {
                if (host.TriggerColliderEnabled == 1)
                    _triggerSeenEnabled = true;

                bool rewardTaken = host.RewardSpawned && IsRewardTaken(controller, now);
                reading = new SideObjectiveSnapshot(
                    SideObjectiveSource.WesleyInterior,
                    ResolveScope(controller, host.EntryName),
                    host.EntryName,
                    host.Count,
                    host.Required,
                    SideObjectiveWesleyInteriorRules.Completed(host.Completed, host.Count, host.Required, host.RewardSpawned),
                    SideObjectiveWesleyInteriorRules.RewardClaimed(_triggerSeenEnabled, host.TriggerColliderEnabled, rewardTaken),
                    revealed: false);
                ReconcileCredits(reading);
            }

            return reading;
        }

        /// <summary>
        /// Every peer: points the sight check at the published entry's props (and, for a spawner
        /// entry, at the enemies it spawns). Cheap when nothing moved: one list-length read.
        /// </summary>
        internal static void RefreshSightTargets(in SideObjectiveSnapshot current, float now)
        {
            if (current.Source != SideObjectiveSource.WesleyInterior)
            {
                // Another source owns the sight targets now; forget ours so regaining the card
                // rebuilds them (#1198).
                _targetsController = null;
                _targetsEntry = null;
                _targetsPropCount = -2;
                return;
            }

            NetworkBehaviour controller = EnsureController(now);
            if (controller == null)
            {
                if (_targetsController != null || _targetsPropCount != -2)
                {
                    SideObjectiveSight.ClearTargets();
                    _targetsController = null;
                    _targetsEntry = null;
                    _targetsPropCount = -2;
                }

                return;
            }

            int propCount = WesleysInteriorToolsCompat.CountObjectiveProps(controller);
            if (ReferenceEquals(controller, _targetsController)
                && string.Equals(current.Entry, _targetsEntry, StringComparison.Ordinal)
                && propCount == _targetsPropCount)
            {
                return;
            }

            _targetsController = controller;
            _targetsEntry = current.Entry;
            _targetsPropCount = propCount;

            SideObjectiveSight.ClearTargets();
            SideObjectiveSight.SetCaseTrigger(WesleysInteriorToolsCompat.GetCaseTrigger(controller));
            PropScratch.Clear();
            WesleysInteriorToolsCompat.CollectObjectiveProps(controller, current.Entry, PropScratch);

            SideObjectiveCopy copy = SideObjectiveCopyTable.Lookup(current.Scope, current.Entry);
            bool spawnedEnemies = copy.Sight == SideObjectiveSightKind.SpawnedEnemy;
            int enemyTypes = 0;
            for (int i = 0; i < PropScratch.Count; i++)
            {
                Component prop = PropScratch[i];
                // A spawner entry's props carry no renderer; sighting the empty spawn point would
                // reveal an enemy nobody has seen, so those entries look at the enemies instead.
                SideObjectiveSight.AddProp(prop, allowPointTarget: !spawnedEnemies);
                if (spawnedEnemies)
                    enemyTypes += AddSpawnerEnemyTypes(prop);
            }

            ContractsBootstrap.Log?.LogDebug(
                $"[MoonContracts.Side] Sight targets for '{current.Entry}' ({current.Scope}): {PropScratch.Count} prop(s)"
                + (spawnedEnemies ? $", {enemyTypes} spawned enemy type(s)." : "."));
            PropScratch.Clear();
        }

        private static void EnsureAttribution()
        {
            if (WesleysInteriorToolsCompat.StepIncreased == null)
            {
                WesleysInteriorToolsCompat.StepIncreased = OnStepIncreased;
                WesleysInteriorToolsCompat.CaseOpened = OnCaseOpened;
            }

            WesleysInteriorToolsCompat.EnsureAttributionPatches();
        }

        /// <summary>
        /// Host-only, from inside Wesley's increase handler after its body ran: credits every step the
        /// count now reaches that has not been credited, and the completion when the count is full,
        /// to the RPC's actor.
        /// </summary>
        private static void OnStepIncreased(NetworkBehaviour controller, ulong sender)
        {
            // A late RPC after the round reset must not re-credit steps under the same round id.
            if (!SideObjectiveNetwork.IsServer || !SideObjectiveRuntime.AcceptsState())
                return;
            if (_controller == null)
            {
                _controller = controller;
                _controllerFound = true;
            }

            var host = default(WesleysInteriorToolsCompat.HostReading);
            if (!WesleysInteriorToolsCompat.TryReadHost(controller, ref host) || !host.Chosen || string.IsNullOrEmpty(host.EntryName))
                return;

            string scope = ResolveScope(controller, host.EntryName);
            ulong actor = ResolveActor(controller, host.EntryName, sender);
            _lastStepActor = actor;
            Credits.Begin(SideObjectiveSource.WesleyInterior, scope, host.EntryName);
            string title = SideObjectiveCopyTable.Lookup(scope, host.EntryName).Title;
            while (Credits.TryTakeStep(host.Count, host.Required, out int step))
                ContractProgressionEvents.PublishSideObjectiveStepCompleted(CreditSource, host.EntryName, title, step, actor);

            if (host.Required > 0 && host.Count >= host.Required && Credits.TryTakeCompletion())
                ContractProgressionEvents.PublishSideObjectiveCompleted(CreditSource, host.EntryName, title, host.Required, actor);
        }

        /// <summary>Host-only: remembers who opened the case, for the claim credit.</summary>
        private static void OnCaseOpened(NetworkBehaviour controller, ulong sender)
        {
            if (!SideObjectiveNetwork.IsServer || !SideObjectiveRuntime.AcceptsState()
                || ContractProgressionEventIds.IsAttributable(_caseOpener))
                return;

            var host = default(WesleysInteriorToolsCompat.HostReading);
            string entry = WesleysInteriorToolsCompat.TryReadHost(controller, ref host) && host.Chosen ? host.EntryName : null;
            _caseOpener = ResolveActor(controller, entry, sender);
        }

        /// <summary>
        /// Host poll: credits whatever the RPC hooks did not. Steps the count reached without a hooked
        /// RPC go to the crew. The completion goes to the finisher of the last step, or to the case
        /// opener on the interiors whose reward opens without a counter; the claim to the case opener,
        /// else the first player seen holding the reward, else the crew.
        /// </summary>
        private static void ReconcileCredits(in SideObjectiveSnapshot reading)
        {
            Credits.Begin(reading.Source, reading.Scope, reading.Entry);
            string title = SideObjectiveCopyTable.Lookup(reading.Scope, reading.Entry).Title;
            while (Credits.TryTakeStep(reading.Count, reading.Required, out int step))
            {
                ContractProgressionEvents.PublishSideObjectiveStepCompleted(
                    CreditSource, reading.Entry, title, step, ContractProgressionEventIds.UnknownPlayerClientId);
            }

            if (reading.Completed && Credits.TryTakeCompletion())
            {
                bool counterFilled = reading.Required > 0 && reading.Count >= reading.Required;
                ContractProgressionEvents.PublishSideObjectiveCompleted(
                    CreditSource, reading.Entry, title, reading.Required, counterFilled ? _lastStepActor : _caseOpener);
            }

            if (reading.RewardClaimed && Credits.TryTakeClaim())
            {
                ulong claimer = ContractProgressionEventIds.IsAttributable(_caseOpener) ? _caseOpener : _rewardHolder;
                ContractProgressionEvents.PublishSideObjectiveRewardClaimed(CreditSource, reading.Entry, title, claimer);
            }
        }

        /// <summary>
        /// The player behind a Wesley ServerRpc. A client's sender id is that client. Sender 0 is the
        /// host's own code: NGO runs a host-sent ServerRpc synchronously, so when the host's player is
        /// at that moment hovering or animating with the case trigger or a trigger under this entry's
        /// props, it was that player; otherwise it was a host-side puzzle event or an enemy death,
        /// and the actor is unknown (crew credit).
        /// </summary>
        private static ulong ResolveActor(NetworkBehaviour controller, string entry, ulong sender)
        {
            if (sender != NetworkManager.ServerClientId)
                return sender;

            StartOfRound start = StartOfRound.Instance;
            PlayerControllerB local = start != null ? start.localPlayerController : null;
            if (local == null || !local.isPlayerControlled || local.isPlayerDead)
                return ContractProgressionEventIds.UnknownPlayerClientId;

            ActorRoots.Clear();
            if (!string.IsNullOrEmpty(entry))
            {
                PropScratch.Clear();
                WesleysInteriorToolsCompat.CollectObjectiveProps(controller, entry, PropScratch);
                for (int i = 0; i < PropScratch.Count; i++)
                    ActorRoots.Add(PropScratch[i].transform);
                PropScratch.Clear();
            }

            bool interacting = SideObjectiveSight.IsInteractingWithTarget(
                local, WesleysInteriorToolsCompat.GetCaseTrigger(controller), ActorRoots, controller.transform);
            ActorRoots.Clear();
            return interacting ? local.actualClientId : ContractProgressionEventIds.UnknownPlayerClientId;
        }

        private static NetworkBehaviour EnsureController(float now)
        {
            if (_controller != null)
                return _controller;
            // Found once and gone since: Wesley's own gates despawn it, and it never comes back.
            if (_controllerFound || _controllerSearches >= MaxControllerSearches || now < _nextControllerSearchAt)
                return null;

            _controllerSearches++;
            _nextControllerSearchAt = now + ControllerSearchIntervalSeconds;
            _controller = WesleysInteriorToolsCompat.FindController();
            if (_controller != null)
            {
                _controllerFound = true;
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Found Wesley's objective controller (search {_controllerSearches}).");
            }
            else if (_controllerSearches >= MaxControllerSearches)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] No Wesley objective controller after {MaxControllerSearches} searches; none this round.");
            }

            return _controller;
        }

        private static string ResolveScope(NetworkBehaviour controller, string entryName)
        {
            if (string.Equals(entryName, _scopeEntry, StringComparison.Ordinal))
                return _scope;

            int count = WesleysInteriorToolsCompat.ReadEntryNames(controller, EntryNames);
            _scope = SideObjectiveCopyTable.ResolveScope(entryName, EntryNames, count);
            _scopeEntry = entryName;
            if (string.IsNullOrEmpty(_scope))
            {
                ContractsBootstrap.Log?.LogDebug(
                    $"[MoonContracts.Side] Wesley entry '{entryName}' is not in the copy table; using the generic copy.");
            }

            return _scope;
        }

        /// <summary>Host-only: the reward item has been picked up, or has left the level. Remembers
        /// the first player seen holding it.</summary>
        private static bool IsRewardTaken(NetworkBehaviour controller, float now)
        {
            if (_reward == null)
            {
                if (_rewardFound)
                {
                    _rewardTaken = true;
                    return true;
                }

                _reward = FindReward(controller, now);
                if (_reward == null)
                    return _rewardTaken;
                _rewardFound = true;
            }

            if (_reward.isHeld && _reward.playerHeldBy != null && !ContractProgressionEventIds.IsAttributable(_rewardHolder))
                _rewardHolder = ContractProgressionEvents.ResolveClientId(_reward.playerHeldBy);

            _rewardTaken |= _reward.isHeld || _reward.hasBeenHeld;
            return _rewardTaken;
        }

        /// <summary>
        /// Bounded: the reward is instantiated under the controller's spawn point, so the children
        /// are asked first; a scene search is the fallback for a spawn netcode re-parented, and runs
        /// at most <see cref="MaxRewardSearches"/> times a round. An attempt is only spent once the
        /// spawn point is readable.
        /// </summary>
        private static GrabbableObject FindReward(NetworkBehaviour controller, float now)
        {
            if (_rewardSearches >= MaxRewardSearches || now < _nextRewardSearchAt)
                return null;

            Transform spawn = WesleysInteriorToolsCompat.GetRewardSpawn(controller);
            if (spawn == null)
                return null;

            _rewardSearches++;
            _nextRewardSearchAt = now + RewardSearchIntervalSeconds;
            GrabbableObject child = spawn.GetComponentInChildren<GrabbableObject>();
            if (child != null)
                return child;

            Vector3 origin = spawn.position;
            float bestSqrDistance = RewardSearchRadiusMeters * RewardSearchRadiusMeters;
            GrabbableObject best = null;
            GrabbableObject[] items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (item == null || item.isInShipRoom || !WesleysInteriorToolsCompat.IsRewardItem(controller, item.itemProperties))
                    continue;

                float sqrDistance = (item.transform.position - origin).sqrMagnitude;
                if (sqrDistance < bestSqrDistance)
                {
                    bestSqrDistance = sqrDistance;
                    best = item;
                }
            }

            return best;
        }

        /// <summary>
        /// Adds every EnemyType a spawner prop's components hold, the prop's children included. A
        /// component with a random pool (JLL's <c>spawnRandom</c> / <c>randomPool</c>) contributes the
        /// pool when it draws from it, or when it names no single type.
        /// </summary>
        private static int AddSpawnerEnemyTypes(Component prop)
        {
            if (prop == null)
                return 0;

            int added = 0;
            BehaviourScratch.Clear();
            prop.GetComponentsInChildren(true, BehaviourScratch);
            for (int i = 0; i < BehaviourScratch.Count; i++)
            {
                MonoBehaviour behaviour = BehaviourScratch[i];
                if (behaviour == null)
                    continue;

                SpawnerFields fields = GetSpawnerFields(behaviour.GetType());
                if (fields == null)
                    continue;

                try
                {
                    int direct = 0;
                    for (int f = 0; f < fields.Direct.Length; f++)
                    {
                        if (fields.Direct[f].GetValue(behaviour) is EnemyType type && type != null)
                        {
                            SideObjectiveSight.AddEnemyType(type);
                            direct++;
                        }
                    }

                    bool random = fields.RandomFlag != null && fields.RandomFlag.GetValue(behaviour) is bool flag && flag;
                    if (fields.Pool != null && (random || direct == 0) && fields.Pool.GetValue(behaviour) is IList pool)
                    {
                        for (int p = 0; p < pool.Count; p++)
                        {
                            if (pool[p] != null && fields.PoolEnemy.GetValue(pool[p]) is EnemyType pooled && pooled != null)
                            {
                                SideObjectiveSight.AddEnemyType(pooled);
                                added++;
                            }
                        }
                    }

                    added += direct;
                }
                catch
                {
                    // A foreign field that will not read is one fewer target, nothing more.
                }
            }

            BehaviourScratch.Clear();
            return added;
        }

        private static SpawnerFields GetSpawnerFields(Type type)
        {
            if (SpawnerFieldCache.TryGetValue(type, out SpawnerFields cached))
                return cached;

            var direct = new List<FieldInfo>();
            FieldInfo randomFlag = null;
            FieldInfo pool = null;
            FieldInfo poolEnemy = null;
            foreach (FieldInfo field in type.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                Type fieldType = field.FieldType;
                if (fieldType == typeof(EnemyType))
                {
                    direct.Add(field);
                }
                else if (fieldType == typeof(bool) && string.Equals(field.Name, "spawnRandom", StringComparison.Ordinal))
                {
                    randomFlag = field;
                }
                else if (pool == null && typeof(IList).IsAssignableFrom(fieldType))
                {
                    Type element = fieldType.IsArray
                        ? fieldType.GetElementType()
                        : fieldType.IsGenericType && fieldType.GetGenericArguments().Length == 1 ? fieldType.GetGenericArguments()[0] : null;
                    FieldInfo enemyField = element != null && !element.IsValueType ? FindEnemyTypeField(element) : null;
                    if (enemyField != null)
                    {
                        pool = field;
                        poolEnemy = enemyField;
                    }
                }
            }

            cached = direct.Count > 0 || pool != null
                ? new SpawnerFields { Direct = direct.ToArray(), RandomFlag = randomFlag, Pool = pool, PoolEnemy = poolEnemy }
                : null;
            SpawnerFieldCache[type] = cached;
            return cached;
        }

        private static FieldInfo FindEnemyTypeField(Type element)
        {
            foreach (FieldInfo field in element.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                if (field.FieldType == typeof(EnemyType))
                    return field;
            }

            return null;
        }
    }
}
