using System;
using System.Collections;
using BepInEx.Configuration;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

using Y4NGZCompany.Bootstrap;

namespace Y4NGZCompany.Contracts._Shared
{
    /// <summary>
    /// #1203: a debug-only preview of the performance report with sample data, so a layout
    /// change can be checked in seconds instead of a full round. Off unless
    /// <c>Diagnostics / Performance Report Preview</c> is on; with it off every entry point
    /// does nothing and no input is bound.
    ///
    /// Triggers: Ctrl+Alt+F11 steps six cells, five cells, closed; Y4NGZDebugTools binds
    /// <see cref="PerformanceReportDebugApi"/> by reflection.
    ///
    /// The preview renders through <see cref="ContractPerformanceReportUi.TryApply"/> but
    /// owns no round-end state, and gives back everything it borrows:
    /// - it never calls FillEndGameStats, SetPlayerLevel or Upgrades' ShowRoundXp, never arms
    ///   the XP/token delay gate (<c>IsPreview</c> keeps <c>AnimateQuotaNumbers</c> off it),
    ///   never touches <see cref="MoonContractRoundEndState"/>'s pending outcome, and writes
    ///   no <see cref="PerformanceReportXpAmounts"/>;
    /// - the XP ledger and every vanilla label it writes are saved on show and restored on
    ///   close, along with the note-label presentation and foreign notes Restore(hud) owns;
    /// - it refuses while a real report is pending or on screen, and a real round boundary
    ///   (ship leave, the end-game RPC and FillEndGameStats ahead of every other prefix,
    ///   landing, orbit reset, disconnect) closes it first.
    /// Local only: no network message, nothing host-side.
    /// </summary>
    internal static class PerformanceReportPreview
    {
        private const int MaxApplyAttempts = 90;
        private const string DisabledMessage =
            "Performance report preview is off (Diagnostics / Performance Report Preview).";

        private static ConfigEntry<bool> _enabled;
        private static bool _active;
        private static HUDManager _hud;
        private static PerformanceReportPreviewVariant _variant;
        private static Coroutine _applyCoroutine;
        private static PerformanceReportXpLedger.SavedState _savedLedger;
        private static VanillaStatsLabels _savedLabels;
        private static float _savedAnimatorSpeed = 1f;
        private static bool _hidHud;

        internal static bool Enabled => _enabled != null && _enabled.Value;

        internal static void Initialize(ConfigEntry<bool> enabled)
        {
            if (_enabled != null)
                _enabled.SettingChanged -= OnEnabledChanged;
            _enabled = enabled;
            if (_enabled != null)
                _enabled.SettingChanged += OnEnabledChanged;
            SyncHotkey();
        }

        internal static string Show(PerformanceReportPreviewVariant variant)
        {
            if (!Enabled)
                return DisabledMessage;

            HUDManager hud = HUDManager.Instance;
            // A preview whose HUD died with its scene is only data to hand back.
            if (_active && _hud != hud)
                CloseInternal();

            string refusal = GetRefusalReason(hud);
            if (refusal != null)
                return Report($"refused: action=show, variant={variant}, reason='{refusal}'.");

            bool replacing = _active;
            if (replacing)
            {
                StopApply();
                ContractPerformanceReportUi.StopPreviewSequence();
                ContractPerformanceReportUi.Restore(hud);
            }
            else
            {
                _savedLedger = PerformanceReportXpLedger.Save();
                _savedLabels = VanillaStatsLabels.Capture(hud.statsUIElements);
                _savedAnimatorSpeed = hud.endgameStatsAnimator != null ? hud.endgameStatsAnimator.speed : 1f;
                _hidHud = !hud.hudHidden;
                if (_hidHud)
                    hud.HideHUD(true);
                ContractPerformanceReportUi.OpenPreviewPanel(hud);
            }

            _active = true;
            _hud = hud;
            _variant = variant;

            int slotCount = GetSlotCount(hud.statsUIElements);
            PerformanceReportPreviewSample sample = PerformanceReportPreviewSample.Build(
                variant, slotCount, GetLocalSlot(), GetLocalName());
            bool ledgerAccepted = WriteSampleLedger(sample);
            WriteSampleLabels(hud.statsUIElements, sample, slotCount);
            // What the FillEndGameStats postfix does for a real report: Coroner's cause is
            // read from the slot before the report writes over it.
            ContractPerformanceReportUi.CaptureForeignPlayerNotes(hud);
            ContractPerformanceReportData report = MoonContractRoundEndState.BuildPreviewReportData(sample, slotCount);
            _applyCoroutine = hud.StartCoroutine(ApplyWhenReady(hud, report));

            // SCRAP and GRADE are always built; the round-end builders decide the rest.
            int cells = 2
                + (report.Directive != null ? 1 : 0)
                + (report.HazardPay != null ? 1 : 0)
                + (report.CrewLosses != null ? 1 : 0)
                + (report.RevisedQuota != null ? 1 : 0);
            return Report(
                $"{(replacing ? "replace" : "show")}: variant={variant}, cells={cells}, rows={sample.Rows.Count}, " +
                $"xpSlot={sample.XpSlot}, xpLedgerAccepted={ledgerAccepted}, " +
                $"quota={sample.QuotaBeforeModifiers}->{sample.RevisedQuota}, hidHud={_hidHud}.");
        }

