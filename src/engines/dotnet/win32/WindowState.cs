using MagneticBorders.Core;

namespace MagneticBorders.Win32;

/// <summary>State shared by inventory and mover (UI thread only).</summary>
public sealed class WindowState
{
    /// <summary>Per window: GetWindowRect minus visual frame, cached at snapshot time.</summary>
    public Dictionary<ulong, Rect> Offsets { get; } = [];
    /// <summary>Windows that failed to move (e.g. elevated); skipped for the session.</summary>
    public HashSet<ulong> Excluded { get; } = [];
}
