using MagneticBorders.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace MagneticBorders.Win32;

/// <summary>Helpers that convert between HWND and the pixel types of core. Internal to the Win32 layer.</summary>
internal static class Frames
{
    /// <summary>Reads the visual frame (DWM extended frame bounds, without the invisible resize border). Returns false if the call fails.</summary>
    public static unsafe bool TryGetFrame(HWND h, out Rect frame)
    {
        RECT r;
        if (PInvoke.DwmGetWindowAttribute(h, DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS, &r, (uint)sizeof(RECT)).Failed)
        {
            frame = default;
            return false;
        }
        frame = new Rect(r.left, r.top, r.right, r.bottom);
        return true;
    }

    /// <summary>Reads the classic GetWindowRect rectangle, which includes the invisible border. Returns false on failure.</summary>
    public static unsafe bool TryGetWindowRect(HWND h, out Rect rect)
    {
        RECT r;
        if (!PInvoke.GetWindowRect(h, &r)) { rect = default; return false; }
        rect = new Rect(r.left, r.top, r.right, r.bottom);
        return true;
    }

    /// <summary>Converts an engine window id back to an HWND.</summary>
    public static HWND ToHwnd(ulong id) => new((nint)id);
}
