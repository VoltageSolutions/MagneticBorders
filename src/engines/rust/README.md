# Magnetic Borders - Rust

Cargo workspace; dependencies point inward only.

| Crate | Layer | Notes |
|---|---|---|
| `mb-core` | Domain | Rect, Edge, WinInfo, `build_seam`, `propagate`; pure |
| `mb-app` | Application | Ports (traits) + generic `DragController` state machine |
| `mb-win32` | Win32 adapters | Inventory, DeferWindowPos mover, WinEvent source (cfg(windows)) |
| `mb-host` | Host | `magnetic-borders-rs` bin: wiring, settings, message loop, Ctrl+C |
| `mb-fixtures` | Test tooling | `fixture-runner` bin + integration test over `../fixtures` |

Build: `cargo build --release --workspace`; test: `cargo test --release --workspace`.
Fixture tool: `fixture-runner <file-or-dir>` or `fixture-runner --print <file>`.
One thread-local slot in `mb-win32::events` routes WinEvent callbacks to the controller.
