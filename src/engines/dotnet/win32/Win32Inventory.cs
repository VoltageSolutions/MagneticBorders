using MagneticBorders.Application;
using MagneticBorders.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MagneticBorders.Win32;

/// <summary>IWindowInventory over Win32: enumerates top-level windows, filters them, and caches frame offsets in WindowState. UI thread only. DI singleton.</summary>
/// <param name="state">Shared offsets and exclusions.</param>
/// <param name="excludedApps">Initial excluded executable names.</param>
public sealed unsafe class Win32Inventory(WindowState state, IReadOnlyCollection<string> excludedApps) : IWindowInventory
{
    /// <summary>Scratch list filled by the EnumWindows callback; reused to avoid allocations.</summary>
    private static readonly List<HWND> s_enum = [];
    /// <summary>Scratch list filled by the EnumDisplayMonitors callback.</summary>
    private static readonly List<Rect> s_areas = [];
    private volatile HashSet<string> _excluded = new(excludedApps, StringComparer.OrdinalIgnoreCase);

    /// <summary>Replaces the excluded exe names (applies on the next drag).</summary>
    public void SetExcluded(IEnumerable<string> apps) => _excluded = new(apps, StringComparer.OrdinalIgnoreCase);
    /// <summary>EnumWindows callback (function pointer, no delegate). Collects handles.</summary>

    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static BOOL EnumCb(HWND h, LPARAM _) { s_enum.Add(h); return true; }

    /// <summary>EnumDisplayMonitors callback (function pointer, no delegate). Collects the work area of each monitor.</summary>
    [System.Runtime.InteropServices.UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static BOOL MonitorCb(HMONITOR mon, HDC dc, RECT* clip, LPARAM _)
    {
        MONITORINFO mi = default;
        mi.cbSize = (uint)sizeof(MONITORINFO);
        if (PInvoke.GetMonitorInfo(mon, &mi)) s_areas.Add(new Rect(mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom));
        return true;
    }

    /// <summary>Visual frame of a window, or null.</summary>
    public Rect? FrameOf(ulong id) => Frames.TryGetFrame(Frames.ToHwnd(id), out var f) ? f : null;

    /// <summary>Builds a snapshot of eligible windows on the dragged window's monitor, the work areas of all monitors, and the z-order of the windows that hide what is below them, and refreshes the per-window frame offsets that Win32Mover needs.</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
        Justification = "GetDpiForWindow needs Windows 10 1607; the product supports Windows 10 and 11 only, and a later TargetPlatformVersion is not a valid value for this target framework.")]
    public Snapshot Snapshot(ulong draggedId)
    {
        var dragged = Frames.ToHwnd(draggedId);
        var mon = PInvoke.MonitorFromWindow(dragged, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        MONITORINFO mi = default;
        mi.cbSize = (uint)sizeof(MONITORINFO);
        PInvoke.GetMonitorInfo(mon, &mi);
        var work = new Rect(mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom);
        uint dpi = PInvoke.GetDpiForWindow(dragged);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;
        int minW = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXMINTRACK);
        int minH = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYMINTRACK);

        s_areas.Clear();
        PInvoke.EnumDisplayMonitors(HDC.Null, (RECT*)null, &MonitorCb, 0);
        var areas = s_areas.ToArray();
        s_areas.Clear();

        s_enum.Clear();
        PInvoke.EnumWindows(&EnumCb, 0);
        var list = new List<WinInfo>();
        var stack = new List<(ulong Id, Rect Frame)>();
        uint self = PInvoke.GetCurrentProcessId();
        // EnumWindows walks the z-order from the top, so the stack comes out top first.
        foreach (var h in s_enum.ToArray())
        {
            ulong id = (ulong)(nint)h.Value;
            if (Covers(h, self) && Frames.TryGetFrame(h, out var covering) && covering.Width > 0 && covering.Height > 0) stack.Add((id, covering));
            if (!Eligible(h, self)) continue;
            if (PInvoke.MonitorFromWindow(h, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST) != mon) continue;
            if (id != draggedId && state.Excluded.Contains(id)) continue;
            if (!Frames.TryGetFrame(h, out var frame) || !Frames.TryGetWindowRect(h, out var wr)) continue;
            state.Offsets[id] = new Rect(wr.L - frame.L, wr.T - frame.T, wr.R - frame.R, wr.B - frame.B);
            list.Add(new WinInfo(id, frame, minW, minH));
        }
        s_enum.Clear();
        return new Snapshot(list, work, scale, areas, stack);
    }

    /// <summary>True when a window hides what is below it: visible, not minimized, not cloaked, not click-through (overlays such as the FancyZones editor), and not a window of this process (SNAP-038).</summary>
    private static bool Covers(HWND h, uint selfPid)
    {
        if (!PInvoke.IsWindowVisible(h) || PInvoke.IsIconic(h)) return false;
        uint cloaked = 0;
        if (PInvoke.DwmGetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Succeeded && cloaked != 0) return false;
        long ex = (uint)PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if ((ex & (long)WINDOW_EX_STYLE.WS_EX_TRANSPARENT) != 0) return false;
        uint pid;
        return PInvoke.GetWindowThreadProcessId(h, &pid) == 0 || pid != selfPid;
    }

    /// <summary>Filter: visible, not minimized or maximized, not cloaked, resizable, not a tool window, not owned, titled, not this process, and not on the excluded list.</summary>
    private bool Eligible(HWND h, uint selfPid)
    {
        if (!PInvoke.IsWindowVisible(h) || PInvoke.IsIconic(h) || PInvoke.IsZoomed(h)) return false;
        uint cloaked = 0;
        if (PInvoke.DwmGetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint)).Succeeded && cloaked != 0) return false;
        long style = (uint)PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_STYLE);
        long ex = (uint)PInvoke.GetWindowLong(h, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        if ((style & (long)WINDOW_STYLE.WS_THICKFRAME) == 0) return false;
        if ((ex & (long)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW) != 0) return false;
        if (!PInvoke.GetWindow(h, GET_WINDOW_CMD.GW_OWNER).IsNull) return false;
        if (PInvoke.GetWindowTextLength(h) == 0) return false;
        uint pid;
        if (PInvoke.GetWindowThreadProcessId(h, &pid) == 0) return false;
        if (pid == selfPid) return false;
        if (_excluded.Count > 0 && _excluded.Contains(ExeName(pid))) return false;
        return true;
    }

    /// <summary>File name of the process executable, or an empty string if it cannot be queried.</summary>
    private static string ExeName(uint pid)
    {
        var proc = PInvoke.OpenProcess(Windows.Win32.System.Threading.PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (proc.IsNull) return "";
        try
        {
            char* buf = stackalloc char[1024];
            uint len = 1024;
            if (!PInvoke.QueryFullProcessImageName(proc, Windows.Win32.System.Threading.PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buf, &len)) return "";
            return Path.GetFileName(new string(buf, 0, (int)len));
        }
        finally { PInvoke.CloseHandle(proc); }
    }
}
