# Magnetic coupling

Magnetic coupling lets you drag the border between touching windows and resize all of them together. It works with windows you placed by hand, with Windows Snap, or with FancyZones.

!!! example "GIF: dragging the shared border of two side-by-side windows"
    File: `assets/gifs/coupling-two-windows.gif`

## How to use it

Put two or more windows edge to edge, then drag the border of one of them. The windows on the other side of that border resize with you. If you grab a corner where four windows meet, both borders move at once.

Two edges count as touching when the gap between them is no bigger than the **Detection zone** (12 pixels by default). A FancyZones gap stays the same while you drag. To unstick a window, move it so the gap is wider than the detection zone.

## Limits

- A window with a minimum or maximum size stops shrinking at that size. When you let go, your window snaps back so nothing overlaps.
- The edge of the screen is never a border.
- No cross-monitor support today - Magnetic Borders only resizes windows connected on the same monitor.
- MB acts only when you resize, not when you move a window - even if they are touching.
