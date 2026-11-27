# Settings app reference

## Project map

All of it lives in `src/settings/`. The solution is `src/settings/MagneticBorders.Settings.slnx`, and shared build properties are in `src/settings/Directory.Build.props`.

| Project | Path | What it holds |
|---|---|---|
| App | `src/settings/app/` | The WinUI 3 exe: entry point, `App`, the two windows, XAML, and a few Win32 calls. Unpackaged, x64. |
| Core | `src/settings/core/` | Plain .NET library with no WinUI references: settings model, JSON store, pipe client, view model. |
| Tests | `src/settings/tests/` | xUnit tests for `AppSettings`, the store, the pipe client and response parser, and the view model. |

Dependencies point one way: `app` references `core`, and `tests` references `core`. The `core` project never references `app`.

## Types in `app/`

| Type | Kind | File | Purpose | Collaborators |
|---|---|---|---|---|
| `Program` | static class | `src/settings/app/Program.cs` | Entry point. Enforces a single instance and starts WinUI. | `App`, `AppInstance` |
| `App` | class | `src/settings/app/App.xaml.cs` | Application root. Owns the shared `ViewModel`, the 2 second poll, and the one window or flyout. | `Program`, `MainWindow`, `FlyoutWindow`, `SettingsViewModel` |
| `App.xaml` | XAML | `src/settings/app/App.xaml` | Merges the WinUI 3 control styles. | `App` |
| `MainWindow` | class | `src/settings/app/MainWindow.xaml.cs` | Full settings window code-behind: title bar, size, remove/add/open-spec handlers. | `App.ViewModel`, `NativeMethods` |
| `MainWindow.xaml` | XAML | `src/settings/app/MainWindow.xaml` | One scrolling page of settings cards in groups: General, Magnetic coupling, Magnetic attraction, Flyout, Excluded apps and About. | `SettingsViewModel`, `Fmt` |
| `FlyoutWindow` | class | `src/settings/app/FlyoutWindow.xaml.cs` | Flyout: borderless, topmost, placed near the notification area, closes on focus loss. `ApplyBackdrop` sets the background. | `App`, `NativeMethods`, `SettingsViewModel` |
| `FlyoutWindow.xaml` | XAML | `src/settings/app/FlyoutWindow.xaml` | Flyout layout: header, an Enabled toggle, Magnetic coupling and Magnetic attraction toggles, and an Open settings button. `SolidFill` is the opaque background used by the solid setting. | `SettingsViewModel` |
| `Fmt` | static class | `src/settings/app/Fmt.cs` | Looks up strings in `Resources.resw` (`Get`) and formats them for `x:Bind` function bindings (`Px`, `Drags`, `StatusLine`, `Summary`, `Version`). | `MainWindow.xaml`, `EngineStatus` |
| `Strings/en-US/Resources.resw` | resources | `src/settings/app/Strings/en-US/Resources.resw` | Every user-facing string. XAML picks them up through `x:Uid`, code through `Fmt.Get`. | `MainWindow.xaml`, `FlyoutWindow.xaml`, `Fmt` |
| `NativeMethods` | internal static class | `src/settings/app/NativeMethods.cs` | `user32` and `dwmapi` P/Invokes: cursor, DPI, foreground, rounded corners. Also holds the `POINT` struct. | `FlyoutWindow`, `MainWindow` |

## Types in `core/`

