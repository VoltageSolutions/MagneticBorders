using System.Diagnostics;
using System.Runtime.InteropServices;
using MagneticBorders.Application;
using MagneticBorders.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MagneticBorders.Win32;

/// <summary>IWindowMover over Win32. Applies a batch with BeginDeferWindowPos / DeferWindowPos / EndDeferWindowPos so all windows repaint together, and falls back to SetWindowPos per window. With verbose on it also writes the "move:" lines described in docs/spec/engine.md. UI thread only.</summary>
/// <param name="state">Offsets and the excluded set.</param>
/// <param name="log">Log sink.</param>
/// <param name="verbose">Writes the verbose "move:" diagnostic lines.</param>
public sealed class Win32Mover(WindowState state, ILog log, bool verbose) : IWindowMover
{
    private const SET_WINDOW_POS_FLAGS Flags =
        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;

    /// <summary>Converts visual frames to window rectangles using the cached offsets, moves the windows, and returns the frames read back. Skips excluded and hung windows; a window whose fallback move fails is excluded for the session.</summary>
    public IReadOnlyList<(ulong Id, Rect Frame)> Apply(IReadOnlyList<(ulong Id, Rect Frame)> moves)
    {
        var todo = new List<(ulong Id, HWND H, Rect W, Rect Frame)>();
        foreach (var (id, f) in moves)
        {
            bool known = state.Offsets.TryGetValue(id, out var o);
            var h = Frames.ToHwnd(id);
            if (!known || state.Excluded.Contains(id) || PInvoke.IsHungAppWindow(h))
            {
                if (verbose) log.Debug(MoveDiagnostics.SkipLine(id, !known ? "no-offsets" : state.Excluded.Contains(id) ? "banned" : "hung"));
                continue;
            }
            todo.Add((id, h, new Rect(f.L + o.L, f.T + o.T, f.R + o.R, f.B + o.B), f));
        }
        if (todo.Count == 0) return [];

        long started = verbose ? Stopwatch.GetTimestamp() : 0;
        (string Step, ulong Id, int Error)? failure = null;
        var hdwp = PInvoke.BeginDeferWindowPos(todo.Count);
        if (hdwp.IsNull) failure = ("begin", 0, Marshal.GetLastPInvokeError());
        else
        {
            foreach (var (id, h, w, _) in todo)
            {
                hdwp = PInvoke.DeferWindowPos(hdwp, h, HWND.Null, w.L, w.T, w.Width, w.Height, Flags);
                if (hdwp.IsNull) { failure = ("defer", id, Marshal.GetLastPInvokeError()); break; }
            }
            if (failure is null && !PInvoke.EndDeferWindowPos(hdwp)) failure = ("end", 0, Marshal.GetLastPInvokeError());
        }
        if (failure is { } failed)
        {
            if (verbose) log.Debug(MoveDiagnostics.DeferredFailureLine(failed.Step, failed.Id, failed.Error));
            foreach (var (id, h, w, _) in todo)
            {
                if (!PInvoke.SetWindowPos(h, HWND.Null, w.L, w.T, w.Width, w.Height, Flags))
                {
                    int error = Marshal.GetLastPInvokeError();
                    state.Excluded.Add(id);
                    log.Info($"window {id:X} excluded (move failed)");
                    if (verbose) log.Debug(MoveDiagnostics.SetWindowPosFailureLine(id, error));
                }
            }
        }
        long elapsedUs = verbose ? (long)Stopwatch.GetElapsedTime(started).TotalMicroseconds : 0;

        var result = new List<(ulong, Rect)>(todo.Count);
        var lines = verbose ? new List<string>(todo.Count) : null;
        int mismatches = 0;
        foreach (var (id, h, w, frame) in todo)
        {
            bool read = Frames.TryGetFrame(h, out var actual);
            if (read) result.Add((id, actual));
            if (lines is null) continue;
            var windowDelta = Frames.TryGetWindowRect(h, out var now) ? MoveDiagnostics.Delta(w, now) : default;
            if (read && actual != frame) mismatches++;
            lines.Add(MoveDiagnostics.WindowLine(id, frame, read ? actual : null, windowDelta));
        }
        if (lines is not null)
        {
            log.Debug(MoveDiagnostics.BatchLine(todo.Count, failure is null ? "deferred" : "individual", (uint)Flags, elapsedUs, mismatches));
            foreach (var line in lines) log.Debug(line);
        }
        return result;
    }
}
