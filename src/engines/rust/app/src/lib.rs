//! Application layer (`mb-app`): the port traits, the generic `DragController` state machine, the settings
//! model and the named-pipe protocol. No OS types; it is generic over its ports, so tests use fakes and
//! `mb-win32` supplies the real adapters. Depends on `mb-core`.
mod controller;
pub mod diagnostics;
mod live;
mod pipe;
mod ports;
mod settings;

pub use controller::DragController;
pub use live::{Live, SnapOptions};
pub use pipe::{handle_request, Action, EngineState, Reply};
pub use ports::{
    Clock, DragEvents, EngineEvents, Log, PointerEvents, PointerHook, Snapshot, Timer, TimerKind, WindowInventory,
    WindowMover,
};
pub use settings::{apply_reload, with_enabled, Settings};

/// Engine version, taken from the workspace package version.
pub const VERSION: &str = env!("CARGO_PKG_VERSION");