| Type | Kind | File | Purpose | Collaborators |
|---|---|---|---|---|
| `SettingsViewModel` | sealed partial class | `src/settings/core/SettingsViewModel.cs` | Shared view model for both windows. Debounced save, status polling, and the commands `RefreshCommand`, `QuitEngineCommand`, `AddAppCommand` and `RemoveAppCommand`. | `ISettingsStore`, `IEngineClient`, `EngineStatus` |
| `AppSettings` | sealed class | `src/settings/core/AppSettings.cs` | Typed view of `settings.json` (schema 1) with clamping and defaults, including `ui.flyout_backdrop`. | `SettingsStore`, `SettingsViewModel` |
| `FlyoutBackdrop` | enum | `src/settings/core/AppSettings.cs` | `Translucent` or `Solid`; saved as the string `translucent` or `solid`. | `AppSettings`, `FlyoutWindow` |
| `ISettingsStore` | interface | `src/settings/core/SettingsStore.cs` | Load, save, and last-write time. A seam for tests. | `SettingsViewModel` |
| `SettingsStore` | sealed class | `src/settings/core/SettingsStore.cs` | Reads `settings.json` and saves it atomically, keeping unknown keys. | `AppSettings` |
| `IEngineClient` | interface | `src/settings/core/EngineClient.cs` | Sends one command to Magnetic Borders. A seam for tests. | `SettingsViewModel` |
| `PipeEngineClient` | sealed class | `src/settings/core/EngineClient.cs` | Named-pipe implementation with a 1 second timeout. | `PipeResponse` |
| `EngineStatus` | sealed record | `src/settings/core/EngineStatus.cs` | Snapshot of a running build (build id for diagnostics, version, pid, counters, uptime that no screen shows). The build id is never shown. | `PipeResponse`, `Fmt` |
| `PipeResponse` | sealed record | `src/settings/core/EngineStatus.cs` | Parsed reply line. Never throws; bad input becomes a failure. | `PipeEngineClient`, `EngineStatus` |
| `AppInfo` | sealed record | `src/settings/core/AppInfo.cs` | Build-stamped version without `+` metadata. | `SettingsViewModel` |

## Types in `tests/`

| Type | Kind | File | Purpose | Collaborators |
|---|---|---|---|---|
| `AppSettingsTests`, `AppInfoTests` | classes | `src/settings/tests/AppSettingsTests.cs` | Parsing, clamping, the backdrop value, `WriteTo` and the version stamp. | `AppSettings`, `AppInfo` |
| `PipeResponseParseTests`, `PipeEngineClientTests` | classes | `src/settings/tests/PipeTests.cs` | Reply parsing and the client against a real in-process pipe. | `PipeResponse`, `EngineStatus`, `PipeEngineClient` |
| `TempDir` | sealed class | `src/settings/tests/SettingsTests.cs` | Temporary folder that deletes itself. | `SettingsStoreTests` |
| `SettingsStoreTests`, `PipeResponseTests`, `ViewModelTests` | classes | `src/settings/tests/SettingsTests.cs` | Defaults, unknown-key preservation and atomic save; status and error replies; saving after a flush and the quit command through a fake client. | `SettingsStore`, `PipeResponse`, `SettingsViewModel` |
| `SettingsStoreExtraTests`, `SettingsViewModelExtraTests` | classes | `src/settings/tests/StoreAndViewModelTests.cs` | Edge cases for the store and view model, using a `ScriptedClient`. | `SettingsStore`, `SettingsViewModel` |

## MVVM in this app

The UI is split the same way as in WPF.

- **Views** are the two `Window` classes. Each has a `.xaml` file for layout and a `.xaml.cs` file for window chrome and a few event handlers.
- **The view model** is `SettingsViewModel`. Both windows bind to the same instance, exposed as `App.ViewModel`, so a toggle in the flyout shows up in the window right away.
- **Bindings** use `{x:Bind ViewModel.Something, Mode=TwoWay}`. Each window has a `ViewModel` property that returns `App.ViewModel`, and `x:Bind` looks it up on the window itself.
- **Properties** come from CommunityToolkit.Mvvm. A field such as `[ObservableProperty] private bool _enabled;` generates `Enabled` with change notification, and a `partial void OnEnabledChanged(bool value)` hook to schedule a save.
- **Commands** come from `[RelayCommand]`. A method `RefreshAsync` generates `RefreshCommand`, and buttons bind with `Command="{x:Bind ViewModel.RefreshCommand}"`. The attribute also handles async methods.
- **Function bindings** such as `{x:Bind local:Fmt.Px(ViewModel.GlueTolerance), Mode=OneWay}` replace WPF value converters. They are plain static methods, so there is no `IValueConverter` to write.

