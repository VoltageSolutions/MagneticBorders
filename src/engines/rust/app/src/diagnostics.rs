//! Text of the verbose `diag:` lines that the drag controller writes. No OS types, so the formatting is unit-testable
//! on any platform. The same text is produced by the C++ and C# engines (`docs/spec/engine.md`).

use mb_core::{Point, Rect};

/// Formats a rectangle as `l,t,r,b`.
#[must_use]
pub fn rect_text(r: Rect) -> String {
    format!("{},{},{},{}", r.l, r.t, r.r, r.b)
}

/// Formats a point as `x,y`.
#[must_use]
pub fn point_text(p: Point) -> String {
    format!("{},{}", p.x, p.y)
}

/// Line for a mouse movement that the hook changed: the position Windows proposed, the cursor position the engine set
/// instead, and the window frame that cursor position gives.
#[must_use]
pub fn pointer_hold_line(proposed: Point, cursor: Point, window: Rect) -> String {
    format!(
        "diag: pointer hold proposed={} cursor={} window={}",
        point_text(proposed),
        point_text(cursor),
        rect_text(window)
    )
}

/// Line for a button release seen by the hook: the position of the release event and the cursor position the engine
/// last set or let through.
#[must_use]
pub fn pointer_release_line(cursor: Point, expected: Point) -> String {
    format!("diag: pointer release cursor={} expected={}", point_text(cursor), point_text(expected))
}

/// Line for the decision at the end of a pointer-mode move: the window's frame (`unavailable` when unreadable), the
/// held position (`none` when no axis was held), and the action, `applied`, `none`, or `skipped-size`.
#[must_use]
pub fn move_correction_line(id: u64, frame: Option<Rect>, held: Option<Rect>, action: &str) -> String {
    format!(
        "diag: move correction id={id} frame={} held={} action={action}",
        frame.map_or_else(|| "unavailable".to_string(), rect_text),
        held.map_or_else(|| "none".to_string(), rect_text)
    )
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn rect_and_point_text() {
        assert_eq!(rect_text(Rect::new(1, -2, 30, 40)), "1,-2,30,40");
        assert_eq!(point_text(Point { x: -5, y: 7 }), "-5,7");
    }

    #[test]
    fn pointer_lines_text() {
        assert_eq!(
            pointer_hold_line(Point { x: 395, y: 260 }, Point { x: 400, y: 260 }, Rect::new(300, 250, 500, 450)),
            "diag: pointer hold proposed=395,260 cursor=400,260 window=300,250,500,450"
        );
        assert_eq!(
            pointer_release_line(Point { x: 398, y: -3 }, Point { x: 400, y: 260 }),
            "diag: pointer release cursor=398,-3 expected=400,260"
        );
    }

    #[test]
    fn move_correction_line_text() {
        let held = Rect::new(300, 250, 500, 450);
        assert_eq!(
            move_correction_line(12, Some(Rect::new(304, 247, 504, 447)), Some(held), "applied"),
            "diag: move correction id=12 frame=304,247,504,447 held=300,250,500,450 action=applied"
        );
        assert_eq!(
            move_correction_line(12, None, None, "none"),
            "diag: move correction id=12 frame=unavailable held=none action=none"
        );
    }
}
