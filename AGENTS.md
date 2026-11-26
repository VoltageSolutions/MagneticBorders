# AGENTS.md

Guidance for AI coding agents (Claude Code, Codex, Copilot, Cursor, Gemini CLI and others) working in this repo. Humans should read [CONTRIBUTING.md](CONTRIBUTING.md).

## What this is

Magnetic Borders is a Windows 10/11 utility that glues touching window edges together: drag the border between docked windows and every window on that border resizes with it, for any layout, including FancyZones. It never takes over window management and never reads modifier keys.

The same engine is built three times, in C++, Rust and C#, so the languages can be compared. One shared WinUI 3 app provides the tray flyout and the settings window. The layout follows PowerToys: a small native engine owns the tray icon and hooks, and a separate WinUI 3 app owns all UI.

| Part | Path | Role |
|---|---|---|
| C++ engine | `src/engines/cpp/` | Reference build (CMake + MSVC). `MagneticBorders.Cpp.exe`. The PowerToys candidate. |
| Rust engine | `src/engines/rust/` | Cargo workspace, `windows` crate. `magnetic-borders-rs.exe`. |
| C# engine | `src/engines/dotnet/` | .NET 10 Native AOT, Generic Host + DI, CsWin32. `MagneticBorders.Net.exe`. |
| Settings app | `src/settings/` | WinUI 3 (Windows App SDK), MVVM. `MagneticBorders.Settings.exe` (`--flyout` or full window). |
| Common | `src/common/` | `assets/`: icon sources (`icon.svg`, `icon-small.svg`), `icon.ico`, brand wordmarks. Shared contract files and engine strings also live here. |
| Fixtures | `tests/fixtures/` | Shared `.layout` test cases. Every engine must pass all of them. |
| Docs | `docs/` | The specs in `docs/spec/` and the docs site. |
| Tools | `tools/` | `ci/run.ps1` (the one build entry point), `build-watcher.ps1` (local build loop), `setup-dev.ps1` (installs the toolchains). |

