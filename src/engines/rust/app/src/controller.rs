//! `DragController`: the drag state machine (Idle, Classifying, Resizing, Ignoring) plus the post-drag verify step.

use crate::ports::*;
use mb_core::{build_seam, propagate, Config, Edge, Rect, Seam};
use std::collections::HashMap;
use std::fmt::Write;
use std::rc::Rc;

const THROTTLE_MS: u64 = 8;
const SETTLE_MS: u64 = 150;
const VERIFY_MS: u64 = 60;

/// Drag seen at start, not yet classified as a resize or a move.
struct Drag {
    id: u64,
    start: Rect,
    snap: Snapshot,
    t0: u64,
}

/// An active glued resize: the seams, throttle bookkeeping and the frames last requested per member.
struct Resize {
    id: u64,
    seams: Vec<Seam>,
    last_apply: Option<u64>,
    latest: Rect,
    pending: Option<Rect>,
    requested: HashMap<u64, Rect>,
    t0: u64,
}

/// Data kept after drag end so the Verify timer can check and repair the final frames.
struct Verify {
    id: u64,
    seams: Vec<Seam>,
    clamps: Vec<i32>,
    requested: HashMap<u64, Rect>,
    snapped: Rect,
    t0: u64,
}

/// Controller state; `Ignoring` covers moves, ineligible windows and drags with no seam.
enum State {
    Idle,
    Classifying(Drag),
    Resizing(Resize),
    Ignoring,
}

/// Turns drag events into glued member moves. Generic over its ports: `I` inventory, `M` mover, `C` clock,
/// `T` timer, `L` log. Not `Send`; it lives on the UI thread, owned by the event source's thread-local slot
/// as a `Box<dyn DragEvents>` once installed.
pub struct DragController<I, M, C, T, L> {
    inventory: I,
    mover: M,
    clock: C,
    timer: T,
    log: L,
    name: String,
    live: Rc<crate::Live>,
    learned: HashMap<u64, (i32, i32)>,
    state: State,
    verify: Option<Verify>,
}

fn ename(e: Edge) -> String {
    format!("{e:?}").to_lowercase()
}

impl<I: WindowInventory, M: WindowMover, C: Clock, T: Timer, L: Log> DragController<I, M, C, T, L> {
    /// Creates an idle controller. `build_name` prefixes log lines; the tolerance is in 96-dpi pixels.
    pub fn new(inventory: I, mover: M, clock: C, timer: T, log: L, build_name: &str, glue_tolerance_px: i32) -> Self {
        Self {
            inventory,
            mover,
            clock,
            timer,
            log,
            name: build_name.to_string(),
            live: Rc::new(crate::Live::new(true, glue_tolerance_px)),
            learned: HashMap::new(),
            state: State::Idle,
            verify: None,
        }
    }

    /// Shares runtime knobs/counters with the host.
    #[must_use]
    pub fn with_live(mut self, live: Rc<crate::Live>) -> Self {
        self.live = live;
        self
    }

    fn info_at(&self, t0: u64, msg: &str) {
        let ms = self.clock.now_ms().saturating_sub(t0);
        self.log.info(&format!("[{} +{}] {}", self.name, ms, msg));
    }

    fn debug_at(&self, t0: u64, msg: &str) {
        let ms = self.clock.now_ms().saturating_sub(t0);
        self.log.debug(&format!("[{} +{}] {}", self.name, ms, msg));
    }

    /// Plans for all seams at the dragged frame: merged moves + clamped positions.
    fn plan_all(seams: &[Seam], frame: Rect) -> (Vec<(u64, Rect)>, Vec<i32>) {
        let mut moves: Vec<(u64, Rect)> = Vec::new();
        let mut clamps = Vec::new();
        for s in seams {
            let plan = propagate(s, frame.get(s.edge));
            clamps.push(plan.clamped_pos);
            for (id, r) in plan.moves {
                let m = s.members.iter().find(|m| m.id == id).unwrap();
                let e = s.moved_edge(m);
                match moves.iter_mut().find(|(i, _)| *i == id) {
                    Some((_, ex)) => *ex = ex.with(e, r.get(e)),
                    None => moves.push((id, r)),
                }
            }
        }
        (moves, clamps)
    }

