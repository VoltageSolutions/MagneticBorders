using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MagneticBorders.Application;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace MagneticBorders.Win32;

/// <summary>
/// Hidden message window on the UI thread: owns the tray icon + context menu and runs work posted from other threads
/// (settings watcher, pipe server). Create and dispose on the UI thread.
/// </summary>
/// <remarks>Creates the host; call Create next on the UI thread.</remarks>
/// <param name="tooltip">Tray tooltip text.</param>
/// <param name="header">Disabled first line of the context menu.</param>
/// <param name="log">Log sink.</param>
/// <param name="isEnabled">Reads the saved master switch (enabled).</param>
/// <param name="toggleEnabled">Flips the saved master switch in settings.json.</param>
/// <param name="reloadSettings">Called after the reload debounce fires.</param>
public sealed unsafe class TrayHost(string tooltip, string header, ILog log, Func<bool> isEnabled, Action toggleEnabled, Action reloadSettings) : IDisposable
{
    private const uint WM_TRAY = 0x8000 + 1;      // WM_APP + 1
    private const uint WM_RUN = 0x8000 + 2;       // drain queued actions
    private const uint NIN_SELECT = 0x400;
    private const uint NIN_KEYSELECT = 0x401;
    private const int IdEnabled = 1, IdSettings = 2, IdExit = 3;
    private const nuint TimerReload = 1, TimerQuit = 2;
    private static TrayHost? s_slot;

    private readonly string _tip = tooltip, _header = header;
    private readonly ILog _log = log;
    private readonly Func<bool> _isEnabled = isEnabled;
    private readonly Action _toggleEnabled = toggleEnabled;
    private readonly Action _reload = reloadSettings;
    private readonly ConcurrentQueue<Action> _queue = new();
    private HWND _hwnd;
    private HICON _icon;
    private uint _taskbarCreated;
    private ulong _lastClick;

    /// <summary>Registers the window class, creates the hidden window, loads the icon and adds the tray icon. UI thread only.</summary>
    public void Create()
    {
        s_slot = this;
        var hInst = PInvoke.GetModuleHandle((PCWSTR)null);
        fixed (char* cls = "MagneticBorders.Net.Tray")
        {
            WNDCLASSEXW wc = default;
            wc.cbSize = (uint)sizeof(WNDCLASSEXW);
            wc.lpfnWndProc = &WndProc;
            wc.hInstance = new HINSTANCE(hInst.Value);
            wc.lpszClassName = (PCWSTR)cls;
            PInvoke.RegisterClassEx(in wc);
            _hwnd = PInvoke.CreateWindowEx(0, "MagneticBorders.Net.Tray", "MagneticBorders", 0, 0, 0, 0, 0, HWND.Null, null, null, null);
        }
        if (_hwnd.IsNull) throw new InvalidOperationException("CreateWindowEx failed");
        _taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        _icon = LoadEmbeddedIcon();
        AddIcon();
    }

    /// <summary>Loads the small icon embedded in the exe, or the stock application icon.</summary>
    private static HICON LoadEmbeddedIcon()
    {
        string? path = Environment.ProcessPath;
        if (path is not null)
        {
            HICON small = default, large = default;
            fixed (char* p = path)
                if (PInvoke.ExtractIconEx(p, 0, &large, &small, 1) > 0)
                {
                    if (!large.IsNull) PInvoke.DestroyIcon(large);
                    if (!small.IsNull) return small;
                }
        }
        return PInvoke.LoadIcon(HINSTANCE.Null, (PCWSTR)(char*)32512);
    }

    /// <summary>Builds the notification data for the tray icon, including the tooltip.</summary>
    private NOTIFYICONDATAW Nid()
    {
        NOTIFYICONDATAW n = default;
        n.cbSize = (uint)sizeof(NOTIFYICONDATAW);
        n.hWnd = _hwnd;
        n.uID = 1;
        n.uFlags = NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP;
        n.uCallbackMessage = WM_TRAY;
        n.hIcon = _icon;
        string tip = _isEnabled() ? _tip : _tip + " – disabled";
        char* dst = (char*)&n.szTip;
        int len = Math.Min(tip.Length, 127);
        for (int i = 0; i < len; i++) dst[i] = tip[i];
        dst[len] = '\0';
        return n;
    }

