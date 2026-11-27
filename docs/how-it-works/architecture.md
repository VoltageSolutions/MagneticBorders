# Application architecture

Magnetic Borders and its settings app are two separate programs. Magnetic Borders has no UI except the notification area icon and its menu. The settings app (WinUI 3) draws the flyout and the settings window. They never share memory. They talk in two ways.

## The settings file

All settings live in `%LOCALAPPDATA%\MagneticBorders\settings.json`. The settings app writes it. It saves to a temporary file in the same folder and then swaps it in, so MB never reads a half-written file. It keeps any keys it does not know. One of its own keys is `ui.flyout_backdrop`, the flyout background (`translucent` or `solid`). Magnetic Borders ignores it.

MB watches the folder on a background thread. The Windows calls are `FindFirstChangeNotification` in C++, `ReadDirectoryChangesW` in Rust, and `FileSystemWatcher` in C#. That thread does not read anything. It only posts a message to the UI thread. The UI thread waits 100 ms, because editors write in several steps, then reads the file and applies it. If the file does not parse, MB keeps its old settings.

The notification area menu's **Enabled** checkbox works the same way. It rewrites the `enabled` key in the file, and the watcher picks it up like any other change.

## The named pipe

For live things that are not settings, Magnetic Borders runs a **named pipe** server at `\\.\pipe\MagneticBorders`. The protocol is one JSON line per request and one JSON line per reply.

The settings app uses it for three jobs.

- `status` asks which build runs, its version, and drag counts. The app asks every 2 seconds while a window is open.
- `reload` tells Magnetic Borders to read the file now.
- `quit` stops MB.

The pipe thread does not touch application state. It posts the request to the UI thread, waits up to 500 ms, and sends the reply back. If the wait times out, the client gets `{"ok":false,"error":"timeout"}`.

A second copy of MB uses the same pipe. It sends `status` to the first one, then shows "already running" and exits.

## Why this split

Magnetic Borders stays small and fast. One settings app serves all three builds, because they all speak the same file and pipe protocol. The same design is used by PowerToys, where a small runner owns the tray and hooks, and a separate app owns the UI.

The exact keys and commands are in the [contract spec](../spec/contract.md).

## How the code is organized

Every build is the same design in a different language. Each one is split into four layers, and dependencies point inward only.

### The parts of the repo

| Part | Path | What it is |
|---|---|---|
| C++ build | `src/engines/cpp/` | CMake and MSVC. `MagneticBorders.Cpp.exe`. The reference build. |
| Rust build | `src/engines/rust/` | A Cargo workspace using the `windows` crate. `magnetic-borders-rs.exe`. |
| C# build | `src/engines/dotnet/` | .NET 10 Native AOT with Generic Host and CsWin32. `MagneticBorders.Net.exe`. |
| Settings app | `src/settings/` | WinUI 3 with MVVM. `MagneticBorders.Settings.exe`, with `--flyout` or the full window. |
| Common | `src/common/assets/` | Icon sources and brand art. |
| Fixtures | `tests/fixtures/` | Shared `.layout` test cases every build must pass. |
| Docs | `docs/` | This site and the specs. |
| Tools | `tools/` | `tools/ci/run.ps1`, the one build entry point, the local build watcher, and `tools/setup-dev.ps1`. |

### The four layers

Inside each build, the folders are the same.

| Layer | Contains | Rule |
|---|---|---|
| `core/` | Pure geometry: rectangles, [seams](seams.md), the clamp. | No OS types, no I/O. |
| `app/` | The `DragController` state machine, the ports it needs, the settings model, and the pipe protocol. | No Win32 types. Window ids are 64-bit integers. |
| `win32/` | Adapters that implement the ports with Win32 calls. | Owns every handle. |
| `host/` | The composition root and the [message loop](message-loop.md). | The only place that sees every concrete type. |

Dependencies run one way: `host` uses `win32`, `win32` uses `app`, and `app` uses `core`. Next to the layers sit `fixture-runner/` and the unit tests.

Because `core` and `app` know nothing about Windows, the tests run them with fake windows. That is how the three builds are checked against the same fixtures.

### Ports

The `app` layer owns the interfaces it needs. The `win32` layer implements them. Tests use fakes.

| Port | Role |
|---|---|
| `WindowInventory` | `snapshot(dragged_id)` lists eligible windows, the work area, and the DPI scale. `frame_of(id)` reads one frame. |
| `WindowMover` | `apply(moves)` moves windows in one batch and returns the frames it got. |
| `Clock` | `now_ms()`. |
| `Timer` | `arm(kind, ms)` for Flush, Settle, and Verify. `cancel_all()`. |
| `Log` | `info` and `debug`. |

The event source drives the controller with `on_move_size_start`, `on_location_change`, and `on_move_size_end`.

### For readers who know WPF and MVVM

There is no view to bind in the main app, so MVVM does not apply there. The nearest match: the domain is the Model, the `DragController` plays the part of the ViewModel (state and commands, no platform types, easy to unit test), and the Win32 adapters are the View. Constructor injection and an IoC container carry over directly. The settings app is plain MVVM.

### How each language does it

| Concern | C++ | Rust | C# |
|---|---|---|---|
| Layers | CMake static libraries that link only inward | Crates in a workspace. The compiler rejects an outward dependency. | Projects in a `.slnx`. References point inward. |
| Ports | Abstract classes | Traits | Interfaces |
| Wiring | Manual constructor injection | Generics wired in `main`, no container | `Microsoft.Extensions.Hosting` with explicit registrations |
| Event loop | `GetMessageW` in the host | `GetMessageW` on the main thread | A dedicated thread started by a hosted service |
| Hook callback to controller | Static function and a `thread_local` pointer | `thread_local!` and `RefCell` | `[UnmanagedCallersOnly]` method and a static field |
| Handles | RAII wrappers | `Drop` on owned wrappers | `IDisposable` classes |
| Win32 bindings | `windows.h` | `windows` crate | CsWin32 |
| Tests | GoogleTest | `cargo test` | xUnit |
| Lint | clang-format and clang-tidy | rustfmt and clippy (pedantic) | `dotnet format` and analyzers |

**C# and Native AOT.** The host stays lean with `Host.CreateEmptyApplicationBuilder`. Every service is registered explicitly, with no assembly scanning and no reflection-based configuration binding, because Native AOT does not support them.

### Rules every build follows

1. Dependencies point inward: host, win32, app, core.
2. The `app` layer owns the ports.
3. One composition root builds the object graph.
4. One thread runs everything Win32. Other threads only post messages to it.
5. No modifier keys, and no globals beyond the one hook slot.

### Read the code

The [code tour](code-tour/cpp.md) walks each build and the settings app file by file.