How it differs from WPF:

| WPF | WinUI 3 here |
|---|---|
| `{Binding}`, resolved at run time through reflection | `{x:Bind}`, compiled and type checked at build time |
| Default mode is OneWay or TwoWay by control | `x:Bind` defaults to OneTime, so `Mode=OneWay` or `Mode=TwoWay` is written explicitly |
| `DataContext` set on the window | No `DataContext`; `x:Bind` reads properties on the window class |
| `IValueConverter` | Static function in `Fmt` |
| `ICommand` written by hand | `[RelayCommand]` generates it |
| `INotifyPropertyChanged` boilerplate | `[ObservableProperty]` generates it |

A few handlers stay in code-behind when they only touch the view: removing a list item (`OnRemoveApp`), Enter in the add box (`OnNewAppKeyDown`), and the Open settings button. Anything that changes data goes through the view model.

## Startup and single instance

1. `Program.Main` runs on an STA thread and calls `AppInstance.FindOrRegisterForKey("MagneticBorders.Settings.Main")`.
2. If another process already owns that key, `main.IsCurrent` is false. It takes this launch's activation arguments, calls `RedirectActivationToAsync` on a worker task, waits for it, and returns. No window is created.
3. If this process owns the key, it subscribes to `main.Activated`, which calls `App.OnRedirectedActivation`. Then `Application.Start` creates `App` with a `DispatcherQueueSynchronizationContext`, so `await` continuations return to the UI thread.
4. `App.OnLaunched` starts a `DispatcherTimer` that calls `RefreshAsync` every 2 seconds and then calls `Show`.
5. `Program.WantsFlyout` checks the launch arguments for `--flyout` (case-insensitive). The notification area icon click in Magnetic Borders starts the exe with `--flyout`. Running it with no arguments opens the full window.

`OnRedirectedActivation` runs on a background thread. It uses `DispatcherQueue.TryEnqueue` to get back to the UI thread before it touches any window.

## How the flyout is positioned and closed

Position (`FlyoutWindow.ShowNearTray`, which first calls `ApplyBackdrop`):

1. `GetCursorPos` gives the cursor, and `DisplayArea.GetFromPoint` finds the monitor under it. Its work area is used, which excludes the taskbar.
2. The window moves to that monitor first so it picks up that monitor's DPI, then the scale is read with `ScaleFor`.
3. It is sized to 360 x 300 effective pixels (`WidthEpx`, `HeightEpx`, clamped to the work area height) and placed 12 effective pixels (`MarginEpx`) from the bottom-right corner.
4. It is shown, then `Activate` and `SetForegroundWindow` are called.

Closing (`CloseSafe` guarantees a single `Close`):

- **Escape** closes it (`OnKeyDown`).
- **Losing activation** closes it, but only 150 ms after it was shown, so the activation churn at startup does not close it.
- **Focus watch**: Windows sometimes refuses foreground, so `Deactivated` never fires. A 250 ms `DispatcherQueueTimer` closes the flyout when the foreground window is not the flyout, after an 800 ms grace period.
- **Open settings** calls `App.OpenSettingsFromFlyout` first, so the process stays alive, then closes the flyout.
- **Notification area icon click toggle**: a click while it is open closes it. A click also deactivates the flyout just before the new launch arrives, so `App` ignores a flyout request within 450 ms of the last close.

## Talking to Magnetic Borders

**settings.json.** The default path is `%LOCALAPPDATA%\MagneticBorders\settings.json`, and Magnetic Borders watches it.

