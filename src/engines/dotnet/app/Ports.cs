using MagneticBorders.Core;

namespace MagneticBorders.Application;

/// <summary>Everything the controller needs at drag start.</summary>
/// <param name="Windows">Eligible windows on the dragged window's monitor, including the dragged one.</param>
/// <param name="WorkArea">Monitor work area in pixels.</param>
/// <param name="DpiScale">DPI of the dragged window divided by 96.</param>
public sealed record Snapshot(IReadOnlyList<WinInfo> Windows, Rect WorkArea, double DpiScale);

/// <summary>Port: reads window state from the OS. Called on the UI thread. Implemented by Win32Inventory.</summary>
public interface IWindowInventory
{
    /// <summary>Enumerates eligible windows on the dragged window's monitor.</summary>
    Snapshot Snapshot(ulong draggedId);
    /// <summary>Current visual frame of a window, or null if it cannot be read.</summary>
    Rect? FrameOf(ulong id);
}

/// <summary>Port: moves windows. Called on the UI thread. Implemented by Win32Mover.</summary>
public interface IWindowMover
{
    /// <summary>Applies frames in one batch; returns the frames actually applied (read back).</summary>
    IReadOnlyList<(ulong Id, Rect Frame)> Apply(IReadOnlyList<(ulong Id, Rect Frame)> moves);
}

/// <summary>Port: monotonic millisecond clock. Implemented by StopwatchClock in the host.</summary>
public interface IClock { ulong NowMs(); }

/// <summary>Port: log sink. Info always writes; Debug only in verbose mode. Implementations must be thread safe because the pipe thread logs too.</summary>
public interface ILog { void Info(string msg); void Debug(string msg); }

/// <summary>The three one-shot timers the controller uses: Flush (deferred throttled apply), Settle (read back after the drag pauses) and Verify (after the drag ends).</summary>
public enum TimerKind { Flush, Settle, Verify }

/// <summary>Port: one-shot timers whose callbacks arrive on the UI thread. Implemented by Win32Timer.</summary>
public interface ITimer
{
    /// <summary>One-shot on the UI thread; re-arming a kind replaces it.</summary>
    void Arm(TimerKind kind, int ms);
    /// <summary>Cancels every armed timer.</summary>
    void CancelAll();
}
