using System;
using System.Collections.Generic;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>#1198. What one trial probe reads on the host.</summary>
    internal enum SideObjectiveTrialProbeKind
    {
        /// <summary>A Wesley <c>TriggerCounter</c>'s count. The host's own count: the server RPC
        /// increments it and every peer's hover tip shows it.</summary>
        Counter = 0,

        /// <summary>The GameObject is active. Synced events and trial animations switch it on every
        /// peer.</summary>
        Active = 1,

        /// <summary>The GameObject was seen active and is now inactive.</summary>
        Inactive = 2,

        /// <summary>The Collider was seen enabled and is now disabled, while its GameObject is active in
        /// the hierarchy. A random pick's unused copies stay inactive and never count.</summary>
        ColliderOff = 3,

        /// <summary>An Animator bool parameter is true (<see cref="SideObjectiveTrialProbe.Names"/>[0]).</summary>
        AnimatorBool = 4,

        /// <summary>The Animator's base layer is in, or moving into, one of
        /// <see cref="SideObjectiveTrialProbe.Names"/>. The list names the state and every state after
        /// it, so a poll that misses a short state still counts it.</summary>
        AnimatorState = 5,
    }

    /// <summary>#1198. One host-readable signal: a probe kind on the object at
    /// <see cref="Path"/> under the anchor <see cref="Anchor"/>.</summary>
    internal sealed class SideObjectiveTrialProbe
    {
        internal SideObjectiveTrialProbe(SideObjectiveTrialProbeKind kind, string anchor, string path, params string[] names)
        {
            Kind = kind;
            Anchor = anchor ?? string.Empty;
            Path = path ?? string.Empty;
            Names = names ?? Array.Empty<string>();
        }

        internal SideObjectiveTrialProbeKind Kind { get; }

        /// <summary>A <c>TrialObject</c> GameObject name, or a scene path for a trial with
        /// <see cref="SideObjectiveTrialDefinition.SceneName"/>.</summary>
        internal string Anchor { get; }

        /// <summary>A <c>Transform.Find</c> path under the anchor; empty for the anchor itself.</summary>
        internal string Path { get; }

        internal string[] Names { get; }
    }

    /// <summary>
    /// #1198. One card step. A single <see cref="SideObjectiveTrialProbeKind.Counter"/> probe gives
    /// its count; otherwise each satisfied probe is one tick. The count is capped at
    /// <see cref="Ticks"/>, so a step that lists the alternatives of a random pick ("any of the four
    /// fuel inlets") has one tick and many probes.
    /// </summary>
    internal sealed class SideObjectiveTrialStep
    {
        internal SideObjectiveTrialStep(string label, int ticks, params SideObjectiveTrialProbe[] probes)
        {
            Label = label ?? string.Empty;
            Ticks = Math.Max(ticks, 1);
            Probes = probes ?? Array.Empty<SideObjectiveTrialProbe>();
        }

        /// <summary>The player-facing action for this step; the card displays it with the aggregate
        /// trial count while cumulative tick order selects this step.</summary>
        internal string Label { get; }

        internal int Ticks { get; }

        internal SideObjectiveTrialProbe[] Probes { get; }

        internal bool IsCounter => Probes.Length == 1 && Probes[0].Kind == SideObjectiveTrialProbeKind.Counter;
    }

    /// <summary>#1198. A sight target: the renderers under the object at <see cref="Path"/>.</summary>
    internal readonly struct SideObjectiveTrialTarget
    {
        internal SideObjectiveTrialTarget(string anchor, string path)
        {
            Anchor = anchor ?? string.Empty;
            Path = path ?? string.Empty;
        }

        internal string Anchor { get; }
        internal string Path { get; }
    }

    /// <summary>#1198. One Wesley's Moons trial: how it is recognised, its steps, its completion,
    /// its reward and what a crewmate has to see.</summary>
    internal sealed class SideObjectiveTrialDefinition
    {
        internal SideObjectiveTrialDefinition(
            string key,
            string title,
            string claimVerb,
            string recoveredText,
            string ticketItemName,
            string sceneName,
            string identityAnchor,
            SideObjectiveTrialStep[] steps,
            SideObjectiveTrialStep completion,
            SideObjectiveTrialTarget[] sightTargets)
        {
            Key = key;
            SceneName = sceneName;
            IdentityAnchor = identityAnchor;
            TicketItemName = ticketItemName;
            Steps = steps ?? Array.Empty<SideObjectiveTrialStep>();
            Completion = completion;
            SightTargets = sightTargets ?? Array.Empty<SideObjectiveTrialTarget>();
            Copy = new SideObjectiveCopy(key, key, title, claimVerb, SideObjectiveSightKind.Prop, recoveredText);

            int required = 0;
            int probes = 0;
            for (int i = 0; i < Steps.Length; i++)
            {
                required += Steps[i].Ticks;
                probes += Steps[i].Probes.Length;
            }

            Required = required;
            StepProbeCount = probes;
        }

        /// <summary>The snapshot's scope and entry.</summary>
        internal string Key { get; }

        /// <summary>Set for a trial with no <c>TrialObject</c> marker (Roart): the scene it lives in,
        /// and its anchors are scene paths.</summary>
        internal string SceneName { get; }

        /// <summary>The anchor whose presence identifies the trial. A <c>TrialObject</c> name matches
        /// itself and its numbered copies ("GrinderMachineContainer (3)").</summary>
        internal string IdentityAnchor { get; }

        /// <summary>The trial ticket the reward drops; null when the trial drops none.</summary>
        internal string TicketItemName { get; }

        internal SideObjectiveTrialStep[] Steps { get; }

        /// <summary>One tick: any satisfied probe completes the trial.</summary>
        internal SideObjectiveTrialStep Completion { get; }

        internal SideObjectiveTrialTarget[] SightTargets { get; }

        internal SideObjectiveCopy Copy { get; }

        /// <summary>The card total: every step's ticks.</summary>
        internal int Required { get; }

        /// <summary>Step probes in step order, the index space of <see cref="SideObjectiveTrialRules.Tally"/>.</summary>
        internal int StepProbeCount { get; }
    }

    /// <summary>#1198. Where a trial reading stands.</summary>
    internal readonly struct SideObjectiveTrialProgress
    {
        internal SideObjectiveTrialProgress(int count, int required)
        {
            Count = count;
            Required = required;
        }

        internal int Count { get; }
        internal int Required { get; }
    }

    /// <summary>
    /// #1198. The six Wesley's Moons trials, built by hand from the Wesley's Moons 7.0.13 and
    /// Wesley's Interiors bundles (scene wiring, animator state machines and animation curves). The
    /// trial names are the Galetry videotapes' item names ("Reshape the incapable videotape" unlocks
    /// ReleaseScene); Roart's "Bury the child" tape unlocks no scene and is matched by Emma's body and
    /// head, so it is INFERRED. Step labels are the in-game hover tips where there are any.
    ///
    /// Every probe reads state the host holds itself: the TriggerCounter count the server RPC raises,
    /// and GameObject, Collider and Animator state that Wesley's synced events (JLL JClientSync,
    /// ItemConsumer with runEventOnAllClients, RadioSender/RadioReceiver chains run from those) set on
    /// every peer, the host included.
    ///
    /// Unity-free and Netcode-free: tests/ContractHudPresentationRegression links this file.
    /// </summary>
    internal static class SideObjectiveTrialTable
    {
        internal const string Release = "Release";
        internal const string Crowed = "Crowed";
        internal const string Descent = "Descent";
        internal const string Extort = "Extort";
        internal const string Narcotic = "Narcotic";
        internal const string Roart = "Roart";

        internal const string RoartSceneName = "RoartScene";
        internal const string ClaimTicket = "Collect the ticket";
        internal const string TrialCompleteText = "Trial complete";

        internal static readonly SideObjectiveCopy Fallback = new SideObjectiveCopy(
            string.Empty, string.Empty, "Complete the trial", SideObjectiveCopyTable.FallbackClaimVerb, SideObjectiveSightKind.Prop);

        private const SideObjectiveTrialProbeKind Counter = SideObjectiveTrialProbeKind.Counter;
        private const SideObjectiveTrialProbeKind Active = SideObjectiveTrialProbeKind.Active;
        private const SideObjectiveTrialProbeKind Inactive = SideObjectiveTrialProbeKind.Inactive;
        private const SideObjectiveTrialProbeKind ColliderOff = SideObjectiveTrialProbeKind.ColliderOff;
        private const SideObjectiveTrialProbeKind Bool = SideObjectiveTrialProbeKind.AnimatorBool;
        private const SideObjectiveTrialProbeKind State = SideObjectiveTrialProbeKind.AnimatorState;

        private static SideObjectiveTrialProbe P(SideObjectiveTrialProbeKind kind, string anchor, string path, params string[] names)
            => new SideObjectiveTrialProbe(kind, anchor, path, names);

        private static SideObjectiveTrialStep S(string label, int ticks, params SideObjectiveTrialProbe[] probes)
            => new SideObjectiveTrialStep(label, ticks, probes);

        private static SideObjectiveTrialTarget T(string anchor, string path = "") => new SideObjectiveTrialTarget(anchor, path);

        private static readonly SideObjectiveTrialDefinition[] Definitions =
        {
            // Release, Expanded Facility. Parts: ItemConsumer -> TriggerCounter (3 entries, bools
            // InsertA-C). Gas tank: ItemConsumer (all clients) shows GeneratorContainer/GasTank and
            // radios TrialVictimThing (InsertD). Button: JClientSync -> InsertE -> VictimStageF, then
            // DelayScript 32 s -> radio -> RevelationDoorOpen hides the door at 5 s and drops a
            // bloodied diamond and the ticket.
            new SideObjectiveTrialDefinition(
                Release, "Reshape the incapable", ClaimTicket, null, "Reshape the incapable ticket",
                null, "TrialVictimThing",
                new[]
                {
                    S("Insert machine parts", 3, P(Counter, "TrialVictimThing", "InsertTrigger")),
                    S("Use gas tank", 1, P(Active, "GeneratorContainer", "GasTank")),
                    S("Press button", 1, P(State, "TrialVictimThing", "", "VictimStageF")),
                },
                S("Revelation door open", 1, P(Inactive, "RevelationDoorContainer", "RevelationDoor")),
                new[] { T("TrialVictimThing"), T("GeneratorContainer"), T("RevelationDoorContainer") }),

            // Crowed (moon "328 Crowd"), Dilapidated Manor. Radios: JClientSync -> RadioSender ->
            // RadioReceiver -> AnimatedObjectTrigger.SetBoolOnClientOnly sets BigButton PressA-C.
            // First press: PressHalf (HangingCrossStageB); it drops a gas tank. Generator:
            // ItemConsumer (all clients) shows GasTank. Second press: PressFull (HangingCrossStageD),
            // which shows Liverspawner at 9.8 s: a diamond-filled liver and the ticket.
            new SideObjectiveTrialDefinition(
                Crowed, "Teach the disloyal", ClaimTicket, null, "Teach the disloyal ticket",
                null, "HangingCross",
                new[]
                {
                    S("Switch channel", 3,
                        P(Bool, "HangingCross", "BigButton", "PressA"),
                        P(Bool, "HangingCross", "BigButton", "PressB"),
                        P(Bool, "HangingCross", "BigButton", "PressC")),
                    S("Press", 1, P(State, "HangingCross", "", "HangingCrossStageB", "HangingCrossStageC", "HangingCrossStageD")),
                    S("Use gas tank", 1, P(Active, "GeneratorContainer", "GasTank")),
                    S("Press", 1, P(State, "HangingCross", "", "HangingCrossStageD")),
                },
                S("Liver spawned", 1, P(Active, "HangingCross", "Liverspawner")),
                new[] { T("HangingCross"), T("GeneratorContainer"), T("RadioContainer"), T("RadioContainer 1"), T("RadioContainer 2") }),

            // Descent, Extended Mineshaft. Articles: TriggerCounter (8 entries, StateB-StateP). Button:
            // StateR. Valves: the right valve per set radios a ReceiverManager whose AnimationManager
            // sets StateS1/S2/S3 on the rack animator. Second button: StateV. Big valve: StateWebley
            // (RackAnimStateW), which shows CharredDiamond at 20 s: a charred diamond and the ticket.
            new SideObjectiveTrialDefinition(
                Descent, "Incinerate the lies", ClaimTicket, null, "Incinerate the lies ticket",
                null, "RackAnimContainer",
                new[]
                {
                    S("Insert false article", 8, P(Counter, "RackAnimContainer", "ArticleInserter")),
                    S("Press", 1, P(State, "RackAnimContainer", "",
                        "RackAnimStateR", "RackAnimStateS", "RackAnimStateS 0", "RackAnimStateS 0 0",
                        "RackAnimStateT", "RackAnimStateU", "RackAnimStateV", "RackAnimStateW")),
                    S("Turn valve", 3,
                        P(Bool, "RackAnimContainer", "", "StateS1"),
                        P(Bool, "RackAnimContainer", "", "StateS2"),
                        P(Bool, "RackAnimContainer", "", "StateS3")),
                    S("Press", 1, P(State, "RackAnimContainer", "", "RackAnimStateV", "RackAnimStateW")),
                    S("Turn valve", 1, P(State, "RackAnimContainer", "", "RackAnimStateW")),
                },
                S("Charred diamond spawned", 1, P(Active, "RackAnimContainer", "CharredDiamond")),
                new[] { T("RackAnimContainer"), T("ValveSetA"), T("ValveSetB"), T("ValveSetC") }),

            // Extort, outdoors. Three of six grinders survive a seeded pick; fuelling one (ItemConsumer,
            // all clients) plays GrinderGrind, which shows its GasTank. Gore: TriggerCounter (3
            // entries). Button: JClientSync -> send (StufferSend). Blue key: ItemConsumer ->
            // AnimatedObjectTrigger key (StufferGrind), which shows diamondSpawner at 17 s: small
            // diamonds and the ticket.
            new SideObjectiveTrialDefinition(
                Extort, "Grind the deluded", ClaimTicket, null, "Grind the deluded ticket",
                null, "GrinderMachineContainer",
                new[]
                {
                    S("Use gas tank", 3,
                        P(Active, "Trial", "GrinderMachineContainer/GasTank"),
                        P(Active, "Trial", "GrinderMachineContainer (1)/GasTank"),
                        P(Active, "Trial", "GrinderMachineContainer (2)/GasTank"),
                        P(Active, "Trial", "GrinderMachineContainer (3)/GasTank"),
                        P(Active, "Trial", "GrinderMachineContainer (4)/GasTank"),
                        P(Active, "Trial", "GrinderMachineContainer (5)/GasTank")),
                    S("Insert gore", 3, P(Counter, "Trial", "StufferContainer/GoreInsert1")),
                    S("Press", 1, P(State, "Trial", "StufferContainer", "StufferSend", "StufferSendPost", "StufferGrind")),
                    S("Insert Blue key", 1, P(State, "Trial", "StufferContainer", "StufferGrind")),
                },
                S("Diamonds spawned", 1, P(Active, "Trial", "StufferContainer/diamondSpawner")),
                new[]
                {
                    T("Trial", "StufferContainer"),
                    T("Trial", "GrinderMachineContainer"), T("Trial", "GrinderMachineContainer (1)"),
                    T("Trial", "GrinderMachineContainer (2)"), T("Trial", "GrinderMachineContainer (3)"),
                    T("Trial", "GrinderMachineContainer (4)"), T("Trial", "GrinderMachineContainer (5)"),
                }),

            // Narcotic, outdoors. One of four fuel inlets survives a seeded pick (the rest are
            // explosive decoys); fuelling it (ItemConsumer, all clients) shows its GasTank. Acid:
            // TriggerCounter (2 entries), then StageD shows RewardSpawner at 16 s: small diamonds and
            // the ticket.
            new SideObjectiveTrialDefinition(
                Narcotic, "Medicate the unwell", ClaimTicket, null, "Medicate the unwell ticket",
                null, "AcidTrapContainer",
                new[]
                {
                    S("Use gas tank", 1,
                        P(Active, "AcidTrapContainer", "FuelTrigger/GasTank"),
                        P(Active, "AcidTrapContainer", "FuelTrigger (1)/GasTank"),
                        P(Active, "AcidTrapContainer", "FuelTrigger (2)/GasTank"),
                        P(Active, "AcidTrapContainer", "FuelTrigger (3)/GasTank")),
                    S("Use acid canister", 2, P(Counter, "AcidTrapContainer", "Poison")),
                },
                S("Reward spawned", 1, P(Active, "AcidTrapContainer", "RewardSpawner")),
                new[] { T("AcidTrapContainer") }),

            // Roart, outdoors, no TrialObject and no gate: Environment/RitualStuff is always in the
            // scene. One of four nooses and one of four graves survive seeded picks. Remove body:
            // JClientSync disables the noose trigger's collider. Place body: ItemConsumer (all
            // clients) -> placebody (TableAnimA2). Blade: CutB-CutD (TableAnimB-D). Collect head:
            // JClientSync hides Table/InteractTrigger (4). Bury head: ItemConsumer (all clients) shows
            // the grave's DirtPileTop. No reward item, so the claim is the burial itself.
            new SideObjectiveTrialDefinition(
                Roart, "Bury the child", SideObjectiveCopyTable.FallbackClaimVerb, TrialCompleteText, null,
                RoartSceneName, "Environment/RitualStuff",
                new[]
                {
                    S("Remove body", 1,
                        P(ColliderOff, "Environment/RitualStuff", "GhostGirlNooseSpot/InteractTrigger"),
                        P(ColliderOff, "Environment/RitualStuff", "GhostGirlNooseSpot (1)/InteractTrigger"),
                        P(ColliderOff, "Environment/RitualStuff", "GhostGirlNooseSpot (2)/InteractTrigger"),
                        P(ColliderOff, "Environment/RitualStuff", "GhostGirlNooseSpot (3)/InteractTrigger")),
                    S("Place body", 1, P(State, "Environment/RitualStuff", "Table",
                        "TableAnimA2", "TableAnimA", "TableAnimB", "TableAnimC", "TableAnimD")),
                    S("Use blade", 3,
                        P(State, "Environment/RitualStuff", "Table", "TableAnimB", "TableAnimC", "TableAnimD"),
                        P(State, "Environment/RitualStuff", "Table", "TableAnimC", "TableAnimD"),
                        P(State, "Environment/RitualStuff", "Table", "TableAnimD")),
                    S("Collect head", 1, P(Inactive, "Environment/RitualStuff", "Table/InteractTrigger (4)")),
                },
                S("Bury head", 1,
                    P(Active, "Environment/RitualStuff", "DirtPile/DirtPileTop"),
                    P(Active, "Environment/RitualStuff", "DirtPile (1)/DirtPileTop"),
                    P(Active, "Environment/RitualStuff", "DirtPile (2)/DirtPileTop"),
                    P(Active, "Environment/RitualStuff", "DirtPile (3)/DirtPileTop")),
                new[]
                {
                    T("Environment/RitualStuff", "Table"),
                    T("Environment/RitualStuff", "GhostGirlNooseSpot"), T("Environment/RitualStuff", "GhostGirlNooseSpot (1)"),
                    T("Environment/RitualStuff", "GhostGirlNooseSpot (2)"), T("Environment/RitualStuff", "GhostGirlNooseSpot (3)"),
                    T("Environment/RitualStuff", "DirtPile"), T("Environment/RitualStuff", "DirtPile (1)"),
                    T("Environment/RitualStuff", "DirtPile (2)"), T("Environment/RitualStuff", "DirtPile (3)"),
                }),
        };

        internal static int Count => Definitions.Length;

        internal static SideObjectiveTrialDefinition At(int index)
        {
            return Definitions[index];
        }

        internal static SideObjectiveTrialDefinition Find(string key)
        {
            if (string.IsNullOrEmpty(key))
                return null;

            for (int i = 0; i < Definitions.Length; i++)
            {
                if (string.Equals(Definitions[i].Key, key, StringComparison.Ordinal))
                    return Definitions[i];
            }

            return null;
        }

        /// <summary>
        /// The action at the first cumulative tick boundary not reached by the aggregate count.
        /// Counts alone cannot identify a later step completed out of order. A degraded reading
        /// (0/1), or all ticks reached before completion, has no identifiable pending action.
        /// </summary>
        internal static string ProgressLabel(SideObjectiveTrialDefinition definition, int count, int required)
        {
            if (definition == null || required != definition.Required || count >= required)
                return Fallback.Title;

            int remaining = Math.Max(count, 0);
            for (int i = 0; i < definition.Steps.Length; i++)
            {
                SideObjectiveTrialStep step = definition.Steps[i];
                if (remaining < step.Ticks)
                    return step.Label;
                remaining -= step.Ticks;
            }

            return Fallback.Title;
        }

        /// <summary>
        /// Which trial this round holds. A scene trial is identified by the level's scene name; a
        /// marker trial by its identity anchor among the live <c>TrialObject</c> names (Wesley
        /// despawns them when its [Trials] gates are off, so a gated trial has none). Null for none,
        /// and for marker objects that match no known trial.
        /// </summary>
        internal static SideObjectiveTrialDefinition Identify(IList<string> trialObjectNames, string sceneName)
        {
            for (int i = 0; i < Definitions.Length; i++)
            {
                SideObjectiveTrialDefinition definition = Definitions[i];
                if (definition.SceneName != null)
                {
                    if (string.Equals(definition.SceneName, sceneName, StringComparison.Ordinal))
                        return definition;
                    continue;
                }

                if (trialObjectNames == null)
                    continue;
                for (int n = 0; n < trialObjectNames.Count; n++)
                {
                    if (SideObjectiveTrialRules.MatchesAnchor(trialObjectNames[n], definition.IdentityAnchor))
                        return definition;
                }
            }

            return null;
        }

        /// <summary>
        /// True when <paramref name="objectName"/> is an anchor some known trial uses. Extort's
        /// <c>Trial</c> root carries a marker but no NetworkObject, so it outlives Wesley's gate
        /// despawn; a leftover like that is not a new trial.
        /// </summary>
        internal static bool IsKnownAnchor(string objectName)
        {
            for (int i = 0; i < Definitions.Length; i++)
            {
                SideObjectiveTrialDefinition definition = Definitions[i];
                if (SideObjectiveTrialRules.MatchesAnchor(objectName, definition.IdentityAnchor))
                    return true;
                for (int s = 0; s < definition.Steps.Length; s++)
                {
                    if (UsesAnchor(definition.Steps[s], objectName))
                        return true;
                }

                if (UsesAnchor(definition.Completion, objectName))
                    return true;
                for (int t = 0; t < definition.SightTargets.Length; t++)
                {
                    if (SideObjectiveTrialRules.MatchesAnchor(objectName, definition.SightTargets[t].Anchor))
                        return true;
                }
            }

            return false;
        }

        /// <summary>True when any name in <paramref name="trialObjectNames"/> belongs to no known trial.</summary>
        internal static bool HasUnknown(IList<string> trialObjectNames)
        {
            if (trialObjectNames == null)
                return false;
            for (int i = 0; i < trialObjectNames.Count; i++)
            {
                if (!IsKnownAnchor(trialObjectNames[i]))
                    return true;
            }

            return false;
        }

        private static bool UsesAnchor(SideObjectiveTrialStep step, string objectName)
        {
            if (step == null)
                return false;
            for (int p = 0; p < step.Probes.Length; p++)
            {
                if (SideObjectiveTrialRules.MatchesAnchor(objectName, step.Probes[p].Anchor))
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// #1198. How probe readings become the snapshot. Unity-free so the rules are pinned.
    /// A probe value is its count for a counter, 1 or 0 otherwise, and <see cref="Unresolved"/> when
    /// the object behind it could not be found.
    /// </summary>
    internal static class SideObjectiveTrialRules
    {
        internal const int Unresolved = -1;
        private const string CloneSuffix = "(Clone)";

        /// <summary>A runtime GameObject name without the "(Clone)" a network spawn adds.</summary>
        internal static string StripClone(string name)
        {
            if (string.IsNullOrEmpty(name))
                return string.Empty;
            return name.EndsWith(CloneSuffix, StringComparison.Ordinal)
                ? name.Substring(0, name.Length - CloneSuffix.Length).TrimEnd()
                : name;
        }

        /// <summary>The anchor itself, or one of its numbered copies ("Anchor (2)").</summary>
        internal static bool MatchesAnchor(string objectName, string anchor)
        {
            if (string.IsNullOrEmpty(anchor))
                return false;
            string name = StripClone(objectName);
            if (string.Equals(name, anchor, StringComparison.Ordinal))
                return true;
            return name.Length > anchor.Length + 2
                && name.StartsWith(anchor, StringComparison.Ordinal)
                && name[anchor.Length] == ' '
                && name[anchor.Length + 1] == '('
                && name[name.Length - 1] == ')';
        }

        /// <summary>Host latch: a trial step never goes back, so a reading only ever raises the value,
        /// and a probe that stops reading keeps what it had.</summary>
        internal static int Latch(int latched, int reading)
        {
            if (reading == Unresolved)
                return latched;
            return latched == Unresolved ? reading : Math.Max(latched, reading);
        }

        /// <summary><see cref="SideObjectiveTrialProbeKind.Inactive"/>: only after it was seen active,
        /// so an object that simply starts hidden cannot complete a step.</summary>
        internal static bool InactiveReached(bool seenActive, bool activeNow)
        {
            return seenActive && !activeNow;
        }

        /// <summary><see cref="SideObjectiveTrialProbeKind.ColliderOff"/>: seen enabled first, and the
        /// object is in play (a random pick's inactive copies never count).</summary>
        internal static bool ColliderOffReached(bool seenEnabled, bool activeInHierarchy, bool enabledNow)
        {
            return seenEnabled && activeInHierarchy && !enabledNow;
        }

        /// <summary>One step's ticks from its latched probe values starting at <paramref name="offset"/>.</summary>
        internal static int StepTicks(SideObjectiveTrialStep step, IList<int> values, int offset)
        {
            if (step == null || values == null)
                return 0;

            if (step.IsCounter)
                return offset < values.Count ? Clamp(values[offset], 0, step.Ticks) : 0;

            int satisfied = 0;
            for (int i = 0; i < step.Probes.Length && offset + i < values.Count; i++)
            {
                if (values[offset + i] > 0)
                    satisfied++;
            }

            return Math.Min(satisfied, step.Ticks);
        }

        /// <summary>
        /// The card count and total. A changed trial (any step probe unresolved) falls back to reveal
        /// plus completion only: 0 of 1 until the completion moves it on.
        /// </summary>
        internal static SideObjectiveTrialProgress Tally(SideObjectiveTrialDefinition definition, IList<int> stepValues, bool degraded)
        {
            if (definition == null || degraded)
                return new SideObjectiveTrialProgress(0, 1);

            int count = 0;
            int offset = 0;
            for (int i = 0; i < definition.Steps.Length; i++)
            {
                SideObjectiveTrialStep step = definition.Steps[i];
                count += StepTicks(step, stepValues, offset);
                offset += step.Probes.Length;
            }

            return new SideObjectiveTrialProgress(count, definition.Required);
        }

        /// <summary>True when any step probe did not resolve: the trial falls back.</summary>
        internal static bool IsDegraded(IList<int> resolvedStepValues)
        {
            if (resolvedStepValues == null)
                return true;
            for (int i = 0; i < resolvedStepValues.Count; i++)
            {
                if (resolvedStepValues[i] == Unresolved)
                    return true;
            }

            return false;
        }

        /// <summary>
        /// The completion needs one resolved probe. A step list of random-pick alternatives resolves
        /// when any of them does; with none there is no completion to read, and the trial stands down.
        /// </summary>
        internal static bool CompletionReadable(IList<int> resolvedCompletionValues)
        {
            if (resolvedCompletionValues == null)
                return false;
            for (int i = 0; i < resolvedCompletionValues.Count; i++)
            {
                if (resolvedCompletionValues[i] != Unresolved)
                    return true;
            }

            return false;
        }

        /// <summary>A trial with a ticket is claimed when the ticket is taken; one without (Roart) is
        /// claimed by the completion itself.</summary>
        internal static bool RewardClaimed(SideObjectiveTrialDefinition definition, bool completed, bool ticketTaken)
        {
            return completed && (definition == null || definition.TicketItemName == null || ticketTaken);
        }

        /// <summary>
        /// One source at a time on the card: the Wesley's Interiors secondary when it has one, else
        /// the trial. Under Wesley's defaults they never meet (secondaries despawn on IndoorTrials
        /// moons); with "Disable with trials" or "Only on trial moons" off they can, and the
        /// secondary keeps the card so its credits stay consistent.
        /// </summary>
        internal static SideObjectiveSnapshot SelectReading(in SideObjectiveSnapshot interior, in SideObjectiveSnapshot trial)
        {
            return interior.HasObjective ? interior : trial;
        }

        private static int Clamp(int value, int min, int max)
        {
            return value < min ? min : value > max ? max : value;
        }
    }
}
