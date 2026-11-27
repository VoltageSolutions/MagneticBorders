//! `PointerDrag`: magnetic attraction driven by the mouse cursor during a move (SNAP-070 to SNAP-076). No OS types.

use crate::{Attraction, Point, Rect};

/// Returns `r` with its left edge at `x`, same width.
fn at_x(r: &Rect, x: i32) -> Rect {
    Rect::new(x, r.t, r.r + x - r.l, r.b)
}

/// Returns `r` with its top edge at `y`, same height.
fn at_y(r: &Rect, y: i32) -> Rect {
    Rect::new(r.l, y, r.r, r.b + y - r.t)
}

/// Runs an `Attraction` from mouse movement instead of from window positions. Windows keeps the window at a fixed
/// offset from the cursor during a move, so holding the window means holding the cursor: on a held axis the cursor
/// stays put while the mouse's movement builds up in the free position, and on a free axis the cursor goes where
/// the mouse sends it. Windows then moves the window to the held position itself, so nothing is corrected after
/// the fact. Single-threaded and pure: the caller supplies the clock.
#[derive(Debug)]
pub struct PointerDrag {
    attraction: Attraction,
    frame0: Rect,
    cursor0: Point,
    cursor: Point,
    free: Rect,
}

impl PointerDrag {
    /// Starts from the window's frame and the cursor position at the same moment.
    pub fn new(attraction: Attraction, frame: Rect, cursor: Point) -> Self {
        Self { attraction, frame0: frame, cursor0: cursor, cursor, free: frame }
    }

    /// Takes the position Windows is about to give the cursor and returns where the cursor must go instead. The
    /// result equals `proposed` when nothing is held.
    pub fn move_cursor(&mut self, proposed: Point, now_ms: u64) -> Point {
        let dx = proposed.x - self.cursor.x;
        let dy = proposed.y - self.cursor.y;
        let held_x = self.attraction.held_x();
        let held_y = self.attraction.held_y();
        // SNAP-071: a held axis builds up the mouse's movement; a free axis follows the cursor, so a free position
        // left far out by an earlier hold starts again from where the window is.
        let x = if held_x { self.free.l + dx } else { self.frame0.l + proposed.x - self.cursor0.x };
        self.free = at_x(&self.free, x);
        let y = if held_y { self.free.t + dy } else { self.frame0.t + proposed.y - self.cursor0.y };
        self.free = at_y(&self.free, y);

        let held = self.attraction.update(&self.free, now_ms);
        // SNAP-072: the cursor goes where the held window position puts it.
        let mut out = Point { x: self.cursor0.x + held.l - self.frame0.l, y: self.cursor0.y + held.t - self.frame0.t };
        // SNAP-073: an axis let go in this move carries on from where the cursor is, so neither the cursor nor the
        // window jumps.
        if held_x && !self.attraction.held_x() {
            out.x = self.cursor.x + dx;
        }
        if held_y && !self.attraction.held_y() {
            out.y = self.cursor.y + dy;
        }
        self.cursor = out;
        out
    }

    /// The attraction, for its events and counters.
    pub fn attraction(&mut self) -> &mut Attraction {
        &mut self.attraction
    }

