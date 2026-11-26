using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MagneticBorders.Application;
using Windows.Win32;
using Windows.Win32.Foundation;
using ITimer = MagneticBorders.Application.ITimer;

namespace MagneticBorders.Win32;

/// <summary>One-shot thread timers (SetTimer with null HWND); must be used from the UI thread.</summary>
public sealed unsafe class Win32Timer : ITimer
{
    private static Win32Timer? s_slot;
    private readonly nuint[] _ids = new nuint[3];

    /// <summary>Set by the composition root to controller.OnTimer.</summary>
    public Action<TimerKind>? Fired { get; set; }

    /// <summary>Registers this instance in the static slot used by the native callback.</summary>
    public Win32Timer() { s_slot = this; }

    /// <summary>Arms the one-shot timer for the kind, replacing any pending one.</summary>
    public void Arm(TimerKind kind, int ms)
    {
        Kill(kind);
        _ids[(int)kind] = PInvoke.SetTimer(HWND.Null, 0, (uint)Math.Max(1, ms), &Cb);
    }

    /// <summary>Kills every armed timer.</summary>
    public void CancelAll()
    {
        foreach (var k in Enum.GetValues<TimerKind>()) Kill(k);
    }

    /// <summary>Kills one timer if armed.</summary>
    private void Kill(TimerKind kind)
    {
        ref var id = ref _ids[(int)kind];
        if (id != 0) { PInvoke.KillTimer(HWND.Null, id); id = 0; }
    }

    /// <summary>Maps a fired timer id to its TimerKind, disarms it (one-shot) and raises Fired.</summary>
    private void OnFire(nuint id)
    {
        for (int i = 0; i < _ids.Length; i++)
        {
            if (_ids[i] != id) continue;
            Kill((TimerKind)i);
            Fired?.Invoke((TimerKind)i);
            return;
        }
        PInvoke.KillTimer(HWND.Null, id);
    }
    /// <summary>SetTimer callback (function pointer). Delivered on the UI thread by the message loop.</summary>

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void Cb(HWND hwnd, uint msg, nuint id, uint time)
    {
        try { s_slot?.OnFire(id); } catch { }
    }
}