        internal static string Close()
        {
            if (!Enabled)
                return DisabledMessage;
            if (!_active)
                return Report("close: nothing open.");

            PerformanceReportPreviewVariant variant = _variant;
            CloseInternal();
            return Report($"close: variant={variant}, restored ledger, labels and HUD.");
        }

        /// <summary>The hotkey's single step: six cells, then five, then closed.</summary>
        internal static string Cycle()
        {
            if (!_active)
                return Show(PerformanceReportPreviewVariant.SixCells);
            return _variant == PerformanceReportPreviewVariant.SixCells
                ? Show(PerformanceReportPreviewVariant.FiveCells)
                : Close();
        }

        /// <summary>
        /// A real round boundary, or the flag being switched off, closes an open preview
        /// before anything else runs, so a real report never renders over sample state. A
        /// no-op when nothing is open.
        /// </summary>
        internal static void CloseIfOpen(string reason)
        {
            if (!_active)
                return;

            PerformanceReportPreviewVariant variant = _variant;
            CloseInternal();
            Report($"close: variant={variant}, reason='{reason}'.");
        }

        private static string GetRefusalReason(HUDManager hud)
        {
            if (hud == null || hud.statsUIElements == null || StartOfRound.Instance == null)
                return "not in a game";
            if (StartOfRound.Instance.shipIsLeaving)
                return "the ship is leaving; a real round-end report is coming";
            if (MoonContractRoundEndState.IsPerformanceReportPendingOrApplying)
                return "a real round-end report is pending";
            if (ContractPerformanceReportUi.IsRoundEndPresentationActive)
                return "a real round-end presentation is on screen";
            if (!_active && ContractPerformanceReportUi.IsEndgameStatsPanelOpen(hud))
                return "the endgame panel is already open";
            return null;
        }

        private static IEnumerator ApplyWhenReady(HUDManager hud, ContractPerformanceReportData report)
        {
            // The panel was triggered this frame; like the real apply, start measuring after it.
            yield return new WaitForEndOfFrame();
            for (int attempt = 0; attempt < MaxApplyAttempts; attempt++)
            {
                if (hud == null)
                    yield break;
                if (ContractPerformanceReportUi.TryApply(hud, report, logMissing: false))
                {
                    _applyCoroutine = null;
                    yield break;
                }

                yield return null;
            }

            _applyCoroutine = null;
            ContractsBootstrap.Log?.LogWarning(
                $"[MoonContracts] Performance report preview could not be placed after {MaxApplyAttempts} frames; closed it.");
            CloseInternal();
        }

        private static void CloseInternal()
        {
            StopApply();
            ContractPerformanceReportUi.StopPreviewSequence();
            HUDManager hud = _hud;
            if (hud != null)
            {
                // Restore first: it gives back the note-label presentation, the hidden vanilla
                // bottom row and the title; the texts the preview wrote go back after it.
                ContractPerformanceReportUi.Restore(hud);
                _savedLabels?.Restore();
                ContractPerformanceReportUi.ClosePreviewPanel(hud, _savedAnimatorSpeed);
                if (_hidHud)
                    hud.HideHUD(false);
            }

            ContractPerformanceReportUi.ClearForeignPlayerNotes();
            PerformanceReportXpLedger.Restore(_savedLedger);
            _active = false;
            _hud = null;
            _savedLedger = null;
            _savedLabels = null;
            _savedAnimatorSpeed = 1f;
            _hidHud = false;
        }

