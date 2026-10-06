using System;
using UnityEngine;
using Y4NGZCore.Modules.Hud;

namespace Y4NGZCompany.Experience.UITheme
{
    // #660: the theme/visibility/motion substrate moved to Y4NGZUI.dll. These three types keep
    // the exact pre-move names because already-shipped consumers resolve them reflectively and
    // degrade SILENTLY when the probe misses:
    //
    //   - Y4NGZUpgrades' OptionalUiTheme resolves "Y4NGZCompany.Experience.UITheme.UiTheme,
    //     Y4NGZCompany" (assembly-qualified Type.GetType) and reads CurrentPreset, the colour
    //     properties, the ThemeChanged event (GetEvent + AddEventHandler), the
    //     ProtectedSlotResolver FIELD (GetField + SetValue — which is why that member must stay
    //     a real field here, bridged to Y4NGZUI through Y4NGZCore's UiPresentationSeams by
    //     Plugin.Awake), and calls Refresh.
    //   - BetterArmory's ArmoryUiStyle scans loaded assemblies for the same three full names and
    //     reads Accent, IsVisible, CurrentOffset, and the MotionSampled event.
    //
    // #731: Y4NGZUI is an optional companion of every Y4NGZ package, Contracted included, so this
    // assembly no longer references it. Every member forwards to Y4NGZCore's neutral substrate
    // instead — UiThemeState and UiThemeCommands for the theme, GameplayUiVisibilitySignal and
    // GameplayHudMotionSignal for the HUD gate and offset. Y4NGZUI publishes into those when it
    // is installed, so old consumers see exactly the values new ones do; when it is not, they see
    // the Default (Orange) palette, a visible HUD and a resting offset, which is what that
    // profile renders. Contract code in this assembly compiles against these same forwards, which
    // is deliberate: it keeps `using Y4NGZCompany.Experience.UITheme;` files on one set of names.
    //
    // New consumers resolve "Y4NGZUI.<Type>, Y4NGZUI" instead; y4ngz313/Y4NGZUpgrades#352
    // repoints ours. Do not add state here — Y4NGZUI owns the substrate and Y4NGZCore owns the
    // neutral read side.

    public static class UiTheme
    {
        public static HudColorPreset CurrentPreset => UiThemeState.CurrentPreset;
        public static UiColorTheme CurrentTheme => UiThemeState.CurrentTheme;
        public static UiPalette Palette => UiThemeState.Palette;
        public static bool OrangeMode => UiThemeState.OrangeMode;
        public static bool GreenMode => UiThemeState.GreenMode;
        public static bool BlueMode => UiThemeState.BlueMode;
        public static bool GlobalUiOverhaulEnabled => UiThemeCommands.GlobalUiOverhaulEnabled;
        public static bool HasPreview => UiThemeCommands.HasPreview;

        public static Color Accent => UiThemeState.Accent;
        public static Color Dim => UiThemeState.Dim;
        public static Color Faint => UiThemeState.Faint;
        public static Color Panel => UiThemeState.Panel;
        public static Color White => UiThemeState.White;
        public static Color Muted => UiThemeState.Muted;
        public static Color Warning => UiThemeState.Warning;
        public static Color Danger => UiThemeState.Danger;

        public static string AccentHex => UiThemeState.AccentHex;
        public static string DimHex => UiThemeState.DimHex;
        public static string WhiteHex => UiThemeState.WhiteHex;

        /// <summary>
        /// A real field, never a property: an already-shipped Y4NGZUpgrades writes it with
        /// GetField(...).SetValue. Y4NGZCompany's Plugin.Awake bridges it into
        /// <c>Y4NGZCore.Modules.Hud.UiPresentationSeams.ProtectedSlotResolverSource</c>; a
        /// resolver written directly to Y4NGZUI (#352 and later) wins over this one.
        /// </summary>
        public static Func<int, bool> ProtectedSlotResolver;

        public static event Action<HudColorPreset> ThemeChanged
        {
            add => UiThemeState.ThemeChanged += value;
            remove => UiThemeState.ThemeChanged -= value;
        }

        public static void Refresh() => UiThemeCommands.Refresh();
        public static void PreviewPreset(HudColorPreset preset) => UiThemeCommands.PreviewPreset(preset);
        public static void CommitPreview() => UiThemeCommands.CommitPreview();
        public static void RevertPreview() => UiThemeCommands.RevertPreview();
        public static void CommitPreset(HudColorPreset preset) => UiThemeCommands.CommitPreset(preset);
    }

    public static class GameplayUiVisibility
    {
        private static readonly Y4NGZ.Compatibility.OptionalUnderPauseMenuSignal UnderPauseMenu =
            new Y4NGZ.Compatibility.OptionalUnderPauseMenuSignal(
                typeof(GameplayUiVisibilitySignal), message => Plugin.Log?.LogWarning(message));

        public static bool IsVisible => GameplayUiVisibilitySignal.IsVisible;

        /// <summary>
        /// #823. The gate without the pause-menu term, for the contract card, which lives on the
        /// vanilla HUD canvas beneath the pause menu and must keep drawing while it is open.
        /// </summary>
        public static bool IsVisibleUnderPauseMenu => UnderPauseMenu.IsVisible;

        public static event Action<bool> VisibilityChanged
        {
            add => GameplayUiVisibilitySignal.Changed += value;
            remove => GameplayUiVisibilitySignal.Changed -= value;
        }

        public static event Action<bool> UnderPauseMenuVisibilityChanged
        {
            add => UnderPauseMenu.Subscribe(value);
            remove => UnderPauseMenu.Unsubscribe(value);
        }
    }

    public static class GameplayHudMotion
    {
        public static Vector2 CurrentOffset => GameplayHudMotionSignal.CurrentOffset;

        public static event Action<Vector2> MotionSampled
        {
            add => GameplayHudMotionSignal.MotionSampled += value;
            remove => GameplayHudMotionSignal.MotionSampled -= value;
        }

        // Explicit layout-changed channel (#716 C4): raised on resolution, safe-area or
        // scene changes so consumers can re-anchor without a forced zero motion sample.
        public static event Action LayoutChanged
        {
            add => GameplayHudMotionSignal.LayoutChanged += value;
            remove => GameplayHudMotionSignal.LayoutChanged -= value;
        }
    }
}