    /// <summary>Adds the tray icon with the version 4 callback protocol. Called again when the taskbar is recreated.</summary>
    private void AddIcon()
    {
        var n = Nid();
        if (!PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in n)) { _log.Info("tray: NIM_ADD failed"); return; }
        n.Anonymous.uVersion = 4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in n);
    }

    /// <summary>Refreshes the tooltip (call on the UI thread).</summary>
    public void Refresh()
    {
        if (_hwnd.IsNull) return;
        var n = Nid();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, in n);
    }

    /// <summary>Thread-safe: runs the action on the UI thread.</summary>
    public void Post(Action a)
    {
        _queue.Enqueue(a);
        if (!_hwnd.IsNull) PInvoke.PostMessage(_hwnd, WM_RUN, 0, 0);
    }

    /// <summary>Thread-safe: runs func on the UI thread and waits up to timeoutMs for its result.</summary>
    public string? Invoke(Func<string> f, int timeoutMs)
    {
        string? result = null;
        using var done = new ManualResetEventSlim();
        Post(() => { try { result = f(); } finally { done.Set(); } });
        return done.Wait(timeoutMs) ? result : null;
    }

    /// <summary>UI thread: (re)arms the 100 ms settings-reload debounce.</summary>
    public void DebounceReload() => PInvoke.SetTimer(_hwnd, TimerReload, 100, null);

    /// <summary>UI thread: exits the message loop shortly (lets a pipe reply flush first).</summary>
    public void QuitSoon() => PInvoke.SetTimer(_hwnd, TimerQuit, 150, null);

    /// <summary>Starts the settings app, logging if it is missing.</summary>
    private void LaunchSettings(string args)
    {
        if (!SettingsApp.Launch(args, _log)) _log.Info("settings app not found");
    }

    /// <summary>Handles tray icon mouse events: left click opens the settings flyout (or the menu when the app is missing), right click opens the menu.</summary>
    private void OnTray(uint ev)
    {
        switch (ev)
        {
            case PInvoke.WM_LBUTTONUP:
            case NIN_SELECT:
            case NIN_KEYSELECT:
                ulong now = (ulong)Environment.TickCount64;
                if (now - _lastClick < 400) return;
                _lastClick = now;
                if (SettingsApp.Find() is not null) LaunchSettings("--flyout");
                else ShowMenu();
                break;
            case PInvoke.WM_RBUTTONUP:
            case PInvoke.WM_CONTEXTMENU:
                ShowMenu();
                break;
        }
    }

    /// <summary>Appends one menu item.</summary>
    private static void Append(HMENU menu, MENU_ITEM_FLAGS flags, nuint id, string? text)
    {
        fixed (char* t = text) PInvoke.AppendMenu(menu, flags, id, (PCWSTR)t);
    }

    /// <summary>Shows the context menu at the cursor and runs the chosen command. UI thread.</summary>
    private void ShowMenu()
    {
        var menu = PInvoke.CreatePopupMenu();
        Append(menu, MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_GRAYED, 0, _header);
        Append(menu, _isEnabled() ? MENU_ITEM_FLAGS.MF_STRING | MENU_ITEM_FLAGS.MF_CHECKED : MENU_ITEM_FLAGS.MF_STRING, IdEnabled, "Enabled");
        Append(menu, MENU_ITEM_FLAGS.MF_STRING, IdSettings, "Settings…");
        Append(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
        Append(menu, MENU_ITEM_FLAGS.MF_STRING, IdExit, "Exit");
        System.Drawing.Point p;
        PInvoke.GetCursorPos(&p);
        PInvoke.SetForegroundWindow(_hwnd);
        var cmd = PInvoke.TrackPopupMenuEx(menu, 0x100u | 0x2u, p.X, p.Y, _hwnd, null);
        PInvoke.PostMessage(_hwnd, PInvoke.WM_NULL, 0, 0);
        PInvoke.DestroyMenu(menu);
        switch ((int)cmd.Value)
        {
            case IdEnabled: _toggleEnabled(); break;
            case IdSettings: LaunchSettings(""); break;
            case IdExit: PInvoke.PostQuitMessage(0); break;
        }
    }

    /// <summary>Instance window procedure: tray callbacks, posted actions (WM_RUN), timers and TaskbarCreated.</summary>
    private LRESULT Handle(HWND h, uint msg, WPARAM w, LPARAM l)
    {
        if (msg == WM_TRAY) { OnTray((uint)(l.Value & 0xFFFF)); return (LRESULT)0; }
        if (msg == WM_RUN)
        {
            while (_queue.TryDequeue(out var a))
                try { a(); } catch (Exception ex) { _log.Info("error: " + ex.Message); }
            return (LRESULT)0;
        }
        if (msg == PInvoke.WM_TIMER)
        {
            PInvoke.KillTimer(h, w.Value);
            if (w.Value == TimerReload) { try { _reload(); } catch (Exception ex) { _log.Info("error: " + ex.Message); } }
            else if (w.Value == TimerQuit) PInvoke.PostQuitMessage(0);
            return (LRESULT)0;
        }
        if (_taskbarCreated != 0 && msg == _taskbarCreated) { AddIcon(); return (LRESULT)0; }
        return PInvoke.DefWindowProc(h, msg, w, l);
    }
    /// <summary>Native window procedure. Routes through the static slot to the live TrayHost.</summary>

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND h, uint msg, WPARAM w, LPARAM l)
    {
        var s = s_slot;
        if (s is null) return PInvoke.DefWindowProc(h, msg, w, l);
        try { return s.Handle(h, msg, w, l); }
        catch { return (LRESULT)0; }
    }

    /// <summary>Removes the tray icon, destroys the window and icon. UI thread.</summary>
    public void Dispose()
    {
        if (_hwnd.IsNull) return;
        var n = Nid();
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, in n);
        PInvoke.DestroyWindow(_hwnd);
        _hwnd = HWND.Null;
        if (!_icon.IsNull) PInvoke.DestroyIcon(_icon);
        if (ReferenceEquals(s_slot, this)) s_slot = null;
    }
}