        private static void StopApply()
        {
            if (_applyCoroutine != null && _hud != null)
                _hud.StopCoroutine(_applyCoroutine);
            _applyCoroutine = null;
        }

        private static bool WriteSampleLedger(PerformanceReportPreviewSample sample)
        {
            PerformanceReportXpLedger.Clear();
            bool accepted = true;
            foreach (PerformanceReportPreviewRow row in sample.Rows)
            {
                if (row.HasLedger)
                    accepted &= PerformanceReportXpLedger.RecordSources(
                        row.Slot, row.SourceKeys, row.SourceLabels, row.SourceAmounts, row.SourceTotal);
            }

            return accepted;
        }

        // What vanilla's FillEndGameStats writes, from the sample: every slot blanked the way
        // vanilla blanks an absent player, then the sample rows, the scrap figures and grade.
        private static void WriteSampleLabels(EndOfGameStatUIElements ui, PerformanceReportPreviewSample sample, int slotCount)
        {
            for (int slot = 0; slot < slotCount; slot++)
            {
                SetText(ui.playerNamesText, slot, string.Empty);
                SetText(ui.playerNotesText, slot, string.Empty);
                SetState(ui, slot, enabled: false, sprite: null);
            }

            foreach (PerformanceReportPreviewRow row in sample.Rows)
            {
                SetText(ui.playerNamesText, row.Slot, row.Name);
                SetText(ui.playerNotesText, row.Slot, row.VanillaNotes);
                SetState(ui, row.Slot, enabled: true, sprite: row.State == PerformanceReportPreviewPlayerState.Dead
                    ? ui.deceasedIcon
                    : row.State == PerformanceReportPreviewPlayerState.Missing ? ui.missingIcon : ui.aliveIcon);
            }

            if (ui.quotaNumerator != null)
                ui.quotaNumerator.text = sample.ScrapCollected.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ui.quotaDenominator != null)
                ui.quotaDenominator.text = sample.ScrapInLevel.ToString(System.Globalization.CultureInfo.InvariantCulture);
            if (ui.gradeLetter != null)
                ui.gradeLetter.text = sample.Grade;
            if (ui.allPlayersDeadOverlay != null)
                ui.allPlayersDeadOverlay.enabled = false;
        }

        private static void SetText(TextMeshProUGUI[] labels, int slot, string value)
        {
            if (labels != null && slot >= 0 && slot < labels.Length && labels[slot] != null)
                labels[slot].text = value ?? string.Empty;
        }

        private static void SetState(EndOfGameStatUIElements ui, int slot, bool enabled, Sprite sprite)
        {
            Image[] states = ui.playerStates;
            if (states == null || slot < 0 || slot >= states.Length || states[slot] == null)
                return;
            states[slot].enabled = enabled;
            if (sprite != null)
                states[slot].sprite = sprite;
        }

        private static int GetSlotCount(EndOfGameStatUIElements ui)
        {
            int notes = ui.playerNotesText != null ? ui.playerNotesText.Length : 0;
            int names = ui.playerNamesText != null ? ui.playerNamesText.Length : 0;
            return Math.Min(notes, names);
        }

        // The slot Upgrades records the local player's ledger under in a real round.
        private static int GetLocalSlot()
        {
            GameNetcodeStuff.PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            return local != null && local.playerClientId <= int.MaxValue ? (int)local.playerClientId : 0;
        }

        private static string GetLocalName()
        {
            GameNetcodeStuff.PlayerControllerB local = GameNetworkManager.Instance != null
                ? GameNetworkManager.Instance.localPlayerController
                : null;
            return local != null ? local.playerUsername : null;
        }

        private static string Report(string message)
        {
            string line = "[MoonContracts.ReportDiag] Performance report preview " + message;
            ContractsBootstrap.Log?.LogInfo(line);
            return line;
        }

        private static void OnEnabledChanged(object sender, EventArgs args)
        {
            SyncHotkey();
            if (!Enabled)
                CloseIfOpen("preview switched off");
        }

