using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MagneticBorders.Application;
using MagneticBorders.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MagneticBorders.Win32;

/// <summary>IPointerHook over a WH_MOUSE_LL hook that exists only between Install and Remove (SNAP-070). For each mouse movement it asks the sink where the cursor may go; when that differs, it blocks the movement and puts the cursor there with SetCursorPos, so Windows moves the window to the held position itself. A button release is passed on to the sink. The hook runs on the installing thread, which must pump messages. At most one at a time. UI thread only.</summary>
/// <param name="log">Log sink for callback failures.</param>
public sealed unsafe class Win32PointerHook(ILog log) : IPointerHook, IDisposable
{
    /// <summary>The one static slot routing the unmanaged callback to the live instance.</summary>
    private static Win32PointerHook? s_slot;

    private readonly ILog _log = log;
    private HHOOK _hook;
    private IPointerEvents? _sink;

    /// <summary>Installs the hook. Returns false when SetWindowsHookEx fails or another hook of this kind is installed.</summary>
    public bool Install(IPointerEvents sink)
    {
        if (!_hook.IsNull) Remove();
        if (s_slot is not null) return false;
        var module = PInvoke.GetModuleHandle((PCWSTR)null);
        _hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &MouseCb, new HINSTANCE(module.Value), 0);
        if (_hook.IsNull) return false;
        _sink = sink;
        s_slot = this;
        return true;
    }

    /// <summary>Removes the hook. Safe to call when it is not installed.</summary>
    public void Remove()
    {
        if (!_hook.IsNull) PInvoke.UnhookWindowsHookEx(_hook);
        _hook = default;
        _sink = null;
        if (ReferenceEquals(s_slot, this)) s_slot = null;
    }

    /// <summary>GetCursorPos, or null when it fails.</summary>
    public Point? ReadCursor()
    {
        POINT p;
        if (!PInvoke.GetCursorPos(&p)) return null;
        return new Point(p.x, p.y);
    }

    /// <summary>Removes the hook.</summary>
    public void Dispose() => Remove();

    /// <summary>True for the release of any mouse button.</summary>
    private static bool IsButtonUp(uint msg) =>
        msg == PInvoke.WM_LBUTTONUP || msg == PInvoke.WM_RBUTTONUP || msg == PInvoke.WM_MBUTTONUP || msg == PInvoke.WM_XBUTTONUP;

    /// <summary>UnmanagedCallersOnly entry for the mouse hook. Replaces a mouse movement by the held cursor position when the sink asks for it and passes everything else on.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT MouseCb(int code, WPARAM wp, LPARAM lp)
    {
        var self = s_slot;
        if (code == 0 && self?._sink is { } sink)
        {
            try
            {
                uint msg = (uint)wp.Value;
                if (msg == PInvoke.WM_MOUSEMOVE)
                {
                    var m = (MSLLHOOKSTRUCT*)lp.Value;
                    if (sink.OnPointerMove(new Point(m->pt.x, m->pt.y)) is { } to)
                    {
                        PInvoke.SetCursorPos(to.X, to.Y);
                        return (LRESULT)1; // the original movement is replaced by the held cursor position
                    }
                }
                else if (IsButtonUp(msg))
                {
                    sink.OnPointerRelease(); // may remove this hook
                }
            }
            catch (Exception ex) { self._log.Info("error: " + ex.Message); }
        }
        return PInvoke.CallNextHookEx(HHOOK.Null, code, wp, lp);
    }
}
