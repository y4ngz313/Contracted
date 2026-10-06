using System;
using System.Linq;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts.Defuse
{
    internal static class DefuseTerminalPages
    {
        private const string PageName = "ContractedFieldSupport";
        private sealed class State
        {
            internal readonly DefuseTerminalSession Session = new DefuseTerminalSession();
            internal TerminalNode Page;
            internal TerminalNode HomePage;
        }
        private static readonly ConditionalWeakTable<Terminal, State> States = new ConditionalWeakTable<Terminal, State>();
        private static State Get(Terminal terminal) => States.GetValue(terminal, _ => new State());
        private static TerminalNode Page(Terminal terminal, string text)
        {
            State state = Get(terminal);
            if (state.Page == null)
            {
                state.Page = ScriptableObject.CreateInstance<TerminalNode>();
                state.Page.name = PageName;
                state.Page.clearPreviousText = true;
                state.Page.maxCharactersToType = 128;
            }
            state.Page.displayText = "\n" + (text ?? string.Empty).Trim() + "\n\n";
            return state.Page;
        }
        internal static void Register(Terminal terminal)
        {
            if (terminal?.terminalNodes == null) return;
            State state = Get(terminal);
            state.Session.Reset();
            if (state.HomePage == null)
            {
                state.HomePage = ScriptableObject.CreateInstance<TerminalNode>();
                state.HomePage.name = PageName + "Home";
                state.HomePage.clearPreviousText = true;
                state.HomePage.maxCharactersToType = 128;
                state.HomePage.displayText = "\n" + DefuseTerminalSession.Home() + "\n";
            }
            var keywords = (terminal.terminalNodes.allKeywords ?? Array.Empty<TerminalKeyword>()).ToList();
            foreach (string word in new[] { "defuse", "fieldsupport" })
            {
                var existing = keywords.FirstOrDefault(k => k != null && k.word == word);
                if (existing != null)
                {
                    // TerminalNodes is shared across scene instances. Rebind only our own keywords.
                    if (existing.name == PageName + word) existing.specialKeywordResult = state.HomePage;
                    continue;
                }
                var keyword = ScriptableObject.CreateInstance<TerminalKeyword>();
                keyword.name = PageName + word;
                keyword.word = word;
                keyword.specialKeywordResult = state.HomePage;
                keywords.Add(keyword);
            }
            terminal.terminalNodes.allKeywords = keywords.ToArray();
        }
        internal static bool TryParse(Terminal terminal, out TerminalNode result)
        {
            result = null;
            if (terminal?.screenText == null || terminal.textAdded <= 0) return false;
            string screen = terminal.screenText.text ?? string.Empty;
            if (terminal.textAdded > screen.Length) return false;
            State state = Get(terminal);
            bool atHome = state.HomePage != null && terminal.currentNode == state.HomePage;
            if (atHome) state.Session.Reset();
            bool owned = atHome || (terminal.currentNode == state.Page && state.Page != null);
            if (!state.Session.TryHandle(screen.Substring(screen.Length - terminal.textAdded), owned,
                DefuseContract.GetFieldSupportStatusText, DefuseContract.GetManualText,
                DefuseContract.GetDiagnosticInfoText, Submit, out string text)) return false;
            result = Page(terminal, text);
            return true;
        }
        private static string Submit(string marking, string display, string[] wires) => DefuseContract.SubmitDiagnostic(
            marking, display, Count(wires, "RED"), Count(wires, "BLUE"), Count(wires, "YELLOW"),
            Count(wires, "GREEN"), Count(wires, "WHITE"), Count(wires, "BLACK"), wires.Length, string.Join(",", wires));
        private static int Count(string[] wires, string color) => wires.Count(w => w == color);
        internal static void Reset(Terminal terminal)
        {
            if (terminal != null && States.TryGetValue(terminal, out State state))
            {
                state.Session.Reset();
                if (state.Page != null) state.Page.displayText = "\n" + DefuseTerminalSession.Home() + "\n";
            }
        }
        internal static void AddHelp(Terminal terminal, TerminalNode node, ref string text)
        {
            TerminalKeyword help = terminal?.terminalNodes?.allKeywords?.FirstOrDefault(k => k != null && k.word == "help");
            if (node != null && help?.specialKeywordResult == node && !(text ?? string.Empty).Contains(">DEFUSE"))
                text += "\n>DEFUSE\nField Support: bomb diagnostic, instructions, and report.\n";
        }
    }
    [HarmonyPatch(typeof(Terminal), "Start")]
    internal static class DefuseTerminalStartPatch
    {
        [HarmonyPostfix] private static void Postfix(Terminal __instance) => DefuseTerminalPages.Register(__instance);
    }
    [HarmonyPatch(typeof(Terminal), "ParsePlayerSentence")]
    internal static class DefuseTerminalParsePatch
    {
        [HarmonyPrefix] private static bool Prefix(Terminal __instance, ref TerminalNode __result)
        {
            try
            {
                if (!DefuseTerminalPages.TryParse(__instance, out TerminalNode node)) return true;
                __result = node; return false;
            }
            catch (Exception e) { ContractsBootstrap.Log?.LogWarning($"[Defuse.Terminal] {e}"); return true; }
        }
    }
    [HarmonyPatch(typeof(Terminal), "QuitTerminal")]
    internal static class DefuseTerminalQuitPatch
    {
        [HarmonyPostfix] private static void Postfix(Terminal __instance) => DefuseTerminalPages.Reset(__instance);
    }
    [HarmonyPatch(typeof(Terminal), "TextPostProcess")]
    internal static class DefuseTerminalHelpPatch
    {
        [HarmonyPostfix] private static void Postfix(Terminal __instance, TerminalNode node, ref string __result) =>
            DefuseTerminalPages.AddHelp(__instance, node, ref __result);
    }
}
