using System.Globalization;

namespace MagneticBorders.Settings.Core;

/// <summary>The backdrop the flyout is expected to show and why.</summary>
/// <param name="Effective">"acrylic", "solid" (the setting) or "solid-fallback" (acrylic was asked for but Windows draws a solid color).</param>
/// <param name="Reason">Short text for the log.</param>
public readonly record struct BackdropChoice(string Effective, string Reason);

/// <summary>
/// Text of the lines the flyout writes to settings.log each time it opens (see docs/spec/settings-app.md). Pure
/// formatting with no UI types, so it is unit-testable; FlyoutWindow gathers the values.
/// </summary>
public static class FlyoutDiagnostics
{
    /// <summary>Which backdrop Windows is expected to draw for the setting, given what the system supports.</summary>
    public static BackdropChoice ChooseBackdrop(FlyoutBackdrop setting, bool acrylicSupported, bool transparencyEffects, bool energySaverOn)
    {
        if (setting == FlyoutBackdrop.Solid) return new("solid", "setting is solid");
        if (!acrylicSupported) return new("solid-fallback", "DesktopAcrylicController is not supported");
        if (!transparencyEffects) return new("solid-fallback", "transparency effects are off");
        if (energySaverOn) return new("solid-fallback", "energy saver is on");
        return new("acrylic", "setting is translucent");
    }

    /// <summary>Formats an HRESULT or a COLORREF as 0x and eight hex digits.</summary>
    public static string Hex(int value) => string.Create(CultureInfo.InvariantCulture, $"0x{value:x8}");

    /// <summary>Line with the OS build and the theme state. Themes are the words WinUI and the system report.</summary>
    public static string Open(string os, string appTheme, string contentTheme, string systemTheme, bool highContrast) =>
        string.Create(CultureInfo.InvariantCulture, $"flyout open os={os} app_theme={appTheme} content_theme={contentTheme} system_theme={systemTheme} high_contrast={OnOff(highContrast)}");

    /// <summary>Line with the backdrop decision and the system facts behind it.</summary>
    public static string Backdrop(FlyoutBackdrop setting, BackdropChoice choice, bool acrylicSupported, bool micaSupported, bool transparencyEffects) =>
        string.Create(CultureInfo.InvariantCulture,
            $"flyout backdrop setting={(setting == FlyoutBackdrop.Solid ? "solid" : "translucent")} effective={choice.Effective} reason=\"{choice.Reason}\" desktop_acrylic_supported={Bool(acrylicSupported)} mica_supported={Bool(micaSupported)} transparency_effects={OnOff(transparencyEffects)}");

    /// <summary>Line with the energy saver and power supply state.</summary>
    public static string Power(string energySaver, string powerSupply) =>
        string.Create(CultureInfo.InvariantCulture, $"flyout power energy_saver={energySaver} power_supply={powerSupply}");

    /// <summary>Line with the DWM attribute calls and their HRESULTs; the border color read back shows whether DWM kept it.</summary>
    public static string Dwm(int cornerHr, int borderColor, int borderHr, int borderReadback, int readbackHr, int frameHr) =>
        string.Create(CultureInfo.InvariantCulture,
            $"flyout dwm corner_preference=round hr={Hex(cornerHr)} border_color={Hex(borderColor)} hr={Hex(borderHr)} border_color_readback={Hex(borderReadback)} hr={Hex(readbackHr)} frame_extend hr={Hex(frameHr)}");

    /// <summary>Line with the window DPI, size and position in physical pixels, and the work area it was placed in.</summary>
    public static string Window(uint dpi, int width, int height, int x, int y, int workLeft, int workTop, int workRight, int workBottom) =>
        string.Create(CultureInfo.InvariantCulture,
            $"flyout window dpi={dpi} scale={(dpi == 0 ? 1.0 : dpi / 96.0):F2} size={width}x{height} position={x},{y} work_area={workLeft},{workTop},{workRight},{workBottom}");

    /// <summary>Line for an activation change of the flyout window.</summary>
    public static string Activation(string state, long msSinceShown) =>
        string.Create(CultureInfo.InvariantCulture, $"flyout activation state={state} after_ms={msSinceShown}");

    /// <summary>Line for the flyout closing.</summary>
    public static string Closed(long msSinceShown) =>
        string.Create(CultureInfo.InvariantCulture, $"flyout closed after_ms={msSinceShown}");

    private static string OnOff(bool on) => on ? "on" : "off";

    private static string Bool(bool value) => value ? "true" : "false";
}
