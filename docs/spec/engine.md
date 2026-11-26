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
| ENG-006 | The engine must log its build name, version and pid at startup. |
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
| ENG-050 | The log file must be `%LOCALAPPDATA%\MagneticBorders\logs\<engine>.log`, where `<engine>` is `cpp`, `rust` or `dotnet`. |
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