- `SettingsViewModel` marks itself dirty when a setting changes and restarts a 300 ms `System.Threading.Timer`. When it fires, `FlushSave` runs on a thread-pool thread under a lock.
- `FlushSave` reloads the file, applies the view model values (zones clamped to 0 through 64), and calls `SettingsStore.Save`.
- `Save` reads the current JSON object, lets `AppSettings.WriteTo` overwrite only the keys the app knows (`schema`, `enabled`, `glue`, `snap`, `ui`, `excluded_apps`, `start_at_login`), and keeps everything else. Nested `glue`, `snap` and `ui` objects are merged, so unknown keys inside them survive too.
- The result goes to a temp file in the same folder, then `File.Replace` swaps it in (`File.Move` for the first save). Magnetic Borders never reads a half-written file.
- `ReloadFromDisk` sets `_loading` so assigning properties does not trigger a save. `RefreshAsync` calls it when the file's write time changed and nothing is pending.
- `Closed` on `MainWindow` and `Dispose` both call `FlushSave`, so a quick change is not lost.

**Pipe client.** `PipeEngineClient.SendAsync` opens `\\.\pipe\MagneticBorders`, writes `{"cmd":"status"}` plus a newline, reads one line, and parses it with `PipeResponse.Parse`. One connection per call. A 1 second timeout covers the whole exchange. If Magnetic Borders is missing, slow, or the pipe errors, it returns `PipeResponse.Failure("engine not reachable")` instead of throwing. The app sends `status` (the poll) and `quit` (the Quit Magnetic Borders button). There is no pause. The master **Enabled** switch is a saved setting, so the flyout, the settings window and the notification area menu all write the same `enabled` key.

**Status polling.** `App` calls `RefreshAsync` every 2 seconds. It sets `Status` to the parsed `EngineStatus`, or to null when no running copy answers. `EngineRunning` follows `Status`, and the display text comes from `Fmt.StatusLine` and `Fmt.Summary`. `FeaturesEnabled`, `CouplingZoneEnabled` and `AttractionZoneEnabled` dim the cards that do not apply when the master switch or a feature is off.

## The flyout background

The **Flyout background** card in the Flyout group chooses between **Translucent** and **Solid**. The card's `ComboBox` binds to `SettingsViewModel.FlyoutBackdropIndex` (0 or 1), which maps to the `FlyoutBackdrop` enum. A change schedules a save like any other setting, and `AppSettings` stores it as `ui.flyout_backdrop` (`translucent` or `solid`). Magnetic Borders ignores the key. `FlyoutWindow.ApplyBackdrop` runs each time the flyout opens. For translucent it sets `SystemBackdrop` to a `DesktopAcrylicBackdrop`. For solid it sets no backdrop and shows `SolidFill`, an opaque theme-colored layer.

## Strings

Every user-facing string lives in `src/settings/app/Strings/en-US/Resources.resw`. In XAML an element gets `x:Uid="EnabledLabel"` and the resource named `EnabledLabel.Text` (or `.Header`, `.Content`, `.Description`) supplies the value. In code, `Fmt.Get("Key")` reads the same file through a `ResourceLoader`, and `Fmt.Px`, `Fmt.Drags` and the other formatters use format strings from it (`PixelsFormat`, `DragsFormat`). The two features appear as **Magnetic coupling** and **Magnetic attraction** everywhere. To add a language, add a `Strings/<locale>/Resources.resw` with the same names. New text never goes in XAML or C# literals.

## Designing the UI

WinUI 3 has no XAML designer in Visual Studio, so design happens against the running app instead.

- **XAML Hot Reload**: start the app with the debugger (F5) and edit a `.xaml` file. Changes appear in the running window when the file is saved or typing pauses.
- **Live Visual Tree** and **Live Property Explorer** (Debug > Windows): inspect the real element tree, pick an element in the app, and see its bound values and layout.
- **XAML Live Preview** (Debug > Windows): overlays the running app on a design mock-up image so things can be lined up.
- **Open the flyout from Visual Studio**: in the `app` project, open Debug > MagneticBorders.Settings Debug Properties and put `--flyout` in **Command line arguments**. Press F5 and the flyout opens near the cursor's monitor. Clear the box for the full window. Remember that a second launch redirects to the first instance, so close any running copy first, or the debugger session exits right away.
- The flyout closes when it loses focus. When Hot Reload tools attach in Visual Studio, focus leaves the app and the flyout closes. Use the full window for layout work on shared controls, and test the flyout in short runs.


