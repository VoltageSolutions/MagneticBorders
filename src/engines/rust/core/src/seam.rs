//! Seam construction (`build_seam`) and propagation (`propagate`): which windows move with a dragged edge, and where.

use crate::{Config, Edge, Rect, WinInfo};

/// Which side of the seam a member sits on, relative to the dragged window.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Side {
    /// The member's same-named edge lies on the seam (it shares the dragged window's side).
    Same,
    /// The member's opposite edge lies on the seam (it faces the dragged window).
    Opposite,
}

/// A window glued to the dragged edge.
#[derive(Clone, Debug)]
pub struct Member {
    /// Window id.
    pub id: u64,
    /// Which of its edges sits on the seam.
    pub side: Side,
    /// `member_edge` - X (signed); the gap the member keeps while the seam moves.
    pub d: i32,
    /// Frame when the seam was built.
    pub frame: Rect,
    /// Minimum width used when clamping.
    pub min_w: i32,
    /// Minimum height used when clamping.
    pub min_h: i32,
}

/// The set of windows that move together with one dragged edge. Built once per edge at drag start.
#[derive(Clone, Debug)]
pub struct Seam {
    /// The dragged window's edge the seam is built on.
    pub edge: Edge,
    /// Seam position (coordinate of that edge) when built.
    pub x: i32,
    /// Windows glued to the seam, in discovery order.
    pub members: Vec<Member>,
}

/// Result of `propagate`: the new member frames for one seam position.
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Plan {
    /// `(id, new frame)` for every member; the dragged window is not included.
    pub moves: Vec<(u64, Rect)>,
    /// Seam position after clamping to every member's minimum size.
    pub clamped_pos: i32,
}

impl Seam {
    /// Which edge of `m` moves with the seam.
    pub fn moved_edge(&self, m: &Member) -> Edge {
        match m.side {
            Side::Same => self.edge,
            Side::Opposite => self.edge.opposite(),
        }
    }

    /// Ids of all members.
    pub fn ids(&self) -> Vec<u64> {
        self.members.iter().map(|m| m.id).collect()
    }
}

fn gap((a0, a1): (i32, i32), (b0, b1): (i32, i32)) -> i32 {
    (a0.max(b0) - a1.min(b1)).max(0)
}

/// Finds the windows glued to `dragged_id`'s `edge`, or `None` when the edge is on the work-area border
/// or nothing touches it. Members within `glue_tolerance` of the seam are chained transitively along it.
/// Called by `DragController` at classification and by the fixture runner.
pub fn build_seam(windows: &[WinInfo], dragged_id: u64, edge: Edge, work_area: Rect, cfg: Config) -> Option<Seam> {
    let tol = cfg.glue_tolerance;
    let dragged = windows.iter().find(|w| w.id == dragged_id)?;
    let x = dragged.frame.get(edge);
    if (x - work_area.get(edge)).abs() <= tol {
        return None;
    }
    let opp = edge.opposite();
    let mut cands: Vec<Member> = windows
        .iter()
        .filter(|w| w.id != dragged_id)
        .filter_map(|w| {
            let (side, d) = if (w.frame.get(opp) - x).abs() <= tol {
                (Side::Opposite, w.frame.get(opp) - x)
            } else if (w.frame.get(edge) - x).abs() <= tol {
                (Side::Same, w.frame.get(edge) - x)
            } else {
                return None;
            };
            Some(Member { id: w.id, side, d, frame: w.frame, min_w: w.min_w, min_h: w.min_h })
        })
        .collect();
    let mut span = dragged.frame.span_perp(edge);
    let mut members = Vec::new();
    while let Some(i) = cands.iter().position(|c| gap(c.frame.span_perp(edge), span) <= tol) {
        let c = cands.remove(i);
        let s = c.frame.span_perp(edge);
        span = (span.0.min(s.0), span.1.max(s.1));
        members.push(c);
    }
    if members.is_empty() {
        None
    } else {
        Some(Seam { edge, x, members })
    }
}

