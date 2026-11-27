namespace MagneticBorders.Core;

/// <summary>Which side of a window rectangle is meant: Left, Top, Right or Bottom. Used by the seam builder to say which edge of the dragged window is being resized.</summary>
public enum Edge { Left, Top, Right, Bottom }

/// <summary>Physical pixels; R and B exclusive.</summary>
public readonly record struct Rect(int L, int T, int R, int B)
{
    /// <summary>Horizontal extent in pixels (R minus L).</summary>
    public int Width => R - L;
    /// <summary>Vertical extent in pixels (B minus T).</summary>
    public int Height => B - T;
}

/// <summary>A screen point in pixels, for example the mouse cursor.</summary>
/// <param name="X">Horizontal coordinate.</param>
/// <param name="Y">Vertical coordinate.</param>
public readonly record struct Point(int X, int Y);

/// <summary>One candidate window in a snapshot: opaque id (the HWND value in the Win32 layer), visual frame, and minimum size.</summary>
/// <param name="Id">Opaque window identifier; the Win32 layer stores the HWND value.</param>
/// <param name="Frame">Visual frame (DWM extended frame bounds), not the GetWindowRect rectangle.</param>
/// <param name="MinW">Declared minimum width in pixels.</param>
/// <param name="MinH">Declared minimum height in pixels.</param>
public readonly record struct WinInfo(ulong Id, Rect Frame, int MinW, int MinH);

/// <summary>Domain settings for one seam build.</summary>
/// <param name="GlueTolerance">Maximum distance in pixels at which two edges still count as touching. Already scaled by DPI.</param>
public readonly record struct Config(int GlueTolerance);

/// <summary>Extension methods on Edge and Rect that let the seam algorithm treat all four edges the same way.</summary>
public static class EdgeExt
{
    /// <summary>Left/Right edges form a vertical line (x coordinate).</summary>
    public static bool IsVertical(this Edge e) => e is Edge.Left or Edge.Right;

    /// <summary>The edge on the other side of the same axis (Left to Right, Top to Bottom and back).</summary>
    public static Edge Opposite(this Edge e) => e switch
    {
        Edge.Left => Edge.Right,
        Edge.Right => Edge.Left,
        Edge.Top => Edge.Bottom,
        _ => Edge.Top,
    };

    /// <summary>Returns the coordinate of the given edge: x for Left and Right, y for Top and Bottom.</summary>
    public static int Coord(this Rect r, Edge e) => e switch
    {
        Edge.Left => r.L,
        Edge.Top => r.T,
        Edge.Right => r.R,
        _ => r.B,
    };

    /// <summary>Returns a copy of the rectangle with the given edge moved to v. Does not enforce a minimum size.</summary>
    public static Rect With(this Rect r, Edge e, int v) => e switch
    {
        Edge.Left => r with { L = v },
        Edge.Top => r with { T = v },
        Edge.Right => r with { R = v },
        _ => r with { B = v },
    };
}
