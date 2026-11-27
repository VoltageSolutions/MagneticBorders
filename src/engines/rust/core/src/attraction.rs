//! Magnetic attraction: the pure per-drag state that pulls a moved window's edges onto screen edges and onto the
//! edges of other windows (SNAP-001 to SNAP-060). No OS types.

use crate::{Edge, Rect};
use std::collections::VecDeque;

/// Length of the span of the drag the speed is measured over (SNAP-013).
const SPEED_WINDOW_MS: u64 = 80;

/// What kind of line an edge is held on.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum TargetKind {
    /// A side of the work area (SNAP-030).
    Screen,
    /// Another window's facing edge, for example left to right (SNAP-033).
    EdgeToEdge,
    /// Another window's edge of the same side, for stacked windows (SNAP-034).
    Aligned,
}

/// Why a held edge was let go.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum ReleaseReason {
    /// The free position moved farther than the breakaway distance from the line (SNAP-041).
    Breakaway,
    /// The target stopped being a target, for example the window slid off its end (SNAP-035).
    TargetGone,
}

/// Tunables of one attraction, already scaled to physical pixels.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct AttractionConfig {
    /// Pull-in distance in pixels (SNAP-010).
    pub pull_in: i32,
    /// Breakaway distance in pixels (SNAP-011). Values below `pull_in` are raised to `pull_in` (SNAP-043).
    pub breakaway: i32,
    /// Highest speed in pixels per second at which an edge may start being held (SNAP-012).
    pub max_speed: f64,
    /// Work-area sides are targets (SNAP-030).
    pub screen_edges: bool,
    /// Other windows are targets (SNAP-032).
    pub windows: bool,
}

impl Default for AttractionConfig {
    fn default() -> Self {
        Self { pull_in: 16, breakaway: 24, max_speed: 600.0, screen_edges: true, windows: true }
    }
}

/// Which sides of the work area are targets for the same side of the dragged window (SNAP-031).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct SideTargets {
    /// Left side.
    pub left: bool,
    /// Top side.
    pub top: bool,
    /// Right side.
    pub right: bool,
    /// Bottom side.
    pub bottom: bool,
}

impl Default for SideTargets {
    fn default() -> Self {
        Self { left: true, top: true, right: true, bottom: true }
    }
}

impl SideTargets {
    /// True when the side of the given edge is a target.
    pub fn allows(&self, e: Edge) -> bool {
        match e {
            Edge::Left => self.left,
            Edge::Top => self.top,
            Edge::Right => self.right,
            Edge::Bottom => self.bottom,
        }
    }
}

/// True when two half-open spans [a0, a1) and [b0, b1) overlap.
fn spans_overlap(a0: i32, a1: i32, b0: i32, b1: i32) -> bool {
    a0 < b1 && b0 < a1
}

/// Decides which sides of `work` are targets: a side is not a target when another work area in `all` touches it
/// (within 1 px) with an overlapping span (SNAP-031). `work` itself, found in `all`, is skipped.
pub fn screen_side_targets(work: &Rect, all: &[Rect]) -> SideTargets {
    let mut s = SideTargets::default();
    for o in all {
        if o == work {
            continue;
        }
        let x_overlap = spans_overlap(work.l, work.r, o.l, o.r);
        let y_overlap = spans_overlap(work.t, work.b, o.t, o.b);
        if y_overlap && (o.r - work.l).abs() <= 1 {
            s.left = false;
        }
        if y_overlap && (o.l - work.r).abs() <= 1 {
            s.right = false;
        }
        if x_overlap && (o.b - work.t).abs() <= 1 {
            s.top = false;
        }
        if x_overlap && (o.t - work.b).abs() <= 1 {
            s.bottom = false;
        }
    }
    s
}

/// A half-open span [lo, hi) along an edge.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct Span {
    /// First coordinate.
    pub lo: i32,
    /// One past the last coordinate.
    pub hi: i32,
}

/// Another window as a target: its frame and, for each edge (indexed by `Edge`), the parts of that edge that are
/// not covered by windows above it in the z-order (SNAP-038).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct TargetWindow {
    /// Visual frame.
    pub frame: Rect,
    /// Visible spans of the left, top, right, and bottom edges, in `Edge` order.
    pub visible: [Vec<Span>; 4],
}

/// Builds a target from a frame and the frames of the windows above it. An edge pixel is covered when it lies
/// inside one of `above` (SNAP-038). With no windows above, every edge is fully visible.
pub fn make_target(frame: &Rect, above: &[Rect]) -> TargetWindow {
    let mut visible: [Vec<Span>; 4] = Default::default();
    for e in Edge::ALL {
        let vertical = e.is_vertical();
        // The edge's own row or column of pixels, inside the frame (r and b are exclusive).
        let at = if e.is_far() { frame.get(e) - 1 } else { frame.get(e) };
        let mut spans =
            vec![if vertical { Span { lo: frame.t, hi: frame.b } } else { Span { lo: frame.l, hi: frame.r } }];
        for o in above {
            let crosses = if vertical { o.l <= at && at < o.r } else { o.t <= at && at < o.b };
            if !crosses {
                continue;
            }
            let cut = if vertical { Span { lo: o.t, hi: o.b } } else { Span { lo: o.l, hi: o.r } };
            let mut next = Vec::new();
            for s in spans {
                if cut.hi <= s.lo || s.hi <= cut.lo {
                    next.push(s);
                    continue;
                }
                if s.lo < cut.lo {
                    next.push(Span { lo: s.lo, hi: cut.lo });
                }
                if cut.hi < s.hi {
                    next.push(Span { lo: cut.hi, hi: s.hi });
                }
            }
            spans = next;
        }
        visible[e as usize] = spans;
    }
    TargetWindow { frame: *frame, visible }
}

