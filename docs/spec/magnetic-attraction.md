# Magnetic attraction

While a window is moved slowly, an edge that comes near a screen edge or another window's edge is pulled onto it and held there. The window can slide along the edge it is held to. Moving on past the edge by a set distance pulls it free. A fast move never snaps, so moving a window across the screen does not catch on everything it passes. This follows how windows align on macOS.

Magnetic Borders does not take over the drag. Windows runs the drag, and Magnetic Borders moves the window to the held position each time Windows moves it ([D-018](decisions.md)).

## When it applies

| ID | Rule |
|---|---|
| SNAP-001 | Magnetic attraction must be active only when `enabled` and `snap.enabled` are both true. |
| SNAP-002 | Only a drag that begins with `EVENT_SYSTEM_MOVESIZESTART` and whose first location change leaves width and height unchanged (a move, see RESIZE-016) may be handled. |
| SNAP-003 | The dragged window must be eligible under RESIZE-001 to RESIZE-006. |
| SNAP-004 | Magnetic attraction must never read the keyboard or mouse buttons. There is no key that turns it off during a drag: a fast move is the way past an edge. |

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
| SNAP-020 | The **free position** is where Windows puts the window from the mouse. The **held position** is the free position with each held edge moved onto its line. |
| SNAP-021 | Each axis must be handled on its own. A window can be held on one vertical line and one horizontal line at once (a corner). |
| SNAP-022 | Along a held edge the window must follow the mouse. This is sliding. |
| SNAP-023 | Every location change that is not Magnetic Borders' own move must update the free position, and the window must be moved to the held position when the two differ. |
| SNAP-024 | A location change whose frame equals the last held position Magnetic Borders applied must be treated as its own move and must not update the free position. |
| SNAP-025 | Moves of the dragged window must not be throttled. |

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
| SNAP-037 | When several targets on one axis are within the pull-in distance, the nearest one must win. A tie must go to a window over a screen edge. |

## Holding and releasing

| ID | Rule |
|---|---|
| SNAP-040 | A free edge must start being held when a target line is within the pull-in distance, the speed is within the snapping speed, and the line was not just released (SNAP-042). |
| SNAP-041 | A held edge must be released when the free position is more than the breakaway distance from the line. The window then jumps back to the free position, under the mouse. |
| SNAP-042 | A released line must not hold that edge again until the free position has moved more than the pull-in distance away from the line. |
| SNAP-043 | When `snap.breakaway_px` is less than `snap.distance_px`, the pull-in distance must be used as the breakaway distance. |

## End of the drag

| ID | Rule |
|---|---|
| SNAP-050 | On `EVENT_SYSTEM_MOVESIZEEND`, if any edge is held and the window's current frame equals the last free position, the held position must be applied once. |
| SNAP-051 | If the window's size changed by the end of the drag (Windows Snap, FancyZones or another tool placed it), Magnetic attraction must not move it. |
| SNAP-052 | After the drag ends nothing more may be applied. Unlike Magnetic coupling there is no Verify pass, so a tool that places the window after the drop always wins. |

## Logging

| ID | Rule |
|---|---|
| SNAP-060 | The log must record when an edge starts being held (edge, line, target kind, speed) and when it is released (edge, reason), plus one line per drag with the number of snaps. |
