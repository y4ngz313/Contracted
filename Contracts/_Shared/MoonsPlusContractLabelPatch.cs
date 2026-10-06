using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// Optional TerminalStuff 3.10.x adapter. Never changes moon identity or routing.
    /// Two channels (#1219): a best-effort inline short tag on each row, and a mandatory
    /// full contract line in the footer for the highlighted moon.
    /// </summary>
    internal static class MoonsPlusContractLabelPatch
    {
        private const BindingFlags Members = BindingFlags.Public | BindingFlags.NonPublic;
        private static Harmony _harmony;
        private static Assembly _attemptedAssembly;
        private static FieldInfo _level, _menuItem, _moonInfo, _menu, _displayed, _menuNode, _displayVideo;
        private static PropertyInfo _name, _prefix, _suffix, _enabled, _reels, _showingReel, _activeSelection;
        private static Terminal _terminal;
        private static bool _reportedFailure, _reportedDrop;
        private static readonly Vector3[] ReelCorners = new Vector3[4];

        // #653: a missing optional mod must not become a zero-target PatchAll error.
        internal static void Apply(Harmony harmony)
        {
            _harmony = harmony;
            EnsurePatched();
        }

        internal static void EnsurePatched(Terminal terminal = null)
        {
            if (terminal != null) _terminal = terminal;
            if (_harmony == null) return;
            Assembly assembly = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == "darmuhsTerminalStuff");
            if (assembly == null || assembly == _attemptedAssembly) return;
            _attemptedAssembly = assembly;

            try
            {
                Type moon = assembly.GetType("TerminalStuff.MoonsTweaks.MoonInfo");
                Type moonsPlus = assembly.GetType("TerminalStuff.MoonsTweaks.MoonsPlus");
                MethodInfo refresh = moon?.GetMethod("AddToMenuName", Members | BindingFlags.Instance,
                    null, Type.EmptyTypes, null);
                MethodInfo footer = moonsPlus?.GetMethod("GetMoonsFooter", Members | BindingFlags.Static,
                    null, Type.EmptyTypes, null);
                _level = moon?.GetField("Level", Members | BindingFlags.Instance);
                _menuItem = moon?.GetField("MenuItem", Members | BindingFlags.Instance);
                Type item = _menuItem?.FieldType;
                _name = item?.GetProperty("Name", Members | BindingFlags.Instance);
                _prefix = item?.GetProperty("Prefix", Members | BindingFlags.Instance);
                _suffix = item?.GetProperty("Suffix", Members | BindingFlags.Instance);
                _moonInfo = item?.GetField("moonInfo", Members | BindingFlags.Instance);
                _enabled = assembly.GetType("TerminalStuff.Configs.Commands")?
                    .GetProperty("TerminalMoonsPlus", Members | BindingFlags.Static);
                _reels = assembly.GetType("TerminalStuff.Configs.MoonsPlusConfig")?
                    .GetProperty("ShowVideoReels", Members | BindingFlags.Static);
                _showingReel = moonsPlus?.GetProperty("ShowingReel", Members | BindingFlags.Static);
                // OpenLib BetterMenu<MoonMenuItem>: the displayed (filtered, sorted) items and
                // the absolute index of the highlighted one; its node carries the page's reel.
                _menu = moonsPlus?.GetField("MoonsPlusMenu", Members | BindingFlags.Static);
                Type menu = _menu?.FieldType;
                _displayed = menu?.GetField("DisplayMenuItemsOfType", Members | BindingFlags.Instance);
                _activeSelection = menu?.GetProperty("ActiveSelection", Members | BindingFlags.Instance);
                _menuNode = menu?.GetField("MenuNode", Members | BindingFlags.Instance);
                _displayVideo = typeof(TerminalNode).GetField("displayVideo", Members | BindingFlags.Instance);

                bool shared = _level?.FieldType == typeof(SelectableLevel)
                    && IsStringProperty(_name) && _enabled?.PropertyType == typeof(ConfigEntry<bool>)
                    && _menu != null;
                bool rows = shared && refresh != null && refresh.ReturnType == typeof(void)
                    && IsStringProperty(_prefix) && IsStringProperty(_suffix) && _suffix.CanWrite
                    && _reels?.PropertyType == typeof(ConfigEntry<bool>)
                    && _showingReel?.PropertyType == typeof(bool)
                    && _menuNode?.FieldType == typeof(TerminalNode)
                    && _displayVideo != null && typeof(UnityEngine.Object).IsAssignableFrom(_displayVideo.FieldType);
                bool footerSupported = shared && footer != null && footer.ReturnType == typeof(string)
                    && _moonInfo != null && _moonInfo.FieldType.IsAssignableFrom(moon)
                    && _displayed != null && typeof(IList).IsAssignableFrom(_displayed.FieldType)
                    && _activeSelection?.PropertyType == typeof(int) && _activeSelection.CanRead;

                if (!rows || !footerSupported)
                    ReportFailure("Unsupported TerminalStuff Moons Plus API ("
                        + (rows ? "footer" : footerSupported ? "moon rows" : "moon rows and footer")
                        + "); that contract channel is skipped. The independent contract-list terminal command is unaffected.");
                if (rows)
                    _harmony.Patch(refresh, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(MoonsPlusContractLabelPatch), nameof(Postfix))) { priority = Priority.Last });
                if (footerSupported)
                    _harmony.Patch(footer, postfix: new HarmonyMethod(
                        AccessTools.Method(typeof(MoonsPlusContractLabelPatch), nameof(FooterPostfix))) { priority = Priority.Last });
                if (rows || footerSupported)
                    ContractsBootstrap.Log?.LogInfo("[MoonContracts.MoonsPlus] Patched"
                        + (rows ? " MoonInfo.AddToMenuName (inline tags, reel-aware budget)" : "")
                        + (footerSupported ? " MoonsPlus.GetMoonsFooter (highlighted moon's contract)" : "") + ".");
            }
            catch (Exception e)
            {
                ReportFailure("Could not install moon-row adapter: " + e.Message);
            }
        }

        private static bool IsStringProperty(PropertyInfo property) => property != null
            && property.CanRead && property.PropertyType == typeof(string)
            && property.GetIndexParameters().Length == 0;

        // These are the renderer's concealment placeholders. Do not disclose contracts on
        // undiscovered/locked routes or run their side-effectful visibility getters again.
        private static bool IsConcealed(string name) =>
            string.IsNullOrEmpty(name) || name == "[ ??? ]" || name == "[ROUTE LOCKED]";

        private static bool MoonsPlusEnabled() => (_enabled.GetValue(null) as ConfigEntry<bool>)?.Value == true;

        private static void Postfix(object __instance)
        {
            try
            {
                if (!MoonsPlusEnabled()) return;
                var level = _level.GetValue(__instance) as SelectableLevel;
                object item = _menuItem.GetValue(__instance);
                if (level == null || item == null) return;
                string name = _name.GetValue(item) as string;
                string suffix = MoonsPlusContractLabelFormatter.RemoveLabel(_suffix.GetValue(item) as string);

                if (IsConcealed(name))
                {
                    _suffix.SetValue(item, suffix);
                    return;
                }

                MoonContractState.RefreshAssignments("moons plus row");
                string title = MoonContractState.GetDisplayNameForLevel(level);
                if (title == "None")
                {
                    _suffix.SetValue(item, suffix);
                    return;
                }
                if (_terminal == null) _terminal = UnityEngine.Object.FindObjectOfType<Terminal>();
                var screen = _terminal?.screenText?.textComponent;
                if (screen == null) return;
                float margins = Mathf.Max(0f, screen.margin.x) + Mathf.Max(0f, screen.margin.z);
                float width = screen.rectTransform.rect.width - margins;
                if ((_reels.GetValue(null) as ConfigEntry<bool>)?.Value == true && PageShowsReel())
                {
                    // Rows are rebuilt after FilterView chose this page's reel but before the
                    // node is shown, so reserve the left pane from that choice. A reel-less page
                    // keeps the full width even if the previous page's reel is still up.
                    width *= 0.45f;
                    var reel = _terminal.terminalImage?.rectTransform;
                    if (reel != null && (bool)_showingReel.GetValue(null))
                    {
                        // An active reel always has the same 200x150 rect, so a reel that is
                        // already up is the one this page will show.
                        reel.GetWorldCorners(ReelCorners);
                        float left = ReelCorners.Min(c => screen.rectTransform.InverseTransformPoint(c).x);
                        width = Mathf.Min(width, left - screen.rectTransform.rect.xMin - Mathf.Max(0f, screen.margin.x));
                    }
                }

                float Measure(string text) => Mathf.Max(0f,
                    screen.GetPreferredValues(text, float.PositiveInfinity, float.PositiveInfinity).x - margins);
                string shortName = MoonContractState.GetShortNameForLevel(level);
                _suffix.SetValue(item, MoonsPlusContractLabelFormatter.AddLabel(_prefix.GetValue(item) as string,
                    name, suffix, shortName, width, Measure, out bool dropped, out float rowWidth, out float budget));
                if (dropped && !_reportedDrop)
                {
                    _reportedDrop = true;
                    ContractsBootstrap.Log?.LogInfo(string.Format(CultureInfo.InvariantCulture,
                        "[MoonContracts.MoonsPlus] Inline contract tag did not fit a moon row (row {0:0.#}, budget {1:0.#}, short '{2}'); the footer shows the highlighted moon's contract.",
                        rowWidth, budget, shortName));
                }
            }
            catch (Exception e)
            {
                ReportFailure("Could not decorate moon row: " + e.Message);
            }
        }

        private static void FooterPostfix(ref string __result)
        {
            try
            {
                if (!MoonsPlusEnabled()) return;
                object menu = _menu.GetValue(null);
                if (menu == null || !(_displayed.GetValue(menu) is IList items)) return;
                // ActiveSelection is the absolute index into the displayed list, already clamped
                // to the current page before OpenLib asks for the footer.
                int selection = (int)_activeSelection.GetValue(menu);
                if (selection < 0 || selection >= items.Count) return;
                object item = items[selection];
                if (item == null || !_menuItem.FieldType.IsInstanceOfType(item)) return;
                string name = _name.GetValue(item) as string;
                if (IsConcealed(name)) return;
                object moon = _moonInfo.GetValue(item);
                if (moon == null || !(_level.GetValue(moon) is SelectableLevel level) || level == null) return;

                MoonContractState.RefreshAssignments("moons plus footer");
                string title = MoonContractState.GetDisplayNameForLevel(level);
                if (title == "None") return;
                __result = MoonsPlusContractLabelFormatter.AddFooterLine(__result, title, name);
            }
            catch (Exception e)
            {
                ReportFailure("Could not add the contract footer: " + e.Message);
            }
        }

        private static bool PageShowsReel()
        {
            object menu = _menu.GetValue(null);
            var node = menu == null ? null : _menuNode.GetValue(menu) as TerminalNode;
            return node != null && (_displayVideo.GetValue(node) as UnityEngine.Object) != null;
        }

        private static void ReportFailure(string message)
        {
            if (_reportedFailure) return;
            _reportedFailure = true;
            ContractsBootstrap.Log?.LogWarning("[MoonContracts.MoonsPlus] " + message);
        }
    }
}
