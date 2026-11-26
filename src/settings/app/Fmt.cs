using System.Globalization;
using MagneticBorders.Settings.Core;
using Microsoft.Windows.ApplicationModel.Resources;

namespace MagneticBorders.Settings;

/// <summary>
/// Looks up localized strings from Strings/en-US/Resources.resw and formats them for display. The Format methods
/// are called from XAML as x:Bind function bindings (for example local:Fmt.Px(...)); code uses Get. UI thread only.
/// </summary>
public static class Fmt
{
    /// <summary>Resource loader for the default resource map (Resources.resw).</summary>
    private static readonly ResourceLoader Loader = new();

    /// <summary>Localized string for a resource key without a control suffix, for example "WindowTitle".</summary>
    public static string Get(string key) => Loader.GetString(key);

    /// <summary>Localized composite format filled with the arguments.</summary>
    private static string F(string key, params object[] args) => string.Format(CultureInfo.CurrentCulture, Get(key), args);

    /// <summary>Opacity for a label whose control is disabled (0.4) or enabled (1), so text greys out like the control beside it.</summary>
    public static double Dim(bool enabled) => enabled ? 1.0 : 0.4;
    /// <summary>Formats a slider value as whole pixels, for example "12 px".</summary>
    public static string Px(double v) => F("PixelsFormat", (int)Math.Round(v));
    /// <summary>Drag counters as text, or the placeholder when Magnetic Borders is not running.</summary>
    public static string Drags(EngineStatus? s) => s is null ? Get("NoValue") : F("DragsFormat", s.Drags, s.GluedDrags);
    /// <summary>One-line status of the flyout header: "Running · N coupled drags" or "Not running".</summary>
    public static string StatusLine(EngineStatus? s) => s is null ? Get("StatusNotRunning") : F("StatusRunning", s.GluedDrags);
    /// <summary>Description of the status card: "Version x.y.z" or "Not running".</summary>
    public static string Summary(EngineStatus? s) => s is null ? Get("StatusNotRunning") : Version(s.Version);
    /// <summary>Uptime as hours and minutes, minutes and seconds, or seconds; the placeholder when Magnetic Borders is not running.</summary>
    public static string Uptime(EngineStatus? s)
    {
        if (s is null) return Get("NoValue");
        var t = TimeSpan.FromSeconds(s.UptimeSeconds);
        return t.TotalHours >= 1 ? F("UptimeHoursMinutes", (int)t.TotalHours, t.Minutes)
             : t.TotalMinutes >= 1 ? F("UptimeMinutesSeconds", t.Minutes, t.Seconds)
             : F("UptimeSeconds", t.Seconds);
    }
    /// <summary>"Version x.y.z" line of the About card.</summary>
    public static string Version(string version) => F("VersionFormat", version);
}
