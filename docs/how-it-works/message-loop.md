# The message loop

## A thread that pumps messages

Windows delivers many things as messages to a thread: window messages, timer ticks, notification area icon clicks, and WinEvent hook callbacks. The thread must run a loop that picks them up, or nothing is ever delivered.

=== "C++"

    ```cpp
    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    ```

=== "Rust"

    ```rust
    let mut msg = MSG::default();
    while GetMessageW(&mut msg, None, 0, 0).0 > 0 {
        let _ = TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }
    ```

=== "C#"

    ```csharp
    MSG msg;
    while (PInvoke.GetMessage(&msg, HWND.Null, 0, 0).Value > 0)
    {
        PInvoke.TranslateMessage(&msg);
        PInvoke.DispatchMessage(&msg);
    }
    ```

`GetMessage` sleeps until a message arrives, so an idle Magnetic Borders uses no CPU. The loop ends when something posts `WM_QUIT`.

In WPF or WinForms, this is the loop that `Application.Run` hides.

## Why everything runs on one thread

[Out-of-context hook callbacks](watching-windows.md) arrive on the thread that installed the hook. So the hooks have to live on a thread with a message loop. MB then puts everything else that touches windows on that same thread: the drag controller, the timers, the mover, the notification area icon, and a hidden message window.

The result is that events arrive in order, one at a time. The drag controller needs no locks. It behaves like an actor.

## Other threads

Two background threads exist, and both only **post** messages to the UI thread.

| Thread | Job |
|---|---|
| Settings watcher | Watches the data folder. When `settings.json` changes, it posts "settings changed". |
| Pipe server | Waits for requests from the settings app. It posts each request to the UI thread and waits up to 500 ms for the answer. |

A third source is Ctrl+C in console mode, which posts `WM_QUIT`.

The UI thread does the real work. Anything that touches application state runs there.

## The hidden window

The notification area icon and the posted messages need a window handle, so MB creates a hidden message-only window on the UI thread. Its window procedure receives the notification area icon clicks, the timer ticks that belong to it, and the messages from the background threads.
