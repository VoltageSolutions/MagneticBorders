# Background app

Each build is a background Windows app with a notification area icon, a context menu, a single-instance guard, a log file, and an optional start-at-login entry. All three behave the same. `--console` adds a console that mirrors the log.

## Process

| ID | Rule |
|---|---|
| ENG-001 | The engine must use the Windows subsystem, so no console window opens by default. |
| ENG-002 | `--console` must allocate a console and mirror the log to it. |
| ENG-003 | `--verbose` must enable verbose logging. |
| ENG-004 | The engine must always write the log file, with or without `--console`. |
| ENG-005 | At startup the engine must check `SPI_GETDRAGFULLWINDOWS` and log a warning if it is off. |
| ENG-006 | The engine must log its build name, version, and pid at startup. |
| ENG-007 | An unhandled startup error must show a message box titled `Magnetic Borders - fatal error`. |

## Single instance

| ID | Rule |
|---|---|
| ENG-010 | The engine must hold the named mutex `Local\MagneticBorders.Instance`. All three engines use the same name. |
| ENG-011 | A second launch of any build must show a message box "Magnetic Borders is already running." and exit. The text must not name the build. |

## Notification area icon

| ID | Rule |
|---|---|
| ENG-020 | The engine must show a notification area icon with `Shell_NotifyIcon` and `NOTIFYICON_VERSION_4`, using the embedded icon. |
| ENG-021 | The tooltip must be `Magnetic Borders`, followed by ` – disabled` (with an en dash) while `enabled` is false. |
| ENG-022 | The engine must re-add the icon when `TaskbarCreated` arrives. |
| ENG-023 | A left click must launch the settings app with `--flyout`. If the settings app is not found, it must open the context menu. |
| ENG-024 | A right click must open the context menu. |
| ENG-025 | The notification area icon and the posted messages must belong to a hidden message window on the UI thread. |

## Context menu

| ID | Rule |
|---|---|
| ENG-030 | The first item must be a disabled header `Magnetic Borders <version>`. |
| ENG-031 | The second item must be a checkbox named `Enabled`. It must be checked while `enabled` is true, and choosing it must flip the saved `enabled` key. |
| ENG-032 | The third item must be `Settings...` and must launch the settings app with no arguments. |
| ENG-033 | A separator must come before `Exit`, the last item, which ends the engine cleanly. |

## Finding the settings app

