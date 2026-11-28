# Magnetic attraction

While a window is moved slowly, an edge that comes near a screen edge or another window's edge is pulled onto it and held there. The window can slide along the edge it is held to. Moving on past the edge by a set distance pulls it free. A fast move never snaps, so moving a window across the screen does not catch on everything it passes. This follows how windows align on macOS.

Magnetic Borders does not take over the drag. Windows runs the drag and keeps the window at a fixed offset from the cursor, so Magnetic Borders holds the window by holding the cursor: while a window is moved, a mouse hook decides where each mouse movement may put the cursor ([D-018](decisions.md)).

## When it applies

| ID | Rule |
|---|---|
| SNAP-001 | Magnetic attraction must be active only when `enabled` and `snap.enabled` are both true. |
| SNAP-002 | Only a drag that begins with `EVENT_SYSTEM_MOVESIZESTART` and whose first location change leaves width and height unchanged (a move, see RESIZE-016) may be handled. |
| SNAP-003 | The dragged window must be eligible under RESIZE-001 to RESIZE-006. |
| SNAP-004 | Magnetic attraction must never read the keyboard or key state. The mouse hook sees only mouse movement and button releases, and only during a move. There is no key that turns it off during a drag: a fast move is the way past an edge. |

## Distances and speed

All distances are in effective pixels and are scaled by the DPI of the dragged window's monitor, `round(value * dpi / 96)`, like RESIZE-014.

| ID | Rule |
|---|---|
| SNAP-010 | The **pull-in distance** must be `snap.distance_px` (default 16). An edge within this distance of a target line may snap to it. |
| SNAP-011 | The **breakaway distance** must be `snap.breakaway_px` (default 24). A held edge is released when the window's free position is farther than this from the line. |
| SNAP-012 | The **snapping speed** must be `snap.max_speed` in effective pixels per second (default 600). An edge may only start snapping while the window moves at this speed or slower. |
| SNAP-013 | The speed must be measured from the free positions of the last 80 ms of the drag. With fewer than two positions in that span, the speed is the last measured value, or 0 at the start of a drag. |
| SNAP-014 | The speed limit must apply only to starting a snap. A held edge stays held at any speed until it breaks away. |

## Free position and held position

| ID | Rule |
|---|---|
| SNAP-020 | The **free position** is where the window would be from the mouse's movement alone. The **held position** is the free position with each held edge moved onto its line. |
| SNAP-021 | Each axis must be handled on its own. A window can be held on one vertical line and one horizontal line at once (a corner). |
| SNAP-022 | Along a held edge the window must follow the mouse. This is sliding. |
| SNAP-023 | Without the mouse hook (SNAP-076), every location change that is not Magnetic Borders' own move must update the free position, and the window must be moved to the held position when the two differ. |
| SNAP-024 | Without the mouse hook, a location change whose frame equals the last held position Magnetic Borders applied must be treated as its own move and must not update the free position. |
| SNAP-025 | Without the mouse hook, moves of the dragged window must not be throttled. |

## Target lines

| ID | Rule |
|---|---|
| SNAP-030 | When `snap.screen_edges` is true (default), each side of the work area of the dragged window's monitor (at drag start) must be a target for the same side of the window. The taskbar is outside the work area, so the window snaps against it. |
| SNAP-031 | A work-area side that lies on another monitor's work area must not be a target, so a window moves between monitors freely. |
| SNAP-032 | When `snap.windows` is true (default), the other eligible windows on the same monitor (RESIZE-007) must be targets. |
| SNAP-033 | **Edge to edge:** the dragged window's left side must target another window's right side, and right must target left (top and bottom likewise), when the two windows overlap on the other axis or are within the pull-in distance of overlapping. |
| SNAP-034 | **Aligned:** the dragged window's left side must target another window's left side, and right must target right (top and bottom likewise), only when the two windows are stacked: the other axis has an edge-to-edge gap within the pull-in distance. |
| SNAP-035 | An edge-to-edge or aligned target must stop being a target as soon as its condition no longer holds. A held edge on such a target is released, for example when the window slides off the end of the other window. |
| SNAP-036 | Windows that overlap the dragged window must not create inner targets: no left-to-left or top-to-top target unless SNAP-034 holds. |
| SNAP-038 | Only the visible parts of another window may attract. An edge pixel is hidden when a window above the target in the z-order covers it: a visible, not minimized, uncloaked window that is not click-through (`WS_EX_TRANSPARENT`). The dragged window never counts as covering. An edge-to-edge target needs its edge visible within the pull-in distance of the dragged window's span. An aligned target needs the edge the dragged window is stacked against visible within the pull-in distance of the dragged window's span. |
| SNAP-037 | When several targets on one axis are within the pull-in distance, the nearest one must win. A tie must go to a window over a screen edge. |

