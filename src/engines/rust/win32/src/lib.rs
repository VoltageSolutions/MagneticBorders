//! Win32 adapters (`mb-win32`): implement the `mb-app` ports with `WinEvent` hooks, EnumWindows/DWM,
//! `DeferWindowPos`, `SetTimer`, and a low-level mouse hook, and own the tray icon, settings watcher, and named-pipe server.
//! Windows-only (`cfg(windows)`); all of it runs on one UI thread, other threads only post messages to it.
//! Depends on `mb-core` and `mb-app`.
#![cfg(windows)]

mod app;
mod diagnostics;
mod events;
mod inventory;
mod misc;
mod mover;
mod pointer;
mod timer;
mod util;

pub use app::{pipe_request, AppHandler, AppInfo, Tray};
pub use diagnostics::log_system_diagnostics;
pub use events::EventSource;
pub use inventory::Win32Inventory;
pub use misc::{
    alloc_console, drag_full_windows_enabled, message_box, run_message_loop, set_dpi_awareness, set_run_at_login,
    AppLog, ConsoleLog, MonotonicClock, SingleInstance,
};
pub use mover::Win32Mover;
pub use pointer::Win32PointerHook;
pub use timer::Win32Timer;
pub use util::Shared;

#[derive(Debug, thiserror::Error)]
/// Error type of this crate.
pub enum Win32Error {
    /// A Win32 call failed.
    #[error("win32: {0}")]
    Api(#[from] windows::core::Error),
}
