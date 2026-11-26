# Design decisions

The choices that shaped Magnetic Borders, and why. Read this before changing something that looks odd: there is usually a reason here.

Each entry says what was decided, why, and what was rejected. New decisions go at the end with the next number.

## D-001: Magnetic coupling is detected from geometry, not tracked as state

Any window edge within the detection zone of the edge being dragged joins the drag. Nothing remembers which windows were snapped together. This makes it work the same for windows placed by hand, by Windows Snap, or by FancyZones, and it means there is nothing to get out of sync. Uncoupling is just moving a window apart.

## D-002: No modifier keys, ever

Shift and Ctrl belong to FancyZones during a drag, and Alt or Win belong to Grab And Move. The engines never read key state and never register a default hotkey. An earlier draft used Alt to break the glue, which collided with Grab And Move.

## D-003: An out-of-process helper, not a window manager

The engines watch WinEvent hooks (`EVENT_SYSTEM_MOVESIZESTART`, `EVENT_OBJECT_LOCATIONCHANGE`, `EVENT_SYSTEM_MOVESIZEEND`) and move other windows with `DeferWindowPos`. Nothing is injected into other processes. Tools such as AquaSnap replace window handling; this tool only follows the drag Windows is already doing.

## D-004: One UI thread owns everything Win32

Out-of-context WinEvent callbacks arrive on the thread that installed the hook, and that thread must run a message loop. The hooks, the drag controller, timers, the notification area icon and the mover all live on that thread. Other threads (the settings file watcher, the pipe server) only post messages to it. No locks are needed in the controller.

## D-005: Ports and adapters in every engine

Each engine has `core/` (pure geometry), `app/` (drag controller and the ports it needs), `win32/` (adapters) and `host/` (composition root). Dependencies point inward. The geometry and the controller are unit-tested without Windows, and the same structure in three languages makes them easy to compare.

## D-006: Three engines, one behaviour

The engine exists in C++, Rust and C# so the languages can be compared for clarity, memory and responsiveness. Shared `.layout` fixtures in `tests/fixtures/` keep them in agreement: every engine must pass every fixture. C++ is the reference build because PowerToys' FancyZones and module interface are C++.

## D-007: The trailing flush

Location changes are throttled to one apply per 8 ms. The first prototype only applied a skipped update when the drag ended, so a fast drag followed by a pause left a gap. Now a skipped update arms a timer that applies the latest frame. A 150 ms Settle check re-reads the neighbours when the user pauses, and a 60 ms Verify check after release confirms the snap-back took effect.

## D-008: Learned minimum sizes

Another process's `WM_GETMINMAXINFO` cannot be read reliably, so each engine learns a window's minimum when the window refuses a size, and clamps the seam from then on. The OS lets the dragged window keep moving; on release it snaps back to the clamp.

## D-009: Background engines plus one shared WinUI 3 settings app

This mirrors PowerToys: a small native runner owns the notification area icon and hooks, and a separate WinUI 3 app owns all UI. The engines and the settings app share `settings.json` (watched for live changes) and the named pipe `\\.\pipe\MagneticBorders` (status and commands). One UI serves whichever engine is running. WinUI 3 rather than WPF because PowerToys uses it and it supports Native AOT.

## D-010: Not a Windows service

Services run in session 0 and cannot see or hook the user's desktop. Start at login (an `HKCU\...\Run` entry) gives the same always-on behaviour.

## D-011: A local build watcher instead of CI, for now

Gitea has no Windows runners, so `tools/build-watcher.ps1` builds on the developer's Windows PC when a request file appears in `.build/`. Building on request rather than on every save avoids builds of half-written code, and the compact `status-*.json` keeps agents from reading whole logs. Continuous integration followed on GitHub Actions; see D-016.

## D-012: Repository layout follows PowerToys

Source lives in `src/` (engines, settings, common), cross-engine tests in `tests/`, docs in `docs/`, tooling in `tools/`. Every engine uses the same folder names so the languages read side by side. Unit tests stay beside their code, which is what each language's tooling expects.

## D-013: Icon

A horseshoe magnet at 45 degrees with two sparks, following Microsoft's app icon guidance: one ambient light from the top left, gradients at 120 degrees, 7 px corner radius on the poles and body. Below 40 px the sparks are dropped (`icon-small.svg`) because they become noise.

