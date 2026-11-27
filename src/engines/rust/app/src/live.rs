//! `Live`: runtime knobs and counters shared between the host and the controller.

use std::cell::Cell;

/// Magnetic attraction tunables in effective pixels (96 dpi); the controller scales them by the monitor dpi.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SnapOptions {
    /// Pull-in distance (`snap.distance_px`).
    pub distance_px: i32,
    /// Breakaway distance (`snap.breakaway_px`).
    pub breakaway_px: i32,
    /// Snapping speed limit in pixels per second (`snap.max_speed`).
    pub max_speed: i32,
    /// Work-area sides are targets (`snap.screen_edges`).
    pub screen_edges: bool,
    /// Other windows are targets (`snap.windows`).
    pub windows: bool,
}

impl Default for SnapOptions {
    fn default() -> Self {
        Self { distance_px: 16, breakaway_px: 24, max_speed: 600, screen_edges: true, windows: true }
    }
}

/// Runtime knobs and counters shared (single UI thread) between host and controller.
#[derive(Debug)]
pub struct Live {
    /// False while the engine is disabled or magnetic coupling is off; resizes are then ignored.
    pub active: Cell<bool>,
    /// Glue tolerance in 96-dpi pixels; scaled by monitor dpi per drag.
    pub tolerance_px: Cell<i32>,
    /// Drags that changed the window size (candidates for gluing).
    pub drags: Cell<u64>,
    /// Drags that found at least one seam.
    pub glued_drags: Cell<u64>,
    /// False while the engine is disabled or magnetic attraction is off; moves are then ignored (SNAP-001).
    pub snap_active: Cell<bool>,
    /// Magnetic attraction tunables; read at the start of every drag.
    pub snap: Cell<SnapOptions>,
    /// Move drags handled by magnetic attraction.
    pub move_drags: Cell<u64>,
    /// Times an edge started being held.
    pub snaps: Cell<u64>,
}

impl Live {
    /// Creates the shared state with zeroed counters, magnetic attraction on and default tunables. Held in an
    /// `Rc` by the host and the controller.
    pub fn new(active: bool, tolerance_px: i32) -> Self {
        Self {
            active: Cell::new(active),
            tolerance_px: Cell::new(tolerance_px),
            drags: Cell::new(0),
            glued_drags: Cell::new(0),
            snap_active: Cell::new(true),
            snap: Cell::new(SnapOptions::default()),
            move_drags: Cell::new(0),
            snaps: Cell::new(0),
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn new_zeroes_the_counters() {
        let l = Live::new(true, 12);
        assert!(l.active.get());
        assert_eq!(l.tolerance_px.get(), 12);
        assert_eq!((l.drags.get(), l.glued_drags.get()), (0, 0));
        assert_eq!((l.move_drags.get(), l.snaps.get()), (0, 0));
    }

    #[test]
    fn attraction_starts_on_with_the_default_tunables() {
        let l = Live::new(true, 12);
        assert!(l.snap_active.get());
        assert_eq!(
            l.snap.get(),
            SnapOptions { distance_px: 16, breakaway_px: 24, max_speed: 600, screen_edges: true, windows: true }
        );
    }
}
