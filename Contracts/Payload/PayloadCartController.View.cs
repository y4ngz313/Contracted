using GameNetcodeStuff;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Y4NGZCompany.Contracts.Payload
{
    public partial class PayloadCartController
    {
        private TextMeshProUGUI[] _viewTipLines;
        private string[] _savedViewTips;
        private string _viewTipText;
        private bool _viewTipDirty = true;
        private bool _viewTipController;
        private GrabbableObject _viewTipItem;
        private bool _viewTipPlayerDead;

        internal bool IsThirdPersonView => _thirdPersonView.IsApplied;

        private bool CanContinueLocalPush(PlayerControllerB player)
        {
            StartOfRound round = StartOfRound.Instance;
            return _localPushActive && player != null && player == _localPushPlayer
                && !player.isPlayerDead && player.isPlayerControlled && IsAttachedToLocalPlayer
                && (round == null || (!round.shipIsLeaving && !round.newGameIsLoading));
        }

        internal void ToggleLocalView()
        {
            if (!CanContinueLocalPush(LocalPlayer()))
                return;

            if (_thirdPersonView.IsApplied)
            {
                _thirdPersonView.Disable();
                RestoreGrabDistance(_localPushPlayer);
            }
            else
            {
                SaveAndExtendGrabDistance(_localPushPlayer);
                _thirdPersonView.Enable(_localPushPlayer);
            }
            _viewTipDirty = true;
            RefreshViewControlTip();
        }

        private void OnViewActionChange(object changed, InputActionChange change)
        {
            if (change == InputActionChange.BoundControlsChanged)
                _viewTipDirty = true;
        }

        private static string ViewToggleBindingDisplay()
        {
            InputAction action = InputSystem.actions?.FindAction("ItemSecondaryUse", false);
            if (action == null)
                return "Unbound";
            bool controller = StartOfRound.Instance != null && StartOfRound.Instance.localPlayerUsingController;
            // Same keyboard/controller binding slots as vanilla's control-tip display; the
            // effective binding includes any player override, unlike a literal Q label.
            int index = controller ? 1 : 0;
            if (index >= action.bindings.Count)
                return "Unbound";
            string display = action.GetBindingDisplayString(index);
            return string.IsNullOrEmpty(display) ? "Unbound" : display;
        }

        private void RefreshViewControlTip()
        {
            TextMeshProUGUI[] lines = HUDManager.Instance != null ? HUDManager.Instance.controlTipLines : null;
            if (!ReferenceEquals(lines, _viewTipLines))
            {
                RestoreViewControlTips();
                _viewTipLines = lines;
                if (lines != null)
                {
                    _savedViewTips = new string[lines.Length];
                    _viewTipItem = _localPushPlayer != null && _localPushPlayer.isHoldingObject
                        ? _localPushPlayer.currentlyHeldObjectServer : null;
                    _viewTipPlayerDead = _localPushPlayer != null && _localPushPlayer.isPlayerDead;
                    for (int i = 0; i < lines.Length; i++)
                        _savedViewTips[i] = lines[i] != null ? lines[i].text : string.Empty;
                }
                _viewTipDirty = true;
            }
            if (lines == null || lines.Length == 0)
                return;
            DiscardStaleItemTips();

            // Preserve fresh item tips written while pushing as well as the original snapshot.
            // Write the vanilla source, not a second overlay: VanillaControlTipMirror and the
            // optional Y4NGZUI overlay already mirror these lines.
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i] != null && _viewTipText != null
                    && lines[i].text != (i == 0 ? _viewTipText : string.Empty))
                    _savedViewTips[i] = lines[i].text;
            }
            bool controller = StartOfRound.Instance != null && StartOfRound.Instance.localPlayerUsingController;
            if (_viewTipDirty || controller != _viewTipController)
            {
                _viewTipText = $"{(_thirdPersonView.IsApplied ? "First" : "Third")} person : [{ViewToggleBindingDisplay()}]";
                _viewTipController = controller;
                _viewTipDirty = false;
            }
            for (int i = 0; i < lines.Length; i++)
            {
                string text = i == 0 ? _viewTipText : string.Empty;
                if (lines[i] != null && lines[i].text != text)
                    lines[i].text = text;
            }
        }

        private void DiscardStaleItemTips()
        {
            // Local drop clears isHoldingObject before the server acknowledges the object reference.
            GrabbableObject item = _localPushPlayer != null && _localPushPlayer.isHoldingObject
                ? _localPushPlayer.currentlyHeldObjectServer : null;
            bool dead = _localPushPlayer != null && _localPushPlayer.isPlayerDead;
            if (!ReferenceEquals(item, _viewTipItem) || dead != _viewTipPlayerDead)
            {
                // Clearing an already blank leased slot is not an observable text change.
                // Invalidate the old item's snapshot before collecting the new item's writes.
                if (_savedViewTips != null)
                    System.Array.Clear(_savedViewTips, 0, _savedViewTips.Length);
                _viewTipItem = item;
                _viewTipPlayerDead = dead;
            }
        }

        private void RestoreViewControlTips()
        {
            DiscardStaleItemTips();
            if (_viewTipLines != null && _savedViewTips != null)
            {
                for (int i = 0; i < _viewTipLines.Length; i++)
                {
                    if (_viewTipLines[i] != null && _viewTipLines[i].text == (i == 0 ? _viewTipText : string.Empty))
                        _viewTipLines[i].text = _savedViewTips[i] ?? string.Empty;
                }
            }
            _viewTipLines = null;
            _savedViewTips = null;
            _viewTipText = null;
            _viewTipItem = null;
            _viewTipPlayerDead = false;
            _viewTipDirty = true;
        }
    }
}