/// One engage or release, for the log (SNAP-060).
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct AttractionEvent {
    /// True when an edge started being held, false when it was released.
    pub engaged: bool,
    /// The window edge concerned.
    pub edge: Edge,
    /// The target line.
    pub line: i32,
    /// Kind of the target line.
    pub kind: TargetKind,
    /// Speed in pixels per second at the time of the event.
    pub speed: f64,
    /// Reason of a release; `None` for an engage.
    pub reason: Option<ReleaseReason>,
}

/// A held edge: which edge, the line, what it is held on and, for window targets, which window.
#[derive(Clone, Copy, Debug)]
struct Held {
    edge: Edge,
    line: i32,
    kind: TargetKind,
    /// Index into `others`, or `None` for a screen side.
    window: Option<usize>,
}

/// A possible target found for a free edge.
#[derive(Clone, Copy, Debug)]
struct Candidate {
    edge: Edge,
    line: i32,
    kind: TargetKind,
    window: Option<usize>,
    /// Distance from the free edge to the line.
    dist: i32,
}

/// One free position with its time.
#[derive(Clone, Copy, Debug)]
struct Sample {
    ms: u64,
    l: i32,
    t: i32,
}

/// The two edges of an axis.
fn axis_edges(vertical: bool) -> [Edge; 2] {
    if vertical {
        [Edge::Left, Edge::Right]
    } else {
        [Edge::Top, Edge::Bottom]
    }
}

/// Returns `r` moved by (dx, dy).
fn shifted(r: &Rect, dx: i32, dy: i32) -> Rect {
    Rect::new(r.l + dx, r.t + dy, r.r + dx, r.b + dy)
}

/// True when candidate `a` beats `b`: nearer, or equally near and a window rather than a screen edge (SNAP-037).
fn better(a_dist: i32, a_kind: TargetKind, b_dist: i32, b_kind: TargetKind) -> bool {
    if a_dist != b_dist {
        return a_dist < b_dist;
    }
    a_kind != TargetKind::Screen && b_kind == TargetKind::Screen
}

/// The attraction of one move drag. Feed it every free position with `update`; it returns the held position.
/// The x axis (left and right edges) and the y axis (top and bottom edges) are independent, so a corner holds two
/// lines. Single-threaded and pure: the caller supplies the clock.
#[derive(Debug)]
pub struct Attraction {
    cfg: AttractionConfig,
    others: Vec<TargetWindow>,
    work: Rect,
    sides: SideTargets,
    held_x: Option<Held>,
    held_y: Option<Held>,
    /// Released lines per `Edge` (SNAP-042).
    blocked: [Vec<i32>; 4],
    samples: VecDeque<Sample>,
    speed: f64,
    current: Rect,
    snaps: u64,
    events: Vec<AttractionEvent>,
}

impl Attraction {
    /// Creates the attraction. `others` are the other eligible windows on the same monitor, `work` is the work
    /// area of that monitor and `sides` tells which of its sides are targets.
    pub fn new(config: AttractionConfig, others: Vec<TargetWindow>, work: Rect, sides: SideTargets) -> Self {
        let mut cfg = config;
        cfg.pull_in = cfg.pull_in.max(0);
        cfg.breakaway = cfg.breakaway.max(cfg.pull_in); // SNAP-043
        Self {
            cfg,
            others,
            work,
            sides,
            held_x: None,
            held_y: None,
            blocked: Default::default(),
            samples: VecDeque::new(),
            speed: 0.0,
            current: Rect::default(),
            snaps: 0,
            events: Vec::new(),
        }
    }

    /// Convenience for windows with nothing above them: every edge of `others` is fully visible.
    pub fn from_frames(config: AttractionConfig, others: &[Rect], work: Rect, sides: SideTargets) -> Self {
        Self::new(config, others.iter().map(|f| make_target(f, &[])).collect(), work, sides)
    }

    /// Takes the free position at time `now_ms` and returns the held position: the free position with each held
    /// edge moved onto its line. The size never changes.
    pub fn update(&mut self, free: &Rect, now_ms: u64) -> Rect {
        self.measure(free, now_ms);
        // x targets are checked against the y position the window had (held or free), y targets against the new x
        // position. When y changed, x is checked once more so a slide off a target is seen in the same update.
        let dy = self.held_y.map_or(0, |h| h.line - free.get(h.edge));
        let mut dx = self.resolve(true, &shifted(free, 0, dy), free);
        let new_dy = self.resolve(false, &shifted(free, dx, 0), free);
        if new_dy != dy {
            dx = self.resolve(true, &shifted(free, 0, new_dy), free);
        }
        self.current = shifted(free, dx, new_dy);
        self.current
    }

