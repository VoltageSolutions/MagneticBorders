using System.Runtime.InteropServices;

namespace MagneticBorders.Settings;

/// <summary>Minimal user32 and dwmapi P/Invokes used for window placement, DPI, and foreground handling.</summary>
internal static class NativeMethods
{
    /// <summary>Screen point in physical pixels (Win32 POINT).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    /// <summary>Cursor position in screen coordinates; picks the monitor for the flyout.</summary>
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    /// <summary>DPI of the monitor a window is on (96 = 100 percent); 0 when the handle is invalid.</summary>
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(nint hwnd);
    /// <summary>Asks Windows to give the window foreground; it may refuse.</summary>
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);
    /// <summary>Handle of the current foreground window; used by the flyout focus watch.</summary>
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    /// <summary>True when the window is minimized.</summary>
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    /// <summary>Shows, hides, minimizes, or restores a window.</summary>
    [DllImport("user32.dll")] public static extern bool ShowWindow(nint hwnd, int cmd);
    /// <summary>ShowWindow command that restores a minimized window.</summary>
    public const int SW_RESTORE = 9;
    /// <summary>Sets a DWM window attribute (corner preference and border colour).</summary>
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    /// <summary>Extends the window frame into the client area by the given margins.</summary>
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(nint hwnd, ref MARGINS margins);

    /// <summary>Frame margins in pixels (Win32 MARGINS).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left; public int Right; public int Top; public int Bottom; }

    /// <summary>DWM attribute id for the window corner preference.</summary>
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    /// <summary>DWM attribute id for the window border colour.</summary>
    public const int DWMWA_BORDER_COLOR = 34;
    /// <summary>Border colour value that restores the system default.</summary>
    public const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);
    /// <summary>Corner preference value: rounded corners.</summary>
    public const int DWMWCP_ROUND = 2;

    /// <summary>DPI scale factor for a window (1.0 at 96 DPI; 1.0 when the DPI is unknown).</summary>
    public static double ScaleFor(nint hwnd)
    {
        var dpi = GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }
}
