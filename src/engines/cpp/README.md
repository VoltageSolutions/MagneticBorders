# Magnetic Borders - C++ build

Ports and adapters; dependencies point inward: host -> win32 -> app -> core.

| Folder | Target | Role |
|---|---|---|
| core/ | mb_core | Domain: Rect, WinInfo, Edge, Seam, Plan, build_seam, propagate (no OS types) |
| app/ | mb_app | Ports (pure virtual) + DragController state machine |
| win32/ | mb_win32 | Inventory, DeferWindowPos mover, WinEvent hooks, clock/log (RAII for hooks/handles) |
| host/ | magnetic_borders | Composition root (main.cpp), settings.json, PerMonitorV2 manifest |
| tools | fixture_runner | Parses ../fixtures/*.layout and checks domain behaviour |
| tests | mb_tests | GoogleTest: domain + controller with fake ports |

Build: `cmake --preset release && cmake --build --preset release && ctest --preset release`.
ARM64 cross build: `cmake --preset release-arm64` from an ARM64 developer prompt (tests are not built). `debug` is the preset for IDEs.
CI entry point: `pwsh tools/ci/run.ps1 -Target cpp -Steps restore,lint,build,test,coverage,publish [-Arch arm64] [-Ci]`.
Lint uses `.clang-format` and `.clang-tidy` (warnings are errors; NOLINT needs a reason). Dependencies are pinned by SHA-256 in `CMakeLists.txt`.
Output: `MagneticBorders.Cpp.exe`. Globals: the thread_local hook slot and the Ctrl+C thread id.