Every engine uses the same folders, so the languages can be read side by side: `core/` (pure geometry), `app/` (drag controller, ports, settings, pipe protocol), `win32/` (adapters), `host/` (composition root), `fixture-runner/`, and unit tests beside the code (`tests/` in C++ and C#, inside each crate in Rust). Dependencies point inward only: core <- app <- win32 <- host. Keep Win32 types out of core and app. The settings app uses `app/` (WinUI 3 exe), `core/` (view models, settings model, pipe client) and `tests/`.

## Build, test and run

The engines and the settings app build on Windows only. The C++ domain library and fixture runner have no Windows headers and also build with g++/clang.

Every build step is `pwsh tools/ci/run.ps1 -Target <cpp|rust|dotnet|settings|docs> -Steps <restore,lint,build,test,coverage,publish,smoke> [-Arch x64|arm64] [-Ci]`. `smoke` (settings only) starts the published app with `--smoke`, which opens the settings window and the flyout and exits; it catches start-up crashes that only the Native AOT build has. The build watcher and the GitHub workflows both call it, so results match. Agents do not run compilers on the Windows machine directly. Go through the watcher (`tools/build-watcher.ps1`):

1. Write `.build/request-<name>.json`, for example `{"id":"cpp-12","targets":["cpp"],"steps":["lint","build","test","coverage"],"arch":"x64","ci":false}`. `targets` are `cpp`, `rust`, `dotnet`, `settings`, `docs` (and `commit`, `push`, `compare`). `steps` defaults to restore, build, and test or publish when `test` or `publish` is true. `arch` is `x64` or `arm64`. `ci` runs locked restores and deterministic builds as GitHub does.
2. Poll `.build/status-<name>.json` until `state` is `done` and `id` matches.
3. Read `results.<target>.errors` (the first 30 error lines). Open the log it names only with grep or tail.
4. Published exes land in `artifacts/<target>/<arch>/`, and coverage in `artifacts/coverage/<target>.cobertura.xml`.

`commit` uses `.build/commit-msg.txt` as the message and `push` pushes the current branch. Do not commit or push unless asked. `pwsh tools/setup-dev.ps1` (elevated, Visual Studio closed) installs the tools.

### Quality rules

Run `lint,build,test,coverage` for each part you change before you finish. All four must pass.

| Target | Lint | Coverage |
|---|---|---|
| `cpp` | clang-format `--Werror` and clang-tidy (all findings are errors); `/W4 /WX` | OpenCppCoverage, GoogleTest |
| `rust` | `cargo fmt --check` and clippy pedantic with `-D warnings` | `cargo llvm-cov` |
| `dotnet` | `dotnet format --verify-no-changes`, analyzers at `latest-recommended`, warnings as errors, no IL2xxx/IL3xxx | coverlet, xUnit |
| `settings` | the same as `dotnet`, plus XamlStyler (passive) | coverlet, ReportGenerator |
| `docs` | strict Zensical build | none |

- The coverage step fails below 40 percent line coverage. Codecov (`codecov.yml`) expects 40 percent for the project and 60 percent for a patch. Aim for 80 percent in `core` and `app`.
- Add or update tests with every change. Behavior changes also need a fixture when they are geometric.
- Builds are deterministic: do not unpin toolchains or edit lock files by hand. Run `restore` and commit the changed lock file.
- ARM64 is cross-built (`-Arch arm64`); tests, lint and coverage run on x64.

### Versions

The version lives in four files: `src/engines/cpp/CMakeLists.txt`, `src/engines/rust/Cargo.toml` (and the workspace crates in `Cargo.lock`), `src/engines/dotnet/Directory.Build.props` and `src/settings/Directory.Build.props`. Change it only with `pwsh tools/ci/set-version.ps1 -Version x.y.z`. `pwsh tools/ci/check-version.ps1` verifies that all four agree and that a `release/vX.Y.Z` branch carries X.Y.Z.

### GitHub workflows

In `.github/workflows/`, named after GameControllerForZwift's workflows without the platform prefix:

- `ci.yml` (**CI**): every app on every pull request and push to `main`. Jobs: Version, Lint (per part), x64 (build, test, coverage, publish, and the settings smoke test), ARM64 (cross-built), ARM smoke test (on an ARM64 runner), Unit Test Results (one check with the totals), Coverage (Codecov, one flag per part), Vulnerabilities (NuGet and RustSec), Parity, and Tester build (a downloadable build linked from a comment on the pull request).
- `cd.yml` (**CD**): builds, packages and drafts the GitHub release when a new version lands on `main`.
- `docs-ci.yml` (**Docs CI**): strict docs build on pull requests that touch the docs.
- `docs-cd.yml` (**Docs CD**): builds the docs and deploys them to GitHub Pages from `main`.
- `codeql.yml` (**CodeQL**): on pull requests, on push to `main`, and monthly. Coverage runs in CI, so it is not combined here.
- `release-branch.yml` (**Release Branch**): opens the next `release/v...` branch and sets its version.

The single required check is **CI result**. Do not require the other jobs by name.

## Source of truth

- The specs in `docs/spec/` define behaviour: [magnetic coupling](docs/spec/magnetic-coupling.md) (resize behaviour and domain algorithm), [fixtures](docs/spec/fixtures.md) (fixture format), [engine](docs/spec/engine.md), [contract](docs/spec/contract.md) (`settings.json` and the named pipe) and [settings app](docs/spec/settings-app.md). Read the relevant one before changing behaviour, and update it in the same change.
- The three engines must behave identically. A behaviour change lands in all three, with a fixture when it is geometric.
- `src/common/assets/icon.svg` is the icon source. Regenerate `icon.ico` from `icon-small.svg` (16-32 px) and `icon.svg` (40-256 px). Never edit a copy.

## Conventions

- Never read modifier-key state or register a default hotkey. FancyZones owns Shift and Ctrl during drags; Grab And Move owns Alt and Win.
- Everything Win32 runs on one UI thread with a message loop. Other threads only post messages to it.
- Leave zero warnings. Every build treats warnings as errors; see the quality rules above.
- Comments and docs describe what the code does now. Do not write history ("previously", "used to") or future plans.
- Docs voice: Home pages may address the reader ("you"). How it works and Spec use the app name ("Magnetic Borders" or "MB") or impersonal instructions, never "I". US spelling, active voice, serial comma, short sentences, no em dashes.
- User-facing text belongs in resource files so it can be localized: `.resw` with `x:Uid` in the settings app, `STRINGTABLE` resources in the C++ engine, the equivalent in Rust and C#. Do not add new hard-coded UI strings.
- Keep private data out of the repo: no machine names, local user paths, or log files.
- When an AI helped with a commit, end the message with a `Co-Authored-By` trailer. Do not add links to AI sessions.
