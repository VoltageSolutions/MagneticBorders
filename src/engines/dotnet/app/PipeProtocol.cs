using System.Text.Json;

namespace MagneticBorders.Application;

/// <summary>Side effect a pipe command asks the engine to perform after the reply is built.</summary>
public enum PipeAction { None, Reload, Quit }

/// <summary>Values reported by the status command.</summary>
/// <param name="Engine">Engine name.</param>
/// <param name="Version">Product version.</param>
/// <param name="Pid">Process id.</param>
/// <param name="Enabled">Master switch from settings.</param>
/// <param name="GlueEnabled">Magnetic coupling switch from settings.</param>
/// <param name="TolerancePx">Magnetic coupling tolerance in pixels.</param>
/// <param name="Drags">Resize drags seen.</param>
/// <param name="GluedDrags">Resize drags that glued at least one window.</param>
/// <param name="UptimeSeconds">Seconds since start.</param>
public sealed record StatusSnapshot(
    string Engine, string Version, int Pid, bool Enabled, bool GlueEnabled, int TolerancePx,
    long Drags, long GluedDrags, long UptimeSeconds);

/// <summary>Reply line plus the action the caller must run.</summary>
/// <param name="Response">One JSON line without the newline.</param>
/// <param name="Action">Requested side effect.</param>
public readonly record struct PipeResult(string Response, PipeAction Action);

/// <summary>Pure request-line -> response-line logic for \\.\pipe\MagneticBorders.</summary>
public static class PipeProtocol
{
    /// <summary>Pipe name without the \\.\pipe\ prefix.</summary>
    public const string PipeName = "MagneticBorders";
    private const string Unknown = "{\"ok\":false,\"error\":\"unknown command\"}";

    /// <summary>Parses one request line (JSON object with a cmd string) and returns the reply and action. Unknown or malformed input yields an error reply and PipeAction.None. Called on the UI thread.</summary>
    public static PipeResult Handle(string line, StatusSnapshot s)
    {
        string? cmd = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("cmd", out var c) && c.ValueKind == JsonValueKind.String)
                cmd = c.GetString();
        }
        catch (JsonException) { cmd = null; }
        return cmd switch
        {
            "status" => new(Status(s), PipeAction.None),
            "reload" => new("{\"ok\":true}", PipeAction.Reload),
            "quit" => new("{\"ok\":true}", PipeAction.Quit),
            _ => new(Unknown, PipeAction.None),
        };
    }

    /// <summary>Formats a bool as a JSON literal.</summary>
    private static string B(bool v) => v ? "true" : "false";

    /// <summary>Formats the status reply by hand so no reflection or serializer is needed under Native AOT.</summary>
    public static string Status(StatusSnapshot s) =>
        $"{{\"ok\":true,\"engine\":\"{s.Engine}\",\"version\":\"{s.Version}\",\"pid\":{s.Pid},\"enabled\":{B(s.Enabled)}," +
        $"\"glue\":{{\"enabled\":{B(s.GlueEnabled)},\"tolerance_px\":{s.TolerancePx}}},\"drags\":{s.Drags},\"glued_drags\":{s.GluedDrags},\"uptime_s\":{s.UptimeSeconds}}}";
}
