using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MagneticBorders.Application;

/// <summary>Constants that identify this engine.</summary>
public static class EngineInfo
{
    /// <summary>Engine name used in the log file name and the status reply.</summary>
    public const string Name = "dotnet";
}

/// <summary>Build-time application info supplied by the composition root.</summary>
public interface IAppInfo
{
    /// <summary>Product version without build metadata.</summary>
    string Version { get; }
}

/// <summary>Reads the version stamped by the build (AssemblyInformationalVersionAttribute); AOT-safe.</summary>
public sealed class AppInfo(string version) : IAppInfo
{
    public string Version { get; } = version;

    /// <summary>Reads the informational version from the assembly and strips any +metadata suffix. Falls back to 0.0.0.</summary>
    public static AppInfo FromAssembly(System.Reflection.Assembly assembly)
    {
        var v = assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            is [System.Reflection.AssemblyInformationalVersionAttribute a, ..] ? a.InformationalVersion : "0.0.0";
        var plus = v.IndexOf('+');
        return new AppInfo(plus >= 0 ? v[..plus] : v);
    }

    /// <summary>Same as FromAssembly for the running executable.</summary>
    public static AppInfo FromEntryAssembly() => FromAssembly(System.Reflection.Assembly.GetEntryAssembly()!);
}

/// <summary>Shared settings.json (schema 1) as consumed by the engine.</summary>
public sealed record SettingsModel
{
    /// <summary>Master switch (enabled).</summary>
    public bool Enabled { get; init; } = true;
    /// <summary>Glue switch (glue.enabled).</summary>
    public bool GlueEnabled { get; init; } = true;
    /// <summary>Glue tolerance in pixels, clamped to 0..64 by the parser (glue.tolerance_px).</summary>
    public int GlueTolerancePx { get; init; } = 12;
    /// <summary>Snap switch (snap.enabled). Parsed but snapping is not implemented in this engine.</summary>
    public bool SnapEnabled { get; init; } = true;
    /// <summary>Snap distance in pixels, clamped to 0..64 (snap.distance_px). Parsed only.</summary>
    public int SnapDistancePx { get; init; } = 16;
    /// <summary>Executable names whose windows never take part in gluing (excluded_apps).</summary>
    public IReadOnlyList<string> ExcludedApps { get; init; } = [];
    /// <summary>Whether the engine writes its Run key entry (start_at_login).</summary>
    public bool StartAtLogin { get; init; }
}

/// <summary>Reads settings.json with System.Text.Json's JsonDocument (no reflection, AOT-safe).</summary>
public static class SettingsParser
{
    /// <summary>
    /// Returns the settings document with the top-level "enabled" key set, keeping every other key (known or unknown) in order.
    /// Blank text gives a fresh object. False (with an error) when the text is not a JSON object, so the caller never overwrites a file it cannot understand. Pure: the host writes the file atomically.
    /// </summary>
    public static bool TryWithEnabled(string json, bool enabled, out string result, out string? error)
    {
        result = "";
        error = null;
        try
        {
            JsonNode? root = string.IsNullOrWhiteSpace(json)
                ? new JsonObject()
                : JsonNode.Parse(json, null, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (root is not JsonObject obj) { error = "root is not an object"; return false; }
            obj["enabled"] = enabled;
            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
                obj.WriteTo(w);
            result = Encoding.UTF8.GetString(ms.ToArray()) + "\n";
            return true;
        }
        catch (JsonException ex) { error = ex.Message; return false; }
    }

    /// <summary>Parses settings.json text. Missing keys give defaults; invalid JSON returns false (caller keeps previous settings).</summary>
    public static bool TryParse(string json, out SettingsModel model, out string? error)
    {
        model = new SettingsModel();
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { error = "root is not an object"; return false; }
            var d = new SettingsModel();
            bool enabled = Bool(root, "enabled", d.Enabled);
            bool startAtLogin = Bool(root, "start_at_login", d.StartAtLogin);
            bool glueEnabled = d.GlueEnabled, snapEnabled = d.SnapEnabled;
            int tol = d.GlueTolerancePx, dist = d.SnapDistancePx;
            if (root.TryGetProperty("glue", out var g) && g.ValueKind == JsonValueKind.Object)
            {
                glueEnabled = Bool(g, "enabled", glueEnabled);
                tol = Int(g, "tolerance_px", tol);
            }
            if (root.TryGetProperty("snap", out var sn) && sn.ValueKind == JsonValueKind.Object)
            {
                snapEnabled = Bool(sn, "enabled", snapEnabled);
                dist = Int(sn, "distance_px", dist);
            }
            var excluded = new List<string>();
            if (root.TryGetProperty("excluded_apps", out var ex) && ex.ValueKind == JsonValueKind.Array)
                foreach (var e in ex.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString())))
                    excluded.Add(e.GetString()!.Trim());
            model = new SettingsModel
            {
                Enabled = enabled,
                GlueEnabled = glueEnabled,
                GlueTolerancePx = Math.Clamp(tol, 0, 64),
                SnapEnabled = snapEnabled,
                SnapDistancePx = Math.Clamp(dist, 0, 64),
                ExcludedApps = excluded,
                StartAtLogin = startAtLogin,
            };
            return true;
        }
        catch (JsonException ex) { error = ex.Message; return false; }
    }

    /// <summary>Reads a boolean property or returns the default.</summary>
    private static bool Bool(JsonElement o, string name, bool def) =>
        o.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;

    /// <summary>Reads an int property or returns the default.</summary>
    private static int Int(JsonElement o, string name, int def) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : def;
}
