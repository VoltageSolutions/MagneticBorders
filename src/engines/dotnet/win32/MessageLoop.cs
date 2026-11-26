using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MagneticBorders.Win32;

/// <summary>Thin wrappers for the standard Win32 message loop and related system queries. Run is called on the UI thread.</summary>
public static unsafe class MessageLoop
{
    /// <summary>Native id of the calling thread; the host stores it so PostQuit can target the UI thread.</summary>
    public static uint CurrentThreadId => PInvoke.GetCurrentThreadId();

    /// <summary>Forces creation of the calling thread's message queue so PostQuit can reach it.</summary>
    public static void EnsureQueue()
    {
        MSG m;
        PInvoke.PeekMessage(&m, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE);
    }

    /// <summary>Pumps messages until WM_QUIT. WinEvent callbacks, timers and tray messages are all delivered from inside this loop.</summary>
    public static void Run()
    {
        MSG msg;
        while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0).Value > 0)
        {
            PInvoke.TranslateMessage(&msg);
            PInvoke.DispatchMessage(&msg);
        }
    }

    /// <summary>Posts WM_QUIT to the thread with the given id. Safe from any thread.</summary>
    public static void PostQuit(uint threadId) => PInvoke.PostThreadMessage(threadId, PInvoke.WM_QUIT, 0, 0);

    /// <summary>True when Windows shows window contents while dragging; live gluing needs it.</summary>
    public static bool FullWindowDragEnabled()
    {
        BOOL on = default;
        PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETDRAGFULLWINDOWS, 0, &on, 0);
        return on;
    }
}
