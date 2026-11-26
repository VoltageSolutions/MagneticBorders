# Timers

Three one-shot timers keep a drag correct. They are ordinary Win32 timers (`SetTimer`), so they fire on the [UI thread](message-loop.md). Arming a timer of the same kind replaces the old one. All three builds wrap them in a `Win32Timer` class.

| Timer | Delay | Purpose |
|---|---|---|
| Flush | The rest of 8 ms | Applies the latest frame that was held back. |
| Settle | 150 ms after the last change | Re-reads the neighbors when you pause. |
| Verify | 60 ms after you let go | Checks that the final result stuck. |

## Flush

The controller applies at most one batch every 8 ms. If a location change arrives sooner, it keeps that frame as pending and arms Flush for the time that is left. When Flush fires, the pending frame goes out. Without it, a fast drag that ends in a pause would leave the neighbors a few pixels behind.

## Settle

Some apps enforce their size limits late, so a neighbor may take a frame and then change it. After every location change, the controller arms Settle at 150 ms. If it fires, you have paused. The controller re-reads every neighbor. If a neighbor's size differs from what MB asked for, MB learns its minimum, plan again with the dragged window's current frame, and apply once more. It logs one `settled` line.

## Verify

When you let go, Windows finishes its own resize and can overwrite the snap-back. At 60 ms after release, the controller re-reads the dragged window and the neighbors. If the dragged edge is not at the clamp, or a neighbor is off, it applies once more. It logs `end +<ms> ok` or `end +<ms> fixed`, then drops the seam.

## In the three builds

=== "C++"

    ```cpp
    void Win32Timer::arm(TimerKind kind, int delay_ms) {
        auto& id = ids_[static_cast<std::size_t>(kind)];
        if (id) KillTimer(nullptr, id);               // replace the old one
        id = SetTimer(nullptr, 0, delay_ms, &Win32Timer::on_timer);
    }
    ```

=== "Rust"

    ```rust
    // timer.rs
    let id = unsafe { SetTimer(None, 0, ms.max(1) as u32, Some(timer_proc)) };
    ```

=== "C#"

    ```csharp
    // Win32Timer.Arm
    _ids[(int)kind] = PInvoke.SetTimer(HWND.Null, 0, (uint)Math.Max(1, ms), &Cb);
    ```
