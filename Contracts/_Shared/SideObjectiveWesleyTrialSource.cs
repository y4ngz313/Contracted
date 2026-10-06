using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compatibility;
namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1198. The Wesley's Moons trials as a side-objective source: which trial this round holds,
    /// the host's step and completion reads, the host's ticket watch, and every peer's sight targets.
    ///
    /// The trial is found by a bounded scene search from the moment the round goes live
    /// (<see cref="MaxTrialSearches"/> searches, <see cref="TrialSearchIntervalSeconds"/> apart):
    /// the live <c>TrialObject</c> markers name it, and Roart, which has none, is found by its scene
    /// and a <c>GameObject.Find</c> of its ritual root. Probes are resolved once per trial against
    /// <see cref="SideObjectiveTrialTable"/> and cached; a poll is field and component reads only.
    ///
    /// Fail-soft, one log line per trial per session: a step probe that no longer resolves after a
    /// Wesley update drops the trial to reveal plus completion (0 of 1); a completion that no
    /// longer resolves stands the trial down; marker objects that match no known trial are not
    /// tracked. Clients only look for the trial the host published, for the sight check.
    /// </summary>
    internal static class SideObjectiveWesleyTrialSource
    {
        private const int MaxTrialSearches = 12;
        private const float TrialSearchIntervalSeconds = 2.5f;
        private const int MaxTicketSearches = 10;
        private const float TicketSearchIntervalSeconds = 1.5f;

        /// <summary>One resolved probe and its host latch.</summary>
        private sealed class Slot
        {
            internal SideObjectiveTrialProbeKind Kind;
            internal GameObject Target;
            internal Component Counter;
            internal Collider Collider;
            internal Animator Animator;
            internal int BoolHash;
            internal int[] StateHashes;
            internal bool Seen;
            internal int Latched;
        }

        private static SideObjectiveTrialDefinition _definition;
        private static Transform _identity;
        private static int _searches;
        private static float _nextSearchAt;
        private static readonly Dictionary<string, Transform> Anchors = new Dictionary<string, Transform>(StringComparer.Ordinal);
        private static readonly List<Component> TrialObjects = new List<Component>(32);
        private static readonly List<string> TrialObjectNames = new List<string>(32);
        private static readonly Dictionary<string, Transform> SearchAnchors = new Dictionary<string, Transform>(StringComparer.Ordinal);

        private static bool _probesResolved;
        private static bool _degraded;
        private static bool _disabled;
        private static Slot[] _stepSlots = Array.Empty<Slot>();
        private static Slot[] _completionSlots = Array.Empty<Slot>();
        private static int[] _stepValues = Array.Empty<int>();
        /// <summary>A re-search's previous slots, handed to <see cref="ResolveProbes"/> so the latches
        /// and seen-edges of probes that resolved before survive it.</summary>
        private static Slot[] _carryStepSlots;
        private static int[] _carryStepValues;
        private static Slot[] _carryCompletionSlots;

        private static GrabbableObject _ticket;
        private static bool _ticketFound;
        private static bool _ticketTaken;
        private static int _ticketSearches;
        private static float _nextTicketSearchAt;

        private static string _targetsKey;
        private static Transform _targetsIdentity;

        /// <summary>Session-wide: each fail-soft message is written once, not once per round.</summary>
        private static readonly HashSet<string> Logged = new HashSet<string>(StringComparer.Ordinal);

        internal static void ResetRunState()
        {
            _definition = null;
            _identity = null;
            _searches = 0;
            _nextSearchAt = 0f;
            Anchors.Clear();
            SearchAnchors.Clear();
            TrialObjects.Clear();
            TrialObjectNames.Clear();
            _carryStepSlots = null;
            _carryStepValues = null;
            _carryCompletionSlots = null;
            ForgetProbes();
            _ticket = null;
            _ticketFound = false;
            _ticketTaken = false;
            _ticketSearches = 0;
            _nextTicketSearchAt = 0f;
            _targetsKey = null;
            _targetsIdentity = null;
        }

        /// <summary>Host-only: this round's trial as a reading, or none. The caller publishes.</summary>
        internal static SideObjectiveSnapshot PollHost(float now)
        {
            SideObjectiveTrialDefinition definition = EnsureTrial(now);
            if (definition == null)
                return SideObjectiveSnapshot.None;

            if (!_probesResolved)
                ResolveProbes(definition);
            if (_disabled)
                return SideObjectiveSnapshot.None;

            bool completed = false;
            try
            {
                for (int i = 0; i < _stepSlots.Length; i++)
                    _stepValues[i] = Read(_stepSlots[i], _stepValues[i]);

                for (int i = 0; i < _completionSlots.Length; i++)
                {
                    Slot slot = _completionSlots[i];
                    if (slot == null)
                        continue;
                    slot.Latched = Read(slot, slot.Latched);
                    completed |= slot.Latched > 0;
                }
            }
            catch (Exception e)
            {
                // Latched values stand; the next poll tries again.
                LogOnce("read:" + definition.Key, $"Reading the {definition.Key} trial failed; its steps keep their last values: {e.GetType().Name}: {e.Message}");
            }

            SideObjectiveTrialProgress progress = SideObjectiveTrialRules.Tally(definition, _stepValues, _degraded);
            bool ticketTaken = completed && definition.TicketItemName != null && IsTicketTaken(definition, now);
            return new SideObjectiveSnapshot(
                SideObjectiveSource.WesleyTrial,
                definition.Key,
                definition.Key,
                progress.Count,
                progress.Required,
                completed,
                SideObjectiveTrialRules.RewardClaimed(definition, completed, ticketTaken),
                revealed: false);
        }

        /// <summary>
        /// Every peer: points the sight check at the published trial's props. Rebuilt only when the
        /// trial or its identity object changes.
        /// </summary>
        internal static void RefreshSightTargets(in SideObjectiveSnapshot current, float now)
        {
            if (current.Source != SideObjectiveSource.WesleyTrial)
            {
                // Another source owns the sight targets now; forget ours so regaining the card
                // rebuilds them.
                _targetsKey = null;
                _targetsIdentity = null;
                return;
            }

            SideObjectiveTrialDefinition definition = WesleysTrialsCompat.IsUsable ? EnsureTrial(now) : null;
            if (definition == null || !string.Equals(definition.Key, current.Entry, StringComparison.Ordinal))
            {
                if (_targetsKey != null)
                {
                    SideObjectiveSight.ClearTargets();
                    _targetsKey = null;
                    _targetsIdentity = null;
                }

                return;
            }

            if (string.Equals(_targetsKey, definition.Key, StringComparison.Ordinal) && ReferenceEquals(_targetsIdentity, _identity))
                return;

            _targetsKey = definition.Key;
            _targetsIdentity = _identity;
            SideObjectiveSight.ClearTargets();
            int added = 0;
            for (int i = 0; i < definition.SightTargets.Length; i++)
            {
                SideObjectiveTrialTarget target = definition.SightTargets[i];
                Transform transform = Locate(target.Anchor, target.Path);
                if (transform == null)
                    continue;
                SideObjectiveSight.AddProp(transform, allowPointTarget: true);
                added++;
            }

            ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Sight targets for trial '{definition.Key}': {added} of {definition.SightTargets.Length} prop(s).");
        }

        /// <summary>
        /// The round's trial, searching within the budget. A trial whose identity object has gone
        /// (Wesley despawned it) is forgotten, and the remaining searches may find another. On the
        /// host, a trial whose probes did not all resolve is searched again while the budget lasts:
        /// dungeon props can still be spawning when the first search finds the trial.
        /// </summary>
        private static SideObjectiveTrialDefinition EnsureTrial(float now)
        {
            if (_definition != null)
            {
                if (_identity != null)
                {
                    bool incomplete = _probesResolved && (_degraded || _disabled);
                    if (!incomplete || _searches >= MaxTrialSearches || now < _nextSearchAt)
                        return _definition;

                    _searches++;
                    _nextSearchAt = now + TrialSearchIntervalSeconds;
                    Search();
                    return _definition;
                }

                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Trial '{_definition.Key}' is gone.");
                _definition = null;
                _identity = null;
                Anchors.Clear();
                ForgetProbes();
            }

            if (_searches >= MaxTrialSearches || now < _nextSearchAt)
                return null;

            _searches++;
            _nextSearchAt = now + TrialSearchIntervalSeconds;
            Search();
            return _definition;
        }

        private static void Search()
        {
            RoundManager round = RoundManager.Instance;
            string sceneName = round != null && round.currentLevel != null ? round.currentLevel.sceneName : null;

            // Built aside and committed only when a trial is identified, so a re-search that finds
            // nothing leaves the current trial, its anchors and its latches alone.
            WesleysTrialsCompat.FindTrialObjects(TrialObjects);
            TrialObjectNames.Clear();
            SearchAnchors.Clear();
            for (int i = 0; i < TrialObjects.Count; i++)
            {
                Component marker = TrialObjects[i];
                if (marker == null)
                    continue;
                string name = SideObjectiveTrialRules.StripClone(marker.gameObject.name);
                TrialObjectNames.Add(name);
                if (!SearchAnchors.ContainsKey(name))
                    SearchAnchors[name] = marker.transform;
            }

            SideObjectiveTrialDefinition definition = SideObjectiveTrialTable.Identify(TrialObjectNames, sceneName);
            if (definition == null)
            {
                if (_searches >= MaxTrialSearches && SideObjectiveTrialTable.HasUnknown(TrialObjectNames))
                {
                    string names = string.Join(", ", TrialObjectNames.ToArray());
                    LogOnce("unknown:" + names, $"Wesley trial objects [{names}] match no known trial; not tracked.");
                }

                TrialObjects.Clear();
                return;
            }

            Transform identity = null;
            if (definition.SceneName != null)
            {
                SearchAnchors.Clear();
                AddSceneAnchors(definition);
                SearchAnchors.TryGetValue(definition.IdentityAnchor, out identity);
            }
            else
            {
                for (int i = 0; i < TrialObjects.Count && identity == null; i++)
                {
                    Component marker = TrialObjects[i];
                    if (marker != null && SideObjectiveTrialRules.MatchesAnchor(marker.gameObject.name, definition.IdentityAnchor))
                        identity = marker.transform;
                }
            }

            TrialObjects.Clear();
            if (identity == null)
            {
                SearchAnchors.Clear();
                return;
            }

            // A re-search of the same trial keeps what the host has already seen: resolved probes
            // hand their latches to the new slots in ResolveProbes.
            bool same = ReferenceEquals(definition, _definition);
            if (same && _probesResolved)
            {
                _carryStepSlots = _stepSlots;
                _carryStepValues = _stepValues;
                _carryCompletionSlots = _completionSlots;
            }

            Anchors.Clear();
            foreach (KeyValuePair<string, Transform> pair in SearchAnchors)
                Anchors[pair.Key] = pair.Value;
            SearchAnchors.Clear();
            _definition = definition;
            _identity = identity;
            ForgetProbes();
            if (!same)
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.Side] Found Wesley trial '{definition.Key}' (search {_searches}).");
        }

        /// <summary>A scene trial's anchors: one <c>GameObject.Find</c> per distinct path, once per
        /// found trial.</summary>
        private static void AddSceneAnchors(SideObjectiveTrialDefinition definition)
        {
            AddSceneAnchor(definition.IdentityAnchor);
            for (int s = 0; s < definition.Steps.Length; s++)
            {
                SideObjectiveTrialProbe[] probes = definition.Steps[s].Probes;
                for (int p = 0; p < probes.Length; p++)
                    AddSceneAnchor(probes[p].Anchor);
            }

            if (definition.Completion != null)
            {
                for (int p = 0; p < definition.Completion.Probes.Length; p++)
                    AddSceneAnchor(definition.Completion.Probes[p].Anchor);
            }

            for (int t = 0; t < definition.SightTargets.Length; t++)
                AddSceneAnchor(definition.SightTargets[t].Anchor);
        }

        private static void AddSceneAnchor(string path)
        {
            if (string.IsNullOrEmpty(path) || SearchAnchors.ContainsKey(path))
                return;
            GameObject found = GameObject.Find(path);
            SearchAnchors[path] = found != null ? found.transform : null;
        }

        private static Transform Locate(string anchor, string path)
        {
            if (!Anchors.TryGetValue(anchor, out Transform root) || root == null)
                return null;
            return string.IsNullOrEmpty(path) ? root : root.Find(path);
        }

        private static void ForgetProbes()
        {
            _probesResolved = false;
            _degraded = false;
            _disabled = false;
            _stepSlots = Array.Empty<Slot>();
            _completionSlots = Array.Empty<Slot>();
            _stepValues = Array.Empty<int>();
        }

        /// <summary>Host-only, once per found trial: binds every probe to its object and decides
        /// whether the trial runs in full, falls back, or stands down.</summary>
        private static void ResolveProbes(SideObjectiveTrialDefinition definition)
        {
            _probesResolved = true;
            _stepSlots = new Slot[definition.StepProbeCount];
            _stepValues = new int[definition.StepProbeCount];
            StringBuilder failed = null;

            int index = 0;
            for (int s = 0; s < definition.Steps.Length; s++)
            {
                SideObjectiveTrialStep step = definition.Steps[s];
                for (int p = 0; p < step.Probes.Length; p++, index++)
                {
                    Slot slot = Resolve(step.Probes[p], step.Ticks);
                    _stepSlots[index] = slot;
                    _stepValues[index] = slot != null ? 0 : SideObjectiveTrialRules.Unresolved;
                    if (slot == null)
                        Describe(ref failed, step.Probes[p]);
                    else
                        _stepValues[index] = Carry(slot, _carryStepSlots, _carryStepValues, index);
                }
            }

            SideObjectiveTrialProbe[] completion = definition.Completion != null ? definition.Completion.Probes : Array.Empty<SideObjectiveTrialProbe>();
            _completionSlots = new Slot[completion.Length];
            var completionValues = new int[completion.Length];
            for (int p = 0; p < completion.Length; p++)
            {
                Slot slot = Resolve(completion[p], 1);
                _completionSlots[p] = slot;
                completionValues[p] = slot != null ? 0 : SideObjectiveTrialRules.Unresolved;
                if (slot != null)
                    slot.Latched = Carry(slot, _carryCompletionSlots, null, p);
            }

            _carryStepSlots = null;
            _carryStepValues = null;
            _carryCompletionSlots = null;

            _degraded = SideObjectiveTrialRules.IsDegraded(_stepValues);
            _disabled = !SideObjectiveTrialRules.CompletionReadable(completionValues);
            // Earlier searches retry (props may still be spawning); only the last one is a change.
            bool final = _searches >= MaxTrialSearches;
            if (_disabled && final)
            {
                LogOnce("disabled:" + definition.Key,
                    $"Trial '{definition.Key}' found, but its completion no longer resolves; it is not tracked. Wesley's Moons may have changed it.");
            }
            else if (_degraded && final)
            {
                LogOnce("degraded:" + definition.Key,
                    $"Trial '{definition.Key}': {failed} did not resolve; showing reveal and completion only. Wesley's Moons may have changed it.");
            }
        }

        private static void Describe(ref StringBuilder failed, SideObjectiveTrialProbe probe)
        {
            failed = failed ?? new StringBuilder();
            if (failed.Length > 0)
                failed.Append(", ");
            failed.Append(probe.Anchor);
            if (probe.Path.Length > 0)
                failed.Append('/').Append(probe.Path);
            failed.Append(" (").Append(probe.Kind).Append(')');
        }

        /// <summary>The slot for a probe, or null when its object, component, parameter or state is
        /// not there - or a counter no longer has the table's number of entries.</summary>
        private static Slot Resolve(SideObjectiveTrialProbe probe, int ticks)
        {
            Transform target = Locate(probe.Anchor, probe.Path);
            if (target == null)
                return null;

            var slot = new Slot { Kind = probe.Kind, Target = target.gameObject };
            try
            {
                switch (probe.Kind)
                {
                    case SideObjectiveTrialProbeKind.Counter:
                        slot.Counter = WesleysTrialsCompat.GetCounter(target);
                        if (!WesleysTrialsCompat.TryReadCounter(slot.Counter, out _, out int entries) || entries != ticks)
                            return null;
                        return slot;

                    case SideObjectiveTrialProbeKind.Active:
                        return slot;

                    case SideObjectiveTrialProbeKind.Inactive:
                        slot.Seen = slot.Target.activeSelf;
                        return slot;

                    case SideObjectiveTrialProbeKind.ColliderOff:
                        slot.Collider = target.GetComponent<Collider>();
                        if (slot.Collider == null)
                            return null;
                        slot.Seen = slot.Collider.enabled && slot.Target.activeInHierarchy;
                        return slot;

                    case SideObjectiveTrialProbeKind.AnimatorBool:
                        slot.Animator = target.GetComponent<Animator>();
                        if (slot.Animator == null || probe.Names.Length == 0 || !HasBool(slot.Animator, probe.Names[0]))
                            return null;
                        slot.BoolHash = Animator.StringToHash(probe.Names[0]);
                        return slot;

                    case SideObjectiveTrialProbeKind.AnimatorState:
                        slot.Animator = target.GetComponent<Animator>();
                        if (slot.Animator == null || probe.Names.Length == 0)
                            return null;
                        slot.StateHashes = new int[probe.Names.Length];
                        string layer = slot.Animator.GetLayerName(0);
                        for (int i = 0; i < probe.Names.Length; i++)
                        {
                            // The poll compares short-name hashes; either hash form proving the
                            // state exists is enough, so HasState's form never decides degradation.
                            slot.StateHashes[i] = Animator.StringToHash(probe.Names[i]);
                            if (!slot.Animator.HasState(0, slot.StateHashes[i])
                                && !slot.Animator.HasState(0, Animator.StringToHash(layer + "." + probe.Names[i])))
                            {
                                return null;
                            }
                        }

                        return slot;

                    default:
                        return null;
                }
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// The starting value for a freshly resolved slot: the previous slot's latch at the same index
        /// when a re-search replaced it, carrying its seen-edge too; 0 otherwise.
        /// </summary>
        private static int Carry(Slot slot, Slot[] previousSlots, int[] previousValues, int index)
        {
            if (previousSlots == null || index >= previousSlots.Length || previousSlots[index] == null)
                return 0;

            Slot previous = previousSlots[index];
            slot.Seen |= previous.Seen;
            int value = previousValues != null && index < previousValues.Length ? previousValues[index] : previous.Latched;
            return Math.Max(value, 0);
        }

        private static bool HasBool(Animator animator, string name)
        {
            AnimatorControllerParameter[] parameters = animator.parameters;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (parameters[i].type == AnimatorControllerParameterType.Bool && string.Equals(parameters[i].name, name, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        /// <summary>One host read, latched: a slot whose object has gone keeps its value.</summary>
        private static int Read(Slot slot, int latched)
        {
            if (slot == null)
                return latched;

            int reading = SideObjectiveTrialRules.Unresolved;
            switch (slot.Kind)
            {
                case SideObjectiveTrialProbeKind.Counter:
                    if (WesleysTrialsCompat.TryReadCounter(slot.Counter, out int amount, out _))
                        reading = amount;
                    break;

                case SideObjectiveTrialProbeKind.Active:
                    if (slot.Target != null)
                        reading = slot.Target.activeSelf ? 1 : 0;
                    break;

                case SideObjectiveTrialProbeKind.Inactive:
                    if (slot.Target != null)
                    {
                        bool active = slot.Target.activeSelf;
                        slot.Seen |= active;
                        reading = SideObjectiveTrialRules.InactiveReached(slot.Seen, active) ? 1 : 0;
                    }

                    break;

                case SideObjectiveTrialProbeKind.ColliderOff:
                    if (slot.Collider != null)
                    {
                        bool inPlay = slot.Collider.gameObject.activeInHierarchy;
                        bool enabled = slot.Collider.enabled;
                        slot.Seen |= inPlay && enabled;
                        reading = SideObjectiveTrialRules.ColliderOffReached(slot.Seen, inPlay, enabled) ? 1 : 0;
                    }

                    break;

                case SideObjectiveTrialProbeKind.AnimatorBool:
                    if (slot.Animator != null)
                        reading = slot.Animator.isActiveAndEnabled && slot.Animator.GetBool(slot.BoolHash) ? 1 : 0;
                    break;

                case SideObjectiveTrialProbeKind.AnimatorState:
                    if (slot.Animator != null)
                        reading = slot.Animator.isActiveAndEnabled && InStates(slot.Animator, slot.StateHashes) ? 1 : 0;
                    break;
            }

            return SideObjectiveTrialRules.Latch(latched, reading);
        }

        private static bool InStates(Animator animator, int[] hashes)
        {
            int current = animator.GetCurrentAnimatorStateInfo(0).shortNameHash;
            int next = animator.IsInTransition(0) ? animator.GetNextAnimatorStateInfo(0).shortNameHash : 0;
            for (int i = 0; i < hashes.Length; i++)
            {
                if (hashes[i] == current || (next != 0 && hashes[i] == next))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Host-only: the trial ticket has been picked up, or found and gone since (fed to Release's
        /// ticket machine). Bounded: at most <see cref="MaxTicketSearches"/> scene searches, only after
        /// the completion, for a ticket this round spawned on the moon.
        /// </summary>
        private static bool IsTicketTaken(SideObjectiveTrialDefinition definition, float now)
        {
            if (_ticketTaken)
                return true;

            if (_ticket == null)
            {
                if (_ticketFound)
                {
                    _ticketTaken = true;
                    return true;
                }

                if (_ticketSearches >= MaxTicketSearches || now < _nextTicketSearchAt)
                    return false;

                _ticketSearches++;
                _nextTicketSearchAt = now + TicketSearchIntervalSeconds;
                _ticket = FindTicket(definition.TicketItemName);
                if (_ticket == null)
                {
                    if (_ticketSearches >= MaxTicketSearches)
                        LogOnce("ticket:" + definition.Key, $"No '{definition.TicketItemName}' found after trial '{definition.Key}' completed; its claim step stays open.");
                    return false;
                }

                _ticketFound = true;
            }

            _ticketTaken = _ticket.isHeld || _ticket.hasBeenHeld;
            return _ticketTaken;
        }

        private static GrabbableObject FindTicket(string itemName)
        {
            GrabbableObject[] items = UnityEngine.Object.FindObjectsOfType<GrabbableObject>();
            for (int i = 0; i < items.Length; i++)
            {
                GrabbableObject item = items[i];
                if (item == null || item.itemProperties == null || item.isInShipRoom || item.scrapPersistedThroughRounds)
                    continue;
                if (string.Equals(item.itemProperties.itemName, itemName, StringComparison.OrdinalIgnoreCase))
                    return item;
            }

            return null;
        }

        private static void LogOnce(string key, string message)
        {
            if (Logged.Add(key))
                ContractsBootstrap.Log?.LogWarning("[MoonContracts.Trials] " + message);
        }
    }
}
