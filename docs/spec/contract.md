# Contract

The three engines and the settings app share two interfaces: the file `settings.json` and the named pipe `\\.\pipe\MagneticBorders`. Every engine implements the same contract, so one settings app drives whichever engine runs. Changing either interface means changing all four apps and this page.

## Paths

| Item | Path |
|---|---|
| Data directory | `%LOCALAPPDATA%\MagneticBorders\` |
| Settings | `<data>\settings.json` |
| Logs | `<data>\logs\<engine>.log` |

## settings.json

```json
{
  "schema": 1,
  "enabled": true,
  "glue": { "enabled": true, "tolerance_px": 12 },
  "snap": {
    "enabled": true,
    "distance_px": 16,
    "breakaway_px": 24,
    "max_speed": 600,
    "screen_edges": true,
    "windows": true
  },
  "ui": { "flyout_backdrop": "translucent" },
  "excluded_apps": [],
  "start_at_login": false
}
```

### Schema and defaults

| ID | Rule |
|---|---|
| CFG-001 | The file must be a UTF-8 JSON object with `"schema": 1`. |
| CFG-002 | `enabled` must be a boolean, default `true`. It is the saved master switch. |
| CFG-003 | `glue.enabled` must be a boolean, default `true`. |
| CFG-004 | `glue.tolerance_px` must be an integer from 0 to 64, default 12. 0 means edges must touch exactly. |
| CFG-005 | `snap.enabled` must be a boolean, default `true`. |
| CFG-006 | `snap.distance_px` must be an integer from 0 to 64, default 16. It is the pull-in distance of Magnetic attraction. |
| CFG-007 | `excluded_apps` must be an array of executable names, default empty (no apps are excluded by default). |
| CFG-008 | `start_at_login` must be a boolean, default `false`. |
| CFG-009 | Retired. Engines used to ignore `snap.*`; Magnetic attraction now uses it. |
| CFG-010 | A key that is missing or has the wrong type must fall back to its default. |
| CFG-011 | A number outside its range must be clamped into range. |
| CFG-012 | A blank string in `excluded_apps` must be ignored. |
| CFG-013 | A missing file must mean all defaults. |
| CFG-014 | `ui.flyout_backdrop` must be the string `translucent` or `solid`, default `translucent`. It sets the flyout background. Engines must ignore it. |
| CFG-015 | `snap.breakaway_px` must be an integer from 0 to 128, default 24. |
| CFG-016 | `snap.max_speed` must be an integer from 100 to 5000, default 600, in effective pixels per second. |
| CFG-017 | `snap.screen_edges` must be a boolean, default `true`. |
| CFG-018 | `snap.windows` must be a boolean, default `true`. |

### Reading and writing

| ID | Rule |
|---|---|
| CFG-020 | A text that is not a JSON object must be treated as a parse error: the engine keeps its previous settings and logs a warning. |
| CFG-021 | The settings app must preserve unknown keys when it saves, including unknown keys inside `glue`, `snap`, and `ui`. |
| CFG-022 | Every writer must write atomically: a temp file in the same directory, then a replace. |
| CFG-023 | Parsing must be a pure function with no file or OS access, so it can be unit-tested. |

### Watching and applying

| ID | Rule |
|---|---|
| CFG-030 | Each engine must watch the data directory for changes on a background thread that only posts a message to the UI thread. |
| CFG-031 | Changes must be debounced by 100 ms before the file is re-read. |
| CFG-032 | `enabled`, `glue.enabled`, `glue.tolerance_px` and `excluded_apps` must apply on the next drag without a restart. |
| CFG-033 | Magnetic coupling must act only when `enabled` and `glue.enabled` are both true. |
| CFG-034 | `start_at_login` must be owned by the running engine (see ENG-060). |

## Named pipe

The engine is the server and the settings app is the client. The pipe name is `\\.\pipe\MagneticBorders`.

| ID | Rule |
|---|---|
| PIPE-001 | The protocol must be UTF-8, one JSON object per line, one response line per request line. |
| PIPE-002 | The pipe must be reachable by the current user only (default DACL). |
| PIPE-003 | The server must run on a background thread. Anything that touches engine state must be posted to the UI thread, and the reply must wait up to 500 ms for it. |
| PIPE-004 | Request handling must be a pure function from a request line and state to a response line and an action, so tests need no Win32. |
| PIPE-005 | Every response must be a JSON object with an `ok` boolean. |

### Commands

| ID | Request | Response and effect |
|---|---|---|
| PIPE-010 | `{"cmd":"status"}` | `{"ok":true,"engine":"cpp","version":"0.2.0","pid":1234,"enabled":true,"glue":{"enabled":true,"tolerance_px":12},"drags":17,"glued_drags":9,"uptime_s":360}`. `engine` is `cpp`, `rust` or `dotnet`. |
| PIPE-011 | Retired | There is no pause command. |
| PIPE-012 | Retired | There is no resume command. |
| PIPE-013 | `{"cmd":"reload"}` | `{"ok":true}`. The engine re-reads `settings.json` now. |
| PIPE-014 | `{"cmd":"quit"}` | `{"ok":true}`, then the engine exits cleanly. |
| PIPE-015 | Any other command, including the retired `pause` and `resume`, or a line that is not valid JSON | `{"ok":false,"error":"unknown command"}`. |

`enabled` is the saved master switch. There is no runtime override.
