# Magnetic coupling

When a window is resized by its edge, every window with an edge on the same line resizes with it. Magnetic coupling is detected from geometry only, uses visual frames in physical pixels, clamps at learned minimum sizes, and snaps the dragged window back on release if it went past the clamp.

The rule IDs keep the `RESIZE` prefix so that old references stay valid. The settings keys keep the `glue` name too.

## Which windows take part

A window is eligible when all of these hold.

| ID | Rule |
|---|---|
| RESIZE-001 | An eligible window must be visible, not minimized and not maximized. |
| RESIZE-002 | An eligible window must not be cloaked (`DWMWA_CLOAKED` is zero). |
| RESIZE-003 | An eligible window must have a resizable frame (`WS_THICKFRAME`) and no `WS_EX_TOOLWINDOW`. |
| RESIZE-004 | An eligible window must have no owner and a non-empty title. |
| RESIZE-005 | A window of the engine's own process must not be eligible. |
| RESIZE-006 | A window whose executable name is in `excluded_apps` must not be eligible. |
| RESIZE-007 | Only windows on the same monitor as the dragged window may take part. |
| RESIZE-008 | A window that failed to move (for example access denied for an elevated window) must be excluded until Magnetic Borders restarts. |
| RESIZE-009 | A window that `IsHungAppWindow` reports must be skipped when moving. |

## Geometry

| ID | Rule |
|---|---|
| RESIZE-010 | All geometry must use the visual frame (`DWMWA_EXTENDED_FRAME_BOUNDS`) in physical pixels. |
| RESIZE-011 | Rects must be `{left, top, right, bottom}` with right and bottom exclusive. |
| RESIZE-012 | The mover must convert a frame to a window rect with the border offsets cached at snapshot time. |
| RESIZE-013 | The process must be Per-Monitor DPI aware V2. |
| RESIZE-014 | The tolerance must be `round(glue.tolerance_px * dpi / 96)` for the dragged window's monitor. |

## Classifying a drag

| ID | Rule |
|---|---|
| RESIZE-015 | Only drags that begin with `EVENT_SYSTEM_MOVESIZESTART` may be handled. |
| RESIZE-016 | A drag whose first location change leaves width and height unchanged must be treated as a move and ignored. |
| RESIZE-017 | For a resize, the grabbed edges must be the sides whose coordinate changed: at most one horizontal and one vertical edge. |
| RESIZE-018 | A drag of an ineligible window must be ignored. |
| RESIZE-019 | When Magnetic coupling is not active, no drag may be handled. It is active only when `enabled` is true and `glue.enabled` is true. |

## Seams

| ID | Rule |
|---|---|
| RESIZE-020 | A grabbed edge within the tolerance of the work-area edge on the same side must not build a seam. |
| RESIZE-021 | A same-side member must have its edge of the dragged edge's kind within the tolerance of the dragged edge. |
| RESIZE-022 | An opposite member must have its opposite edge within the tolerance of the dragged edge. |
| RESIZE-023 | A window matching both must count as opposite. |
| RESIZE-024 | A candidate must join when the gap between its perpendicular span and the seam span is at most the tolerance. Overlap counts as gap 0. |
| RESIZE-025 | The seam span must grow to include each joined member, and joining must repeat until nothing changes. |
| RESIZE-026 | Each member must store its signed offset from the dragged edge, and the offset must be kept during propagation. |
| RESIZE-027 | A seam with no members must not exist, and the drag must be ignored. |
| RESIZE-028 | A corner drag must build one independent seam per grabbed edge. |
| RESIZE-029 | Seams must be built from the frames at drag start. |

## Propagation and clamping

| ID | Rule |
|---|---|
| RESIZE-030 | A member's new edge must be the new dragged position plus its offset. |
| RESIZE-031 | The new position must be clamped so every member keeps at least its minimum width (vertical seam) or height (horizontal seam). |
| RESIZE-032 | If the clamp bounds cross, the clamped position must be the drag-start position. |
| RESIZE-033 | The dragged window must not appear in the list of moves. |
| RESIZE-034 | All members of all seams must be applied in one batch per update. |
| RESIZE-035 | Every window outside the seams must be left unchanged. |
| RESIZE-036 | A window's minimum must start at the system minimum track size. |
| RESIZE-037 | If a member's applied size is larger than requested, its minimum must be set to the applied size until Magnetic Borders restarts. |

## Timing

| ID | Rule |
|---|---|
| RESIZE-040 | The controller must apply at most one batch per 8 ms. |
| RESIZE-041 | An update skipped by the throttle must be kept as pending and applied by the Flush timer when the throttle window ends. |
| RESIZE-042 | A pending frame must never stay unapplied while the mouse is still. |
| RESIZE-043 | After each location change in Resizing, the Settle timer must be armed at 150 ms. |
| RESIZE-044 | When Settle fires, the controller must re-read every member, learn minimums for members whose size differs from the last request, re-plan with the dragged window's current frame, and apply. |
| RESIZE-045 | On `MOVESIZEEND` in Resizing, the controller must cancel the timers, apply the final frame, and move the dragged edge to the clamp if it passed it. |
| RESIZE-046 | After the final apply, the Verify timer must be armed at 60 ms. |
| RESIZE-047 | When Verify fires, the controller must re-apply once if the dragged edge is not at the clamp or a member's frame mismatches, then drop the seams. |

## Logging

| ID | Rule |
|---|---|
| RESIZE-050 | Every log line must start with the build name and the milliseconds since drag start. |
| RESIZE-051 | A built seam must log one line with the grabbed edges and the member ids. |
| RESIZE-052 | A Settle that re-plans must log one `settled` line with the dragged position, the clamp, the gap, and each mismatching member's requested and actual size. |
| RESIZE-053 | Verify must log `end +<ms> ok` or `end +<ms> fixed` with details. |
| RESIZE-054 | With `--verbose`, every apply must log its position, clamp, and member count. |

## Input

| ID | Rule |
|---|---|
| RESIZE-060 | The engine must never read modifier-key state. |
| RESIZE-061 | The engine must never register a hotkey by default. |
| RESIZE-062 | The engine must not inject code into other processes. |

??? note "Edge cases"
    - Elevated windows fail the first move with access denied and are then excluded (RESIZE-008). The seam continues with the other members.
    - Apps that refuse a size are handled by learned minimums (RESIZE-037). A window that refuses is not dropped from the seam.
    - A window that closes during a drag fails its move and is excluded until Magnetic Borders restarts.
