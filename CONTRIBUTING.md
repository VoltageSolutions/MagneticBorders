# Contributing

Thank you for helping. Ideas, bug reports, and pull requests are all welcome. I am happy to talk through an idea before you write code.

## Ideas and bugs

Open an issue. For a bug, tell me what you did, what you expected, what happened, and which engine you ran (C++, Rust, or C#). Attach that engine's log from `%LOCALAPPDATA%\MagneticBorders\logs\`.

## What you need

- Windows 11
- Visual Studio 2026 with the Desktop development with C++ workload
- The .NET 10 SDK
- Rust through `rustup`, with the MSVC toolchain
- PowerShell 7 for the build watcher

The docs site needs Python and `pip install -r docs/requirements.txt`.

## First-time setup

Close Visual Studio, open an elevated PowerShell 7 window, and run:

```powershell
pwsh tools/setup-dev.ps1
```

It installs or checks everything below, so you do not have to do it by hand.

## Building each part

The repo holds four apps. Pick the one you want to change.

### C++ engine

Open `src/engines/cpp` in Visual Studio with **File, Open, Folder**. Visual Studio reads `CMakePresets.json` and offers the `release` preset. Build, then run the tests from Test Explorer.

```powershell
cd src/engines/cpp
cmake --preset release
cmake --build --preset release
ctest --preset release
```

### Rust engine

Open `src/engines/rust` in VS Code with the rust-analyzer extension.

```powershell
cd src/engines/rust
cargo build --release --workspace
cargo test --release --workspace
cargo clippy --workspace
```

### C# engine

Open `src/engines/dotnet/MagneticBorders.slnx` in Visual Studio, or the folder in VS Code with the C# Dev Kit.

```powershell
cd src/engines/dotnet
dotnet build -c Release
dotnet test
```

### Settings app

Open `src/settings/MagneticBorders.Settings.slnx` in Visual Studio with the .NET desktop development workload. WinUI 3 has no XAML designer. Run the app with F5 and use **XAML Hot Reload** and the **Live Visual Tree** instead.

```powershell
cd src/settings
dotnet build MagneticBorders.Settings.slnx -c Release -p:Platform=x64
dotnet test
```

### Docs

```powershell
pip install -r docs/requirements.txt
zensical serve                   # live preview at http://localhost:8000
zensical build --clean --strict  # must finish with no warnings
```

## Check before a pull request

Run the same steps CI runs, for each part you changed:

```powershell
pwsh tools/ci/run.ps1 -Target <part> -Steps lint,build,test,coverage
```

`<part>` is `cpp`, `rust`, `dotnet`, `settings`, or `docs`. The build watcher and GitHub both use this script, so a green run here means a green run there. Lint is read-only. It tells you what to fix and never rewrites your files.

## The build watcher

`tools/build-watcher.ps1` builds everything on request, so half-written code is never built. Start it in a terminal and leave it running.

```powershell
pwsh tools/build-watcher.ps1 -Check   # report the toolchains it finds
pwsh tools/build-watcher.ps1          # watch .build/ for requests
```

Ask for a build by writing a request file, then read the status file.

```json
{"id":"cpp-1","targets":["cpp"],"test":true,"publish":true}
```

Save that as `.build/request-<name>.json`. A request can also name `steps`, `arch` (`x64` or `arm64`), and `ci`. The watcher answers in `.build/status-<name>.json` when `state` is `done`. Targets are `cpp`, `rust`, `dotnet`, `settings`, and `compare`. Published exes land in `artifacts/<target>/<arch>/`. To run them together, `pwsh tools/ci/stage-local.ps1` arranges them in `artifacts/local/<arch>/` with the same layout as a release zip, so each build finds the settings app.

## Tests

- **Fixtures.** Tiny `.layout` files in `tests/fixtures/` describe windows, a drag, and the expected result. Every engine runs all of them.
- **Unit tests.** They sit next to each engine's code and in `src/settings/tests/`.
- **By hand.** Before a release, try two side-by-side windows, a FancyZones layout with gaps, a corner drag, and an elevated window, at 100 and 150 percent scaling.

## Versioning

The version lives in four files. A release bumps all four together.

| Part | File | Field |
|---|---|---|
| C++ engine | `src/engines/cpp/CMakeLists.txt` | `project(... VERSION x.y.z)` |
| Rust engine | `src/engines/rust/Cargo.toml` | `[workspace.package] version` |
| C# engine | `src/engines/dotnet/Directory.Build.props` | `<Version>` |
| Settings app | `src/settings/Directory.Build.props` | `<Version>` |

Change it with `pwsh tools/ci/set-version.ps1 -Version x.y.z`. It edits all four files and the Rust `Cargo.lock`, then runs `tools/ci/check-version.ps1` to confirm they agree.

## Pull requests

- `main` is the latest version. Work on a feature branch.
- Every part you changed passes `lint,build,test,coverage`: no warnings, all tests and fixtures green, and coverage at or above 40 percent. Codecov asks for 60 percent on the lines you change.
- A behavior change lands in all three engines and updates the matching spec page in `docs/spec/`.
- The docs still build with `zensical build --clean --strict`.
- If you change the UI, add screenshots to the description: each changed screen in light and dark mode.
- Be kind. The [Code of Conduct](CODE_OF_CONDUCT.md) applies.

AI coding agents should read [AGENTS.md](AGENTS.md).