## Where to change things

| To... | Change... |
|---|---|
| Add or edit a setting that is saved | `src/settings/core/AppSettings.cs` (property, `FromJson`, `WriteTo`), then `src/settings/core/SettingsViewModel.cs` (observable property, change hook, `ReloadFromDisk`, `FlushSave`). Update `docs/spec/contract.md` and all three builds. |
| Add a control to the window | `src/settings/app/MainWindow.xaml` |
| Add a control to the flyout | `src/settings/app/FlyoutWindow.xaml` |
| Change the flyout background choices | `FlyoutBackdrop` in `src/settings/core/AppSettings.cs`, `FlyoutWindow.ApplyBackdrop`, and the Flyout card in `src/settings/app/MainWindow.xaml` |
| Add or change a visible string | `src/settings/app/Strings/en-US/Resources.resw`, then `x:Uid` in the XAML or `Fmt.Get` in code |
| Change flyout size, margin, or close timing | constants and timers in `src/settings/app/FlyoutWindow.xaml.cs` |
| Change launch arguments or instance behavior | `src/settings/app/Program.cs` and `src/settings/app/App.xaml.cs` |
| Change status polling interval | `OnLaunched` in `src/settings/app/App.xaml.cs` |
| Add a pipe command | `src/settings/core/EngineClient.cs` plus a command in `SettingsViewModel`; every build must handle it |
| Change how a status field is parsed | `PipeResponse.Parse` in `src/settings/core/EngineStatus.cs` |
| Format a value for display | `src/settings/app/Fmt.cs` |
| Add a Win32 call | `src/settings/app/NativeMethods.cs` |
| Add a test | the matching file in `src/settings/tests/` (`AppSettingsTests.cs`, `PipeTests.cs`, `SettingsTests.cs` or `StoreAndViewModelTests.cs`) |

## Tests and quality

Every step runs through `pwsh tools/ci/run.ps1 -Target settings -Steps lint,build,test,coverage`. The local build watcher and the GitHub workflows call the same script. The step definitions are in `tools/ci/targets/settings.ps1`.

- **Test framework.** xUnit with `coverlet.collector`. The tests project references only `core`, so it never loads WinUI. It runs on x64 only.
- **Where tests live.** `src/settings/tests/`, in the four files listed above. The pipe client tests talk to a real named pipe created inside the test process.
- **Coverage.** `tests/coverage.runsettings` selects Cobertura output. The step merges the raw reports with ReportGenerator (a pinned dotnet tool) into `artifacts/coverage/settings.cobertura.xml` and fails below 40 percent line coverage. The WinUI `app` project has no tests, so the numbers describe `core`.
- **Lint.** `dotnet format MagneticBorders.Settings.slnx --verify-no-changes --severity error`, then XamlStyler in passive mode over every `.xaml` file with the repo's `Settings.XamlStyler` config. Passive mode reports files that need formatting and never rewrites them. `Directory.Build.props` sets `AnalysisLevel` to `latest-recommended`, `EnforceCodeStyleInBuild` and `TreatWarningsAsErrors`, so analyzer findings fail the build. Vulnerability advisories (`NU1901` to `NU1904`) are reported but are not errors, so a new advisory cannot break a reproducible build.
- **Deterministic builds.** `Deterministic` is on, `Directory.Packages.props` pins every package version, and `packages.lock.json` pins the resolved graph. With `-Ci` the script restores with `--locked-mode` and sets `ContinuousIntegrationBuild`.
- **Architectures.** The project builds for `x64` and `ARM64` (`Platforms` in `Directory.Build.props`, `-p:Platform`). x64 is built, tested and published natively. ARM64 is built and published but not tested.