| ID | Rule |
|---|---|
| ENG-040 | The engine must look first for `MagneticBorders.Settings.exe` next to its own exe. |
| ENG-041 | The engine must look next in `settings\` (the release and tester layout), then in `..\settings\` and `..\..\settings\<arch>\` (the local build layouts), relative to its folder. |
| ENG-042 | If both fail, the engine must give up without an error dialog. |

## Logs and counters

| ID | Rule |
|---|---|
| ENG-050 | The log file must be `%LOCALAPPDATA%\MagneticBorders\logs\<engine>.log`, where `<engine>` is `cpp`, `rust`, or `dotnet`. |
| ENG-051 | The engine must truncate the log file at startup if it is larger than 1 MB. |
| ENG-052 | The engine must count `drags` (resize drags seen) and `glued_drags` (drags that built a seam). |
| ENG-053 | The version must come from one source per build system (see [CONTRIBUTING.md](https://github.com/VoltageSolutions/MagneticBorders/blob/main/CONTRIBUTING.md#versioning)). |

## Start at login

| ID | Rule |
|---|---|
| ENG-060 | When `start_at_login` is true, the running engine must write `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\MagneticBorders` with the quoted path of its own exe. |
| ENG-061 | When `start_at_login` is false, the running engine must delete that value. |
| ENG-062 | The engine must apply a change to `start_at_login` without a restart. |

## Enabled switch

| ID | Rule |
|---|---|
| ENG-070 | There must be no pause state. The saved `enabled` key is the only on/off switch. |
| ENG-071 | The notification area menu checkbox, the flyout toggle, and the settings window toggle must all write `enabled` to `settings.json`. |

## Diagnostics

Verbose logging records how each move batch is applied and what the system looks like, so a report of flicker or a refused size can be traced to a cause. Without `--verbose` the engine writes none of these lines and makes none of the extra reads they need.

| ID | Rule |
|---|---|
| ENG-080 | With `--verbose`, the engine must write one `diag: system` line and one `diag: monitor` line per monitor at startup, after the startup line and before the first drag. |
| ENG-081 | With `--verbose`, every move batch must write one `move: batch` line, then one `move: hwnd` line per window in the batch. |
| ENG-082 | With `--verbose`, a window left out of a batch must write a `move: skipped` line. |
| ENG-083 | With `--verbose`, a failed deferred batch must write a `move: deferred batch failed` line with the Win32 error number, and every `SetWindowPos` failure in the fallback must write a `move: SetWindowPos failed` line. |
| ENG-084 | Without `--verbose`, the mover must not read back window rectangles, measure time or build any of these lines. |
| ENG-085 | All three engines must write the same text after the log prefix. The prefix (time stamp and level) may differ. |
| ENG-086 | With `--verbose`, taking the snapshot at the start of a drag must write one `diag: window` line per candidate window, the dragged window included when it is eligible. |
| ENG-087 | With `--verbose`, the end of a move (not a resize) must write one `diag: move result` line after the `move end` line. |
| ENG-088 | With `--verbose`, each mouse movement that the pointer hook changes must write `diag: pointer hold proposed=x,y cursor=x,y window=l,t,r,b`: the position Windows proposed, the cursor position the engine set, and the window frame that position gives. Movements let through unchanged write nothing. |
| ENG-089 | With `--verbose`, a mouse button release seen by the pointer hook during a move must write `diag: pointer release cursor=x,y expected=x,y`: the position of the release event and the last cursor position the engine set or let through. |
| ENG-090 | With `--verbose`, the end of a move with the pointer hook must write `diag: move correction id=N frame=l,t,r,b held=l,t,r,b\|none action=applied\|none\|skipped-size` before the `move end` line. `frame` is `unavailable` when the window cannot be read. `held` is the target position that SNAP-075 computes from the window's frame and the held axes (the position that is applied when `action` is `applied`), and `none` when no axis is held or the frame is unavailable. `action` is `applied` when that target is applied (SNAP-075), `skipped-size` when the window's size changed, and `none` otherwise. |
| ENG-091 | With `--verbose`, each calibration of the cursor-to-window offset (SNAP-077) must write `diag: pointer calibrate axis=x\|y error=N cursor=x,y window=l,t,r,b`: the axis, the measured error, the cursor position the engine set, and the window frame that position gives. The C++ and Rust builds write it. The C# build does not calibrate and never writes it. |

### Capture a log

1. Exit Magnetic Borders from its notification area menu. Only one instance runs at a time.
1. Start the engine from a terminal with `--verbose`. Add `--console` to watch the lines live.

   | Engine | Command |
   |---|---|
   | C++ | `MagneticBorders.Cpp.exe --verbose` |
   | Rust | `magnetic-borders-rs.exe --verbose` (`-v` also works) |
   | C# | `MagneticBorders.Net.exe --verbose` |

1. Reproduce the problem with a short drag, then exit the engine.
1. Attach `%LOCALAPPDATA%\MagneticBorders\logs\<engine>.log`. The settings app writes `settings.log` in the same folder (see [settings app](settings-app.md#diagnostics)).

A drag writes many lines, and the engine trims the file only at startup when it is over 1 MB. Capture one problem per run.

### Start-up lines

```text
diag: system os_build=22631.3880 dpi_awareness=per-monitor-v2 dwm_composition=on monitors=2
diag: monitor 0 rect=0,0,1920,1200 work=0,0,1920,1160 dpi=144x144 primary=yes
```

| Field | Meaning |
|---|---|
| `os_build` | `CurrentBuildNumber` and `UBR` from the registry, or `unknown`. |
| `dpi_awareness` | DPI awareness of the engine thread: `unaware`, `system`, `per-monitor`, `per-monitor-v2` or `unknown`. Anything but `per-monitor-v2` means Windows scales or virtualizes coordinates, which breaks pixel-exact seams. |
| `dwm_composition` | `on` when desktop composition is enabled. |
| `rect`, `work` | Monitor and work area in virtual-screen pixels, as `left,top,right,bottom`. |
| `dpi` | Effective DPI of the monitor, as `x` by `y`. 96 is 100 percent. |

### Move lines

```text
move: batch windows=2 mode=deferred flags=0x0214 elapsed_us=812 mismatches=1
move: hwnd=0x1a2b requested=0,0,960,1000 actual=0,0,960,1000
move: hwnd=0x3c4d requested=960,0,1920,1000 actual=960,0,1940,1000 delta=0,0,20,0 window_delta=0,0,20,0
```

| Line | Meaning |
|---|---|
| `move: batch` | One applied batch. `mode` is `deferred` (one `BeginDeferWindowPos` and `EndDeferWindowPos` batch) or `individual` (the deferred batch failed and each window got its own `SetWindowPos`). `flags` are the `SetWindowPos` flags (`0x0214` is `SWP_NOACTIVATE`, `SWP_NOZORDER` and `SWP_NOOWNERZORDER`). `elapsed_us` is the time to apply the batch in microseconds, without the read back. `mismatches` counts windows whose read back frame differs from the request. |
| `move: hwnd` | One window after the batch. `requested` is the visual frame the engine asked for and `actual` is the visual frame read back right after (`DWMWA_EXTENDED_FRAME_BOUNDS`), or `unavailable`. `delta` (actual minus requested) appears only when they differ, and `window_delta` (`GetWindowRect` minus the requested window rectangle) only when that differs. A `delta` means the app refused or corrected the size, for example at its minimum size or by snapping to a size step. |
| `move: skipped` | A window left out of the batch. `reason` is `no-offsets` (never inventoried; the Rust engine uses zero offsets instead and never writes this reason), `banned` (an earlier move failed, for example an elevated window) or `hung`. |
| `move: deferred batch failed` | A call of the deferred batch failed. `step` is `begin`, `defer` (with the `hwnd` that failed) or `end`, and `error` is the Win32 error number. The engine then moves each window with `SetWindowPos`. |
| `move: SetWindowPos failed` | A fallback move failed. The window is excluded until the engine restarts. Error 5 is access denied. |

### Snap lines

```text
diag: window id=1052040 frame=1000,100,1900,890 rect=1000,100,1900,890 border=0,0,0,0 dpi=120 class=CASCADIA_HOSTING_WINDOW_CLASS exe=WindowsTerminal.exe dragged=no
diag: window id=395786 frame=100,100,990,890 rect=90,100,1000,900 border=-10,0,10,10 dpi=120 class=Notepad exe=notepad.exe dragged=yes
diag: move result id=395786 frame=1000,100,1890,890 rect=990,100,1900,900 held=1000,100,1890,890
```

| Line | Meaning |
|---|---|
| `diag: window` | One candidate window when a drag starts: every eligible window on the monitor of the dragged window, with `dragged=yes` for the dragged window. Windows that only appear in the covering stack are not written. |
| `diag: move result` | The dragged window when a move ends, after the `move end` line. `held` is the position Magnetic attraction held at the end, or `none` when no axis was held. A resize writes no such line. |

| Field | Meaning |
|---|---|
| `id` | Window handle as a decimal number, the same value as `id=` in the `move start` and `move end` lines. |
| `frame` | Visual frame (`DWMWA_EXTENDED_FRAME_BOUNDS`) as `left,top,right,bottom`, or `unavailable`. The engine aligns edges on this rectangle. |
| `rect` | `GetWindowRect` as `left,top,right,bottom`, or `unavailable`. |
| `border` | `rect` minus `frame` per side. A normal window has an invisible resize border, so the left and top values are negative and the right and bottom values are positive (about 10 px each at 120 DPI). `0,0,0,0` on a window that has such a border means the frame bounds include the border, so aligned edges leave a visible gap of that size. |
| `dpi` | `GetDpiForWindow` of the window. |
| `class` | Window class name, or `unknown`. |
| `exe` | File name of the process executable, or `unknown`. |
| `dragged` | `yes` for the dragged window, otherwise `no`. |

`diag: move result` compares `frame` with `held`. A `frame` that differs from `held` after a `move end` line shows that the window did not end where attraction held it.

### Reading the lines

- Flicker while dragging often shows as `mismatches` above 0 in consecutive batches for the same window: the window corrects its own size after each move, and the neighbor repaints twice.
- `mode=individual` means the windows repaint one by one instead of together.
- A large `elapsed_us` (several milliseconds) means a window is slow to process the move. Windows blocks the engine in `SetWindowPos` until the window handles its size messages.
- A `dpi_awareness` other than `per-monitor-v2`, or monitors with different `dpi` values, can explain offsets of a pixel or more between `requested` and `actual`.
- A `border` of `0,0,0,0` next to a gap in the layout points at a window whose frame bounds are its outer window rectangle. Look at its `class` and `exe`.