    fn learn(&mut self, seams: &mut [Seam], id: u64, want: Rect, got: Rect) {
        for s in seams.iter_mut() {
            let vertical = s.edge.is_vertical();
            if let Some(m) = s.members.iter_mut().find(|m| m.id == id) {
                let entry = self.learned.entry(id).or_insert((m.min_w, m.min_h));
                if vertical && got.width() > want.width() {
                    m.min_w = got.width();
                    entry.0 = got.width();
                } else if !vertical && got.height() > want.height() {
                    m.min_h = got.height();
                    entry.1 = got.height();
                }
            }
        }
    }

    /// Plan, record requests, apply in one batch, learn minimums. Returns clamps.
    fn apply_plan(&mut self, seams: &mut [Seam], requested: &mut HashMap<u64, Rect>, frame: Rect) -> Vec<i32> {
        let (moves, clamps) = Self::plan_all(seams, frame);
        for (id, r) in &moves {
            requested.insert(*id, *r);
        }
        let applied = self.mover.apply(&moves);
        for (id, got) in applied {
            if let Some((_, want)) = moves.iter().find(|(i, _)| *i == id) {
                self.learn(seams, id, *want, got);
            }
        }
        clamps
    }

    /// Members whose current size differs from what was last requested.
    fn mismatches(&mut self, seams: &[Seam], requested: &HashMap<u64, Rect>) -> Vec<(u64, Rect, Rect)> {
        let mut out: Vec<(u64, Rect, Rect)> = Vec::new();
        for s in seams {
            for m in &s.members {
                if out.iter().any(|o| o.0 == m.id) {
                    continue;
                }
                let (Some(&want), Some(got)) = (requested.get(&m.id), self.inventory.frame_of(m.id)) else { continue };
                if got.width() != want.width() || got.height() != want.height() {
                    out.push((m.id, want, got));
                }
            }
        }
        out
    }

    fn do_apply(&mut self, r: &mut Resize, frame: Rect, now: u64) {
        let clamps = self.apply_plan(&mut r.seams, &mut r.requested, frame);
        r.last_apply = Some(now);
        r.pending = None;
        let msg = r
            .seams
            .iter()
            .zip(&clamps)
            .map(|(s, c)| format!("{}={} clamp={} members={}", ename(s.edge), frame.get(s.edge), c, s.members.len()))
            .collect::<Vec<_>>()
            .join(" ");
        self.debug_at(r.t0, &format!("apply {msg}"));
    }

    fn take_resizing(&mut self) -> Option<Resize> {
        if matches!(self.state, State::Resizing(_)) {
            if let State::Resizing(r) = std::mem::replace(&mut self.state, State::Idle) {
                return Some(r);
            }
        }
        None
    }

    fn classify(&mut self, d: &Drag, frame: Rect) -> State {
        if frame.width() == d.start.width() && frame.height() == d.start.height() {
            return State::Ignoring; // a move
        }
        self.live.drags.set(self.live.drags.get() + 1);
        let tol = (f64::from(self.live.tolerance_px.get()) * d.snap.dpi_scale).round() as i32;
        let cfg = Config { glue_tolerance: tol };
        let mut windows = d.snap.windows.clone();
        for w in &mut windows {
            if let Some(&(mw, mh)) = self.learned.get(&w.id) {
                w.min_w = w.min_w.max(mw);
                w.min_h = w.min_h.max(mh);
            }
        }
        let seams: Vec<Seam> = Edge::ALL
            .iter()
            .filter(|&&e| frame.get(e) != d.start.get(e))
            .filter_map(|&e| build_seam(&windows, d.id, e, d.snap.work_area, cfg))
            .collect();
        if seams.is_empty() {
            return State::Ignoring;
        }
        self.live.glued_drags.set(self.live.glued_drags.get() + 1);
        for s in &seams {
            self.info_at(d.t0, &format!("seam {} x={} members={:?}", ename(s.edge), s.x, s.ids()));
        }
        State::Resizing(Resize {
            id: d.id,
            seams,
            last_apply: None,
            latest: frame,
            pending: None,
            requested: HashMap::new(),
            t0: d.t0,
        })
    }

