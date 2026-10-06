using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Y4NGZCompany.Bootstrap;
using Y4NGZCompany.Core.Compatibility;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1219 H2. While Contracted suppresses Lategame Upgrades' contract shop, the exact word
    /// <c>contracts</c> prints our contract list instead of opening LGU's
    /// <c>ContractApplication</c>. LGU's singular <c>contract</c> and every other application
    /// are never touched, and <c>contractlist</c> stays the guaranteed command either way.
    ///
    /// <para>The seam is Interactive Terminal API 1.3.3's
    /// <c>TerminalPatcher.CustomParser(ref Terminal __instance, ref TerminalNode __result)</c>,
    /// a private static Harmony postfix on <c>Terminal.ParsePlayerSentence</c>. It reads the
    /// untrimmed typed text, and when a registered application matches it spawns the
    /// interactive terminal and replaces the parse result with the help node, so no vanilla
    /// prefix can win against it. A prefix on <c>CustomParser</c> itself skips it for the one
    /// command it would resolve to LGU's contract application and hands back our page; for
    /// every other command it runs unchanged. The prefix names the postfix's own arguments by
    /// index, because <c>__instance</c>/<c>__result</c> would bind to the static, void
    /// postfix rather than to its <c>ref</c> parameters.</para>
    ///
    /// <para>Everything is decided live on each call: who owns the word (read from ITAPI's
    /// registry at that moment, by the type argument of the <c>RegisterApplication&lt;T&gt;</c>
    /// factory closure; the factory is never invoked) and whether the shop is suppressed
    /// (<see cref="LategameUpgradesShopSuppression.Suppressing"/>, the predicate that already
    /// refuses LGU's purchases: the host's switch on the host, always on connected clients).
    /// ITAPI is resolved by name, so nothing here is a compile reference to it; a missing or
    /// reshaped member is logged once and leaves the word with LGU.</para>
    /// </summary>
    internal static class MoonContractLguTerminalTakeover
    {
        internal const string Word = "contracts";

        private const string LogPrefix = "[MoonContracts][Terminal]";
        private const string ManagerTypeName = "InteractiveTerminalAPI.UI.InteractiveTerminalManager";
        private const string PatcherTypeName = "InteractiveTerminalAPI.Patches.TerminalComponents.TerminalPatcher";

        private enum Binding { Unresolved, Ready, Broken }

        private static Binding _binding;
        private static FieldInfo _registeredApplications;
        private static FieldInfo _commandSensitive;
        private static MethodInfo _containsApplication;
        private static bool _applied;
        private static bool _prefixAttached;
        private static readonly HashSet<string> ReportedFailures = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>True while typing <see cref="Word"/> prints our list: the prefix is on
        /// ITAPI's parser, the shop is suppressed, and ITAPI resolves the word to LGU's
        /// contract application right now. HELP advertises the word only then.</summary>
        internal static bool CanTakeOverContracts
            => _prefixAttached && LategameUpgradesShopSuppression.Suppressing && IsLguContractApplication(Word);

        /// <summary>ITAPI's own <c>ContainsApplication</c>. False when ITAPI is absent or its
        /// shape differs (logged once).</summary>
        internal static bool ContainsApplication(string word)
        {
            if (string.IsNullOrEmpty(word) || Bind() != Binding.Ready)
                return false;

            try
            {
                return _containsApplication.Invoke(null, new object[] { word }) is bool contains && contains;
            }
            catch (Exception e)
            {
                ReportOnce("ContainsApplication", $"{LogPrefix} Interactive Terminal API ContainsApplication('{word}') failed: {(e.InnerException ?? e).Message}");
                return false;
            }
        }

        internal static void Apply(Harmony harmony)
        {
            if (_applied || harmony == null)
                return;
            _applied = true;

            // The owner test needs the registry; without it the word can never be proven LGU's.
            if (Bind() != Binding.Ready)
                return;

            // LGU's application type missing (LGU not installed, or the type renamed) is
            // reported here. LGU present with contracts disabled is not: the type exists, the
            // word is simply unregistered, and the live owner test answers no.
            if (FindType(LategameUpgradesMembers.ContractApplicationType) == null)
            {
                ReportOnce("unavailable:" + LategameUpgradesMembers.ContractApplicationType,
                    $"{LogPrefix} Lategame Upgrades type {LategameUpgradesMembers.ContractApplicationType} not found; '{Word}' is not taken over, the contract list is on '{MoonContractsTerminalCommand.Word}' only.",
                    warning: false);
                return;
            }

            string member = PatcherTypeName + ".CustomParser(ref Terminal, ref TerminalNode)";
            try
            {
                MethodInfo customParser = FindType(PatcherTypeName)?.GetMethod("CustomParser", BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null, types: new[] { typeof(Terminal).MakeByRefType(), typeof(TerminalNode).MakeByRefType() }, modifiers: null);
                if (customParser == null || customParser.ReturnType != typeof(void))
                {
                    ReportUnavailable(member, "not found");
                    return;
                }

                member = $"Harmony postfix {PatcherTypeName}.CustomParser on Terminal.ParsePlayerSentence";
                MethodInfo parse = AccessTools.Method(typeof(Terminal), "ParsePlayerSentence");
                HarmonyLib.Patches info = parse == null ? null : Harmony.GetPatchInfo(parse);
                if (info == null || !info.Postfixes.Any(patch => patch.PatchMethod == customParser))
                {
                    ReportUnavailable(member, "not found");
                    return;
                }

                member = $"prefix on {PatcherTypeName}.CustomParser";
                harmony.Patch(customParser, prefix: new HarmonyMethod(AccessTools.Method(typeof(MoonContractLguTerminalTakeover), nameof(CustomParser_Prefix))));
                _prefixAttached = true;
                ContractsBootstrap.Log?.LogInfo($"{LogPrefix} '{Word}' prefix attached to {PatcherTypeName}.CustomParser; it prints the contract list while LGU's contract shop is suppressed.");
            }
            catch (Exception e)
            {
                ReportUnavailable(member, Unreadable(e));
            }
        }

        /// <summary>Runs before ITAPI's parser postfix. <paramref name="__0"/> and
        /// <paramref name="__1"/> are the postfix's <c>ref Terminal</c> and
        /// <c>ref TerminalNode</c>, the latter aliasing <c>ParsePlayerSentence</c>'s result.</summary>
        private static bool CustomParser_Prefix(ref Terminal __0, ref TerminalNode __1)
        {
            try
            {
                Terminal terminal = __0;
                if (terminal == null || terminal.screenText == null)
                    return true;

                string text = terminal.screenText.text;
                int added = terminal.textAdded;
                if (text == null || added != Word.Length || added > text.Length)
                    return true;

                // Exactly the string ITAPI reads, so the two agree on which command this is.
                string command = text.Substring(text.Length - added);
                if (!string.Equals(command, Word, StringComparison.OrdinalIgnoreCase)
                    || !LategameUpgradesShopSuppression.Suppressing
                    || !IsLguContractApplication(command)
                    || !MoonContractsTerminalCommand.TryGetPage(terminal, out TerminalNode page))
                    return true;

                __1 = page;
                return false;
            }
            catch (Exception e)
            {
                ReportOnce("prefix", $"{LogPrefix} '{Word}' takeover failed ({e.Message}); Lategame Upgrades keeps the word.");
                return true;
            }
        }

        /// <summary>True when ITAPI would open LGU's contract application for
        /// <paramref name="command"/>. Mirrors <c>InteractiveTerminalManager.Initialize</c>:
        /// the exact key first, replaced by the first case-insensitive key when the command
        /// was registered with a case flag (LGU registers <c>contracts</c> with one).</summary>
        private static bool IsLguContractApplication(string command)
        {
            if (Bind() != Binding.Ready)
                return false;

            try
            {
                if (!(_registeredApplications.GetValue(null) is IDictionary applications)
                    || !(_commandSensitive.GetValue(null) is IDictionary sensitive))
                {
                    ReportOnce("registry", $"{LogPrefix} Interactive Terminal API registry ({ManagerTypeName}.registeredApplications/commandSensitive) is not a dictionary; '{Word}' stays with Lategame Upgrades, the contract list is on '{MoonContractsTerminalCommand.Word}' only.");
                    return false;
                }

                object factory = applications.Contains(command) ? applications[command] : null;
                if (sensitive.Contains(command.ToLower()))
                {
                    foreach (DictionaryEntry entry in applications)
                    {
                        if (entry.Key is string key && string.Equals(key, command, StringComparison.OrdinalIgnoreCase))
                        {
                            factory = entry.Value;
                            break;
                        }
                    }
                }

                if (factory == null)
                    return false;

                // RegisterApplication<T> stores () => new T(): an instance method on the
                // cached closure singleton InteractiveTerminalManager+<>c__14`1<T>, whose T is
                // the application type. The target's runtime type is that exact closed type on
                // every runtime, even where the JIT shares generic code; the method's declaring
                // type is only the fallback for a static factory.
                var function = factory as Delegate;
                Type closure = function?.Target?.GetType() ?? function?.Method?.DeclaringType;
                if (closure == null || !closure.IsGenericType)
                {
                    ReportOnce("factory", $"{LogPrefix} Interactive Terminal API factory for '{command}' has no RegisterApplication<T> closure type argument (Func.Target/Method.DeclaringType generic argument); '{Word}' stays with its owner, the contract list is on '{MoonContractsTerminalCommand.Word}' only.");
                    return false;
                }

                return closure.GetGenericArguments().Any(argument =>
                    string.Equals(argument.FullName, LategameUpgradesMembers.ContractApplicationType, StringComparison.Ordinal));
            }
            catch (Exception e)
            {
                ReportOnce("registry-read", $"{LogPrefix} Interactive Terminal API registry unreadable ({e.Message}); '{Word}' stays with Lategame Upgrades, the contract list is on '{MoonContractsTerminalCommand.Word}' only.");
                return false;
            }
        }

        private static Binding Bind()
        {
            if (_binding != Binding.Unresolved)
                return _binding;

            string member = ManagerTypeName;
            try
            {
                Type manager = FindType(ManagerTypeName);
                if (manager == null)
                {
                    ReportUnavailable(member, "not found");
                    return _binding = Binding.Broken;
                }

                var missing = new List<string>();
                member = ManagerTypeName + ".registeredApplications";
                _registeredApplications = manager.GetField("registeredApplications", BindingFlags.NonPublic | BindingFlags.Static);
                if (!IsDictionaryField(_registeredApplications))
                    missing.Add(member);

                member = ManagerTypeName + ".commandSensitive";
                _commandSensitive = manager.GetField("commandSensitive", BindingFlags.NonPublic | BindingFlags.Static);
                if (!IsDictionaryField(_commandSensitive))
                    missing.Add(member);

                member = ManagerTypeName + ".ContainsApplication(string)";
                _containsApplication = manager.GetMethod("ContainsApplication", BindingFlags.Public | BindingFlags.Static,
                    binder: null, types: new[] { typeof(string) }, modifiers: null);
                if (_containsApplication == null || _containsApplication.ReturnType != typeof(bool))
                    missing.Add(member);

                if (missing.Count == 0)
                    return _binding = Binding.Ready;

                ReportUnavailable(string.Join(", ", missing), "not found");
            }
            catch (Exception e)
            {
                // A field whose type cannot load, or an ambiguous overload, reads as missing.
                ReportUnavailable(member, Unreadable(e));
            }

            return _binding = Binding.Broken;
        }

        private static bool IsDictionaryField(FieldInfo field) => field != null && typeof(IDictionary).IsAssignableFrom(field.FieldType);

        private static Type FindType(string fullName)
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    Type type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null)
                        return type;
                }
                catch (Exception)
                {
                    // An assembly that cannot answer (a broken or dynamic one) is not ITAPI.
                }
            }

            return null;
        }

        private static string Unreadable(Exception e) => $"unreadable ({e.GetType().Name}: {e.Message})";

        private static void ReportUnavailable(string member, string reason)
            => ReportOnce("unavailable:" + member, $"{LogPrefix} Interactive Terminal API {member} {reason}; '{Word}' is not taken over, the contract list is on '{MoonContractsTerminalCommand.Word}' only.");

        private static void ReportOnce(string key, string message, bool warning = true)
        {
            if (!ReportedFailures.Add(key))
                return;
            if (warning)
                ContractsBootstrap.Log?.LogWarning(message);
            else
                ContractsBootstrap.Log?.LogInfo(message);
        }
    }
}
