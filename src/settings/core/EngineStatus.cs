using System.Text.Json;
using System.Text.Json.Nodes;

namespace MagneticBorders.Settings.Core;

/// <summary>Snapshot of a running engine, parsed from a "status" pipe response. Immutable.</summary>
public sealed record EngineStatus(
    string Engine, string Version, int Pid, bool Enabled,
    bool GlueEnabled, int TolerancePx, long Drags, long GluedDrags, long UptimeSeconds);

/// <summary>Parsed pipe reply: Ok, an Error text when not Ok, and a Status for status replies. Fields it does not know are ignored.</summary>
public sealed record PipeResponse(bool Ok, string? Error, EngineStatus? Status)
{
    /// <summary>Builds a failed response.</summary>
    public static PipeResponse Failure(string error) => new(false, error, null);

    /// <summary>Parses one response line. Never throws; malformed input yields a failure.</summary>
    public static PipeResponse Parse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return Failure("empty response");
        try
        {
            if (JsonNode.Parse(line) is not JsonObject o) return Failure("response is not an object");
            var ok = o["ok"]?.GetValue<bool>() ?? false;
            if (!ok) return Failure(o["error"]?.GetValue<string>() ?? "unknown error");
            EngineStatus? status = null;
            if (o["engine"] is JsonValue)
            {
                var glue = o["glue"] as JsonObject;
                status = new EngineStatus(
                    o["engine"]!.GetValue<string>(),
                    o["version"]?.GetValue<string>() ?? "",
                    o["pid"]?.GetValue<int>() ?? 0,
                    o["enabled"]?.GetValue<bool>() ?? true,
                    glue?["enabled"]?.GetValue<bool>() ?? true,
                    glue?["tolerance_px"]?.GetValue<int>() ?? 0,
                    o["drags"]?.GetValue<long>() ?? 0,
                    o["glued_drags"]?.GetValue<long>() ?? 0,
                    o["uptime_s"]?.GetValue<long>() ?? 0);
            }
            return new PipeResponse(true, null, status);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return Failure("malformed response");
        }
    }
}