    /// True when at least one edge is held after the last update.
    pub fn held(&self) -> bool {
        self.held_x.is_some() || self.held_y.is_some()
    }

    /// True when the left or right edge is held after the last update.
    pub fn held_x(&self) -> bool {
        self.held_x.is_some()
    }

    /// True when the top or bottom edge is held after the last update.
    pub fn held_y(&self) -> bool {
        self.held_y.is_some()
    }

    /// The position returned by the last update (an empty rect before the first one).
    pub fn current(&self) -> Rect {
        self.current
    }

    /// Number of times an edge started being held.
    pub fn snaps(&self) -> u64 {
        self.snaps
    }

    /// Speed in pixels per second measured at the last update (SNAP-013).
    pub fn speed(&self) -> f64 {
        self.speed
    }

    /// Returns the engage and release events since the last call and clears them.
    pub fn take_events(&mut self) -> Vec<AttractionEvent> {
        std::mem::take(&mut self.events)
    }

    /// Records the sample and refreshes `speed` (SNAP-013).
    fn measure(&mut self, free: &Rect, now_ms: u64) {
        self.samples.push_back(Sample { ms: now_ms, l: free.l, t: free.t });
        while self.samples.len() > 1 && now_ms.saturating_sub(self.samples[0].ms) > SPEED_WINDOW_MS {
            self.samples.pop_front();
        }
        let (Some(first), Some(last)) = (self.samples.front(), self.samples.back()) else { return };
        if self.samples.len() < 2 || last.ms <= first.ms {
            return; // keep the last measured value
        }
        let dx = f64::from(last.l - first.l);
        let dy = f64::from(last.t - first.t);
        // The samples span at most the speed window plus one step, so the span always fits.
        let ms = f64::from(u32::try_from(last.ms - first.ms).unwrap_or(u32::MAX));
        self.speed = dx.hypot(dy) * 1000.0 / ms;
    }

    /// True when `line` is blocked for `edge`.
    fn blocked(&self, edge: Edge, line: i32) -> bool {
        self.blocked[edge as usize].contains(&line)
    }

    /// True when a window target for `edge` still satisfies its condition (SNAP-033, SNAP-034) and the part of
    /// the other window it depends on is visible near the dragged window (SNAP-038).
    fn window_target_valid(&self, edge: Edge, kind: TargetKind, reference: &Rect, target: &TargetWindow) -> bool {
        let vertical = edge.is_vertical();
        let other = &target.frame;
        // The other axis of a vertical line is y, and the other axis of a horizontal line is x.
        let (d_lo, d_hi) = if vertical { (reference.t, reference.b) } else { (reference.l, reference.r) };
        let (w_lo, w_hi) = if vertical { (other.t, other.b) } else { (other.l, other.r) };
        let pull_in = self.cfg.pull_in;
        // The edge of the other window this target depends on, and the span of the dragged window along it.
        let mut needed = edge.opposite();
        let mut near_lo = d_lo - pull_in;
        let mut near_hi = d_hi + pull_in;
        match kind {
            TargetKind::EdgeToEdge => {
                let gap = (d_lo - w_hi).max(w_lo - d_hi); // negative while they overlap
                if gap > pull_in {
                    return false;
                }
            }
            TargetKind::Aligned => {
                let below = (d_lo - w_hi).abs(); // dragged window below (or right of) the other
                let above = (d_hi - w_lo).abs();
                if below.min(above) > pull_in {
                    return false;
                }
                // Stacked: the facing edge of the other window must be visible where the dragged window is.
                needed = match (below <= above, vertical) {
                    (true, true) => Edge::Bottom,
                    (true, false) => Edge::Right,
                    (false, true) => Edge::Top,
                    (false, false) => Edge::Left,
                };
                near_lo = (if vertical { reference.l } else { reference.t }) - pull_in;
                near_hi = (if vertical { reference.r } else { reference.b }) + pull_in;
            }
            TargetKind::Screen => return true,
        }
        target.visible[needed as usize].iter().any(|s| s.lo < near_hi && near_lo < s.hi)
    }

    /// Finds the best target for a free edge of the axis, or `None`.
    fn best_candidate(&self, vertical: bool, reference: &Rect, free: &Rect) -> Option<Candidate> {
        let mut best: Option<Candidate> = None;
        let mut consider = |edge: Edge, line: i32, kind: TargetKind, window: Option<usize>| {
            let dist = (free.get(edge) - line).abs();
            if dist > self.cfg.pull_in || self.blocked(edge, line) {
                return;
            }
            if best.is_none_or(|b| better(dist, kind, b.dist, b.kind)) {
                best = Some(Candidate { edge, line, kind, window, dist });
            }
        };
        for edge in axis_edges(vertical) {
            if self.cfg.screen_edges && self.sides.allows(edge) {
                consider(edge, self.work.get(edge), TargetKind::Screen, None);
            }
            if !self.cfg.windows {
                continue;
            }
            for (i, w) in self.others.iter().enumerate() {
                if self.window_target_valid(edge, TargetKind::EdgeToEdge, reference, w) {
                    consider(edge, w.frame.get(edge.opposite()), TargetKind::EdgeToEdge, Some(i));
                }
                if self.window_target_valid(edge, TargetKind::Aligned, reference, w) {
                    consider(edge, w.frame.get(edge), TargetKind::Aligned, Some(i));
                }
            }
        }
        best
    }