        // With the flag off no component exists, so nothing polls the keyboard.
        private static void SyncHotkey()
        {
            MonoBehaviour host = Plugin.CoroutineHost;
            if (host == null)
                return;

            PerformanceReportPreviewHotkey hotkey = host.GetComponent<PerformanceReportPreviewHotkey>();
            if (Enabled && hotkey == null)
            {
                host.gameObject.AddComponent<PerformanceReportPreviewHotkey>();
                ContractsBootstrap.Log?.LogInfo(
                    "[MoonContracts] Performance report preview on: Ctrl+Alt+F11 steps six cells, five cells, closed.");
            }
            else if (!Enabled && hotkey != null)
            {
                UnityEngine.Object.Destroy(hotkey);
            }
        }

        /// <summary>Every label the preview writes, as vanilla (or the last report) left it.</summary>
        private sealed class VanillaStatsLabels
        {
            private EndOfGameStatUIElements _ui;
            private string[] _names;
            private string[] _notes;
            private bool[] _stateEnabled;
            private Sprite[] _stateSprites;
            private string _numerator;
            private string _denominator;
            private string _grade;
            private bool _allDeadOverlay;

            internal static VanillaStatsLabels Capture(EndOfGameStatUIElements ui)
            {
                var saved = new VanillaStatsLabels
                {
                    _ui = ui,
                    _names = CaptureTexts(ui.playerNamesText),
                    _notes = CaptureTexts(ui.playerNotesText),
                    _numerator = ui.quotaNumerator != null ? ui.quotaNumerator.text : null,
                    _denominator = ui.quotaDenominator != null ? ui.quotaDenominator.text : null,
                    _grade = ui.gradeLetter != null ? ui.gradeLetter.text : null,
                    _allDeadOverlay = ui.allPlayersDeadOverlay != null && ui.allPlayersDeadOverlay.enabled,
                };

                Image[] states = ui.playerStates;
                int count = states != null ? states.Length : 0;
                saved._stateEnabled = new bool[count];
                saved._stateSprites = new Sprite[count];
                for (int i = 0; i < count; i++)
                {
                    if (states[i] == null)
                        continue;
                    saved._stateEnabled[i] = states[i].enabled;
                    saved._stateSprites[i] = states[i].sprite;
                }

                return saved;
            }

            internal void Restore()
            {
                RestoreTexts(_ui.playerNamesText, _names);
                RestoreTexts(_ui.playerNotesText, _notes);
                Image[] states = _ui.playerStates;
                for (int i = 0; states != null && i < states.Length && i < _stateEnabled.Length; i++)
                {
                    if (states[i] == null)
                        continue;
                    states[i].enabled = _stateEnabled[i];
                    states[i].sprite = _stateSprites[i];
                }

                if (_ui.quotaNumerator != null)
                    _ui.quotaNumerator.text = _numerator;
                if (_ui.quotaDenominator != null)
                    _ui.quotaDenominator.text = _denominator;
                if (_ui.gradeLetter != null)
                    _ui.gradeLetter.text = _grade;
                if (_ui.allPlayersDeadOverlay != null)
                    _ui.allPlayersDeadOverlay.enabled = _allDeadOverlay;
            }

            private static string[] CaptureTexts(TextMeshProUGUI[] labels)
            {
                int count = labels != null ? labels.Length : 0;
                var texts = new string[count];
                for (int i = 0; i < count; i++)
                    texts[i] = labels[i] != null ? labels[i].text : null;
                return texts;
            }

            private static void RestoreTexts(TextMeshProUGUI[] labels, string[] texts)
            {
                for (int i = 0; labels != null && i < labels.Length && i < texts.Length; i++)
                {
                    if (labels[i] != null)
                        labels[i].text = texts[i];
                }
            }
        }
    }

    /// <summary>
    /// #1203: Ctrl+Alt+F11, present only while the preview flag is on. Plain F-keys belong to
    /// other tools (CCTV, Y4NGZDebugTools' F9/F10, Better Armory's F9) and Ctrl+Alt+F7 is the
    /// Bundy spawn, so this is the first free modifier combination.
    /// </summary>
    internal sealed class PerformanceReportPreviewHotkey : MonoBehaviour
    {
        private void Update()
        {
            Keyboard keyboard = Keyboard.current;
            if (keyboard == null || !keyboard.f11Key.wasPressedThisFrame)
                return;
            if (!(keyboard.leftCtrlKey.isPressed || keyboard.rightCtrlKey.isPressed)
                || !(keyboard.leftAltKey.isPressed || keyboard.rightAltKey.isPressed))
                return;

            PerformanceReportPreview.Cycle();
        }
    }
}
