# Settings

You change configure Magnetic Borders in the flyout and in the settings window. Changes save as you make them and apply within a moment.

## The flyout

Magnetic Borders persists an icon in the notifications of your taskbar (the system tray) while it's running. Left-click that icon to open the flyout menu. It has switches to turn off Magnetic Borders entirely, or to disable the specific **Magnetic coupling** and **Magnetic attraction** features. A short line under each name says what it does. You can also open the settings window.

!!! example "GIF: the flyout opening and closing from the tray icon"
    File: `assets/gifs/flyout-open.gif`

## The settings window

Right-click the tray icon and choose **Settings...** or press **Open settings** in the flyout.

| Setting name | Function |
|---|---|
| Enabled | Completely disables Magnetic Borders. |
| Start at login | Starts Magnetic Borders when you sign in to Windows. |
| Flyout background | **Translucent** or **Solid**. |
| Magnetic coupling | Turns [the window resizing feature](magnetic-coupling.md) on or off. |
| Coupling detection zone | How far apart two edges can be and still resize together, from 0 to 64 pixels. The default is 12. |
| Magnetic attraction | Turns [snapping while you move a window](magnetic-attraction.md) on or off. |
| Snap to screen edges | Snap against the edges of the screen and the taskbar. On by default. |
| Snap to other windows | Snap against other windows on the same monitor. On by default. |
| Pull-in distance | How close an edge must come before it snaps, from 0 to 64 pixels. The default is 16. |
| Breakaway distance | How far past an edge you must move to pull the window free, from 0 to 128 pixels. The default is 24. |
| Snapping speed | Windows moving faster than this pass edges without snapping, from 100 to 5000 pixels per second. The default is 600. |
| Excluded apps | Programs Magnetic Borders leaves alone. |

!!! example "GIF: scrolling through the settings window"
    File: `assets/gifs/settings-window.gif`

A change to a switch, a slider, or Excluded apps applies on your next drag.

The settings live in `%LOCALAPPDATA%\MagneticBorders\settings.json`. The full list of keys is in the [contract spec](spec/contract.md).
