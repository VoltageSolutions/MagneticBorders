# Magnetic attraction

Magnetic attraction snaps a window against the edge of the screen, the taskbar, or another window while you move it. It works like window alignment on macOS.

!!! example "GIF: moving a window toward a screen edge and snapping against it"
    File: `assets/gifs/attraction-edge.gif`

## How to use it

Move a window slowly toward another window or the edge of the screen. When its edge comes within the **Pull-in distance** (16 pixels by default), it snaps flush and holds there.

- **Slide.** While an edge is held, keep moving along it. The window slides against the edge without letting go, so you can line it up exactly where you want.
- **Corners.** A window can be held on two edges at once, such as the side of another window and the top of the screen.
- **Line up.** When you stack one window above another, their left and right sides snap into line. Windows side by side line up their tops and bottoms.
- **Pull free.** Keep moving past the edge. Once you are past the **Breakaway distance** (24 pixels by default), the window lets go and jumps back under your mouse.
- **Move past.** A window moving faster than the **Snapping speed** never starts to snap, so a quick move across the screen doesn't catch on everything it passes.

## Limits

- There is no key to skip snapping, because Shift, Ctrl, Alt and Win belong to FancyZones, Windows and Grab And Move. Move faster instead.
- A window only snaps to windows on the same monitor, and the border between two monitors is not an edge.
- Windows draws the window where your mouse puts it, then Magnetic Borders moves it back to the edge. On some apps you may see the window flicker while it is held.
- If Windows Snap or FancyZones places the window when you let go, their placement wins.
