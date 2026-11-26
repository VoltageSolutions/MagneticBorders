# Settings app

`MagneticBorders.Settings.exe` is one WinUI 3 app with two surfaces: a small flyout and a full settings window. It reads and writes `settings.json` and talks to the running engine over the pipe. It holds all UI, so the engines stay headless.

## Platform

| ID | Rule |
|---|---|
| UI-001 | The app must be .NET 10 with the Windows App SDK, unpackaged (`WindowsPackageType=None`), self-contained, x64. |
| UI-002 | The app must use MVVM with CommunityToolkit.Mvvm and the SettingsCard and SettingsExpander controls from the Windows Community Toolkit. |
| UI-003 | The app must follow the system light or dark theme. The settings window must use a Mica backdrop. The flyout must use a translucent backdrop or a solid one, as `ui.flyout_backdrop` says (CFG-014). |
| UI-004 | The settings model, JSON load and save, the pipe client and the view models must live in a library with no WinUI references, so they can be unit-tested. |
| UI-005 | The app must be a single instance. A second launch must forward its arguments (`--flyout` or none) to the running instance and exit. |
| UI-006 | The app icon must be the shared `icon.ico`. |

## Flyout (`--flyout`)

A small borderless window of about 360 by 420 effective pixels.

| ID | Rule |
|---|---|
| UI-010 | The flyout must be anchored at the bottom right of the work area of the monitor under the cursor, above the taskbar. |
| UI-011 | The flyout must have rounded corners and close when it loses focus. |
| UI-012 | The header must show the icon, "Magnetic Borders", and a status line such as "Running · 9 coupled drags", or "Not running". |
| UI-013 | The **Enabled** toggle must save `enabled`. |
| UI-014 | Retired. There is no pause button. |
| UI-015 | The **Magnetic coupling** toggle must save `glue.enabled`. |
| UI-016 | The **Magnetic attraction** toggle must save `snap.enabled` and carry the caption "Coming soon". |
| UI-017 | The **Open settings** button must open the full window and close the flyout. |

## Full window (no arguments)

One scrolling page in the style of Windows 11 Settings, with these groups in order.

| ID | Rule |
|---|---|
| UI-020 | **General** must have an Enabled toggle, a Start at login toggle, and a **Flyout background** choice of Translucent or Solid that saves `ui.flyout_backdrop`. |
| UI-021 | **General** must have a **Magnetic Borders** status expander showing the version, uptime and drag counts, with **Refresh** and **Quit Magnetic Borders** buttons. The Start at login description must read "Start Magnetic Borders when you sign in." |
| UI-022 | **Magnetic coupling** must have a toggle and a **Detection zone** slider from 0 to 64 px with a value label. |
| UI-023 | The Magnetic coupling slider's helper text must read "How far apart two edges can be and still stick together. 0 = they must touch." |
| UI-024 | **Magnetic attraction** must have a toggle and a **Detection zone** slider from 0 to 64 px, and an info bar saying the feature is coming soon. |
| UI-025 | **Excluded apps** must list executable names, each with a **Remove** button, and a text box with an **Add** button. |
| UI-026 | **About** must show the icon, the version and a link to the documentation. |
| UI-027 | Every change must save immediately, debounced by 300 ms. |
| UI-028 | While a window is open, the app must poll the running app's status every 2 seconds. |
| UI-029 | **Quit Magnetic Borders** must send `quit`. |
| UI-030 | When Magnetic Borders is not running, the status area must say "Not running" and the buttons that need it must be disabled. |

## Rules for all surfaces

| ID | Rule |
|---|---|
| UI-040 | The app must never read or register a modifier key or hotkey. |
| UI-041 | The app must never write a setting the schema does not define, and must keep unknown keys. |
| UI-042 | The app must work with each of the three builds without knowing which is running. It must not show the build name or the word "engine" anywhere. |
