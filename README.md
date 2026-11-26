<p align="center">
  <img width="128" align="center" src="src/common/assets/icon-256.png" alt="Magnetic Borders icon">
</p>
<h1 align="center">
  Magnetic Borders
</h1>

<p align="center">
  Drag the border between two docked windows and both resize together, for any layout on Windows, including FancyZones.
</p>

<p align="center">
  <a href="https://github.com/VoltageSolutions/MagneticBorders/actions/workflows/ci.yml" title="CI"><img alt="CI" src="https://github.com/VoltageSolutions/MagneticBorders/actions/workflows/ci.yml/badge.svg?branch=main"></a>
  &nbsp;
  <a href="https://VoltageSolutions.github.io/MagneticBorders" title="Docs"><img alt="Docs" src="https://github.com/VoltageSolutions/MagneticBorders/actions/workflows/docs-cd.yml/badge.svg?branch=main"></a>
  &nbsp;
  <a href="https://github.com/VoltageSolutions/MagneticBorders/releases"><img alt="Current Release" src="https://img.shields.io/github/release/VoltageSolutions/MagneticBorders.svg"></a>
</p>

## Introduction

Windows Snap resizes snapped windows together when you drag the line between them, but only for its built-in layouts. [FancyZones](https://learn.microsoft.com/en-us/windows/powertoys/fancyzones), included in [Microsoft PowerToys](https://github.com/microsoft/PowerToys), lets you create custom window tile layouts, but each window then resizes on its own. Magnetic Borders adds the missing piece: any windows whose edges touch resize together while you drag one of them. I call this Magnetic coupling.

- It works with windows you placed by hand, with Windows Snap, and with FancyZones layouts, including the gaps between zones.
- It never takes over window management and never reads modifier keys, so FancyZones (Shift and Ctrl) and Grab And Move (Alt or Win) keep working exactly as before.
- It runs in the background with a tray icon. Left-click the icon for a quick flyout, or open the full settings window.

## Installation

The current app version does not come with an installation wizard. Download the latest release of your choice and run it locally.

## Roadmap

- Drag-to-edge tiling, aka snap, such as how it works natively in macOS.
- Provide an installation wizard.
- Guidance around whether or not to run at Administrator level.

It's my long-term hope that this application would be considered for addition to Microsoft's PowerToys.

## Status

Magnetic Borders should be small, performant, and blend into your workflow. At this stage in development, I wasn't sure what technology would best meet this goal, so I've used AI-accelerated workflows to pursue multiple at the same time.

You can run Magnetic Borders based build from C++, C#, or Rust.

All three implementations build and deploy releases. I will leverage feedback and performance testing to determine which implementation to go with long-term.

## Contributing and support

Ideas, bugs, and pull requests are welcome. Start with [CONTRIBUTING.md](CONTRIBUTING.md) and the [Code of Conduct](CODE_OF_CONDUCT.md). The [docs site](https://VoltageSolutions.github.io/MagneticBorders) explains how the app works. If you like it, you can support me on [Ko-fi](https://ko-fi.com/voltagesolutions).

## Quick start

1. Build the repo (below), or download a release, then run one engine from `artifacts\cpp`, `artifacts\rust`, or `artifacts\dotnet`.
2. Dock two or more windows edge to edge, by hand or with FancyZones.
3. Drag the border between them. With Magnetic coupling on, every window on that border follows.
4. Left-click the magnet in the tray for the flyout. The **Enabled** switch turns the app on or off, and the settings window has the rest. Run an engine with `--console` to watch its log.

## Building from source

You need Windows 11, Visual Studio 2026 with the Desktop development with C++ workload, the .NET 10 SDK, and Rust through `rustup` (MSVC toolchain). The build loop is `tools/build-watcher.ps1`; see [CONTRIBUTING.md](CONTRIBUTING.md).

```text
src/engines/cpp      C++ engine (CMake + MSVC)
src/engines/rust     Rust engine (Cargo)
src/engines/dotnet   C# engine (.NET 10 Native AOT)
                     each engine: core/ app/ win32/ host/ fixture-runner/ + unit tests
src/settings         Settings app and tray flyout (WinUI 3): app/ core/ tests/
src/common           Shared icon and brand assets
tests/fixtures       Shared test layouts every engine must pass
docs                 Documentation site: Home, How it works, Spec
tools                Build watcher
```
