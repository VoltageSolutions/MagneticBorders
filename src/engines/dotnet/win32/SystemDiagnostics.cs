using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MagneticBorders.Application;
using MagneticBorders.Core;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace MagneticBorders.Win32;

/// <summary>Writes the system report of the verbose log: OS build, DPI awareness, DWM composition and monitors. UI thread only.</summary>
public static unsafe class SystemDiagnostics
{
    private const string VersionKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion";
    private const uint MonitorInfoPrimary = 1;

    /// <summary>Scratch list filled by the EnumDisplayMonitors callback.</summary>
    private static readonly List<string> s_monitors = [];

    /// <summary>Writes the start-up system lines ("diag: system ..." and one "diag: monitor ..." per monitor) with log.Debug. Called once when verbose logging is on.</summary>
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
        Justification = "The DPI awareness and monitor DPI calls need Windows 10 1607 and 8.1; the product supports Windows 10 and 11 only, and a later TargetPlatformVersion is not a valid value for this target framework.")]
    public static void Log(ILog log)
    {
        s_monitors.Clear();
        PInvoke.EnumDisplayMonitors(HDC.Null, null, &MonitorCallback, 0);
        BOOL composition = default;
        bool on = PInvoke.DwmIsCompositionEnabled(&composition).Succeeded && composition;
        log.Debug(MoveDiagnostics.SystemLine(OsBuild(), DpiAwareness(), on, s_monitors.Count));
        foreach (var line in s_monitors) log.Debug(line);
        s_monitors.Clear();
    }

    /// <summary>Returns "build.revision" from the registry, or "unknown".</summary>
    private static string OsBuild()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(VersionKey);
            if (key?.GetValue("CurrentBuildNumber") is not string build) return "unknown";
            return key.GetValue("UBR") is int revision ? $"{build}.{revision}" : build;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return "unknown"; }
    }

    /// <summary>Returns the DPI awareness of the calling thread.</summary>
    [SuppressMessage("Interoperability", "CA1416:Validate platform compatibility",
        Justification = "GetThreadDpiAwarenessContext needs Windows 10 1607; the product supports Windows 10 and 11 only.")]
    private static string DpiAwareness()
    {
        var ctx = PInvoke.GetThreadDpiAwarenessContext();
        if (PInvoke.AreDpiAwarenessContextsEqual(ctx, new DPI_AWARENESS_CONTEXT(-4))) return "per-monitor-v2";
        return PInvoke.GetAwarenessFromDpiAwarenessContext(ctx) switch
        {
            DPI_AWARENESS.DPI_AWARENESS_UNAWARE => "unaware",
            DPI_AWARENESS.DPI_AWARENESS_SYSTEM_AWARE => "system",
            DPI_AWARENESS.DPI_AWARENESS_PER_MONITOR_AWARE => "per-monitor",
            _ => "unknown",
        };
    }

    private static Rect ToRect(RECT r) => new(r.left, r.top, r.right, r.bottom);

    /// <summary>EnumDisplayMonitors callback (function pointer, no delegate). Appends one monitor line.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL MonitorCallback(HMONITOR monitor, HDC _, RECT* __, LPARAM ___)
    {
        MONITORINFO info = default;
        info.cbSize = (uint)sizeof(MONITORINFO);
        if (PInvoke.GetMonitorInfo(monitor, &info))
        {
            uint dpiX = 0, dpiY = 0;
            if (PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, &dpiX, &dpiY).Failed) { dpiX = 0; dpiY = 0; }
            s_monitors.Add(MoveDiagnostics.MonitorLine(s_monitors.Count, ToRect(info.rcMonitor), ToRect(info.rcWork), dpiX, dpiY, (info.dwFlags & MonitorInfoPrimary) != 0));
        }
        return true;
    }
}