    fn on_settle(&mut self) {
        let Some(mut r) = self.take_resizing() else { return };
        let mm = self.mismatches(&r.seams, &r.requested);
        for &(id, want, got) in &mm {
            self.learn(&mut r.seams, id, want, got);
        }
        let frame = self.inventory.frame_of(r.id).unwrap_or(r.latest);
        let (_, clamps) = if mm.is_empty() {
            Self::plan_all(&r.seams, frame)
        } else {
            let c = self.apply_plan(&mut r.seams, &mut r.requested, frame);
            (Vec::new(), c)
        };
        let ms = self.clock.now_ms().saturating_sub(r.t0);
        let mut msg = format!("settled +{ms}");
        for (s, c) in r.seams.iter().zip(&clamps) {
            let pos = frame.get(s.edge);
            let _ = write!(msg, " {}={} clamp={} gap={}", ename(s.edge), pos, c, pos - c);
        }
        for (id, want, got) in &mm {
            let _ =
                write!(msg, " id{} req={}x{} got={}x{}", id, want.width(), want.height(), got.width(), got.height());
        }
        self.info_at(r.t0, &msg);
        self.state = State::Resizing(r);
    }

    fn on_verify(&mut self) {
        let Some(mut v) = self.verify.take() else { return };
        let frame = self.inventory.frame_of(v.id);
        let dragged_bad = frame.is_some_and(|f| v.seams.iter().zip(&v.clamps).any(|(s, &c)| f.get(s.edge) != c));
        let mm = self.mismatches(&v.seams, &v.requested);
        let ms = self.clock.now_ms().saturating_sub(v.t0);
        if !dragged_bad && mm.is_empty() {
            self.info_at(v.t0, &format!("end +{ms} ok"));
            return;
        }
        for &(id, want, got) in &mm {
            self.learn(&mut v.seams, id, want, got);
        }
        let mut target = frame.unwrap_or(v.snapped);
        let (_, clamps) = Self::plan_all(&v.seams, target);
        for (s, &c) in v.seams.iter().zip(&clamps) {
            target = target.with(s.edge, c);
        }
        self.apply_plan(&mut v.seams, &mut v.requested, target);
        self.mover.apply(&[(v.id, target)]);
        let ids: Vec<u64> = mm.iter().map(|m| m.0).collect();
        self.info_at(v.t0, &format!("end +{ms} fixed dragged_overwritten={dragged_bad} members={ids:?}"));
    }
}

impl<I: WindowInventory, M: WindowMover, C: Clock, T: Timer, L: Log> DragEvents for DragController<I, M, C, T, L> {
    fn on_move_size_start(&mut self, id: u64) {
        self.timer.cancel_all();
        self.verify = None;
        if !self.live.active.get() {
            self.state = State::Ignoring;
            return;
        }
        let snap = self.inventory.snapshot(id);
        let t0 = self.clock.now_ms();
        self.state = match snap.windows.iter().find(|w| w.id == id) {
            Some(w) => {
                let start = w.frame;
                State::Classifying(Drag { id, start, snap, t0 })
            }
            None => State::Ignoring,
        };
    }

    fn on_location_change(&mut self, id: u64) {
        let Some(frame) = self.inventory.frame_of(id) else { return };
        if matches!(&self.state, State::Classifying(d) if d.id == id) {
            if let State::Classifying(d) = std::mem::replace(&mut self.state, State::Ignoring) {
                self.state = self.classify(&d, frame);
            }
        }
        if !matches!(&self.state, State::Resizing(r) if r.id == id) {
            return;
        }
        let Some(mut r) = self.take_resizing() else { return };
        r.latest = frame;
        let now = self.clock.now_ms();
        match r.last_apply {
            Some(t) if now.saturating_sub(t) < THROTTLE_MS => {
                r.pending = Some(frame);
                self.timer.arm(TimerKind::Flush, THROTTLE_MS - now.saturating_sub(t));
            }
            _ => self.do_apply(&mut r, frame, now),
        }
        self.timer.arm(TimerKind::Settle, SETTLE_MS);
        self.state = State::Resizing(r);
    }

    fn on_timer(&mut self, kind: TimerKind) {
        match kind {
            TimerKind::Flush => {
                if let Some(mut r) = self.take_resizing() {
                    if let Some(f) = r.pending {
                        let now = self.clock.now_ms();
                        self.do_apply(&mut r, f, now);
                    }
                    self.state = State::Resizing(r);
                }
            }
            TimerKind::Settle => self.on_settle(),
            TimerKind::Verify => self.on_verify(),
        }
    }

