using System.Globalization;
using MagneticBorders.Core;

namespace MagneticBorders.Win32;

/// <summary>Text of the verbose diagnostic lines written by Win32Mover and the start-up system report. No Win32 types, so it is unit-testable. The C++ and Rust engines produce the same text (docs/spec/engine.md).</summary>
internal static class MoveDiagnostics
{
    /// <summary>Formats a rectangle as l,t,r,b.</summary>
    public static string RectText(Rect r) => string.Create(CultureInfo.InvariantCulture, $"{r.L},{r.T},{r.R},{r.B}");

    /// <summary>Returns actual minus requested per side.</summary>
    public static Rect Delta(Rect requested, Rect actual) => new(actual.L - requested.L, actual.T - requested.T, actual.R - requested.R, actual.B - requested.B);

    /// <summary>Formats a window id as lower-case hex with a 0x prefix.</summary>
    public static string HexId(ulong id) => string.Create(CultureInfo.InvariantCulture, $"0x{id:x}");

    /// <summary>Formats SetWindowPos flags as 0x and four hex digits.</summary>
    public static string FlagsText(uint flags) => string.Create(CultureInfo.InvariantCulture, $"0x{flags:x4}");

    /// <summary>Line for one applied batch. Mode is "deferred" or "individual".</summary>
    public static string BatchLine(int windows, string mode, uint flags, long elapsedUs, int mismatches) =>
        string.Create(CultureInfo.InvariantCulture, $"move: batch windows={windows} mode={mode} flags={FlagsText(flags)} elapsed_us={elapsedUs} mismatches={mismatches}");

    /// <summary>Line for one window after a batch: the requested visual frame, the visual frame read back ("unavailable" when the read fails), delta= (read back minus requested) when they differ, and window_delta= (GetWindowRect minus the requested window rectangle) when that differs.</summary>
    public static string WindowLine(ulong id, Rect requested, Rect? actual, Rect windowDelta)
    {
        var line = $"move: hwnd={HexId(id)} requested={RectText(requested)} actual={(actual is { } a ? RectText(a) : "unavailable")}";
        if (actual is { } shown && shown != requested) line += " delta=" + RectText(Delta(requested, shown));
        if (windowDelta != default) line += " window_delta=" + RectText(windowDelta);
        return line;
    }

    /// <summary>Line for a failed deferred batch. Step is "begin", "defer" or "end"; id is 0 when no window is involved.</summary>
    public static string DeferredFailureLine(string step, ulong id, int error) =>
        string.Create(CultureInfo.InvariantCulture, $"move: deferred batch failed step={step} hwnd={HexId(id)} error={error}, falling back to SetWindowPos");

    /// <summary>Line for a failed SetWindowPos call.</summary>
    public static string SetWindowPosFailureLine(ulong id, int error) =>
        string.Create(CultureInfo.InvariantCulture, $"move: SetWindowPos failed hwnd={HexId(id)} error={error}");

    /// <summary>Line for a window left out of a batch. Reason is "no-offsets", "banned" or "hung".</summary>
    public static string SkipLine(ulong id, string reason) => $"move: skipped hwnd={HexId(id)} reason={reason}";

    /// <summary>Formats an optional rectangle as l,t,r,b, or "unavailable" when it could not be read.</summary>
    public static string RectOrUnavailable(Rect? r) => r is { } rect ? RectText(rect) : "unavailable";

    /// <summary>Returns rect minus frame per side: the invisible border between GetWindowRect and the visual frame.</summary>
    public static Rect Border(Rect rect, Rect frame) => Delta(frame, rect);

    /// <summary>Line for one candidate window of a drag snapshot. Frame is the DWM extended frame bounds and rect is GetWindowRect. An empty window class or exe is written as "unknown".</summary>
    public static string CandidateLine(ulong id, Rect frame, Rect rect, uint dpi, string windowClass, string exe, bool dragged) =>
        string.Create(CultureInfo.InvariantCulture, $"diag: window id={id} frame={RectText(frame)} rect={RectText(rect)} border={RectText(Border(rect, frame))} dpi={dpi} class={(windowClass.Length == 0 ? "unknown" : windowClass)} exe={(exe.Length == 0 ? "unknown" : exe)} dragged={(dragged ? "yes" : "no")}");

    /// <summary>Line for a window at the end of a move: where it is (frame, rect; "unavailable" when unreadable) and the position Magnetic attraction held ("none" when no axis was held).</summary>
    public static string MoveResultLine(ulong id, Rect? frame, Rect? rect, Rect? held) =>
        string.Create(CultureInfo.InvariantCulture, $"diag: move result id={id} frame={RectOrUnavailable(frame)} rect={RectOrUnavailable(rect)} held={(held is { } h ? RectText(h) : "none")}");

    /// <summary>Start-up line with the system facts. dpiAwareness is "unaware", "system", "per-monitor", "per-monitor-v2" or "unknown"; osBuild is "build.revision" or "unknown".</summary>
    public static string SystemLine(string osBuild, string dpiAwareness, bool dwmComposition, int monitors) =>
        string.Create(CultureInfo.InvariantCulture, $"diag: system os_build={osBuild} dpi_awareness={dpiAwareness} dwm_composition={(dwmComposition ? "on" : "off")} monitors={monitors}");

    /// <summary>Start-up line for one monitor.</summary>
    public static string MonitorLine(int index, Rect bounds, Rect work, uint dpiX, uint dpiY, bool primary) =>
        string.Create(CultureInfo.InvariantCulture, $"diag: monitor {index} rect={RectText(bounds)} work={RectText(work)} dpi={dpiX}x{dpiY} primary={(primary ? "yes" : "no")}");
}
