using UnityEngine;
using UnityEngine.UI;
using Y4NGZCompany.Experience.UITheme;

namespace Y4NGZCompany.Contracts._Shared
{
    internal static class ContractUiTheme
    {
        internal const float HudSymbolThickness = 2f;
        private static Transform _frameClock;
        private static Image _clockFrame;
        private static float _nextFrameProbe;

        // The vanilla scene's TimeNumber is directly beneath Box (the sliced Image).
        // Never select the first matching sprite in ProfitQuota: it contains other boxes.
        internal static Image ClockFrame
        {
            get
            {
                var hud = HUDManager.Instance;
                var clock = hud != null && hud.clockNumber != null ? hud.clockNumber.transform : null;
                if (clock != _frameClock)
                {
                    _frameClock = clock;
                    _clockFrame = null;
                    _nextFrameProbe = 0f;
                }
                if (_clockFrame == null && clock != null && Time.unscaledTime >= _nextFrameProbe)
                {
                    _nextFrameProbe = Time.unscaledTime + 1f;
                    for (var parent = clock.parent; parent != null && parent != hud.HUDContainer; parent = parent.parent)
                    {
                        var candidate = parent.GetComponent<Image>();
                        if (candidate != null && candidate.sprite != null
                            && candidate.sprite.name == "DialogueBoxSimple" && candidate.type == Image.Type.Sliced)
                        {
                            _clockFrame = candidate;
                            break;
                        }
                    }
                }
                return _clockFrame;
            }
        }

        internal static Color HudFrame
        {
            get
            {
                var frame = ClockFrame;
                if (!BlueMode && !GreenMode && frame != null)
                {
                    Color tint = frame.color;
                    tint.a = HudAccent.a; // Arrival/fade animation must not hide the contract.
                    return tint;
                }
                return HudAccent;
            }
        }

        internal static UiColorTheme CurrentTheme => UiTheme.CurrentTheme;
        internal static bool BlueMode => UiTheme.BlueMode;
        internal static bool GreenMode => UiTheme.GreenMode;

        internal static Color Accent => UiTheme.Palette.Accent;

        // Sample before camera post-processing; keep other surfaces on the house palette.
        internal static Color HudAccent
        {
            get
            {
                var clock = HUDManager.Instance != null ? HUDManager.Instance.clockNumber : null;
                if (!BlueMode && !GreenMode && clock != null)
                {
                    Color tint = clock.color;
                    tint.a = Accent.a; // Clock animations must not hide the contract.
                    return tint;
                }
                return Accent;
            }
        }

        internal static string HudAccentHex => "#" + ColorUtility.ToHtmlStringRGBA(HudAccent);

        internal static Color AccentDim => UiTheme.Palette.Dim;

        internal static Color Border => UiTheme.Palette.Border;

        internal static Color PanelBackground => UiTheme.Palette.Panel;

        internal static Color White => UiTheme.Palette.White;

        internal static Color Muted => UiTheme.Palette.Muted;

        internal static Color Warning => UiTheme.Palette.Warning;

        internal static Color Danger => UiTheme.Palette.Danger;

        internal static string AccentHex => UiTheme.Palette.AccentHex;
        internal static string DimHex => UiTheme.Palette.DimHex;
        internal static string WhiteHex => UiTheme.Palette.WhiteHex;
        internal static string WarningHex => UiTheme.Palette.WarningHex;
        internal static string DangerHex => UiTheme.Palette.DangerHex;
    }
}