    fn on_move_size_end(&mut self, id: u64) {
        self.timer.cancel_all();
        let state = std::mem::replace(&mut self.state, State::Idle);
        let State::Resizing(mut r) = state else { return };
        if r.id != id {
            return;
        }
        let frame = self.inventory.frame_of(id).unwrap_or(r.latest);
        let mut clamps = self.apply_plan(&mut r.seams, &mut r.requested, frame);
        // learned minimums may have tightened the clamp
        let (_, c2) = Self::plan_all(&r.seams, frame);
        if c2 != clamps {
            clamps = self.apply_plan(&mut r.seams, &mut r.requested, frame);
        }
        let mut snapped = frame;
        for (s, &c) in r.seams.iter().zip(&clamps) {
            if c != frame.get(s.edge) {
                snapped = snapped.with(s.edge, c);
            }
        }
        if snapped != frame {
            self.mover.apply(&[(id, snapped)]);
        }
        self.info_at(r.t0, &format!("end id={} seams={} snap_back={}", id, r.seams.len(), snapped != frame));
        self.verify = Some(Verify { id, seams: r.seams, clamps, requested: r.requested, snapped, t0: r.t0 });
        self.timer.arm(TimerKind::Verify, VERIFY_MS);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use mb_core::WinInfo;
    use std::cell::{Cell, RefCell};
    use std::rc::Rc;

    type Batches = Rc<RefCell<Vec<Vec<(u64, Rect)>>>>;
    type Frames = Rc<RefCell<HashMap<u64, Rect>>>;
    type Armed = Rc<RefCell<Vec<(TimerKind, u64)>>>;

    struct Inv {
        wins: Vec<WinInfo>,
        frames: Frames,
    }
    impl WindowInventory for Inv {
        fn snapshot(&mut self, _d: u64) -> Snapshot {
            Snapshot { windows: self.wins.clone(), work_area: Rect::new(0, 0, 3440, 1440), dpi_scale: 1.0 }
        }
        fn frame_of(&mut self, id: u64) -> Option<Rect> {
            self.frames.borrow().get(&id).copied()
        }
    }
    struct Mov {
        batches: Batches,
        floor_w: Option<(u64, i32)>,
    }
    impl WindowMover for Mov {
        fn apply(&mut self, moves: &[(u64, Rect)]) -> Vec<(u64, Rect)> {
            self.batches.borrow_mut().push(moves.to_vec());
            moves
                .iter()
                .map(|&(id, r)| match self.floor_w {
                    Some((fid, w)) if fid == id && r.width() < w => (id, r.with(Edge::Left, r.r - w)),
                    _ => (id, r),
                })
                .collect()
        }
    }
    struct Clk(Rc<Cell<u64>>);
    impl Clock for Clk {
        fn now_ms(&self) -> u64 {
            self.0.get()
        }
    }
    struct Tmr(Armed);
    impl Timer for Tmr {
        fn arm(&mut self, kind: TimerKind, ms: u64) {
            self.0.borrow_mut().push((kind, ms));
        }
        fn cancel_all(&mut self) {}
    }
    struct Nop;
    impl Log for Nop {
        fn info(&self, _: &str) {}
        fn debug(&self, _: &str) {}
    }

    struct Rig {
        c: DragController<Inv, Mov, Clk, Tmr, Nop>,
        frames: Frames,
        batches: Batches,
        time: Rc<Cell<u64>>,
        armed: Armed,
    }

    impl Rig {
        fn set(&self, id: u64, r: Rect) {
            self.frames.borrow_mut().insert(id, r);
        }
    }

    fn rig(wins: Vec<WinInfo>, floor: Option<(u64, i32)>) -> Rig {
        let frames: Frames = Rc::new(RefCell::new(wins.iter().map(|w| (w.id, w.frame)).collect()));
        let batches: Batches = Rc::default();
        let armed: Armed = Rc::default();
        let time = Rc::new(Cell::new(1000));
        let c = DragController::new(
            Inv { wins, frames: frames.clone() },
            Mov { batches: batches.clone(), floor_w: floor },
            Clk(time.clone()),
            Tmr(armed.clone()),
            Nop,
            "test",
            12,
        );
        Rig { c, frames, batches, time, armed }
    }

    fn win(id: u64, x: i32, w: i32, min_w: i32) -> WinInfo {
        WinInfo { id, frame: Rect::from_xywh(x, 0, w, 1440), min_w, min_h: 1 }
    }
    fn two() -> Vec<WinInfo> {
        vec![win(1, 0, 1720, 1), win(2, 1720, 1720, 1)]
    }

    #[test]
    fn resize_moves_neighbour_in_one_batch() {
        let mut r = rig(two(), None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 1820, 1440));
        r.c.on_location_change(1);
        assert_eq!(r.batches.borrow().len(), 1);
        assert_eq!(r.batches.borrow()[0], vec![(2, Rect::from_xywh(1820, 0, 1620, 1440))]);
    }

