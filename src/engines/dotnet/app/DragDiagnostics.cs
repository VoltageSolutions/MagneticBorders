using System.Globalization;
using MagneticBorders.Core;

namespace MagneticBorders.Application;

/// <summary>Text of the verbose diag: lines that the drag controller writes. No Win32 types, so it is unit-testable. The C++ and Rust engines produce the same text (docs/spec/engine.md).</summary>
public static class DragDiagnostics
{
    /// <summary>Formats a rectangle as l,t,r,b.</summary>
    public static string RectText(Rect r) => string.Create(CultureInfo.InvariantCulture, $"{r.L},{r.T},{r.R},{r.B}");

    /// <summary>Formats a point as x,y.</summary>
    public static string PointText(Point p) => string.Create(CultureInfo.InvariantCulture, $"{p.X},{p.Y}");

    /// <summary>Line for a mouse movement that the hook changed: the position Windows proposed, the cursor position the engine set instead, and the window frame that cursor position gives.</summary>
    public static string PointerHoldLine(Point proposed, Point cursor, Rect window) =>
        $"diag: pointer hold proposed={PointText(proposed)} cursor={PointText(cursor)} window={RectText(window)}";

    /// <summary>Line for a button release seen by the hook: the position of the release event and the cursor position the engine last set or let through.</summary>
    public static string PointerReleaseLine(Point cursor, Point expected) =>
        $"diag: pointer release cursor={PointText(cursor)} expected={PointText(expected)}";

    /// <summary>Line for the decision at the end of a pointer-mode move: the window's frame (unavailable when unreadable), the held position (none when no axis was held), and the action, applied, none, or skipped-size.</summary>
    public static string MoveCorrectionLine(ulong id, Rect? frame, Rect? held, string action) =>
        string.Create(CultureInfo.InvariantCulture, $"diag: move correction id={id} frame={(frame is { } f ? RectText(f) : "unavailable")} held={(held is { } h ? RectText(h) : "none")} action={action}");
}
