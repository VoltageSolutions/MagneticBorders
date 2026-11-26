# Moving many windows at once

Once Magnetic Borders knows the new frame of every member, it has to apply them. If it moved the windows one at a time, each one would repaint on its own. The seam would tear for a moment on every mouse movement.

## DeferWindowPos

Windows has a batch API for this. Start a batch with `BeginDeferWindowPos`, queue a move with `DeferWindowPos` once per window, and apply them all together with `EndDeferWindowPos`.

MB uses the flags `SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER`. The neighbors are moved and resized, but they do not steal focus and they do not change their stacking order.

=== "C++"

    ```cpp
    bool apply_deferred(const std::vector<Target>& targets) {
        HDWP hdwp = BeginDeferWindowPos(static_cast<int>(targets.size()));
        if (!hdwp) return false;
        for (const auto& t : targets) {
            hdwp = DeferWindowPos(hdwp, t.hwnd, nullptr, t.wr.left, t.wr.top,
                                  t.wr.right - t.wr.left, t.wr.bottom - t.wr.top, kFlags);
            if (!hdwp) return false;  // the handle is invalid after a failure
        }
        return EndDeferWindowPos(hdwp) != FALSE;
    }
    ```

=== "Rust"

    ```rust
    fn batch(&self, items: &[(u64, Rect)], flags: SET_WINDOW_POS_FLAGS) -> Result<()> {
        unsafe {
            let mut hdwp = BeginDeferWindowPos(items.len() as i32)?;
            for &(id, r) in items {
                hdwp = DeferWindowPos(hdwp, hwnd_of(id), None,
                                      r.l, r.t, r.width(), r.height(), flags)?;
            }
            EndDeferWindowPos(hdwp)
        }
    }
    ```

=== "C#"

    ```csharp
    var hdwp = PInvoke.BeginDeferWindowPos(todo.Count);
    foreach (var (_, h, w) in todo)
    {
        hdwp = PInvoke.DeferWindowPos(hdwp, h, HWND.Null,
                                      w.L, w.T, w.Width, w.Height, Flags);
        if (hdwp.IsNull) { ok = false; break; }
    }
    if (ok) ok = PInvoke.EndDeferWindowPos(hdwp);
    ```

The class is `Win32Mover` in all three builds.

## Window rects, not visual frames

MB plans in [visual frames](window-position.md). Before it calls `DeferWindowPos`, it adds each window's cached border offset to turn the frame into a window rect.

## Fallbacks and read-back

If the batch fails, the mover falls back to `SetWindowPos` for each window alone. A window that fails there, usually with "access denied", is left out until Magnetic Borders restarts. See [Windows you cannot move](unmovable-windows.md).

After applying, the mover reads every frame back and returns what it actually got. The controller compares this with what it asked for, and that is how it [learns minimum sizes](seams.md#learning-minimums).

## Throttling

The controller applies at most one batch every 8 ms. If a location change arrives sooner, it keeps the frame as pending, and a [Flush timer](timers.md) applies it when the 8 ms are up.
