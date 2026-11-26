# Watching other windows

A normal program can only see its own windows. To follow a drag in another program, Magnetic Borders asks Windows to tell it about window events. The tool for that is a **WinEvent hook**.

## What a WinEvent hook is

Install a hook with `SetWinEventHook`, passing a range of event numbers and a callback. From then on, Windows calls the callback when one of those events happens anywhere on the desktop.

Magnetic Borders uses three events.

| Event | Meaning |
|---|---|
| `EVENT_SYSTEM_MOVESIZESTART` | Someone grabbed a title bar or a border. |
| `EVENT_OBJECT_LOCATIONCHANGE` | A window moved or changed size. |
| `EVENT_SYSTEM_MOVESIZEEND` | They let go. |

The start and end events are quiet, so MB installs one hook for that range and keeps it for the whole run. Location changes are very noisy, because every window on the desktop sends them. So MB installs that second hook only when a drag starts, limits it to the dragged window's process, and removes it at the end.

## Out of context

The hook uses `WINEVENT_OUTOFCONTEXT`. Windows does not load MB's code into the other program. It sends a message to MB's thread, and the callback runs there. That is why MB needs no injection and cannot crash other apps. It also uses `WINEVENT_SKIPOWNPROCESS`, so MB ignores its own windows.

The callback runs on the thread that installed the hook, so that thread must run a [message loop](message-loop.md).

## In the three builds

=== "C++"

    ```cpp
    constexpr DWORD kHookFlags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;

    // WinEventSource constructor: one permanent hook for start..end.
    session_hook_ = UniqueWinEventHook{SetWinEventHook(
        EVENT_SYSTEM_MOVESIZESTART, EVENT_SYSTEM_MOVESIZEEND,
        nullptr, &WinEventSource::on_win_event, 0, 0, kHookFlags)};

    // When a drag starts: a second hook, only for the dragged window's process.
    location_hook_ = UniqueWinEventHook{SetWinEventHook(
        EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
        nullptr, &WinEventSource::on_win_event, pid, 0, kHookFlags)};
    ```

    The callback is a plain C function, so `on_win_event` is `static`. It finds the object through a `thread_local` pointer, `t_instance`.

=== "Rust"

    ```rust
    // events.rs, on EVENT_MOVESIZESTART:
    let mut pid = 0u32;
    GetWindowThreadProcessId(hwnd, Some(&mut pid));
    let hook = SetWinEventHook(
        EVENT_LOCATIONCHANGE, EVENT_LOCATIONCHANGE,
        None, Some(loc_proc), pid, 0, OUTOFCONTEXT_SKIPOWN,
    );
    LOC_HOOK.with(|c| c.set(hook.0 as usize));
    with_handler(|h| h.on_move_size_start(id));
    ```

    The callbacks are `extern "system"` functions. They reach the controller through a `thread_local!` slot.

=== "C#"

    ```csharp
    // WinEventSource.Install
    s_slot = this;
    _rangeHook = PInvoke.SetWinEventHook(
        PInvoke.EVENT_SYSTEM_MOVESIZESTART, PInvoke.EVENT_SYSTEM_MOVESIZEEND,
        HMODULE.Null, &RangeCb, 0, 0,
        PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void RangeCb(HWINEVENTHOOK hook, uint evt, HWND hwnd,
                                int idObject, int idChild, uint thread, uint time)
        => s_slot?.OnRange(evt, hwnd, idObject, idChild);
    ```

    `[UnmanagedCallersOnly]` lets native code call a static method directly. It works with Native AOT. A static field `s_slot` holds the instance.

## One callback, one global

A C callback has no `this`. Each build therefore keeps exactly one slot (a `thread_local` pointer, a `thread_local!`, or a static field) that routes the callback to the drag controller. It is the only global in each build, apart from the Ctrl+C thread id in console mode.

## Filtering the noise

The location callback checks three things before it forwards an event: `idObject == OBJID_WINDOW`, `idChild == CHILDID_SELF`, and the window is the one being dragged. Scroll bars, carets, and child controls also send location events, and MB ignores them.

If a drag never sends `MOVESIZESTART`, MB never hears about it. Grab And Move works this way, so it is left alone.
