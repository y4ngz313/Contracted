using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Dawn;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1219 H3. A <c>contractlist</c> terminal keyword that lists every routable, visible
    /// moon with its full contract name. It reads <c>StartOfRound.levels</c>, DawnLib's moon
    /// registry and the assignment snapshot directly, so it works whatever renders the moon
    /// catalogue (vanilla, LLL, Moons Plus on or off, any page or filter).
    ///
    /// <para>The keyword is ours only while no other owner holds the word: a vanilla or
    /// OpenLib/TerminalStuff keyword, or an Interactive Terminal API application, keeps it.
    /// The plural <c>contracts</c> is Lategame Upgrades' application; while LGU's contract
    /// shop is suppressed, <see cref="MoonContractLguTerminalTakeover"/> routes it to the same
    /// page (#1219 H2). The page text is rebuilt on every load, so a later orbit, reroll or
    /// host snapshot shows up the next time the command is typed.</para>
    /// </summary>
    internal static class MoonContractsTerminalCommand
    {
        internal const string Word = "contractlist";
        internal const string KeywordName = "ContractedContractsCommand";
        internal const string PageName = "ContractedContractsPage";

        private sealed class State
        {
            internal TerminalNode Page;
        }

        private static readonly ConditionalWeakTable<Terminal, State> States = new ConditionalWeakTable<Terminal, State>();
        private static readonly HashSet<string> ReportedCollisions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool? _announcedRegistered;

        private enum LevelLoaderBinding { Unresolved, Absent, Ready, Broken }
        private static LevelLoaderBinding _lll;
        private static MethodInfo _getExtendedLevel;
        private static PropertyInfo _routeHidden, _routeLocked, _routeRemoved, _contentType;
        private static object _externalContentType;

        /// <summary>Terminal.Start. TerminalNodes is shared across scene instances, so an
        /// existing keyword of ours is rebound to this terminal's page rather than duplicated.</summary>
        internal static void Register(Terminal terminal)
        {
            if (terminal == null || terminal.terminalNodes == null)
                return;

            TerminalNode page = GetOrCreatePage(terminal);
            var keywords = (terminal.terminalNodes.allKeywords ?? Array.Empty<TerminalKeyword>()).ToList();
            TerminalKeyword owned = keywords.FirstOrDefault(IsOwned);

            string owner = ForeignOwner(keywords, Word);
            if (owner != null && ReportedCollisions.Add(owner))
                ContractsBootstrap.Log?.LogWarning(
                    $"[MoonContracts.TerminalCompat] Terminal word '{Word}' is owned by {owner}; leaving it unchanged.");

            bool registered = owner == null;
            keywords.RemoveAll(keyword => IsOwned(keyword) && (!registered || !ReferenceEquals(keyword, owned)));
            if (registered)
            {
                if (owned == null)
                {
                    owned = ScriptableObject.CreateInstance<TerminalKeyword>();
                    owned.name = KeywordName;
                    keywords.Add(owned);
                }

                owned.word = Word;
                owned.isVerb = false;
                owned.specialKeywordResult = page;
            }

            terminal.terminalNodes.allKeywords = keywords.ToArray();

            // A taken word was already reported once per owner above.
            if (_announcedRegistered != registered)
            {
                _announcedRegistered = registered;
                if (registered)
                    ContractsBootstrap.Log?.LogInfo(
                        $"[MoonContracts.TerminalCompat] Contract list command registered as '{Word}'.");
            }
        }

        /// <summary>This terminal's contract page, for <see cref="MoonContractLguTerminalTakeover"/>.
        /// The text is filled when the terminal loads the node, as for the keyword.</summary>
        internal static bool TryGetPage(Terminal terminal, out TerminalNode page)
        {
            page = terminal == null ? null : GetOrCreatePage(terminal);
            return page != null;
        }

        private static TerminalNode GetOrCreatePage(Terminal terminal)
        {
            State state = States.GetValue(terminal, _ => new State());
            if (state.Page == null)
            {
                state.Page = ScriptableObject.CreateInstance<TerminalNode>();
                state.Page.name = PageName;
                state.Page.clearPreviousText = true;
                state.Page.maxCharactersToType = 35;
                state.Page.displayText = string.Empty;
            }

            return state.Page;
        }

        /// <summary>Terminal.LoadNewNode. Rebuilds our page from the current assignment
        /// snapshot just before the terminal prints it; every other node is untouched.</summary>
        internal static void Refresh(Terminal terminal, TerminalNode node)
        {
            if (node == null || terminal == null)
                return;
            if (!States.TryGetValue(terminal, out State state) || !ReferenceEquals(node, state.Page))
                return;

            node.displayText = BuildText();
        }

        /// <summary>HELP lists the word that prints the list right now: <c>contracts</c> while
        /// the LGU takeover is live, otherwise <c>contractlist</c> when that keyword is ours.</summary>
        internal static void AddHelp(Terminal terminal, TerminalNode node, ref string text)
        {
            if (terminal == null || terminal.terminalNodes == null || node == null)
                return;
            TerminalKeyword[] keywords = terminal.terminalNodes.allKeywords;
            if (keywords == null)
                return;

            bool isHelp = false;
            bool registered = false;
            foreach (TerminalKeyword keyword in keywords)
            {
                if (keyword == null)
                    continue;
                if (string.Equals(keyword.word, "help", StringComparison.OrdinalIgnoreCase)
                    && keyword.specialKeywordResult == node)
                    isHelp = true;
                if (IsOwned(keyword))
                    registered = true;
            }
            if (!isHelp)
                return;

            string word = MoonContractLguTerminalTakeover.CanTakeOverContracts
                ? MoonContractLguTerminalTakeover.Word
                : registered ? Word : null;
            if (word == null)
                return;

            string heading = ">" + word.ToUpperInvariant() + "\n";
            if ((text ?? string.Empty).IndexOf(heading, StringComparison.Ordinal) < 0)
                text += "\n" + heading + "Full contract assignments for available moons.\n";
        }

        internal static string BuildText()
        {
            try
            {
                // Hosts roll here if the cycle moved; clients return early and read the
                // replicated snapshot, so this adds no network traffic.
                MoonContractState.RefreshAssignments("contracts command");
                List<SelectableLevel> levels = EligibleLevels();

                var numberless = levels
                    .Select(level => MoonContractCatalogueFormatter.NumberlessMoonName(level.PlanetName))
                    .ToList();
                var rows = new List<MoonContractCommandRow>(levels.Count);
                for (int i = 0; i < levels.Count; i++)
                {
                    string contract = MoonContractState.GetDisplayNameForLevel(levels[i]);
                    // The literal "None" marks the Company building and other levels that
                    // never carry a contract; the catalogue labels skip them the same way.
                    if (string.Equals(contract, "None", StringComparison.Ordinal))
                        continue;

                    string name = numberless[i];
                    // Two moon packs can share a catalogue name. The level itself is the
                    // identity; the full planet name keeps the two lines distinguishable.
                    if (numberless.Count(candidate => string.Equals(candidate, name, StringComparison.OrdinalIgnoreCase)) > 1)
                        name = levels[i].PlanetName?.Trim();
                    rows.Add(new MoonContractCommandRow(name, contract));
                }

                return MoonContractCatalogueFormatter.FormatContractsCommand(rows);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogWarning($"[MoonContracts.TerminalCompat] Failed to build the contract list: {e.Message}");
                return "\nCONTRACTS\n\nThe contract list is unavailable right now.\n\n";
            }
        }

        /// <summary>
        /// Every routable moon no terminal renderer conceals, in <c>StartOfRound.levels</c>
        /// order. DawnLib (a hard dependency) registers every level there, vanilla, LLL and
        /// Dawn-native alike, with its route node and purchase predicate; that predicate is
        /// what DawnLib and Moons Plus use to hide Artifice/Embrion and LLL-hidden or locked
        /// routes. LLL is consulted as well, so a route hidden, locked or unregistered there is
        /// left out too. Levels without a route node (Liquidation) are not routable and are
        /// skipped. Anything unreadable is left out rather than disclosed.
        /// </summary>
        private static List<SelectableLevel> EligibleLevels()
        {
            var levels = new List<SelectableLevel>();
            StartOfRound round = StartOfRound.Instance;
            if (round == null)
                return levels;
            SelectableLevel[] startLevels = round.levels;
            if (startLevels == null)
                return levels;

            foreach (SelectableLevel level in startLevels)
            {
                if (level == null || levels.Any(existing => ReferenceEquals(existing, level)))
                    continue;
                if (IsRoutableAndVisibleInDawn(level) && !IsConcealedByLethalLevelLoader(level))
                    levels.Add(level);
            }

            return levels;
        }

        /// <summary>DawnLib 0.9.25: <c>GetDawnInfo()</c>, <c>DawnMoonInfo.RouteNode</c> and
        /// <c>DawnPurchaseInfo.PurchasePredicate.CanPurchase()</c>. A hidden result (visible or
        /// not purchasable) or a failed result (locked) conceals the moon, the same reading
        /// TerminalStuff's own DawnLib bridge makes.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static bool IsRoutableAndVisibleInDawn(SelectableLevel level)
        {
            try
            {
                DawnMoonInfo info = level.GetDawnInfo();
                ITerminalPurchasePredicate predicate = info?.DawnPurchaseInfo?.PurchasePredicate;
                if (info?.RouteNode == null || predicate == null)
                    return false;

                TerminalPurchaseResult result = predicate.CanPurchase();
                return result is TerminalPurchaseResult.SuccessPurchaseResult;
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.TerminalCompat] DawnLib moon visibility unreadable; moon left out: {e.Message}");
                return false;
            }
        }

        private static bool IsOwned(TerminalKeyword keyword) => keyword != null && keyword.name == KeywordName;

        private static string ForeignOwner(List<TerminalKeyword> keywords, string word)
        {
            TerminalKeyword existing = keywords.FirstOrDefault(keyword => keyword != null
                && !IsOwned(keyword)
                && string.Equals(keyword.word, word, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
                return $"terminal keyword '{existing.name}'";

            // By reflection and never throws: ITAPI absent or reshaped reads as "not taken".
            return MoonContractLguTerminalTakeover.ContainsApplication(word)
                ? "an Interactive Terminal API application"
                : null;
        }

        /// <summary>
        /// LethalLevelLoader's route flags (LLL 1.7.12: <c>LevelManager.GetExtendedLevel</c>,
        /// <c>ExtendedLevel.IsRouteHidden/IsRouteLocked/IsRouteRemoved</c> and
        /// <c>ExtendedContent.ContentType</c>), bound by reflection because Contracted has no
        /// LLL reference. Without LLL, or for a level LLL does not know, LLL has no say.
        /// <c>IsRouteRemoved</c> counts only for LLL's own content: LLL sets it on every
        /// DawnLib-native (<c>External</c>) moon to hand routing to DawnLib, not to hide it.
        /// A binding that no longer matches hides every moon rather than name concealed ones.
        /// </summary>
        private static bool IsConcealedByLethalLevelLoader(SelectableLevel level)
        {
            switch (BindLethalLevelLoader())
            {
                case LevelLoaderBinding.Absent:
                    return false;
                case LevelLoaderBinding.Broken:
                    return true;
            }

            try
            {
                object extended = _getExtendedLevel.Invoke(null, new object[] { level });
                if (extended == null)
                    return false;
                if ((bool)_routeHidden.GetValue(extended) || (bool)_routeLocked.GetValue(extended))
                    return true;
                return (bool)_routeRemoved.GetValue(extended)
                    && !Equals(_contentType.GetValue(extended), _externalContentType);
            }
            catch (Exception e)
            {
                ContractsBootstrap.Log?.LogDebug($"[MoonContracts.TerminalCompat] LLL route visibility unreadable; moon left out: {e.Message}");
                return true;
            }
        }

        private static LevelLoaderBinding BindLethalLevelLoader()
        {
            if (_lll != LevelLoaderBinding.Unresolved)
                return _lll;

            Type manager = AppDomain.CurrentDomain.GetAssemblies()
                .Select(assembly => assembly.GetType("LethalLevelLoader.LevelManager", throwOnError: false))
                .FirstOrDefault(type => type != null);
            if (manager == null)
                return _lll = LevelLoaderBinding.Absent;

            _getExtendedLevel = manager.GetMethod("GetExtendedLevel", BindingFlags.Public | BindingFlags.Static,
                binder: null, types: new[] { typeof(SelectableLevel) }, modifiers: null);
            Type extended = _getExtendedLevel?.ReturnType;
            _routeHidden = BoolProperty(extended, "IsRouteHidden");
            _routeLocked = BoolProperty(extended, "IsRouteLocked");
            _routeRemoved = BoolProperty(extended, "IsRouteRemoved");
            _contentType = extended?.GetProperty("ContentType", BindingFlags.Public | BindingFlags.Instance);
            Type contentType = _contentType?.PropertyType;
            if (contentType != null && contentType.IsEnum && Enum.IsDefined(contentType, "External"))
                _externalContentType = Enum.Parse(contentType, "External");
            if (_routeHidden != null && _routeLocked != null && _routeRemoved != null && _externalContentType != null)
                return _lll = LevelLoaderBinding.Ready;

            ContractsBootstrap.Log?.LogWarning(
                "[MoonContracts.TerminalCompat] LethalLevelLoader route visibility (LevelManager.GetExtendedLevel, ExtendedLevel.IsRouteHidden/IsRouteLocked/IsRouteRemoved, ContentType.External) not found; the contract list stays empty rather than name concealed moons.");
            return _lll = LevelLoaderBinding.Broken;
        }

        private static PropertyInfo BoolProperty(Type type, string name)
        {
            PropertyInfo property = type?.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            return property != null && property.PropertyType == typeof(bool) && property.CanRead ? property : null;
        }
    }
}
