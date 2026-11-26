using MagneticBorders.Application;
using MagneticBorders.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MagneticBorders.Win32;

/// <summary>IWindowMover over Win32. Applies a batch with BeginDeferWindowPos / DeferWindowPos / EndDeferWindowPos so all windows repaint together, and falls back to SetWindowPos per window. UI thread only.</summary>
/// <param name="state">Offsets and the excluded set.</param>
/// <param name="log">Log sink.</param>
public sealed class Win32Mover(WindowState state, ILog log) : IWindowMover
{
    private const SET_WINDOW_POS_FLAGS Flags =
        SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER | SET_WINDOW_POS_FLAGS.SWP_NOOWNERZORDER;

    /// <summary>Converts visual frames to window rectangles using the cached offsets, moves the windows and returns the frames read back. Skips excluded and hung windows; a window whose fallback move fails is excluded for the session.</summary>
    public IReadOnlyList<(ulong Id, Rect Frame)> Apply(IReadOnlyList<(ulong Id, Rect Frame)> moves)
    {
        var todo = new List<(ulong Id, HWND H, Rect W)>();
        foreach (var (id, f) in moves)
        {
            if (state.Excluded.Contains(id) || !state.Offsets.TryGetValue(id, out var o)) continue;
            var h = Frames.ToHwnd(id);
            if (PInvoke.IsHungAppWindow(h)) continue;
            todo.Add((id, h, new Rect(f.L + o.L, f.T + o.T, f.R + o.R, f.B + o.B)));
        }
        if (todo.Count == 0) return [];

        bool ok = false;
        var hdwp = PInvoke.BeginDeferWindowPos(todo.Count);
        if (!hdwp.IsNull)
        {
            ok = true;
            foreach (var (_, h, w) in todo)
            {
                hdwp = PInvoke.DeferWindowPos(hdwp, h, HWND.Null, w.L, w.T, w.Width, w.Height, Flags);
                if (hdwp.IsNull) { ok = false; break; }
            }
            if (ok) ok = PInvoke.EndDeferWindowPos(hdwp);
        }
        if (!ok)
        {
            foreach (var (id, h, w) in todo)
            {
                if (!PInvoke.SetWindowPos(h, HWND.Null, w.L, w.T, w.Width, w.Height, Flags))
                {
                    state.Excluded.Add(id);
                    log.Info($"window {id:X} excluded (move failed)");
                }
            }
        }

        var result = new List<(ulong, Rect)>(todo.Count);
        foreach (var (id, h, _) in todo)
            if (Frames.TryGetFrame(h, out var f)) result.Add((id, f));
        return result;
    }
}
