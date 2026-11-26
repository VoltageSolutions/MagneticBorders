//! Port traits the controller depends on, plus `Snapshot` and `TimerKind`. Implemented by `mb-win32`, faked in tests.

use mb_core::{Rect, WinInfo};

/// Inventory of the dragged window's monitor, taken once at drag start.
pub struct Snapshot {
    /// Eligible windows on that monitor (the dragged one included when eligible).
    pub windows: Vec<WinInfo>,
    /// Monitor work area; edges on its border never form seams.
    pub work_area: Rect,
    /// Monitor dpi divided by 96; scales the glue tolerance.
    pub dpi_scale: f64,
}

/// Reads window state. Called by the controller on the UI thread.
pub trait WindowInventory {
    /// Eligible windows on the dragged window's monitor. The dragged window is
    /// absent from the list when it is not eligible.
    fn snapshot(&mut self, dragged_id: u64) -> Snapshot;
    /// Current visible frame of a window, or `None` when it is gone.
    fn frame_of(&mut self, id: u64) -> Option<Rect>;
}

/// Writes window positions. Called by the controller on the UI thread.
pub trait WindowMover {
    /// Applies all moves in one batch; returns frames actually applied (read back).
    fn apply(&mut self, moves: &[(u64, Rect)]) -> Vec<(u64, Rect)>;
}

/// Monotonic millisecond clock, used for throttling and log timestamps.
pub trait Clock {
    /// Milliseconds since an arbitrary fixed start.
    fn now_ms(&self) -> u64;
}

#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
/// The three timers the controller uses.
pub enum TimerKind {
    /// Applies the pending frame once the throttle window has passed.
    Flush,
    /// Fires when location changes stop; checks members for size mismatches.
    Settle,
    /// Fires shortly after drag end; confirms final frames and repairs them.
    Verify,
}

/// One-shot timers on the UI thread; re-arming a kind replaces it.
pub trait Timer {
    /// Starts (or restarts) the timer of `kind` to fire once after `ms`.
    fn arm(&mut self, kind: TimerKind, ms: u64);
    /// Cancels every armed timer.
    fn cancel_all(&mut self);
}

/// Sink for diagnostic lines.
pub trait Log {
    /// Always-on message.
    fn info(&self, msg: &str);
    /// Verbose message, shown only with `--verbose`.
    fn debug(&self, msg: &str);
}

/// Inbound side of the controller: driven by the event-source adapter and the timers, on the UI thread.
pub trait DragEvents {
    /// The user started moving or sizing window `id`.
    fn on_move_size_start(&mut self, id: u64);
    /// Window `id` changed position or size during a drag.
    fn on_location_change(&mut self, id: u64);
    /// The user released window `id`.
    fn on_move_size_end(&mut self, id: u64);
    /// A timer of `kind` fired.
    fn on_timer(&mut self, kind: TimerKind);
}
