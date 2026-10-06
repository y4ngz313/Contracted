using System;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>What a peer looks at to sight a side objective (#1195).</summary>
    internal enum SideObjectiveSightKind
    {
        /// <summary>The renderers of the objective's own props. A prop with no renderer is sighted at
        /// its transform.</summary>
        Prop = 0,

        /// <summary>The live enemies a renderer-less spawner prop puts into the level, matched by the
        /// <c>EnemyType</c> the spawner holds. The spawner itself is never a target.</summary>
        SpawnedEnemy = 1,
    }

    /// <summary>Player-facing copy for one side objective.</summary>
    internal sealed class SideObjectiveCopy
    {
        internal SideObjectiveCopy(string scope, string entry, string title, string claimVerb, SideObjectiveSightKind sight,
            string recoveredText = null)
        {
            Scope = scope;
            Entry = entry;
            Title = title;
            ClaimVerb = claimVerb;
            Sight = sight;
            RecoveredText = recoveredText ?? SideObjectivePolicy.RecoveredText;
        }

        /// <summary>The interior id; empty for the fallback.</summary>
        internal string Scope { get; }

        /// <summary>Wesley's internal entry key (<c>OptionalObjectiveEntry.Name</c>); empty for the fallback.</summary>
        internal string Entry { get; }

        /// <summary>The objective name, sentence case.</summary>
        internal string Title { get; }

        /// <summary>The reward-claim step, sentence case.</summary>
        internal string ClaimVerb { get; }

        internal SideObjectiveSightKind Sight { get; }

        /// <summary>The recovered row: "Reward recovered", or a trial's own words when it drops no
        /// reward (#1198).</summary>
        internal string RecoveredText { get; }
    }

    /// <summary>
    /// #1195. Copy for all 39 Wesley's Interiors secondary objectives, keyed by interior and entry.
    ///
    /// Wesley's entry names are internal keys; the player-facing names exist only in Wesley's
    /// Interiors CHANGELOG under 5.0.0 ("Added 3 randomised optional objectives"), listed per
    /// interior in the same order as the bundle's <c>ObjectiveEntries</c>. Claim verbs come from each
    /// controller's <c>EnabledTriggerName</c> ("Open case" reads "Open the case"). Rows marked
    /// INFERRED were matched by internal key, prop names and trigger text rather than stated
    /// anywhere; Asten Lab and Carnival have no published names at all.
    ///
    /// Unity-free and Netcode-free: tests/ContractHudPresentationRegression links this file.
    /// </summary>
    internal static class SideObjectiveCopyTable
    {
        internal const string GrandArmory = "GrandArmory";
        internal const string AtlanteanCitadel = "AtlanteanCitadel";
        internal const string Carnival = "Carnival";
        internal const string AstenLab = "AstenLab";
        internal const string ExpandedFacility = "ExpandedFacility";
        internal const string ExtendedMineshaft = "ExtendedMineshaft";
        internal const string FracturedComplex = "FracturedComplex";
        internal const string Greenhouse = "Greenhouse";
        internal const string ArtGallery = "ArtGallery";
        internal const string DeepSewers = "DeepSewers";
        internal const string DilapidatedManor = "DilapidatedManor";
        internal const string RubberRooms = "RubberRooms";
        internal const string ToyStore = "ToyStore";

        internal const string FallbackTitle = "Complete the optional objective";
        internal const string FallbackClaimVerb = "Collect the reward";

        private const SideObjectiveSightKind Prop = SideObjectiveSightKind.Prop;
        private const SideObjectiveSightKind Enemy = SideObjectiveSightKind.SpawnedEnemy;

        internal static readonly SideObjectiveCopy Fallback =
            new SideObjectiveCopy(string.Empty, string.Empty, FallbackTitle, FallbackClaimVerb, Prop);

        private static readonly SideObjectiveCopy[] Rows =
        {
            // Grand Armory - "Open case".
            new SideObjectiveCopy(GrandArmory, "Radios", "Tune the frequencies", "Open the case", Prop),
            new SideObjectiveCopy(GrandArmory, "Code", "Find the code", "Open the case", Prop),
            new SideObjectiveCopy(GrandArmory, "Morse", "Decipher the morse code", "Open the case", Prop),

            // Atlantean Citadel - "Open vault".
            new SideObjectiveCopy(AtlanteanCitadel, "Weight", "Fill the waters of tribute", "Open the vault", Prop), // INFERRED: "Weight" is the tribute lever puzzle.
            new SideObjectiveCopy(AtlanteanCitadel, "Fountains", "Turn on the fountains", "Open the vault", Prop),
            new SideObjectiveCopy(AtlanteanCitadel, "Stars", "Seek the hidden stars", "Open the vault", Prop),

            // Carnival - "Press". No published names; every row INFERRED from keys and props.
            new SideObjectiveCopy(Carnival, "Balloons", "Pop the balloons", "Press the button", Prop), // INFERRED: BalloonObjective, 4 steps.
            new SideObjectiveCopy(Carnival, "Rope", "Solve the rope maze", "Press the button", Prop), // INFERRED: RopeMaze and MaskContainer props.
            new SideObjectiveCopy(Carnival, "Wind", "Brave the wind", "Press the button", Prop), // INFERRED: ExplosionFaceAnimator and Cages props.

            // Asten Lab - "Open door". No published names; every row INFERRED from keys and props.
            new SideObjectiveCopy(AstenLab, "Security", "Check the security monitors", "Open the door", Prop), // INFERRED: SecurityMonitors completes it.
            new SideObjectiveCopy(AstenLab, "Contamination", "Kill the contaminated fleas", "Open the door", Enemy), // INFERRED: ContaminatedFleaSpawner, 5 kills.
            new SideObjectiveCopy(AstenLab, "Lockdown", "Lift the lockdown", "Open the door", Prop), // INFERRED: AcidMakingObjective feeds AcidTrigger.

            // Expanded Facility - "Twist dial". INFERRED: changelog order does not match prop names.
            new SideObjectiveCopy(ExpandedFacility, "Crank", "Restart the power", "Twist the dial", Prop), // INFERRED: PulleyObjective crank.
            new SideObjectiveCopy(ExpandedFacility, "Switches", "Fix the breakers", "Twist the dial", Prop), // INFERRED: SwitchesObjective levers.
            new SideObjectiveCopy(ExpandedFacility, "Machine", "Control the pressure", "Twist the dial", Prop), // INFERRED: BigPipeMachine valves.

            // Extended Mineshaft - "Open door".
            new SideObjectiveCopy(ExtendedMineshaft, "Power", "Flick the switches", "Open the door", Prop),
            new SideObjectiveCopy(ExtendedMineshaft, "Generator", "Fuel the generator", "Open the door", Prop),
            new SideObjectiveCopy(ExtendedMineshaft, "Spider", "Dissolve the webs", "Open the door", Prop),

            // Fractured Complex - "Open doors".
            new SideObjectiveCopy(FracturedComplex, "Fusebox", "Fix the power panels", "Open the doors", Prop),
            new SideObjectiveCopy(FracturedComplex, "Pressure", "Stabilise the pressure", "Open the doors", Prop),
            new SideObjectiveCopy(FracturedComplex, "Security", "Traverse the security room", "Open the doors", Prop),

            // Greenhouse - "Open doors". Plasma is sighted on its CockroachPatch props; the
            // renderer-less MatchItemSpawner beside them is sighted at its transform.
            new SideObjectiveCopy(Greenhouse, "Towers", "Power the towers", "Open the doors", Prop),
            new SideObjectiveCopy(Greenhouse, "Pressure", "Stabilise the water pressure", "Open the doors", Prop),
            new SideObjectiveCopy(Greenhouse, "Plasma", "Burn the Blatodara", "Open the doors", Prop),

            // Art Gallery - no trigger text: a codepad on the container claims it.
            new SideObjectiveCopy(ArtGallery, "Symbols", "Decipher the code", FallbackClaimVerb, Prop), // INFERRED: symbol decals.
            new SideObjectiveCopy(ArtGallery, "Safes", "Crack the vault", FallbackClaimVerb, Prop), // INFERRED: VaultObjectivePuzzle.
            new SideObjectiveCopy(ArtGallery, "Bugs", "Discover the tower", FallbackClaimVerb, Prop), // INFERRED: BigBugPillarObjective.

            // Deep Sewers - "Pull valve".
            new SideObjectiveCopy(DeepSewers, "Flow", "Stabilise the flow", "Pull the valve", Prop),
            new SideObjectiveCopy(DeepSewers, "Knobs", "Restart the power", "Pull the valve", Prop),
            new SideObjectiveCopy(DeepSewers, "Sludge", "Traverse the sludge maze", "Pull the valve", Prop),

            // Dilapidated Manor - no trigger text: a key container claims it.
            new SideObjectiveCopy(DilapidatedManor, "Paintings", "Reveal the key", FallbackClaimVerb, Prop), // INFERRED claim verb.
            new SideObjectiveCopy(DilapidatedManor, "Books", "Pull the books", FallbackClaimVerb, Prop), // INFERRED claim verb.
            new SideObjectiveCopy(DilapidatedManor, "Diner", "Prepare for dinner", FallbackClaimVerb, Prop), // INFERRED claim verb.

            // Rubber Rooms - "Insert key". Titles INFERRED from prop names.
            new SideObjectiveCopy(RubberRooms, "Gut", "Gut the corpse", "Insert the key", Prop), // INFERRED: GuttedManObjective.
            new SideObjectiveCopy(RubberRooms, "Autopsy", "Discover the body", "Insert the key", Prop), // INFERRED: morgue drawers and CauseofDeath.
            new SideObjectiveCopy(RubberRooms, "Blood", "Fill the test", "Insert the key", Prop), // INFERRED: Hand and GuillotineChair.

            // Toy Store - "Insert key" on Key only; Masks and Wires open the chest lid.
            new SideObjectiveCopy(ToyStore, "Key", "Kill the key keeper", "Insert the key", Enemy),
            new SideObjectiveCopy(ToyStore, "Masks", "Turn the masks", "Open the chest", Prop), // INFERRED claim verb.
            new SideObjectiveCopy(ToyStore, "Wires", "Avoid the wires", "Open the chest", Prop), // INFERRED claim verb.
        };

        internal static int Count => Rows.Length;

        internal static SideObjectiveCopy RowAt(int index)
        {
            return Rows[index];
        }

        /// <summary>Player-facing interior name for a known scope; never expose an unknown internal id.</summary>
        internal static string DisplayName(string scope)
        {
            switch (scope)
            {
                case GrandArmory: return "Grand Armory";
                case AtlanteanCitadel: return "Atlantean Citadel";
                case Carnival: return "Carnival";
                case AstenLab: return "Asten Lab";
                case ExpandedFacility: return "Expanded Facility";
                case ExtendedMineshaft: return "Extended Mineshaft";
                case FracturedComplex: return "Fractured Complex";
                case Greenhouse: return "Greenhouse";
                case ArtGallery: return "Art Gallery";
                case DeepSewers: return "Deep Sewers";
                case DilapidatedManor: return "Dilapidated Manor";
                case RubberRooms: return "Rubber Rooms";
                case ToyStore: return "Toy Store";
                default: return string.Empty;
            }
        }

        /// <summary>The copy for an interior's entry, or <see cref="Fallback"/> for an unknown pair.</summary>
        internal static SideObjectiveCopy Lookup(string scope, string entry)
        {
            if (string.IsNullOrEmpty(scope) || string.IsNullOrEmpty(entry))
                return Fallback;

            for (int i = 0; i < Rows.Length; i++)
            {
                SideObjectiveCopy row = Rows[i];
                if (string.Equals(row.Scope, scope, StringComparison.Ordinal)
                    && string.Equals(row.Entry, entry, StringComparison.Ordinal))
                {
                    return row;
                }
            }

            return Fallback;
        }

        /// <summary>
        /// The interior a controller belongs to, from the chosen entry and the controller's full entry
        /// list. Entry keys repeat across interiors (Pressure, Security), so the chosen key alone is
        /// not enough; the interior with the most keys in common wins, and a tie resolves to none.
        /// Empty when the chosen key is in no interior at all, which reads as the generic fallback.
        /// </summary>
        internal static string ResolveScope(string chosenEntry, string[] entryNames, int entryCount)
        {
            if (string.IsNullOrEmpty(chosenEntry))
                return string.Empty;

            string best = string.Empty;
            int bestOverlap = -1;
            bool tied = false;
            for (int i = 0; i < Rows.Length; i++)
            {
                SideObjectiveCopy row = Rows[i];
                if (!string.Equals(row.Entry, chosenEntry, StringComparison.Ordinal))
                    continue;

                int overlap = Overlap(row.Scope, entryNames, entryCount);
                if (overlap > bestOverlap)
                {
                    best = row.Scope;
                    bestOverlap = overlap;
                    tied = false;
                }
                else if (overlap == bestOverlap)
                {
                    tied = true;
                }
            }

            return tied ? string.Empty : best;
        }

        private static int Overlap(string scope, string[] entryNames, int entryCount)
        {
            if (entryNames == null)
                return 0;

            int overlap = 0;
            int count = Math.Min(entryCount, entryNames.Length);
            for (int n = 0; n < count; n++)
            {
                string name = entryNames[n];
                if (string.IsNullOrEmpty(name))
                    continue;

                for (int i = 0; i < Rows.Length; i++)
                {
                    if (string.Equals(Rows[i].Scope, scope, StringComparison.Ordinal)
                        && string.Equals(Rows[i].Entry, name, StringComparison.Ordinal))
                    {
                        overlap++;
                        break;
                    }
                }
            }

            return overlap;
        }
    }
}