/// Computes member frames for a seam moved to `new_pos`, clamped so no member drops below its minimum size.
/// Called on every throttled location change and again at drag end.
pub fn propagate(seam: &Seam, new_pos: i32) -> Plan {
    let (mut lo, mut hi) = (i32::MIN, i32::MAX);
    for m in &seam.members {
        let e = seam.moved_edge(m);
        let min = if seam.edge.is_vertical() { m.min_w } else { m.min_h };
        if e.is_far() {
            lo = lo.max(m.frame.get(e.opposite()) + min - m.d);
        } else {
            hi = hi.min(m.frame.get(e.opposite()) - min - m.d);
        }
    }
    let clamped_pos = if lo > hi { seam.x } else { new_pos.clamp(lo, hi) };
    let moves = seam.members.iter().map(|m| (m.id, m.frame.with(seam.moved_edge(m), clamped_pos + m.d))).collect();
    Plan { moves, clamped_pos }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn w(id: u64, x: i32, y: i32, ww: i32, h: i32) -> WinInfo {
        WinInfo { id, frame: Rect::from_xywh(x, y, ww, h), min_w: 1, min_h: 1 }
    }
    const WA: Rect = Rect { l: 0, t: 0, r: 3440, b: 1440 };

    #[test]
    fn glues_neighbour_and_keeps_gap() {
        let ws = [w(1, 0, 0, 1700, 1440), w(2, 1720, 0, 1720, 1440)];
        let s = build_seam(&ws, 1, Edge::Right, WA, Config { glue_tolerance: 24 }).unwrap();
        let p = propagate(&s, 1800);
        assert_eq!(p.moves, vec![(2, Rect::from_xywh(1820, 0, 1620, 1440))]);
    }

    #[test]
    fn screen_edge_is_not_a_seam() {
        let ws = [w(1, 1720, 0, 1720, 1440), w(2, 0, 0, 1720, 1440)];
        assert!(build_seam(&ws, 1, Edge::Right, WA, Config::default()).is_none());
    }

    #[test]
    fn wide_gap_not_glued() {
        let ws = [w(1, 0, 0, 1700, 1440), w(2, 1740, 0, 1700, 1440)];
        assert!(build_seam(&ws, 1, Edge::Right, WA, Config::default()).is_none());
    }

    #[test]
    fn clamps_to_min_width() {
        let mut b = w(2, 1720, 0, 1720, 1440);
        b.min_w = 1600;
        let ws = [w(1, 0, 0, 1720, 1440), b];
        let s = build_seam(&ws, 1, Edge::Right, WA, Config::default()).unwrap();
        assert_eq!(propagate(&s, 2020).clamped_pos, 1840);
    }

    #[test]
    fn closure_pulls_in_chained_windows() {
        let ws = [w(1, 0, 0, 1720, 720), w(2, 1720, 0, 1720, 720), w(3, 0, 720, 1720, 720), w(4, 1720, 720, 1720, 720)];
        let s = build_seam(&ws, 1, Edge::Right, WA, Config::default()).unwrap();
        assert_eq!(s.members.len(), 3);
    }

    fn wm(id: u64, x: i32, y: i32, ww: i32, h: i32, min_w: i32, min_h: i32) -> WinInfo {
        WinInfo { id, frame: Rect::from_xywh(x, y, ww, h), min_w, min_h }
    }

    #[test]
    fn window_sharing_the_dragged_edge_moves_with_it() {
        let ws = [w(1, 0, 0, 1720, 720), w(3, 0, 720, 1720, 720)];
        let s = build_seam(&ws, 1, Edge::Right, WA, Config::default()).unwrap();
        assert_eq!(s.members[0].side, Side::Same);
        assert_eq!(s.moved_edge(&s.members[0]), Edge::Right);
        let p = propagate(&s, 1800);
        assert_eq!(p.moves, vec![(3, Rect::from_xywh(0, 720, 1800, 720))]);
    }

    #[test]
    fn facing_window_moves_its_opposite_edge() {
        let ws = [w(1, 0, 0, 1720, 1440), w(2, 1720, 0, 1720, 1440)];
        let s = build_seam(&ws, 1, Edge::Right, WA, Config::default()).unwrap();
        assert_eq!(s.members[0].side, Side::Opposite);
        assert_eq!(s.moved_edge(&s.members[0]), Edge::Left);
        assert_eq!(s.ids(), vec![2]);
    }

    #[test]
    fn horizontal_seam_uses_min_height() {
        let ws = [w(1, 0, 0, 1000, 700), wm(2, 0, 700, 1000, 740, 1, 600)];
        let s = build_seam(&ws, 1, Edge::Bottom, WA, Config::default()).unwrap();
        assert_eq!(propagate(&s, 800).moves, vec![(2, Rect::new(0, 800, 1000, 1440))]);
        assert_eq!(propagate(&s, 1000).clamped_pos, 840);
    }

    #[test]
    fn far_edge_clamp_stops_the_seam_moving_left() {
        let ws = [w(1, 1720, 0, 1720, 1440), wm(2, 0, 0, 1720, 1440, 1000, 1)];
        let s = build_seam(&ws, 1, Edge::Left, WA, Config::default()).unwrap();
        assert_eq!(propagate(&s, 500).clamped_pos, 1000);
    }

    #[test]
    fn conflicting_minimums_keep_the_seam_in_place() {
        let ws = [w(1, 0, 0, 1720, 720), wm(3, 0, 720, 1720, 720, 2000, 1), wm(2, 1720, 0, 1720, 1440, 1500, 1)];
        let s = build_seam(&ws, 1, Edge::Right, WA, Config::default()).unwrap();
        assert_eq!(propagate(&s, 1800).clamped_pos, 1720);
    }

    #[test]
    fn unknown_dragged_window_has_no_seam() {
        let ws = [w(1, 0, 0, 1720, 1440), w(2, 1720, 0, 1720, 1440)];
        assert!(build_seam(&ws, 99, Edge::Right, WA, Config::default()).is_none());
    }

    #[test]
    fn window_beyond_the_span_is_not_chained() {
        let ws = [w(1, 0, 0, 1720, 300), w(2, 1720, 600, 1720, 300)];
        assert!(build_seam(&ws, 1, Edge::Right, WA, Config::default()).is_none());
    }
}