    /// The held edge of the axis.
    fn held_mut(&mut self, vertical: bool) -> &mut Option<Held> {
        if vertical {
            &mut self.held_x
        } else {
            &mut self.held_y
        }
    }

    /// Releases the held edge of the axis and blocks its line (SNAP-042).
    fn release(&mut self, vertical: bool, reason: ReleaseReason) {
        let Some(h) = self.held_mut(vertical).take() else { return };
        self.blocked[h.edge as usize].push(h.line);
        self.events.push(AttractionEvent {
            engaged: false,
            edge: h.edge,
            line: h.line,
            kind: h.kind,
            speed: self.speed,
            reason: Some(reason),
        });
    }

    /// Resolves one axis against `free` and returns the shift to add to it. `reference` is the position used for
    /// the other-axis conditions of window targets.
    fn resolve(&mut self, vertical: bool, reference: &Rect, free: &Rect) -> i32 {
        // A released line stays blocked until the free edge is more than the pull-in distance away (SNAP-042).
        let pull_in = self.cfg.pull_in;
        for edge in axis_edges(vertical) {
            let coord = free.get(edge);
            self.blocked[edge as usize].retain(|&line| (coord - line).abs() <= pull_in);
        }
        if let Some(h) = *self.held_mut(vertical) {
            let coord = free.get(h.edge);
            if (coord - h.line).abs() > self.cfg.breakaway {
                self.release(vertical, ReleaseReason::Breakaway);
            } else if h.window.is_some_and(|w| !self.window_target_valid(h.edge, h.kind, reference, &self.others[w])) {
                self.release(vertical, ReleaseReason::TargetGone);
            } else {
                return h.line - coord; // SNAP-022: the other axis keeps following the mouse
            }
            // The released line stays blocked (SNAP-042), and nothing else is held on this axis in the same
            // update, so the edge goes free before it can be caught again.
            return 0;
        }
        if self.speed > self.cfg.max_speed {
            return 0; // SNAP-012, SNAP-040
        }
        let Some(c) = self.best_candidate(vertical, reference, free) else { return 0 };
        *self.held_mut(vertical) = Some(Held { edge: c.edge, line: c.line, kind: c.kind, window: c.window });
        self.snaps += 1;
        self.events.push(AttractionEvent {
            engaged: true,
            edge: c.edge,
            line: c.line,
            kind: c.kind,
            speed: self.speed,
            reason: None,
        });
        c.line - free.get(c.edge)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const WORK: Rect = Rect { l: 0, t: 0, r: 1000, b: 800 };
    /// A window at x 300..500, y 300..500.
    const KW: Rect = Rect { l: 300, t: 300, r: 500, b: 500 };

    /// A 100x100 window with its top-left at (l, t).
    fn bx(l: i32, t: i32) -> Rect {
        Rect::new(l, t, l + 100, t + 100)
    }

    /// An `Attraction` on `WORK` plus a clock that advances 100 ms per step, so steps never count as fast.
    struct Rig {
        a: Attraction,
        t: u64,
    }

    impl Rig {
        fn new() -> Self {
            Self::with(&[], AttractionConfig::default(), SideTargets::default())
        }

        fn with(others: &[Rect], cfg: AttractionConfig, sides: SideTargets) -> Self {
            Self { a: Attraction::from_frames(cfg, others, WORK, sides), t: 0 }
        }

        fn windows(others: &[Rect], cfg: AttractionConfig) -> Self {
            Self::with(others, cfg, SideTargets::default())
        }

        fn step(&mut self, free: Rect) -> Rect {
            self.t += 100;
            self.a.update(&free, self.t)
        }
    }

    /// Config with only the given switches on.
    fn only(screen: bool, windows: bool) -> AttractionConfig {
        AttractionConfig { screen_edges: screen, windows, ..AttractionConfig::default() }
    }

    fn lone() -> Attraction {
        Attraction::from_frames(AttractionConfig::default(), &[], WORK, SideTargets::default())
    }

    fn span(lo: i32, hi: i32) -> Span {
        Span { lo, hi }
    }

    #[test]
    fn pulls_an_edge_within_the_pull_in_distance_onto_the_screen_edge() {
        // SNAP-010, SNAP-030, SNAP-040
        let mut r = Rig::new();
        assert_eq!(r.step(bx(10, 400)), Rect::new(0, 400, 100, 500));
        assert!(r.a.held());
        assert_eq!(r.a.snaps(), 1);
        let mut right = Rig::new();
        assert_eq!(right.step(bx(890, 400)), Rect::new(900, 400, 1000, 500));
    }

    #[test]
    fn pull_in_distance_is_inclusive() {
        // SNAP-010
        let mut inside = Rig::new();
        assert_eq!(inside.step(bx(16, 400)), bx(0, 400));
        let mut outside = Rig::new();
        assert_eq!(outside.step(bx(17, 400)), bx(17, 400));
        assert!(!outside.a.held());
    }

    #[test]
    fn snaps_top_and_bottom_to_the_work_area() {
        // SNAP-030
        let mut top = Rig::new();
        assert_eq!(top.step(bx(400, 12)), bx(400, 0));
        let mut bottom = Rig::new();
        assert_eq!(bottom.step(bx(400, 690)), bx(400, 700));
    }

    #[test]
    fn the_size_never_changes() {
        let mut r = Rig::new();
        let out = r.step(Rect::new(10, 12, 410, 312));
        assert_eq!((out.width(), out.height()), (400, 300));
    }

    #[test]
    fn zero_distance_holds_without_moving() {
        let mut r = Rig::new();
        assert_eq!(r.step(bx(0, 400)), bx(0, 400));
        assert!(r.a.held());
    }

    #[test]
    fn a_negative_pull_in_distance_is_treated_as_zero() {
        let c = AttractionConfig { pull_in: -5, ..AttractionConfig::default() };
        let mut on = Rig::windows(&[], c);
        assert_eq!(on.step(bx(0, 400)), bx(0, 400));
        assert!(on.a.held());
        let mut off = Rig::windows(&[], c);
        assert_eq!(off.step(bx(1, 400)), bx(1, 400));
        assert!(!off.a.held());
    }

    #[test]
    fn a_fast_move_does_not_start_a_snap() {
        // SNAP-012, SNAP-040
        let mut a = lone();
        a.update(&bx(500, 400), 0);
        let out = a.update(&bx(10, 400), 10); // 49 000 px/s
        assert_eq!(out, bx(10, 400));
        assert!(!a.held());
        assert!(a.speed() > 600.0);
    }

    #[test]
    fn the_snapping_speed_is_inclusive() {
        // SNAP-012
        let mut at = lone();
        at.update(&bx(40, 400), 0);
        assert_eq!(at.update(&bx(10, 400), 50), bx(0, 400)); // exactly 600 px/s
        let mut over = lone();
        over.update(&bx(41, 400), 0);
        assert_eq!(over.update(&bx(10, 400), 50), bx(10, 400)); // 620 px/s
    }

    #[test]
    fn the_speed_limit_scales_with_the_config() {
        // SNAP-012
        let c = AttractionConfig { max_speed: 1000.0, ..AttractionConfig::default() };
        let mut a = Attraction::from_frames(c, &[], WORK, SideTargets::default());
        a.update(&bx(50, 400), 0);
        assert_eq!(a.update(&bx(10, 400), 50), bx(0, 400)); // 800 px/s is within 1000
    }

    #[test]
    fn a_speed_limit_does_not_release_a_held_edge() {
        // SNAP-014
        let mut r = Rig::new();
        assert_eq!(r.step(bx(10, 400)), bx(0, 400));
        let out = r.a.update(&bx(5, 450), r.t + 10); // 5 000 px/s
        assert_eq!(out, bx(0, 450));
        assert!(r.a.held());
    }

    #[test]
    fn the_speed_is_zero_at_the_start_and_measured_over_the_last_80_ms() {
        // SNAP-013
        let mut a = lone();
        assert!(a.speed().abs() < f64::EPSILON);
        a.update(&bx(500, 400), 1000);
        assert!(a.speed().abs() < f64::EPSILON); // a single position
        a.update(&bx(510, 400), 1010);
        assert!((a.speed() - 1000.0).abs() < 1e-9);
        a.update(&bx(520, 430), 1020); // from (500,400) over 20 ms: hypot(20,30) / 0.02
        assert!((a.speed() - 1802.775).abs() < 0.01);
    }

    #[test]
    fn with_fewer_than_two_positions_the_last_speed_is_kept() {
        // SNAP-013
        let mut a = lone();
        a.update(&bx(500, 400), 1000);
        a.update(&bx(510, 400), 1010);
        assert!((a.speed() - 1000.0).abs() < 1e-9);
        a.update(&bx(512, 400), 1500); // the earlier positions are older than 80 ms
        assert!((a.speed() - 1000.0).abs() < 1e-9);
        a.update(&bx(512, 400), 1500); // same millisecond: no time has passed
        assert!((a.speed() - 1000.0).abs() < 1e-9);
    }

    #[test]
    fn a_held_edge_slides_along_the_line() {
        // SNAP-022
        let mut r = Rig::new();
        assert_eq!(r.step(bx(10, 100)), bx(0, 100));
        assert_eq!(r.step(bx(5, 200)), bx(0, 200));
        assert_eq!(r.step(bx(8, 300)), bx(0, 300));
    }

    #[test]
    fn breakaway_releases_past_the_breakaway_distance_and_jumps_back() {
        // SNAP-041
        let mut r = Rig::new();
        assert_eq!(r.step(bx(10, 400)), bx(0, 400));
        assert_eq!(r.step(bx(-24, 400)), bx(0, 400)); // exactly 24 away: still held
        assert_eq!(r.step(bx(-25, 400)), bx(-25, 400)); // the window is back under the mouse
        assert!(!r.a.held());
        let events = r.a.take_events();
        assert_eq!(events.len(), 2);
        assert!(!events[1].engaged);
        assert_eq!((events[1].edge, events[1].line), (Edge::Left, 0));
        assert_eq!(events[1].reason, Some(ReleaseReason::Breakaway));
    }

    #[test]
    fn a_breakaway_below_the_pull_in_distance_uses_the_pull_in_distance() {
        // SNAP-043
        let c = AttractionConfig { breakaway: 5, ..AttractionConfig::default() };
        let mut r = Rig::windows(&[], c);
        assert_eq!(r.step(bx(10, 400)), bx(0, 400));
        assert_eq!(r.step(bx(-16, 400)), bx(0, 400)); // 16 is the pull-in distance
        assert_eq!(r.step(bx(-17, 400)), bx(-17, 400));
    }

    #[test]
    fn a_released_edge_can_be_held_again_after_leaving_the_zone() {
        // SNAP-042
        let mut r = Rig::new();
        r.step(bx(10, 400));
        assert_eq!(r.step(bx(-30, 400)), bx(-30, 400)); // breakaway
        assert_eq!(r.step(bx(-30, 400)), bx(-30, 400)); // a later position out of the zone unblocks the line
        assert_eq!(r.step(bx(12, 400)), bx(0, 400));
        assert_eq!(r.a.snaps(), 2);
    }

    #[test]
    fn a_released_line_stays_blocked_when_the_next_position_is_back_in_the_zone() {
        // SNAP-042
        let mut r = Rig::new();
        r.step(bx(10, 400));
        assert_eq!(r.step(bx(-30, 400)), bx(-30, 400)); // breakaway
        assert_eq!(r.step(bx(5, 400)), bx(5, 400)); // straight back into the zone: still blocked
        assert_eq!(r.a.snaps(), 1);
    }

    #[test]
    fn a_slide_off_the_end_of_a_window_releases_and_blocks_the_line_until_far_away() {
        // SNAP-035, SNAP-042
        let mut r = Rig::windows(&[KW], only(false, true));
        assert_eq!(r.step(bx(505, 320)), bx(500, 320)); // left edge on the right side of KW
        assert_eq!(r.step(bx(505, 330)), bx(500, 330)); // sliding along the line
                                                        // Slide until the vertical gap to KW exceeds the pull-in distance.
        assert_eq!(r.step(bx(505, 530)), bx(505, 530));
        let events = r.a.take_events();
        let last = events.last().unwrap();
        assert!(!last.engaged);
        assert_eq!(last.reason, Some(ReleaseReason::TargetGone));
        // Back in range with the edge still near the line: the line stays blocked.
        assert_eq!(r.step(bx(505, 510)).l, 505);
        // Farther than the pull-in distance clears the block.
        assert_eq!(r.step(bx(520, 510)).l, 520);
        assert_eq!(r.step(bx(505, 510)).l, 500);
    }

    #[test]
    fn a_corner_holds_two_axes_and_each_releases_on_its_own() {
        // SNAP-021
        let mut r = Rig::new();
        assert_eq!(r.step(bx(10, 12)), bx(0, 0));
        assert_eq!(r.a.snaps(), 2);
        assert_eq!(r.step(bx(10, -30)), bx(0, -30)); // only y breaks away
        assert!(r.a.held());
        assert_eq!(r.step(bx(-30, -30)), bx(-30, -30));
        assert!(!r.a.held());
    }

    #[test]
    fn edge_to_edge_left_of_a_window() {
        // SNAP-033
        let mut r = Rig::windows(&[KW], only(false, true));
        assert_eq!(r.step(bx(205, 350)), bx(200, 350));
        let events = r.a.take_events();
        assert_eq!(events.len(), 1);
        assert_eq!(events[0].edge, Edge::Right);
        assert_eq!(events[0].line, 300);
        assert_eq!(events[0].kind, TargetKind::EdgeToEdge);
    }

    #[test]
    fn edge_to_edge_right_of_above_and_below_a_window() {
        // SNAP-033
        let mut right = Rig::windows(&[KW], only(false, true));
        assert_eq!(right.step(bx(505, 350)), bx(500, 350));
        let mut above = Rig::windows(&[KW], only(false, true));
        assert_eq!(above.step(bx(350, 195)), bx(350, 200));
        let mut below = Rig::windows(&[KW], only(false, true));
        assert_eq!(below.step(bx(350, 505)), bx(350, 500));
    }

    #[test]
    fn edge_to_edge_needs_overlap_or_being_within_pull_in_of_overlapping() {
        // SNAP-033: 10 px below KW is within pull-in of overlapping, so its right side is a target.
        let mut near = Rig::windows(&[KW], only(false, true));
        assert_eq!(near.step(bx(505, 510)), bx(500, 500));
        let mut far = Rig::windows(&[KW], only(false, true));
        assert_eq!(far.step(bx(505, 530)), bx(505, 530)); // 30 px below: no target at all
        assert!(!far.a.held());
    }

    #[test]
    fn aligned_needs_stacked_windows() {
        // SNAP-034
        let mut stacked = Rig::windows(&[KW], only(false, true));
        assert_eq!(stacked.step(bx(305, 510)), bx(300, 500));
        let events = stacked.a.take_events();
        assert_eq!(events[0].edge, Edge::Left);
        assert_eq!(events[0].kind, TargetKind::Aligned);
        let mut apart = Rig::windows(&[KW], only(false, true));
        assert_eq!(apart.step(bx(305, 530)), bx(305, 530));
        let mut right = Rig::windows(&[KW], only(false, true));
        assert_eq!(right.step(bx(405, 510)), bx(400, 500)); // right to right: 505 -> 500
    }

    #[test]
    fn overlapping_windows_have_no_inner_targets() {
        // SNAP-036
        let mut r = Rig::windows(&[KW], only(false, true));
        assert_eq!(r.step(bx(305, 350)), bx(305, 350));
        assert!(!r.a.held());
        assert_eq!(r.step(bx(395, 350)), bx(395, 350)); // right 495 against KW.r 500 as well
        assert!(!r.a.held());
    }

    #[test]
    fn sliding_off_the_end_of_an_aligned_target_releases() {
        // SNAP-035
        let mut r = Rig::windows(&[KW], only(false, true));
        assert_eq!(r.step(bx(305, 510)), bx(300, 500));
        assert_eq!(r.step(bx(305, 560)), bx(305, 560)); // the gap grew to 60
        let gone =
            r.a.take_events()
                .iter()
                .any(|e| !e.engaged && e.edge == Edge::Left && e.reason == Some(ReleaseReason::TargetGone));
        assert!(gone);
    }

    #[test]
    fn a_window_wins_a_tie_against_a_screen_edge() {
        // SNAP-037: the window's right side is at x = 10, the screen edge at x = 0.
        let w = Rect::new(-90, 100, 10, 300);
        let mut r = Rig::windows(&[w], AttractionConfig::default());
        assert_eq!(r.step(bx(5, 150)), bx(10, 150));
        assert_eq!(r.a.take_events()[0].kind, TargetKind::EdgeToEdge);
    }

    #[test]
    fn the_nearest_target_wins() {
        // SNAP-037
        let w = Rect::new(-90, 100, 10, 300);
        let mut screen = Rig::windows(&[w], AttractionConfig::default());
        assert_eq!(screen.step(bx(4, 150)), bx(0, 150)); // the screen edge is 4 away, the window 6
        let w3 = Rect::new(-90, 350, 14, 500);
        let mut windows = Rig::windows(&[w, w3], only(false, true));
        assert_eq!(windows.step(bx(8, 280)), bx(10, 280));
    }

    #[test]
    fn a_side_on_another_monitor_is_not_a_target() {
        // SNAP-031
        let primary = Rect::new(0, 0, 1920, 1080);
        let s = screen_side_targets(&primary, &[primary, Rect::new(1920, 0, 3840, 1080)]);
        assert!(s.left && s.top && s.bottom);
        assert!(!s.right);
        assert!(!s.allows(Edge::Right));
        assert!(s.allows(Edge::Left) && s.allows(Edge::Top) && s.allows(Edge::Bottom));
    }

    #[test]
    fn every_side_can_touch_another_monitor() {
        // SNAP-031
        let work = Rect::new(1000, 1000, 2000, 2000);
        let all = [
            work,
            Rect::new(0, 1000, 1000, 2000),
            Rect::new(2000, 1000, 3000, 2000),
            Rect::new(1000, 0, 2000, 1000),
            Rect::new(1000, 2000, 2000, 3000),
        ];
        let s = screen_side_targets(&work, &all);
        assert!(!s.left && !s.right && !s.top && !s.bottom);
    }

    #[test]
    fn a_side_is_a_target_when_the_other_monitor_only_touches_at_a_corner_or_is_offset() {
        // SNAP-031
        let primary = Rect::new(0, 0, 1920, 1080);
        let all = [primary, Rect::new(1920, 1080, 3840, 2160), Rect::new(2500, 0, 3000, 1080)];
        let s = screen_side_targets(&primary, &all);
        assert!(s.left && s.top && s.right && s.bottom);
        assert!(screen_side_targets(&primary, &[primary]).right);
    }

    #[test]
    fn an_untargeted_side_does_not_hold() {
        // SNAP-031
        let sides = SideTargets { right: false, ..SideTargets::default() };
        let mut r = Rig::with(&[], AttractionConfig::default(), sides);
        assert_eq!(r.step(bx(895, 400)), bx(895, 400));
        assert!(!r.a.held());
        assert_eq!(r.step(bx(10, 400)), bx(0, 400));
    }

    #[test]
    fn screen_edges_off_removes_the_screen_targets() {
        // SNAP-030
        let mut r = Rig::windows(&[], only(false, true));
        assert_eq!(r.step(bx(10, 400)), bx(10, 400));
        assert!(!r.a.held());
    }

    #[test]
    fn windows_off_removes_the_window_targets() {
        // SNAP-032
        let mut r = Rig::windows(&[KW], only(true, false));
        assert_eq!(r.step(bx(205, 350)), bx(205, 350));
        assert!(!r.a.held());
        assert_eq!(r.step(bx(10, 350)), bx(0, 350));
    }

    #[test]
    fn events_carry_edge_line_kind_and_speed_and_are_cleared() {
        // SNAP-060
        let mut r = Rig::new();
        r.step(bx(10, 400));
        let events = r.a.take_events();
        assert_eq!(events.len(), 1);
        assert!(events[0].engaged);
        assert_eq!((events[0].edge, events[0].line, events[0].kind), (Edge::Left, 0, TargetKind::Screen));
        assert!(events[0].speed.abs() < f64::EPSILON);
        assert_eq!(events[0].reason, None);
        assert_eq!(r.a.take_events().len(), 0);
    }

    #[test]
    fn current_is_the_last_returned_position() {
        let mut r = Rig::new();
        assert_eq!(r.a.current(), Rect::default());
        let out = r.step(bx(10, 400));
        assert_eq!(r.a.current(), out);
    }

    #[test]
    fn only_one_edge_per_axis_is_held_and_it_stays_until_released() {
        // SNAP-021, SNAP-040
        let w = Rect::new(109, 300, 209, 500);
        let mut r = Rig::windows(&[w], AttractionConfig::default());
        assert_eq!(r.step(bx(4, 350)), bx(0, 350)); // left to the screen edge (4) beats right to w.l (5)
        assert_eq!(r.a.snaps(), 1);
        assert_eq!(r.step(bx(6, 350)), bx(0, 350)); // right edge is now 3 from w.l, but left stays held
        assert_eq!(r.a.snaps(), 1);
    }

    #[test]
    fn edges_are_fully_visible_with_nothing_above() {
        // SNAP-038
        let t = make_target(&KW, &[]);
        for e in Edge::ALL {
            assert_eq!(t.visible[e as usize], vec![span(300, 500)]);
        }
    }

    #[test]
    fn a_window_above_cuts_the_edges_it_covers() {
        // SNAP-038: covers the middle of the right edge (column 499) and nothing else.
        let t = make_target(&KW, &[Rect::new(450, 350, 700, 400)]);
        assert_eq!(t.visible[Edge::Right as usize], vec![span(300, 350), span(400, 500)]);
        assert_eq!(t.visible[Edge::Left as usize], vec![span(300, 500)]);
        assert_eq!(t.visible[Edge::Top as usize], vec![span(300, 500)]);
    }

    #[test]
    fn a_window_that_only_touches_the_edge_does_not_cover_it() {
        // SNAP-038: starts at x 500, the first column right of the window; the right edge (column 499) stays visible.
        let t = make_target(&KW, &[Rect::new(500, 300, 700, 500)]);
        assert_eq!(t.visible[Edge::Right as usize], vec![span(300, 500)]);
    }

    #[test]
    fn a_fully_covered_window_has_no_visible_edges() {
        // SNAP-038
        let t = make_target(&KW, &[WORK]);
        assert!(t.visible.iter().all(Vec::is_empty));
    }

    #[test]
    fn a_hidden_window_does_not_attract() {
        // SNAP-038
        let target = make_target(&KW, &[Rect::new(200, 200, 700, 700)]);
        let mut a = Attraction::new(only(false, true), vec![target], WORK, SideTargets::default());
        // The left edge is 10 px right of the hidden window's right edge.
        assert_eq!(a.update(&bx(510, 350), 100), bx(510, 350));
        assert!(!a.held());
    }

    #[test]
    fn only_the_visible_part_of_an_edge_attracts() {
        // SNAP-038: the window's right edge is covered from y 300 to 450; only 450..500 shows.
        let cover = [Rect::new(400, 280, 600, 450)];
        let mut high = Attraction::new(only(false, true), vec![make_target(&KW, &cover)], WORK, SideTargets::default());
        // Next to the covered part (y 300..400, 16 px pull-in reaches 416): no snap.
        assert_eq!(high.update(&bx(510, 300), 100), bx(510, 300));
        assert!(!high.held());
        let mut low = Attraction::new(only(false, true), vec![make_target(&KW, &cover)], WORK, SideTargets::default());
        // Next to the visible part (y 420..520): snaps.
        assert_eq!(low.update(&bx(510, 420), 100), bx(500, 420));
        assert!(low.held());
    }

    #[test]
    fn aligned_needs_the_facing_edge_visible() {
        // SNAP-034, SNAP-038: the dragged window is stacked below KW, its left edge 5 px off KW's left edge.
        let free = Rect::new(305, 500, 405, 600);
        let mut visible = Attraction::new(only(false, true), vec![make_target(&KW, &[])], WORK, SideTargets::default());
        assert_eq!(visible.update(&free, 100).l, 300);
        // Cover KW's bottom edge: no longer stacked against anything visible.
        let cover = [Rect::new(250, 450, 550, 520)];
        let mut hidden =
            Attraction::new(only(false, true), vec![make_target(&KW, &cover)], WORK, SideTargets::default());
        assert_eq!(hidden.update(&free, 100).l, 305);
    }
}
