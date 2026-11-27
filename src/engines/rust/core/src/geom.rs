//! Value types of the domain: `Rect`, `Edge`, `WinInfo` and `Config`. All coordinates are physical pixels.

/// Physical-pixel point, for example a cursor position.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Point {
    /// Horizontal coordinate.
    pub x: i32,
    /// Vertical coordinate.
    pub y: i32,
}

/// Physical-pixel rectangle; r/b exclusive.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub struct Rect {
    /// Left edge (inclusive).
    pub l: i32,
    /// Top edge (inclusive).
    pub t: i32,
    /// Right edge (exclusive).
    pub r: i32,
    /// Bottom edge (exclusive).
    pub b: i32,
}

/// One of the four sides of a `Rect`; names the side of a window a seam is built on.
#[derive(Clone, Copy, Debug, PartialEq, Eq, Hash)]
pub enum Edge {
    /// The left side.
    Left,
    /// The top side.
    Top,
    /// The right side.
    Right,
    /// The bottom side.
    Bottom,
}

impl Edge {
    /// All four edges, in the order the controller tries them.
    pub const ALL: [Edge; 4] = [Edge::Left, Edge::Top, Edge::Right, Edge::Bottom];

    /// The facing edge: Left <-> Right, Top <-> Bottom.
    #[must_use]
    pub fn opposite(self) -> Edge {
        match self {
            Edge::Left => Edge::Right,
            Edge::Right => Edge::Left,
            Edge::Top => Edge::Bottom,
            Edge::Bottom => Edge::Top,
        }
    }

    /// Left/Right edges form vertical lines (x-seams).
    pub fn is_vertical(self) -> bool {
        matches!(self, Edge::Left | Edge::Right)
    }

    /// True for Right/Bottom (the far edge of a rect).
    pub fn is_far(self) -> bool {
        matches!(self, Edge::Right | Edge::Bottom)
    }
}

impl Rect {
    /// Builds a rect from its four edges.
    pub fn new(l: i32, t: i32, r: i32, b: i32) -> Rect {
        Rect { l, t, r, b }
    }

    /// Builds a rect from origin and size.
    pub fn from_xywh(x: i32, y: i32, w: i32, h: i32) -> Rect {
        Rect::new(x, y, x + w, y + h)
    }

    /// Horizontal extent in pixels.
    pub fn width(&self) -> i32 {
        self.r - self.l
    }

    /// Vertical extent in pixels.
    pub fn height(&self) -> i32 {
        self.b - self.t
    }

    /// The coordinate of side `e`.
    pub fn get(&self, e: Edge) -> i32 {
        match e {
            Edge::Left => self.l,
            Edge::Top => self.t,
            Edge::Right => self.r,
            Edge::Bottom => self.b,
        }
    }

    /// Copy of the rect with side `e` set to `v`.
    #[must_use]
    pub fn with(mut self, e: Edge, v: i32) -> Rect {
        match e {
            Edge::Left => self.l = v,
            Edge::Top => self.t = v,
            Edge::Right => self.r = v,
            Edge::Bottom => self.b = v,
        }
        self
    }

    /// Span perpendicular to a seam on `e`: [t,b) for x-seams, [l,r) for y-seams.
    pub fn span_perp(&self, e: Edge) -> (i32, i32) {
        if e.is_vertical() {
            (self.t, self.b)
        } else {
            (self.l, self.r)
        }
    }
}

/// One eligible window as the domain sees it. Produced by a `WindowInventory`, consumed by `build_seam`.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct WinInfo {
    /// Opaque window id (the HWND value on Windows, an index in fixtures).
    pub id: u64,
    /// Visible frame in physical pixels.
    pub frame: Rect,
    /// Minimum width the window accepts.
    pub min_w: i32,
    /// Minimum height the window accepts.
    pub min_h: i32,
}

/// Tuning for seam detection.
#[derive(Clone, Copy, Debug)]
pub struct Config {
    /// Largest gap, in pixels, at which two edges still count as touching.
    pub glue_tolerance: i32,
}

impl Default for Config {
    fn default() -> Self {
        Config { glue_tolerance: 12 }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn opposite_edges_pair_up() {
        assert_eq!(Edge::Left.opposite(), Edge::Right);
        assert_eq!(Edge::Right.opposite(), Edge::Left);
        assert_eq!(Edge::Top.opposite(), Edge::Bottom);
        assert_eq!(Edge::Bottom.opposite(), Edge::Top);
        for e in Edge::ALL {
            assert_eq!(e.opposite().opposite(), e);
        }
    }

    #[test]
    fn left_and_right_are_vertical_and_far_edges_are_right_bottom() {
        assert!(Edge::Left.is_vertical() && Edge::Right.is_vertical());
        assert!(!Edge::Top.is_vertical() && !Edge::Bottom.is_vertical());
        assert!(Edge::Right.is_far() && Edge::Bottom.is_far());
        assert!(!Edge::Left.is_far() && !Edge::Top.is_far());
    }

    #[test]
    fn rect_constructors_agree() {
        let r = Rect::from_xywh(10, 20, 300, 400);
        assert_eq!(r, Rect::new(10, 20, 310, 420));
        assert_eq!((r.width(), r.height()), (300, 400));
    }

    #[test]
    fn get_and_with_address_each_side() {
        let r = Rect::new(1, 2, 3, 4);
        assert_eq!([Edge::Left, Edge::Top, Edge::Right, Edge::Bottom].map(|e| r.get(e)), [1, 2, 3, 4]);
        assert_eq!(r.with(Edge::Left, 9), Rect::new(9, 2, 3, 4));
        assert_eq!(r.with(Edge::Top, 9), Rect::new(1, 9, 3, 4));
        assert_eq!(r.with(Edge::Right, 9), Rect::new(1, 2, 9, 4));
        assert_eq!(r.with(Edge::Bottom, 9), Rect::new(1, 2, 3, 9));
    }

    #[test]
    fn span_perp_is_vertical_extent_for_vertical_seams() {
        let r = Rect::new(1, 2, 30, 40);
        assert_eq!(r.span_perp(Edge::Right), (2, 40));
        assert_eq!(r.span_perp(Edge::Left), (2, 40));
        assert_eq!(r.span_perp(Edge::Top), (1, 30));
        assert_eq!(r.span_perp(Edge::Bottom), (1, 30));
    }

    #[test]
    fn default_glue_tolerance_is_12_pixels() {
        assert_eq!(Config::default().glue_tolerance, 12);
    }
}
