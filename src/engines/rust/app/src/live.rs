//! `Live`: runtime knobs and counters shared between the host and the controller.

use std::cell::Cell;

/// Runtime knobs and counters shared (single UI thread) between host and controller.
#[derive(Debug)]
pub struct Live {
    /// False while the engine is disabled or magnetic coupling is off; drags are then ignored.
    pub active: Cell<bool>,
    /// Glue tolerance in 96-dpi pixels; scaled by monitor dpi per drag.
    pub tolerance_px: Cell<i32>,
    /// Drags that changed the window size (candidates for gluing).
    pub drags: Cell<u64>,
    /// Drags that found at least one seam.
    pub glued_drags: Cell<u64>,
}

impl Live {
    /// Creates the shared state with zeroed counters. Held in an `Rc` by the host and the controller.
    pub fn new(active: bool, tolerance_px: i32) -> Self {
        Self {
            active: Cell::new(active),
            tolerance_px: Cell::new(tolerance_px),
            drags: Cell::new(0),
            glued_drags: Cell::new(0),
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
    }
}