    #[test]
    fn pure_move_is_ignored() {
        let mut r = rig(two(), None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(50, 0, 1720, 1440));
        r.c.on_location_change(1);
        r.c.on_move_size_end(1);
        assert!(r.batches.borrow().is_empty());
    }

    #[test]
    fn trailing_flush_applies_last_frame() {
        let mut r = rig(two(), None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 1800, 1440));
        r.c.on_location_change(1);
        r.time.set(1003);
        r.set(1, Rect::from_xywh(0, 0, 1810, 1440));
        r.c.on_location_change(1);
        assert_eq!(r.batches.borrow().len(), 1, "second frame is only pending");
        assert!(r.armed.borrow().contains(&(TimerKind::Flush, 5)));
        r.time.set(1008);
        r.c.on_timer(TimerKind::Flush); // mouse is still: no drag end
        assert_eq!(r.batches.borrow().len(), 2);
        assert_eq!(r.batches.borrow()[1], vec![(2, Rect::from_xywh(1810, 0, 1630, 1440))]);
    }

    #[test]
    fn settle_learns_async_minimum() {
        let mut r = rig(two(), None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 2000, 1440));
        r.c.on_location_change(1);
        assert!(r.armed.borrow().iter().any(|a| a.0 == TimerKind::Settle));
        // the app enforces a 1500 minimum asynchronously
        r.set(2, Rect::new(1940, 0, 3440, 1440));
        r.time.set(1200);
        r.c.on_timer(TimerKind::Settle);
        let b = r.batches.borrow();
        assert_eq!(b.len(), 2);
        assert_eq!(b[1], vec![(2, Rect::new(1940, 0, 3440, 1440))]);
    }

    #[test]
    fn verify_reapplies_overwritten_snap_back() {
        let mut wins = two();
        wins[1].min_w = 1600;
        let mut r = rig(wins, None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 2020, 1440));
        r.c.on_location_change(1);
        r.c.on_move_size_end(1);
        let snap = Rect::from_xywh(0, 0, 1840, 1440);
        assert_eq!(r.batches.borrow().last().unwrap(), &vec![(1, snap)]);
        assert!(r.armed.borrow().contains(&(TimerKind::Verify, 60)));
        // the system's end-of-drag handling overwrote the snap-back
        r.set(1, Rect::from_xywh(0, 0, 2020, 1440));
        r.set(2, Rect::from_xywh(1840, 0, 1600, 1440));
        let n = r.batches.borrow().len();
        r.c.on_timer(TimerKind::Verify);
        assert!(r.batches.borrow().len() > n);
        assert_eq!(r.batches.borrow().last().unwrap(), &vec![(1, snap)]);
        // once only
        let n = r.batches.borrow().len();
        r.c.on_timer(TimerKind::Verify);
        assert_eq!(r.batches.borrow().len(), n);
    }

    #[test]
    fn snap_back_on_min_width() {
        let mut wins = two();
        wins[1].min_w = 1600;
        let mut r = rig(wins, None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 2020, 1440));
        r.c.on_location_change(1);
        r.c.on_move_size_end(1);
        assert_eq!(r.batches.borrow().last().unwrap(), &vec![(1, Rect::from_xywh(0, 0, 1840, 1440))]);
    }

    #[test]
    fn learns_minimum_from_applied_frame() {
        let mut r = rig(two(), Some((2, 1500)));
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 2000, 1440));
        r.c.on_location_change(1);
        r.c.on_move_size_end(1);
        assert_eq!(r.batches.borrow().last().unwrap(), &vec![(1, Rect::from_xywh(0, 0, 1940, 1440))]);
    }

    #[test]
    fn ineligible_dragged_window_ignored() {
        let mut r = rig(two(), None);
        r.c.on_move_size_start(9);
        r.set(9, Rect::from_xywh(0, 0, 1800, 1440));
        r.c.on_location_change(9);
        assert!(r.batches.borrow().is_empty());
    }

    fn stacked() -> Vec<WinInfo> {
        vec![
            WinInfo { id: 1, frame: Rect::from_xywh(0, 0, 1000, 700), min_w: 1, min_h: 1 },
            WinInfo { id: 2, frame: Rect::from_xywh(0, 700, 1000, 740), min_w: 1, min_h: 1 },
        ]
    }

    #[test]
    fn horizontal_seam_moves_the_window_below() {
        let mut r = rig(stacked(), None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 1000, 800));
        r.c.on_location_change(1);
        assert_eq!(r.batches.borrow()[0], vec![(2, Rect::new(0, 800, 1000, 1440))]);
    }

    #[test]
    fn inactive_engine_ignores_drags() {
        let mut r = rig(two(), None);
        r.c.live.active.set(false);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 1820, 1440));
        r.c.on_location_change(1);
        r.c.on_move_size_end(1);
        assert!(r.batches.borrow().is_empty());
        assert_eq!(r.c.live.drags.get(), 0);
    }

    #[test]
    fn counters_track_resizes_and_seams() {
        let mut r = rig(two(), None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 1820, 1440));
        r.c.on_location_change(1);
        assert_eq!((r.c.live.drags.get(), r.c.live.glued_drags.get()), (1, 1));
        // a resize on a screen border finds no seam
        r.c.on_move_size_start(2);
        r.set(2, Rect::new(1720, 0, 3500, 1440));
        r.c.on_location_change(2);
        assert_eq!((r.c.live.drags.get(), r.c.live.glued_drags.get()), (2, 1));
    }

    #[test]
    fn location_change_of_an_unknown_window_is_ignored() {
        let mut r = rig(two(), None);
        r.c.on_location_change(77);
        assert!(r.batches.borrow().is_empty());
    }

    #[test]
    fn flush_without_pending_frame_applies_nothing() {
        let mut r = rig(two(), None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 1820, 1440));
        r.c.on_location_change(1);
        r.c.on_timer(TimerKind::Flush);
        assert_eq!(r.batches.borrow().len(), 1);
        // outside a resize the timers are no-ops
        let mut idle = rig(two(), None);
        idle.c.on_timer(TimerKind::Flush);
        idle.c.on_timer(TimerKind::Settle);
        idle.c.on_timer(TimerKind::Verify);
        assert!(idle.batches.borrow().is_empty());
    }

    #[test]
    fn settle_with_matching_sizes_changes_nothing() {
        let mut r = rig(two(), None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 1820, 1440));
        r.c.on_location_change(1);
        r.set(2, Rect::new(1820, 0, 3440, 1440));
        r.c.on_timer(TimerKind::Settle);
        assert_eq!(r.batches.borrow().len(), 1);
    }

    #[test]
    fn move_size_end_of_another_window_is_ignored() {
        let mut r = rig(two(), None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 1820, 1440));
        r.c.on_location_change(1);
        r.c.on_move_size_end(2);
        assert!(!r.armed.borrow().iter().any(|a| a.0 == TimerKind::Verify));
    }

    #[test]
    fn verify_after_a_clean_drag_applies_nothing() {
        let mut r = rig(two(), None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 1820, 1440));
        r.c.on_location_change(1);
        r.c.on_move_size_end(1);
        r.set(2, Rect::new(1820, 0, 3440, 1440));
        let n = r.batches.borrow().len();
        r.c.on_timer(TimerKind::Verify);
        assert_eq!(r.batches.borrow().len(), n);
    }

    #[test]
    fn learned_minimum_applies_to_the_next_drag() {
        let mut r = rig(two(), Some((2, 1500)));
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 2000, 1440));
        r.c.on_location_change(1);
        r.c.on_move_size_end(1);
        assert_eq!(r.c.learned.get(&2), Some(&(1500, 1)));
        r.set(1, Rect::from_xywh(0, 0, 1720, 1440));
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 2100, 1440));
        r.c.on_location_change(1);
        r.c.on_move_size_end(1);
        assert_eq!(r.batches.borrow().last().unwrap(), &vec![(1, Rect::from_xywh(0, 0, 1940, 1440))]);
    }
}