## Holding and releasing

| ID | Rule |
|---|---|
| SNAP-040 | A free edge must start being held when a target line is within the pull-in distance, the speed is within the snapping speed, and the line was not just released (SNAP-042). |
| SNAP-041 | A held edge must be released when the free position is more than the breakaway distance from the line. The window then jumps back to the free position, under the mouse. |
| SNAP-042 | A released line must not hold that edge again until a later free position is more than the pull-in distance from the line. Nothing else may be held on that axis in the update that released it. |
| SNAP-043 | When `snap.breakaway_px` is less than `snap.distance_px`, the pull-in distance must be used as the breakaway distance. |

## End of the drag

| ID | Rule |
|---|---|
| SNAP-050 | Without the mouse hook, on `EVENT_SYSTEM_MOVESIZEEND`, if any edge is held and the window's current frame equals the last free position, the held position must be applied once. With the mouse hook, SNAP-075 applies instead. |
| SNAP-051 | If the window's size changed by the end of the drag (Windows Snap, FancyZones, or another tool placed it), Magnetic attraction must not move it, with or without the mouse hook. |
| SNAP-052 | After the drag ends nothing more may be applied. Unlike Magnetic coupling there is no Verify pass, so a tool that places the window after the drop always wins. |

## Holding the cursor

| ID | Rule |
|---|---|
| SNAP-070 | When a drag is classified as a move, Magnetic Borders must install a low-level mouse hook (`WH_MOUSE_LL`) and remove it when the move ends. The hook must never run outside a move. |
| SNAP-071 | The free position must start at the window's frame as read when the drag starts (the move/size start event), before Windows moves the window. On a held axis, each mouse movement must add to the free position. On a free axis, the free position must follow the cursor. |
| SNAP-072 | For each mouse movement, the cursor must go where the held position puts it: the cursor plus the held position's offset from the frame, both read when the drag starts. Windows keeps that cursor-to-window offset fixed for the whole drag. When either cannot be read at drag start, both are read when the hook starts. These readings are estimates, because the cursor may already have moved before the start event is handled (SNAP-077 corrects them). When that differs from where Windows was about to put the cursor, the movement must be blocked and the cursor set there. Windows then moves the window to the held position, so Magnetic Borders never moves the window itself. |
| SNAP-073 | When a movement releases an axis, the cursor must move by that movement only on that axis, so neither the cursor nor the window jumps. |
| SNAP-074 | While an axis is held, the cursor must not move on that axis. Sliding on the other axis follows the mouse. |
| SNAP-075 | The hook must be removed at the end of the move, at the start of the next drag, and as soon as a mouse button is released, so the cursor is never held after a move. After the hook is removed at the end of the move, if any axis is held, the window has the size it had when the drag started, and its frame differs from the held position, the held position must be applied once. Otherwise nothing is applied. The `move end` line then ends with `pointer applied`. |
| SNAP-076 | When the hook cannot be installed or the cursor cannot be read, Magnetic attraction must fall back to moving the window back to the held position after Windows moves it (SNAP-023 to SNAP-025, SNAP-050 to SNAP-052). |
| SNAP-077 | The C++ and Rust builds must calibrate the cursor-to-window offset while an axis is held. On each location change of the dragged window in pointer mode, for each held axis whose leading coordinate (left for x, top for y) equals the one in the previous reported frame, the window has settled on that axis, so its coordinate must equal the cursor minus the offset Windows recorded. The difference between the actual coordinate and the expected one is the error. When the error is not 0 and its size is at most the scaled breakaway distance, the reference frame must shift by the error on that axis, the cursor must be set to the position that puts the window on the held position, and the correction must stay for the rest of the drag. An axis that is not held, a window that has not settled, and a larger error change nothing. The C# build does not implement this rule and relies on SNAP-075. |

## Logging

| ID | Rule |
|---|---|
| SNAP-060 | The log must record when an edge starts being held (edge, line, target kind, speed) and when it is released (edge, reason), plus one line per drag with the number of snaps and whether the mouse hook held the cursor. |