/// <summary>Locates and launches the WinUI 3 settings app.</summary>
public static unsafe class SettingsApp
{
    /// <summary>
    /// Returns the path of MagneticBorders.Settings.exe next to the app, in settings\ (release zip and tester build),
    /// in ..\settings\ (artifacts\local\&lt;arch&gt;\), or in ..\..\settings\&lt;arch&gt;\ (artifacts\&lt;build&gt;\&lt;arch&gt;\), or null.
    /// </summary>
    public static string? Find()
    {
        string? exe = Environment.ProcessPath;
        string? dir = exe is null ? null : Path.GetDirectoryName(exe);
        if (dir is null) return null;
        foreach (var c in new[]
        {
            Path.Join(dir, "MagneticBorders.Settings.exe"),
            Path.Join(dir, "settings", "MagneticBorders.Settings.exe"),
            Path.GetFullPath(Path.Join(dir, "..", "settings", "MagneticBorders.Settings.exe")),
            Path.GetFullPath(Path.Join(dir, "..", "..", "settings", Path.GetFileName(dir), "MagneticBorders.Settings.exe")),
        })
            if (File.Exists(c)) return c;
        return null;
    }

    /// <summary>
    /// Starts the settings app with the given arguments. Returns false if it is missing or fails.
    /// Uses CreateProcess with STARTF_FORCEOFFFEEDBACK so Windows does not show the "app starting" busy cursor
    /// while the new process creates its first window.
    /// </summary>
    public static bool Launch(string args, ILog log)
    {
        var path = Find();
        if (path is null) return false;
        try
        {
            var cmd = ('"' + path + '"' + (args.Length > 0 ? " " + args : "") + "\0").ToCharArray();
            var si = new STARTUPINFOW { cb = (uint)sizeof(STARTUPINFOW), dwFlags = STARTUPINFOW_FLAGS.STARTF_FORCEOFFFEEDBACK };
            Span<char> cmdSpan = cmd;
            if (!PInvoke.CreateProcess(null, ref cmdSpan, null, null, false, 0, null, Path.GetDirectoryName(path), si, out var pi))
            {
                log.Info("launch settings failed: error " + Marshal.GetLastWin32Error());
                return false;
            }
            PInvoke.CloseHandle(pi.hThread);
            PInvoke.CloseHandle(pi.hProcess);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { log.Info("launch settings failed: " + ex.Message); return false; }
    }
}
