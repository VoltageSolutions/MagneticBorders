# The notification area icon

Magnetic Borders has no window of its own. Its only presence is an icon in the notification area of the taskbar. Developers call that area the tray, and the icon a `TrayIcon`, which is why the code uses those names. Add the icon with the function `Shell_NotifyIcon`.

## Adding the icon

Fill in a `NOTIFYICONDATA` structure and send it with a command: `NIM_ADD` to create the icon, `NIM_MODIFY` to change the tooltip, and `NIM_DELETE` to remove it. The structure names a window handle and a callback message number. When the icon is clicked, Windows posts that message to that window. That is why MB has a hidden window on the [UI thread](message-loop.md).

MB asks for the version 4 behavior with `NIM_SETVERSION`. In that mode, Windows reports the mouse action in the low word of `lParam`, such as a left-button release or a context menu request.

=== "C++"

    ```cpp
    NOTIFYICONDATAW nid{sizeof(nid)};
    nid.hWnd = owner_;
    nid.uID = 1;
    nid.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
    nid.uCallbackMessage = msg_;
    nid.hIcon = icon_;
    Shell_NotifyIconW(NIM_ADD, &nid);
    nid.uVersion = NOTIFYICON_VERSION_4;
    Shell_NotifyIconW(NIM_SETVERSION, &nid);
    ```

=== "Rust"

    ```rust
    // app.rs
    let _ = Shell_NotifyIconW(NIM_ADD, &n);
    let _ = Shell_NotifyIconW(NIM_SETVERSION, &n);
    ```

=== "C#"

    ```csharp
    var n = Nid();   // hWnd, uID, callback message, icon, tooltip
    PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, in n);
    n.Anonymous.uVersion = 4;
    PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, in n);
    ```

The classes are `TrayIcon` (C++), `Tray` in `app.rs` (Rust), and `TrayHost` (C#).

## What a click does

| Click | Action |
|---|---|
| Left | Starts `MagneticBorders.Settings.exe --flyout`. If the settings app is missing, the notification area menu opens instead. |
| Right | Opens the context menu: a header, the **Enabled** checkbox, **Settings...**, and **Exit**. |

The tooltip names the build and adds "disabled" when the master switch is off.

## When Explorer restarts

If `explorer.exe` crashes or restarts, the taskbar is rebuilt and every notification area icon vanishes. Windows broadcasts a registered message named `TaskbarCreated`. MB listens for it in its hidden window and adds the icon again.

## Where the icon comes from

Each build embeds `icon.ico` as a resource. The same file is the exe icon. See the [icon spec](../spec/icon.md).
