# Where a window really is

To line up edges, Magnetic Borders must know exactly where each window is.

## The invisible border

Windows 10 and 11 draw windows with a thin visible frame, but a window can also carry an invisible resize border of a few pixels on the left, right, and bottom. It gives the mouse a larger target for resizing. Microsoft documents this in the [`GetWindowRect` remarks](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowrect): "In Windows Vista and later, the Window Rect now may include invisible resize borders."

`GetWindowRect` returns the rectangle **including** that invisible border. Two windows that look perfectly flush can have rectangles that overlap by twice the border width. If Magnetic Borders compared those rectangles, no seam would ever line up.

## The visible frame

`DwmGetWindowAttribute` with `DWMWA_EXTENDED_FRAME_BOUNDS` returns the rectangle that is actually visible. The same Microsoft page says to use it "to get the visible window bounds, not including the invisible resize borders." MB uses it for all geometry, and treats it as the **visual frame**.

=== "C++"

    ```cpp
    std::optional<Rect> visual_frame(HWND hwnd) {
        RECT r{};
        if (FAILED(DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS,
                                         &r, sizeof(r)))) return std::nullopt;
        return Rect{r.left, r.top, r.right, r.bottom};
    }
    ```

=== "Rust"

    ```rust
    pub fn frame_bounds(h: HWND) -> Option<Rect> {
        let mut r = RECT::default();
        unsafe {
            DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS,
                &mut r as *mut RECT as *mut c_void,
                std::mem::size_of::<RECT>() as u32).ok()?;
        }
        Some(from_rect(r))
    }
    ```

=== "C#"

    ```csharp
    public static unsafe bool TryGetFrame(HWND h, out Rect frame)
    {
        RECT r;
        if (PInvoke.DwmGetWindowAttribute(h,
                DWMWINDOWATTRIBUTE.DWMWA_EXTENDED_FRAME_BOUNDS,
                &r, (uint)sizeof(RECT)).Failed) { frame = default; return false; }
        frame = new Rect(r.left, r.top, r.right, r.bottom);
        return true;
    }
    ```

## Moving needs the other rectangle

`SetWindowPos` and `DeferWindowPos` expect the **window rect**, with the invisible border. So when MB takes its snapshot, it also reads `GetWindowRect` and stores the difference for each window: four numbers, one per side. When it moves a window, it adds those numbers back to the visual frame it wants.

```text
offset = windowRect - visualFrame        (taken once, at snapshot time)
windowRect to apply = wanted visual frame + offset
```

This is why the offsets are cached at snapshot time. They stay valid for the whole drag.

## Pixels and DPI

All numbers are physical pixels. The process declares itself **Per-Monitor DPI Aware V2** in its manifest, so Windows never scales coordinates behind its back.

The detection zone is stored in 96 DPI pixels, so MB scales it for the monitor of the dragged window: `round(zone * dpi / 96)`. At 150 percent scaling (144 DPI), a zone of 12 becomes 18. It reads the DPI with `GetDpiForWindow`.

## Rectangles

A rectangle is `{left, top, right, bottom}`, and right and bottom are exclusive. A window at `x = 0` with width `100` has `right = 100`. Two windows touch when one's `right` equals the other's `left`.

## Which windows count

The snapshot lists only windows that can take part. A window is skipped when it is minimized, maximized, or **cloaked** (hidden on another virtual desktop, which `DWMWA_CLOAKED` reports). Tool windows, owned windows, windows with no title, and windows with no resizable frame are skipped as well. So are windows from Magnetic Borders itself and from excluded apps. The snapshot also keeps only windows on the dragged window's monitor, found with `MonitorFromWindow`.
