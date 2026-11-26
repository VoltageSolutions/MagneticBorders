# Windows you cannot move

Some windows refuse to follow. Magnetic Borders handles each case without errors.

## Elevated windows (UIPI)

Windows has a rule called User Interface Privilege Isolation. A normal program cannot send input or move windows that belong to a program running as administrator. The call fails with "access denied".

MB finds out the hard way. The first move that fails marks that window as banned until Magnetic Borders restarts. The other windows in the seam keep working.

=== "C++"

    ```cpp
    if (!SetWindowPos(t.hwnd, nullptr, /* ... */ kFlags)) {
        registry_.ban(t.id);  // e.g. elevated window: access denied
    }
    ```

=== "Rust"

    ```rust
    // mover.rs: a failed per-window SetWindowPos adds the id to the banned set
    self.shared.banned.borrow_mut().insert(id);
    ```

=== "C#"

    ```csharp
    if (!PInvoke.SetWindowPos(h, HWND.Null, w.L, w.T, w.Width, w.Height, Flags))
    {
        state.Excluded.Add(id);
        log.Info($"window {id:X} excluded (move failed)");
    }
    ```

To move elevated windows, Magnetic Borders itself must run elevated. That is not recommended, because any bug would then run with administrator rights.

## Hung apps

If a program stops answering, moving its window can block MB for seconds. Before each move, the mover calls `IsHungAppWindow` and skips windows that report true.

## Cloaked windows

A window on another virtual desktop, or hidden by the shell, still exists. `DwmGetWindowAttribute` with `DWMWA_CLOAKED` reports it. The inventory skips cloaked windows, so MB never moves a window that cannot be seen.

## Windows that close mid-drag

A window can close while you drag. Its move fails, and it is banned like any other failure.

## Apps with a minimum size

Some programs refuse to get smaller than a fixed size. They are not banned. Their limit is [learned](seams.md#learning-minimums), and the seam stops there.