## D-014: User-facing names

The two features are called **Magnetic coupling** and **Magnetic attraction** everywhere a person reads them: the flyout, the settings window, the docs, and the logs. Magnetic coupling makes touching window borders into a seam that resizes together. Magnetic attraction snaps a window against a screen edge or another window while you move it. The settings keys stay `glue.*` and `snap.*` so saved files keep working, and the spec IDs use the `RESIZE` prefix. "Glue" is no longer a user-facing word. "Seam" is fine.

## D-015: No pause, one Enabled switch

An earlier build had a runtime pause next to the saved Enabled switch. Two switches confused people, and a pause that forgets itself on restart is easy to miss. Pause was removed. The master **Enabled** switch is the only on/off control. It appears as a toggle in the flyout and the settings window, and as a checkbox in the notification area menu. All three write the same saved `enabled` key. A timed pause was rejected because it adds state for little gain.

## D-016: One build entry point

`tools/ci/run.ps1` is the only way to restore, lint, build, test, measure coverage and publish a part (`cpp`, `rust`, `dotnet`, `settings`, `docs`). Each part has a script in `tools/ci/targets/` that defines only the steps that apply to it. The local build watcher and the GitHub workflows both call `run.ps1`, so a step that passes on the developer's PC passes in CI, and a failure in CI reproduces with the same command. A request to the watcher can name `targets`, `steps`, `arch` and `ci`. Separate workflow scripts were rejected because two build paths drift apart.

## D-017: Quality bar

Every part is held to the same bar, and CI enforces it.

- **Lint.** C++: clang-format and clang-tidy, with every finding an error. Rust: rustfmt and clippy with the pedantic group, warnings denied. C# (engine and settings app): `dotnet format` and the .NET analyzers at `latest-recommended`, with warnings as errors. XAML: XamlStyler in passive mode. Docs: a strict Zensical build.
- **Coverage.** The targets match GameControllerForZwift: 40 percent for the project and 60 percent for the lines a pull request changes, configured in `codecov.yml`. The `coverage` step fails below 40 percent line coverage. The goal for `core` and `app` is 80 percent.
- **Deterministic builds.** Pinned toolchains (`rust-toolchain.toml`, `global.json`), exact package versions with committed lock files (`Cargo.lock`, `packages.lock.json`), locked restores in CI, and `/Brepro` with path remapping, so the same commit gives the same binary.
- **Architectures.** x64 and ARM64. ARM64 is cross-built and published. Tests, lint and coverage run on x64.

One strict bar was chosen over per-language leniency so the three engines stay comparable, and a failing check means something is wrong, not that it is noisy.

## D-018: Magnetic attraction holds the cursor during Windows' own drag

Windows runs a window move in the dragged app's own thread and keeps the window at a fixed offset from the cursor. While a window is moved, Magnetic Borders runs a low-level mouse hook (`WH_MOUSE_LL`). For each mouse movement it works out the held position and, when the window must not follow the mouse on an axis, blocks the movement and sets the cursor where the held position puts it. Windows then moves the window there itself. Nothing is injected into other processes ([D-003](#d-003-an-out-of-process-helper-not-a-window-manager)), and FancyZones, Windows Snap and Grab And Move keep working because the drag is still Windows' drag.

Moving the window back to the held position after each of Windows' moves is only the fallback. Windows always moves the window first, so a held window jitters with every small movement across the line. Holding the cursor removes the jitter. The trade-off is that the cursor stops on a held axis, like a detent, where macOS lets the cursor run on while the window stays. In-process hooks (a DLL that changes `WM_MOVING`, as AquaSnap does) would keep the cursor free, but they need one DLL per architecture and conflict with D-003. Replacing the drag with Magnetic Borders' own drag loop was rejected because it would break FancyZones and Windows Snap, which watch the system drag.

The hook only runs during a move and only sees mouse movement and button releases, never keys. When it cannot be installed, Magnetic attraction falls back to moving the window back.

The speed limit, breakaway and sliding follow macOS. macOS also lets the Option key bypass snapping. Magnetic Borders never reads keys ([D-002](#d-002-no-modifier-keys-ever), SNAP-004), so a fast move is the bypass.
