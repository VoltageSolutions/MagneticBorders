//! Port traits the controller depends on, plus `Snapshot` and `TimerKind`. Implemented by `mb-win32`, faked in tests.

use mb_core::{Point, Rect, WinInfo};

/// Inventory of the dragged window's monitor, taken once at drag start.
#[derive(Clone, Debug)]
pub struct Snapshot {
    /// Eligible windows on that monitor (the dragged one included when eligible).
    pub windows: Vec<WinInfo>,
    /// Monitor work area; edges on its border never form seams.
    pub work_area: Rect,
    /// Work areas of every monitor, the dragged window's one included; tells which screen sides touch another
    /// monitor (SNAP-031).
    pub work_areas: Vec<Rect>,
    /// Every visible, uncloaked, not minimized window that hides what is below it, in z-order from the top, on any
    /// monitor. Magnetic attraction uses it to find which parts of other windows can be seen (SNAP-038).
    pub stack: Vec<(u64, Rect)>,
    /// Monitor dpi divided by 96; scales the glue tolerance and the snap distances.
    pub dpi_scale: f64,
}

/// Reads window state. Called by the controller on the UI thread.
pub trait WindowInventory {
    /// Eligible windows on the dragged window's monitor. The dragged window is
    /// absent from the list when it is not eligible.
    fn snapshot(&mut self, dragged_id: u64) -> Snapshot;
    /// Current visible frame of a window, or `None` when it is gone.
    fn frame_of(&mut self, id: u64) -> Option<Rect>;
    /// Verbose diagnostics: called when a move ends, with the position Magnetic attraction held (`None` when no axis
    /// was held). The adapter may log where the window is. The default does nothing.
    fn report_move_result(&mut self, _id: u64, _held: Option<Rect>) {}
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

/// Inbound side of the mouse hook during a move (SNAP-070): driven by the hook adapter on the UI thread for every
/// mouse movement while the hook runs.
pub trait PointerEvents {
    /// Windows is about to put the cursor at `proposed`. Returns the position to put it at instead, or `None` to
    /// let the movement through unchanged.
    fn on_pointer_move(&mut self, proposed: Point) -> Option<Point>;
    /// A mouse button was released. The move is over even if its end event has not arrived yet.
    fn on_pointer_release(&mut self);
}

/// Everything the Win32 event adapters deliver to the controller: drag events and mouse movement.
pub trait EngineEvents: DragEvents + PointerEvents {}

impl<T: DragEvents + PointerEvents> EngineEvents for T {}

/// Watches the mouse while a window is moved (SNAP-070). The adapter installs a low-level mouse hook between
/// `start` and `stop` and delivers every movement to the controller as `PointerEvents`. Called on the UI thread.
pub trait PointerHook {
    /// Starts sending mouse movement to the controller. Returns false when the hook cannot be installed.
    fn start(&mut self) -> bool;
    /// Stops sending mouse movement. Safe to call when not started.
    fn stop(&mut self);
    /// The cursor position now, or `None` if it cannot be read.
    fn cursor(&mut self) -> Option<Point>;
}
