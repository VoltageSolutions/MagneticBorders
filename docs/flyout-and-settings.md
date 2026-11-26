# Settings

You change configure Magnetic Borders in the flyout and in the settings window. Changes save as you make them and apply within a moment.

## The flyout

Magnetic Borders persists an icon in the notifications of your taskbar (the system tray) while it's running. Left-click that icon to open the flyout menu. It has switches to turn off Magnetic Borders entirely, or to disable the specific **Magnetic coupling** and **Magnetic attraction** features. You can also open the settings window.

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
| Magnetic attraction | Turns [snap alignment feature](magnetic-attraction.md) on or off. It does nothing yet. |
| Attraction detection zone | How close a window must get before it snaps. The default is 16. |
| Excluded apps | Programs Magnetic Borders leaves alone. |

!!! example "GIF: scrolling through the settings window"
    File: `assets/gifs/settings-window.gif`

A change to a switch, a detection zone, or Excluded apps applies on your next drag.

The settings live in `%LOCALAPPDATA%\MagneticBorders\settings.json`. The full list of keys is in the [contract spec](spec/contract.md).