    /// The window position that matches the cursor returned by the last move.
    pub fn window(&self) -> Rect {
        let x = self.frame0.l + self.cursor.x - self.cursor0.x;
        let y = self.frame0.t + self.cursor.y - self.cursor0.y;
        at_y(&at_x(&self.frame0, x), y)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{AttractionConfig, SideTargets};

    const WORK: Rect = Rect { l: 0, t: 0, r: 1000, b: 800 };
    /// The dragged window: 100x100 at (100, 400), grabbed with the cursor at (150, 410).
    const FRAME: Rect = Rect { l: 100, t: 400, r: 200, b: 500 };
    const GRAB: Point = Point { x: 150, y: 410 };

    fn pt(x: i32, y: i32) -> Point {
        Point { x, y }
    }

    /// A `PointerDrag` with only screen edges as targets and a clock that advances 100 ms per move (never fast).
    /// Windows reports each mouse movement as the cursor's position plus that movement, so `nudge` does the same.
    struct Rig {
        d: PointerDrag,
        cursor: Point,
        t: u64,
    }

    impl Rig {
        fn new() -> Self {
            Self {
                d: PointerDrag::new(
                    Attraction::from_frames(AttractionConfig::default(), &[], WORK, SideTargets::default()),
                    FRAME,
                    GRAB,
                ),
                cursor: GRAB,
                t: 0,
            }
        }

        fn nudge(&mut self, dx: i32, dy: i32) -> Point {
            self.t += 100;
            self.cursor = self.d.move_cursor(pt(self.cursor.x + dx, self.cursor.y + dy), self.t);
            self.cursor
        }
    }

    #[test]
    fn the_cursor_goes_where_the_mouse_sends_it_when_nothing_is_held() {
        // SNAP-072
        let mut r = Rig::new();
        assert_eq!(r.nudge(10, 10), pt(160, 420));
        assert_eq!(r.d.window(), Rect::new(110, 410, 210, 510));
        assert!(!r.d.attraction().held());
    }

    #[test]
    fn pull_in_moves_the_cursor_so_the_window_lands_on_the_line() {
        // SNAP-040, SNAP-072: the left edge would be at 10, so the cursor lands 10 px further left.
        let mut r = Rig::new();
        assert_eq!(r.nudge(-90, 0), pt(50, 410));
        assert_eq!(r.d.window().l, 0);
        assert!(r.d.attraction().held_x());
    }

    #[test]
    fn a_held_axis_keeps_the_cursor_still_while_the_other_axis_slides() {
        // SNAP-022, SNAP-071
        let mut r = Rig::new();
        r.nudge(-90, 0); // held at x = 0, cursor x 50
        assert_eq!(r.nudge(-5, 40), pt(50, 450)); // pushing left and down: only y moves
        assert_eq!(r.nudge(8, 50), pt(50, 500)); // a small wiggle right: x stays
        assert_eq!(r.d.window(), Rect::new(0, 490, 100, 590));
    }

    #[test]
    fn breakaway_lets_go_without_a_jump() {
        // SNAP-041, SNAP-073
        let mut r = Rig::new();
        r.nudge(-90, 0); // free left edge 10, held at 0, cursor x 50
                         // Push left: the free position builds up (0, then -20) while the cursor stays.
        assert_eq!(r.nudge(-10, 0), pt(50, 410));
        assert_eq!(r.nudge(-20, 0), pt(50, 410));
        // The next push takes the free position to -25, past the 24 px breakaway: the cursor moves by this push only.
        assert_eq!(r.nudge(-5, 0), pt(45, 410));
        assert!(!r.d.attraction().held_x());
        assert_eq!(r.d.window().l, -5);
    }

    #[test]
    fn after_breakaway_the_line_waits_until_the_window_leaves_the_zone() {
        // SNAP-042, SNAP-074
        let mut r = Rig::new();
        r.nudge(-90, 0);
        r.nudge(-30, 0);
        r.nudge(-5, 0); // let go, window left edge at -5
        assert!(!r.d.attraction().held_x());
        // Back toward the line: still blocked, the cursor moves freely.
        assert_eq!(r.nudge(10, 0), pt(55, 410));
        assert!(!r.d.attraction().held_x());
        // Out of the zone (left edge 25, more than 16 px away), then back in: held again.
        assert_eq!(r.nudge(20, 0), pt(75, 410));
        assert_eq!(r.nudge(-10, 0), pt(50, 410));
        assert!(r.d.attraction().held_x());
    }

    #[test]
    fn a_corner_holds_the_cursor_on_both_axes() {
        // SNAP-021: left edge 10 from 0 and bottom edge 795 from 800: both pulled.
        let mut r = Rig::new();
        assert_eq!(r.nudge(-90, 295), pt(50, 710));
        assert_eq!(r.nudge(-3, 3), pt(50, 710));
        assert_eq!(r.d.window(), Rect::new(0, 700, 100, 800));
    }

    #[test]
    fn a_fast_move_passes_through_the_line() {
        // SNAP-012
        let mut d = PointerDrag::new(
            Attraction::from_frames(AttractionConfig::default(), &[], WORK, SideTargets::default()),
            FRAME,
            GRAB,
        );
        d.move_cursor(pt(150, 410), 0); // the first move gives the speed its first sample
                                        // 90 px in 10 ms is 9000 px/s: nothing is held.
        assert_eq!(d.move_cursor(pt(60, 410), 10), pt(60, 410));
        assert!(!d.attraction().held());
    }
}
