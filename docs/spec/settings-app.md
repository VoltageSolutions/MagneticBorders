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
| UI-011 | The flyout must have rounded corners, a thin neutral grey border for the light or dark theme that never takes the accent color, and close when it loses focus. |
| UI-012 | The header must show the icon, "Magnetic Borders", and a status line such as "Running · 9 coupled drags", or "Not running". |
| UI-013 | The **Enabled** toggle must save `enabled`. |
| UI-014 | Retired. There is no pause button. |
| UI-015 | The **Magnetic coupling** toggle must save `glue.enabled`. |
| UI-016 | The **Magnetic attraction** toggle must save `snap.enabled`. |
| UI-017 | The **Open settings** button must open the full window and close the flyout. |

## Full window (no arguments)

One scrolling page in the style of Windows 11 Settings, with these groups in order.

| ID | Rule |
|---|---|
| UI-020 | **General** must have an Enabled toggle, a Start at login toggle, and a **Flyout background** choice of Translucent or Solid that saves `ui.flyout_backdrop`. |
| UI-021 | **General** must have a **Magnetic Borders** status expander showing the version, uptime and drag counts, with **Refresh** and **Quit Magnetic Borders** buttons. The Start at login description must read "Start Magnetic Borders when you sign in." |
| UI-022 | **Magnetic coupling** must have a toggle and a **Detection zone** slider from 0 to 64 px with a value label. |
| UI-023 | The Magnetic coupling slider's helper text must read "How far apart two edges can be and still stick together. 0 = they must touch." |
| UI-024 | **Magnetic attraction** must have a toggle; **Snap to screen edges** and **Snap to other windows** toggles (`snap.screen_edges`, `snap.windows`); a **Pull-in distance** slider from 0 to 64 px (`snap.distance_px`); a **Breakaway distance** slider from 0 to 128 px (`snap.breakaway_px`); and a **Snapping speed** slider from 100 to 5000 px per second (`snap.max_speed`), labelled Slow at the low end and Fast at the high end. Each setting must carry a one-line description. The other controls must be disabled while the toggle is off. |
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

## Smoke test

| ID | Rule |
|---|---|
| UI-043 | `MagneticBorders.Settings.exe --smoke [folder]` must open the settings window and then the flyout, let each load, and exit with code 0, without single-instance redirection. It must read and write settings and the crash log in `folder` (a temporary folder by default), never in `%LOCALAPPDATA%`. An unhandled exception must exit with 1 and a timeout with 2. CI runs it on the published x64 and ARM64 builds. |

## Diagnostics

The settings app writes diagnostic lines to `%LOCALAPPDATA%\MagneticBorders\logs\settings.log`, the file that also holds crash entries. Each line starts with the local time. The app replaces the file when it grows past 1 MB, and during a smoke test it writes to the smoke folder instead. Writing never fails the app: a write error is ignored.

| ID | Rule |
|---|---|
| UI-050 | Each time the flyout opens, the app must write the lines `flyout open`, `flyout backdrop`, `flyout power`, `flyout dwm` and `flyout window`, in that order. |
| UI-051 | The flyout must write a `flyout activation` line for every activation change and a `flyout closed` line when it closes. |
| UI-052 | A failure while collecting these facts must write a `flyout diagnostics failed` line and must not stop the flyout from opening. |

| Line | Fields |
|---|---|
| `flyout open` | `os` (Windows version), `app_theme` (the app theme WinUI resolved at start-up), `content_theme` (the theme the flyout content is drawn with), `system_theme` (`dark` or `light` from the system background color) and `high_contrast`. A difference between `app_theme` and `system_theme` shows a theme that changed after the app started. |
| `flyout backdrop` | `setting` (`translucent` or `solid`), `effective` (`acrylic`, `solid` or `solid-fallback`), the `reason` for it, `desktop_acrylic_supported` and `mica_supported` (the `IsSupported()` results of the system backdrop controllers) and `transparency_effects` (`UISettings.AdvancedEffectsEnabled`). `solid-fallback` means acrylic is requested but Windows is expected to draw a solid color: acrylic is unsupported, transparency effects are off or energy saver is on. |
| `flyout power` | `energy_saver` (`PowerManager.EnergySaverStatus`) and `power_supply` (`PowerManager.PowerSupplyStatus`). |
| `flyout dwm` | The HRESULT of each DWM call: the corner preference, the border color applied (a COLORREF, `0x00BBGGRR`) with its HRESULT, the border color read back with its HRESULT, and the frame extension. A read back that differs from the applied color means DWM did not keep it. An HRESULT other than `0x00000000` means the call failed. |
| `flyout window` | `dpi` and `scale` of the window, `size` and `position` in physical pixels, and the `work_area` of the monitor it was placed in. |
| `flyout activation` | The activation `state` (`CodeActivated`, `PointerActivated` or `Deactivated`) and `after_ms`, the time since the flyout was shown. |
| `flyout closed` | `after_ms`, the time since the flyout was shown. |

To capture a log, open the flyout from the notification area icon on the affected machine while the problem shows, close it, and attach `settings.log`.
