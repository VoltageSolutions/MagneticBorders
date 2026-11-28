using System.Text.Json.Nodes;

namespace MagneticBorders.Settings.Core;

/// <summary>How the tray flyout paints its background (JSON "ui.flyout_backdrop": "translucent" or "solid").</summary>
public enum FlyoutBackdrop
{
    /// <summary>Acrylic, as the Windows 11 quick settings flyouts (JSON "translucent"). The default.</summary>
    Translucent,
    /// <summary>The theme's opaque background colour (JSON "solid").</summary>
    Solid,
}

/// <summary>
/// Typed view of settings.json (schema 1), shared by the engines and this app. Unknown keys are not held here;
/// they stay in the JSON file and SettingsStore preserves them on save. A plain data class with no
/// threading of its own; SettingsViewModel and SettingsStore hand copies around.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Upper bound in pixels for both detection zones.</summary>
    public const int MaxZone = 64;
    /// <summary>Upper bound in pixels for the breakaway distance.</summary>
    public const int MaxBreakaway = 128;
    /// <summary>Lower bound of the snapping speed in pixels per second.</summary>
    public const int MinSpeed = 100;
    /// <summary>Upper bound of the snapping speed in pixels per second.</summary>
    public const int MaxSpeed = 5000;

    /// <summary>Master switch (JSON "enabled").</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Magnetic coupling on or off (JSON "glue.enabled").</summary>
    public bool GlueEnabled { get; set; } = true;
    /// <summary>Coupling detection zone in pixels (JSON "glue.tolerance_px"), 0 to MaxZone.</summary>
    public int GlueTolerancePx { get; set; } = 12;
    /// <summary>Magnetic attraction on or off (JSON "snap.enabled").</summary>
    public bool SnapEnabled { get; set; } = true;
    /// <summary>Pull-in distance in pixels (JSON "snap.distance_px"), 0 to MaxZone.</summary>
    public int SnapDistancePx { get; set; } = 16;
    /// <summary>Breakaway distance in pixels (JSON "snap.breakaway_px"), 0 to MaxBreakaway.</summary>
    public int SnapBreakawayPx { get; set; } = 24;
    /// <summary>Snapping speed in pixels per second (JSON "snap.max_speed"), MinSpeed to MaxSpeed.</summary>
    public int SnapMaxSpeed { get; set; } = 600;
    /// <summary>Snap to screen edges (JSON "snap.screen_edges").</summary>
    public bool SnapScreenEdges { get; set; } = true;
    /// <summary>Snap to other windows (JSON "snap.windows").</summary>
    public bool SnapWindows { get; set; } = true;
    /// <summary>Executable names whose windows never couple (JSON "excluded_apps").</summary>
    public List<string> ExcludedApps { get; set; } = new();
    /// <summary>Whether the engine registers itself to start at sign-in (JSON "start_at_login").</summary>
    public bool StartAtLogin { get; set; }
    /// <summary>Flyout background (JSON "ui.flyout_backdrop").</summary>
    public FlyoutBackdrop FlyoutBackdrop { get; set; } = FlyoutBackdrop.Translucent;

    /// <summary>JSON value of "ui.flyout_backdrop" for a backdrop.</summary>
    public static string BackdropToJson(FlyoutBackdrop b) => b == FlyoutBackdrop.Solid ? "solid" : "translucent";

    /// <summary>Parses "ui.flyout_backdrop"; anything but "solid" or "translucent" (case-insensitive) is the default, translucent.</summary>
    public static FlyoutBackdrop BackdropFromJson(string? v) =>
        string.Equals(v, "solid", StringComparison.OrdinalIgnoreCase) ? FlyoutBackdrop.Solid : FlyoutBackdrop.Translucent;

    /// <summary>Copy that does not share the ExcludedApps list.</summary>
    public AppSettings Clone()
    {
        var c = (AppSettings)MemberwiseClone();
        c.ExcludedApps = new List<string>(ExcludedApps);
        return c;
    }

    /// <summary>Limits a detection zone to 0 through MaxZone.</summary>
    public static int ClampZone(int v) => Math.Clamp(v, 0, MaxZone);

    /// <summary>Limits a breakaway distance to 0 through MaxBreakaway.</summary>
    public static int ClampBreakaway(int v) => Math.Clamp(v, 0, MaxBreakaway);

    /// <summary>Limits a snapping speed to MinSpeed through MaxSpeed.</summary>
    public static int ClampSpeed(int v) => Math.Clamp(v, MinSpeed, MaxSpeed);

    /// <summary>Reads known keys from a JSON object; anything missing or of the wrong type falls back to the default.</summary>
    public static AppSettings FromJson(JsonObject? root)
    {
        var s = new AppSettings();
        if (root is null) return s;
        s.Enabled = Bool(root["enabled"], s.Enabled);
        s.StartAtLogin = Bool(root["start_at_login"], s.StartAtLogin);
        if (root["glue"] is JsonObject g)
        {
            s.GlueEnabled = Bool(g["enabled"], s.GlueEnabled);
            s.GlueTolerancePx = ClampZone(Int(g["tolerance_px"], s.GlueTolerancePx));
        }
        if (root["snap"] is JsonObject n)
        {
            s.SnapEnabled = Bool(n["enabled"], s.SnapEnabled);
            s.SnapDistancePx = ClampZone(Int(n["distance_px"], s.SnapDistancePx));
            s.SnapBreakawayPx = ClampBreakaway(Int(n["breakaway_px"], s.SnapBreakawayPx));
            s.SnapMaxSpeed = ClampSpeed(Int(n["max_speed"], s.SnapMaxSpeed));
            s.SnapScreenEdges = Bool(n["screen_edges"], s.SnapScreenEdges);
            s.SnapWindows = Bool(n["windows"], s.SnapWindows);
        }
        if (root["ui"] is JsonObject ui)
        {
            string? b = null;
            try { b = ui["flyout_backdrop"] is JsonValue bv && bv.TryGetValue<string>(out var bs) ? bs : null; }
            catch (InvalidOperationException) { b = null; }
            s.FlyoutBackdrop = BackdropFromJson(b);
        }
        if (root["excluded_apps"] is JsonArray arr)
        {
            s.ExcludedApps.Clear();
            foreach (var item in arr)
            {
                try
                {
                    var v = item?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(v)) s.ExcludedApps.Add(v.Trim());
                }
                catch (InvalidOperationException) { continue; }
            }
        }
        return s;
    }

    /// <summary>Writes known keys into <paramref name="root"/>, leaving every other key untouched.</summary>
    public void WriteTo(JsonObject root)
    {
        root["schema"] = 1;
        root["enabled"] = Enabled;
        root["glue"] = Merge(root["glue"], ("enabled", GlueEnabled), ("tolerance_px", ClampZone(GlueTolerancePx)));
        root["snap"] = Merge(
            root["snap"],
            ("enabled", SnapEnabled),
            ("distance_px", ClampZone(SnapDistancePx)),
            ("breakaway_px", ClampBreakaway(SnapBreakawayPx)),
            ("max_speed", ClampSpeed(SnapMaxSpeed)),
            ("screen_edges", SnapScreenEdges),
            ("windows", SnapWindows));
        var arr = new JsonArray();
        foreach (var a in ExcludedApps) arr.Add((JsonNode?)JsonValue.Create(a));
        root["excluded_apps"] = arr;
        root["start_at_login"] = StartAtLogin;
        root["ui"] = Merge(root["ui"], ("flyout_backdrop", BackdropToJson(FlyoutBackdrop)));
    }

    /// <summary>Returns a copy of an existing JSON object (or a new one) with the given bool, int, and string keys set, so sibling unknown keys survive.</summary>
    private static JsonObject Merge(JsonNode? existing, params (string Key, object Value)[] values)
    {
        var obj = existing as JsonObject ?? new JsonObject();
        // Detach from the old parent so it can be re-assigned.
        if (existing is JsonObject) obj = (JsonObject)obj.DeepClone();
        foreach (var (k, v) in values)
            obj[k] = v switch { bool b => JsonValue.Create(b), int i => JsonValue.Create(i), string t => JsonValue.Create(t), _ => null };
        return obj;
    }

    /// <summary>Reads a JSON boolean, or the default when absent or of another type.</summary>
    private static bool Bool(JsonNode? n, bool d)
    {
        try { return n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : d; }
        catch (InvalidOperationException) { return d; }
    }

    /// <summary>Reads a JSON number as int (doubles are rounded), or the default when absent or of another type.</summary>
    private static int Int(JsonNode? n, int d)
    {
        try
        {
            if (n is JsonValue v)
            {
                if (v.TryGetValue<int>(out var i)) return i;
                if (v.TryGetValue<double>(out var dd)) return (int)Math.Round(dd, MidpointRounding.AwayFromZero);  // CFG-017
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or OverflowException) { return d; }
        return d;
    }
}
