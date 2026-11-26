using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MagneticBorders.Application;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace MagneticBorders.Win32;

/// <summary>Installs WinEvent hooks on the calling (UI) thread and drives the controller.</summary>
public sealed unsafe class WinEventSource(DragController controller, ILog log) : IDisposable
{
    // The one static slot routing unmanaged callbacks to the controller.
    private static WinEventSource? s_slot;

    private readonly ILog _log = log;
    private HWINEVENTHOOK _rangeHook, _locHook;
    private HWND _dragged;

    /// <summary>Installs the move/size range hook. The location hook is added per drag. Must be called on the UI thread; callbacks arrive on that thread.</summary>
    public void Install()
    {
        s_slot = this;
        _rangeHook = PInvoke.SetWinEventHook(
            PInvoke.EVENT_SYSTEM_MOVESIZESTART, PInvoke.EVENT_SYSTEM_MOVESIZEEND, HMODULE.Null, &RangeCb, 0, 0,
            PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        if (_rangeHook.IsNull) throw new InvalidOperationException("SetWinEventHook failed");
    }

    /// <summary>Removes both hooks and clears the static slot.</summary>
    public void Dispose()
    {
        RemoveLocHook();
        if (!_rangeHook.IsNull) { PInvoke.UnhookWinEvent(_rangeHook); _rangeHook = default; }
        if (ReferenceEquals(s_slot, this)) s_slot = null;
    }

    /// <summary>Removes the per-drag location hook.</summary>
    private void RemoveLocHook()
    {
        if (!_locHook.IsNull) { PInvoke.UnhookWinEvent(_locHook); _locHook = default; }
        _dragged = HWND.Null;
    }

    /// <summary>Handles move/size start and end. On start, asks the controller to classify and installs the location hook for the owning process; on end, removes it.</summary>
    private void OnRange(uint evt, HWND hwnd, int idObject, int idChild)
    {
        if (idObject != 0 || idChild != 0) return;
        ulong id = (ulong)(nint)hwnd.Value;
        if (evt == PInvoke.EVENT_SYSTEM_MOVESIZESTART)
        {
            RemoveLocHook();
            controller.OnMoveSizeStart(id);
            if (controller.State == DragState.Ignoring) { controller.OnMoveSizeEnd(id); return; }
            uint pid;
            if (PInvoke.GetWindowThreadProcessId(hwnd, &pid) == 0) { controller.OnMoveSizeEnd(id); return; }
            _dragged = hwnd;
            _locHook = PInvoke.SetWinEventHook(
                PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_LOCATIONCHANGE, HMODULE.Null, &LocCb, pid, 0,
                PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        }
        else if (evt == PInvoke.EVENT_SYSTEM_MOVESIZEEND)
        {
            RemoveLocHook();
            controller.OnMoveSizeEnd(id);
        }
    }

    /// <summary>Forwards location changes of the dragged window to the controller.</summary>
    private void OnLoc(HWND hwnd, int idObject, int idChild)
    {
        if (hwnd != _dragged || idObject != 0 || idChild != 0) return;
        controller.OnLocationChange((ulong)(nint)hwnd.Value);
    }
    /// <summary>UnmanagedCallersOnly entry for the range hook; routes to the live instance.</summary>

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RangeCb(HWINEVENTHOOK hook, uint evt, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var s = s_slot;
        if (s is null) return;
        try { s.OnRange(evt, hwnd, idObject, idChild); }
        catch (Exception ex) { s._log.Info("error: " + ex.Message); }
    }
    /// <summary>UnmanagedCallersOnly entry for the location hook; routes to the live instance.</summary>

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void LocCb(HWINEVENTHOOK hook, uint evt, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        var s = s_slot;
        if (s is null) return;
        try { s.OnLoc(hwnd, idObject, idChild); }
        catch (Exception ex) { s._log.Info("error: " + ex.Message); }
    }
}
