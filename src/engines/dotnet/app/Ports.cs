using MagneticBorders.Core;

namespace MagneticBorders.Application;

/// <summary>Everything the controller needs at drag start.</summary>
/// <param name="Windows">Eligible windows on the dragged window's monitor, including the dragged one.</param>
/// <param name="WorkArea">Monitor work area in pixels.</param>
/// <param name="DpiScale">DPI of the dragged window divided by 96.</param>
/// <param name="WorkAreas">Work areas of every monitor, the dragged window's one included; tells which screen sides touch another monitor. Null counts as one monitor.</param>
/// <param name="Stack">Every visible, uncloaked, not minimized window that hides what is below it, in z-order from the top, on any monitor. Magnetic attraction uses it to find which parts of other windows can be seen (SNAP-038). Null counts as nothing covering anything.</param>
public sealed record Snapshot(
    IReadOnlyList<WinInfo> Windows, Rect WorkArea, double DpiScale,
    IReadOnlyList<Rect>? WorkAreas = null, IReadOnlyList<(ulong Id, Rect Frame)>? Stack = null);

/// <summary>Port: reads window state from the OS. Called on the UI thread. Implemented by Win32Inventory.</summary>
public interface IWindowInventory
{
    /// <summary>Enumerates eligible windows on the dragged window's monitor.</summary>
    Snapshot Snapshot(ulong draggedId);
    /// <summary>Current visual frame of a window, or null if it cannot be read.</summary>
    Rect? FrameOf(ulong id);
    /// <summary>Verbose diagnostics: called when a move ends, with the position Magnetic attraction held (null when no axis was held). The adapter may log where the window is. The default does nothing.</summary>
    void ReportMoveResult(ulong id, Rect? held) { }
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

/// <summary>Sink for mouse movement during a move. Implemented by DragController; the pointer hook calls it on the UI thread for every mouse movement while it runs.</summary>
public interface IPointerEvents
{
    /// <summary>Windows is about to put the cursor at <paramref name="proposed"/>. Returns the position to put it at instead, or null to let the movement through unchanged.</summary>
    Point? OnPointerMove(Point proposed);
    /// <summary>A mouse button was released. The move is over even if its end event has not arrived yet.</summary>
    void OnPointerRelease();
}

/// <summary>Port: watches the mouse while a window is moved (SNAP-070). Implemented by Win32PointerHook with a low-level mouse hook that is installed only between Install and Remove. Called on the UI thread.</summary>
public interface IPointerHook
{
    /// <summary>Starts sending mouse movement to <paramref name="sink"/>. Returns false when the hook cannot be installed.</summary>
    bool Install(IPointerEvents sink);
    /// <summary>Stops sending mouse movement. Safe to call when not installed.</summary>
    void Remove();
    /// <summary>The cursor position now, or null if it cannot be read.</summary>
    Point? ReadCursor();
}
