//! `DragController`: the drag state machine (Idle, Classifying, Resizing, Moving, Ignoring) plus the post-drag
//! verify step.

use crate::ports::*;
use mb_core::{
    build_seam, make_target, propagate, screen_side_targets, Attraction, AttractionConfig, AttractionEvent, Config,
    Edge, Point, PointerDrag, Rect, ReleaseReason, Seam, TargetKind,
};
use std::collections::HashMap;
use std::fmt::Write;
use std::rc::Rc;

const THROTTLE_MS: u64 = 8;
const SETTLE_MS: u64 = 150;
const VERIFY_MS: u64 = 60;

/// Drag seen at start, not yet classified as a resize or a move. `glue_on` and `snap_on` are the switches as they
/// were at drag start.
struct Drag {
    id: u64,
    start: Rect,
    snap: Snapshot,
    t0: u64,
    glue_on: bool,
    snap_on: bool,
}

/// How a move drag holds the window: by moving it back after Windows moves it, or by holding the cursor (SNAP-070).
enum Mode {
    Frame(Attraction),
    Pointer(PointerDrag),
}

/// An active move drag handled by magnetic attraction.
struct Moving {
    id: u64,
    start: Rect,
    mode: Mode,
    /// Last free position fed to the attraction (frame mode).
    last_free: Rect,
    /// Frame the window reported after our last held move (frame mode).
    last_held: Option<Rect>,
    /// Edges held during this drag.
    snaps: u64,
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

/// Controller state; `Ignoring` covers ineligible windows, drags with nothing to do and resizes with no seam.
enum State {
    Idle,
    Classifying(Drag),
    Resizing(Resize),
    Moving(Box<Moving>),
    Ignoring,
}

/// Turns drag events into glued member moves and held positions of moved windows. Generic over its ports: `I` inventory, `M` mover, `C` clock,
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
    hook: Option<Box<dyn PointerHook>>,
}

fn ename(e: Edge) -> String {
    format!("{e:?}").to_lowercase()
}

fn kind_name(k: TargetKind) -> &'static str {
    match k {
        TargetKind::Screen => "screen",
        TargetKind::EdgeToEdge => "edge-to-edge",
        TargetKind::Aligned => "aligned",
    }
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
            hook: None,
        }
    }

    /// Uses `hook` to hold the cursor during moves (SNAP-070). Without a hook, or when it cannot start, a move
    /// moves the window back to the held position after Windows moves it (SNAP-076).
    #[must_use]
    pub fn with_pointer_hook(mut self, hook: Box<dyn PointerHook>) -> Self {
        self.hook = Some(hook);
        self
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
            // a move (SNAP-002)
            return if d.snap_on { self.begin_move(d) } else { State::Ignoring };
        }
        if !d.glue_on {
            return State::Ignoring; // a resize needs magnetic coupling
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

    /// Builds the attraction of a move drag and starts holding the cursor when a hook is available.
    fn begin_move(&mut self, d: &Drag) -> State {
        self.live.move_drags.set(self.live.move_drags.get() + 1);
        let scale = d.snap.dpi_scale;
        let o = self.live.snap.get();
        let cfg = AttractionConfig {
            pull_in: (f64::from(o.distance_px) * scale).round() as i32,
            breakaway: (f64::from(o.breakaway_px) * scale).round() as i32,
            max_speed: f64::from(o.max_speed) * scale,
            screen_edges: o.screen_edges,
            windows: o.windows,
        };
        let others = d
            .snap
            .windows
            .iter()
            .filter(|w| w.id != d.id)
            .map(|w| {
                // SNAP-038: only the parts of w that the windows above it leave visible attract. The dragged window
                // moves, so it never counts as covering anything.
                let above: Vec<Rect> = d
                    .snap
                    .stack
                    .iter()
                    .take_while(|(id, _)| *id != w.id)
                    .filter(|(id, _)| *id != d.id)
                    .map(|&(_, f)| f)
                    .collect();
                make_target(&w.frame, &above)
            })
            .collect();
        let sides = screen_side_targets(&d.snap.work_area, &d.snap.work_areas);
        let attraction = Attraction::new(cfg, others, d.snap.work_area, sides);
        let mode = self.start_pointer(attraction, d.id);
        let pointer = if matches!(mode, Mode::Pointer(_)) { " pointer" } else { "" };
        self.info_at(d.t0, &format!("move start id={}{pointer}", d.id));
        State::Moving(Box::new(Moving {
            id: d.id,
            start: d.start,
            mode,
            last_free: d.start,
            last_held: None,
            snaps: 0,
            t0: d.t0,
        }))
    }

    /// Starts the pointer hook and hands the attraction to a `PointerDrag`. Falls back to frame mode when there is
    /// no hook, it cannot start, or the cursor cannot be read (SNAP-076).
    fn start_pointer(&mut self, attraction: Attraction, id: u64) -> Mode {
        let frame = self.inventory.frame_of(id);
        let (Some(hook), Some(frame)) = (self.hook.as_mut(), frame) else { return Mode::Frame(attraction) };
        if !hook.start() {
            return Mode::Frame(attraction);
        }
        let Some(cursor) = hook.cursor() else {
            hook.stop();
            return Mode::Frame(attraction);
        };
        Mode::Pointer(PointerDrag::new(attraction, frame, cursor))
    }

    /// Stops the pointer hook when the current move holds the cursor (SNAP-075).
    fn stop_pointer(&mut self) {
        if matches!(&self.state, State::Moving(m) if matches!(m.mode, Mode::Pointer(_))) {
            if let Some(hook) = self.hook.as_mut() {
                hook.stop();
            }
        }
    }

    fn take_moving(&mut self) -> Option<Moving> {
        if matches!(self.state, State::Moving(_)) {
            if let State::Moving(m) = std::mem::replace(&mut self.state, State::Idle) {
                return Some(*m);
            }
        }
        None
    }

    /// Counts and logs the engage and release events of the attraction (SNAP-060).
    fn log_snap_events(&self, m: &mut Moving, events: &[AttractionEvent]) {
        for ev in events {
            let edge = ename(ev.edge);
            if ev.engaged {
                self.live.snaps.set(self.live.snaps.get() + 1);
                m.snaps += 1;
                let speed = ev.speed.round() as i64;
                self.info_at(
                    m.t0,
                    &format!("snap hold {edge}={} target={} speed={speed}", ev.line, kind_name(ev.kind)),
                );
            } else {
                let reason = if ev.reason == Some(ReleaseReason::Breakaway) { "breakaway" } else { "target-gone" };
                self.info_at(m.t0, &format!("snap release {edge}={} reason={reason}", ev.line));
            }
        }
    }

    /// Frame mode: feeds the free position to the attraction and puts the window on the held position (SNAP-023).
    fn on_move(&mut self, m: &mut Moving, frame: Rect) {
        let now = self.clock.now_ms();
        let Mode::Frame(attraction) = &mut m.mode else { return };
        if m.last_held == Some(frame) && frame != m.last_free {
            return; // SNAP-024: our own move
        }
        m.last_free = frame;
        let held = attraction.update(&frame, now);
        let events = attraction.take_events();
        self.log_snap_events(m, &events);
        if held == frame {
            m.last_held = None;
            return;
        }
        let applied = self.mover.apply(&[(m.id, held)]); // SNAP-025: never throttled
        m.last_held = Some(applied.first().map_or(held, |&(_, f)| f));
    }

    /// Ends a move drag (SNAP-050 to SNAP-052, SNAP-075).
    fn finish_move(&mut self, m: &mut Moving) {
        let id = m.id;
        let held = match &mut m.mode {
            Mode::Pointer(p) => {
                let a = p.attraction();
                a.held().then(|| a.current())
            }
            Mode::Frame(a) => a.held().then(|| a.current()),
        };
        let mut applied = false;
        match &m.mode {
            Mode::Pointer(_) => {
                // SNAP-075: Windows already put the window where the cursor is, so nothing is applied.
                if let Some(hook) = self.hook.as_mut() {
                    hook.stop();
                }
            }
            Mode::Frame(a) => {
                let frame = self.inventory.frame_of(id);
                if let Some(f) = frame {
                    if a.held()
                        && f == m.last_free
                        && f.width() == m.start.width()
                        && f.height() == m.start.height()
                        && a.current() != f
                    {
                        // SNAP-050, SNAP-051
                        self.mover.apply(&[(id, a.current())]);
                        applied = true;
                    }
                }
            }
        }
        let tail = match (&m.mode, applied) {
            (Mode::Pointer(_), _) => " pointer",
            (Mode::Frame(_), true) => " applied",
            (Mode::Frame(_), false) => "",
        };
        self.info_at(m.t0, &format!("move end id={id} snaps={}{tail}", m.snaps));
        self.inventory.report_move_result(id, held);
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
        self.stop_pointer(); // SNAP-075: a hook left running by the previous drag
        let glue_on = self.live.active.get();
        let snap_on = self.live.snap_active.get();
        if !glue_on && !snap_on {
            self.state = State::Ignoring;
            return;
        }
        let snap = self.inventory.snapshot(id);
        let t0 = self.clock.now_ms();
        self.state = match snap.windows.iter().find(|w| w.id == id) {
            Some(w) => {
                let start = w.frame;
                State::Classifying(Drag { id, start, snap, t0, glue_on, snap_on })
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
        if matches!(&self.state, State::Moving(m) if m.id == id) {
            if let Some(mut m) = self.take_moving() {
                self.on_move(&mut m, frame);
                self.state = State::Moving(Box::new(m));
            }
            return;
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
        if matches!(&self.state, State::Moving(m) if m.id == id) {
            if let Some(mut m) = self.take_moving() {
                self.finish_move(&mut m);
            }
            return;
        }
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

impl<I: WindowInventory, M: WindowMover, C: Clock, T: Timer, L: Log> PointerEvents for DragController<I, M, C, T, L> {
    /// During a move with the pointer hook, returns where the cursor must go so the window lands on its held
    /// position (SNAP-070 to SNAP-074). Otherwise lets the movement through.
    fn on_pointer_move(&mut self, proposed: Point) -> Option<Point> {
        if !matches!(&self.state, State::Moving(m) if matches!(m.mode, Mode::Pointer(_))) {
            return None;
        }
        let mut m = self.take_moving()?;
        let now = self.clock.now_ms();
        let mut out = None;
        if let Mode::Pointer(p) = &mut m.mode {
            let to = p.move_cursor(proposed, now);
            let events = p.attraction().take_events();
            self.log_snap_events(&mut m, &events);
            out = (to != proposed).then_some(to);
        }
        self.state = State::Moving(Box::new(m));
        out
    }

    /// Stops the pointer hook as soon as the mouse button is released, so the cursor can never stay held after a
    /// move (SNAP-075).
    fn on_pointer_release(&mut self) {
        self.stop_pointer();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::SnapOptions;
    use mb_core::WinInfo;
    use std::cell::{Cell, RefCell};
    use std::rc::Rc;

    type Batches = Rc<RefCell<Vec<Vec<(u64, Rect)>>>>;
    type Frames = Rc<RefCell<HashMap<u64, Rect>>>;
    type Armed = Rc<RefCell<Vec<(TimerKind, u64)>>>;

    type Snap = Rc<RefCell<Snapshot>>;

    type Results = Rc<RefCell<Vec<(u64, Option<Rect>)>>>;

    struct Inv {
        snap: Snap,
        frames: Frames,
        results: Results,
    }
    impl WindowInventory for Inv {
        fn snapshot(&mut self, _d: u64) -> Snapshot {
            self.snap.borrow().clone()
        }
        fn frame_of(&mut self, id: u64) -> Option<Rect> {
            self.frames.borrow().get(&id).copied()
        }
        fn report_move_result(&mut self, id: u64, held: Option<Rect>) {
            self.results.borrow_mut().push((id, held));
        }
    }
    struct Mov {
        batches: Batches,
        floor_w: Option<(u64, i32)>,
        /// Frames the system reports back instead of the requested one.
        force: Frames,
    }
    impl WindowMover for Mov {
        fn apply(&mut self, moves: &[(u64, Rect)]) -> Vec<(u64, Rect)> {
            self.batches.borrow_mut().push(moves.to_vec());
            moves
                .iter()
                .map(|&(id, r)| match self.floor_w {
                    _ if self.force.borrow().contains_key(&id) => (id, self.force.borrow()[&id]),
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
    type Lines = Rc<RefCell<Vec<String>>>;

    struct Rec(Lines);
    impl Log for Rec {
        fn info(&self, msg: &str) {
            self.0.borrow_mut().push(msg.to_string());
        }
        fn debug(&self, msg: &str) {
            self.0.borrow_mut().push(msg.to_string());
        }
    }

    /// Pointer hook that records start and stop and reports a settable cursor.
    #[derive(Default)]
    struct HookState {
        fail: bool,
        running: bool,
        starts: u32,
        stops: u32,
        at: Option<Point>,
    }
    type Hook = Rc<RefCell<HookState>>;

    struct Fake(Hook);
    impl PointerHook for Fake {
        fn start(&mut self) -> bool {
            let mut h = self.0.borrow_mut();
            if h.fail {
                return false;
            }
            h.running = true;
            h.starts += 1;
            true
        }
        fn stop(&mut self) {
            let mut h = self.0.borrow_mut();
            if h.running {
                h.stops += 1;
            }
            h.running = false;
        }
        fn cursor(&mut self) -> Option<Point> {
            self.0.borrow().at
        }
    }

    struct Rig {
        c: DragController<Inv, Mov, Clk, Tmr, Rec>,
        snap: Snap,
        frames: Frames,
        batches: Batches,
        time: Rc<Cell<u64>>,
        armed: Armed,
        force: Frames,
        lines: Lines,
        hook: Hook,
        results: Results,
    }

    impl Rig {
        fn set(&self, id: u64, r: Rect) {
            self.frames.borrow_mut().insert(id, r);
        }

        fn logged(&self, needle: &str) -> bool {
            self.lines.borrow().iter().any(|l| l.contains(needle))
        }
    }

    fn rig(wins: Vec<WinInfo>, floor: Option<(u64, i32)>) -> Rig {
        rig_with(wins, floor, false)
    }

    fn rig_with(wins: Vec<WinInfo>, floor: Option<(u64, i32)>, with_hook: bool) -> Rig {
        let frames: Frames = Rc::new(RefCell::new(wins.iter().map(|w| (w.id, w.frame)).collect()));
        let batches: Batches = Rc::default();
        let armed: Armed = Rc::default();
        let time = Rc::new(Cell::new(1000));
        let work_area = Rect::new(0, 0, 3440, 1440);
        let snap: Snap = Rc::new(RefCell::new(Snapshot {
            windows: wins,
            work_area,
            work_areas: vec![work_area],
            stack: Vec::new(),
            dpi_scale: 1.0,
        }));
        let force: Frames = Rc::default();
        let lines: Lines = Rc::default();
        let hook: Hook =
            Rc::new(RefCell::new(HookState { at: Some(Point { x: 300, y: 260 }), ..HookState::default() }));
        let results: Results = Rc::default();
        let mut c = DragController::new(
            Inv { snap: snap.clone(), frames: frames.clone(), results: results.clone() },
            Mov { batches: batches.clone(), floor_w: floor, force: force.clone() },
            Clk(time.clone()),
            Tmr(armed.clone()),
            Rec(lines.clone()),
            "test",
            12,
        );
        if with_hook {
            c = c.with_pointer_hook(Box::new(Fake(hook.clone())));
        }
        Rig { c, snap, frames, batches, time, armed, force, lines, hook, results }
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
    fn a_move_is_ignored_with_attraction_off() {
        // SNAP-001
        let mut r = rig(two(), None);
        r.c.live.snap_active.set(false);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(50, 0, 1720, 1440));
        r.c.on_location_change(1);
        r.c.on_move_size_end(1);
        assert!(r.batches.borrow().is_empty());
        assert_eq!(r.c.live.move_drags.get(), 0);
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

    // ---- magnetic attraction (moves) ----

    const NEAR: Rect = Rect { l: 295, t: 250, r: 495, b: 450 };
    const HELD: Rect = Rect { l: 300, t: 250, r: 500, b: 450 };

    /// Work area 1000x800, the dragged window 1 at 200..400 and window 2 at 500..700.
    fn move_rig(with_hook: bool) -> Rig {
        let wins = vec![
            WinInfo { id: 1, frame: Rect::new(200, 250, 400, 450), min_w: 1, min_h: 1 },
            WinInfo { id: 2, frame: Rect::new(500, 200, 700, 400), min_w: 1, min_h: 1 },
        ];
        let r = rig_with(wins, None, with_hook);
        {
            let mut s = r.snap.borrow_mut();
            s.work_area = Rect::new(0, 0, 1000, 800);
            s.work_areas = vec![s.work_area];
        }
        r
    }

    impl Rig {
        /// Reports the dragged window at `frame` and delivers a location change.
        fn move_to(&mut self, frame: Rect) {
            self.set(1, frame);
            self.time.set(self.time.get() + 100);
            self.c.on_location_change(1);
        }

        /// Sends a mouse movement to the controller as the hook would, one step of the clock later.
        fn pointer(&mut self, x: i32, y: i32) -> Option<Point> {
            self.time.set(self.time.get() + 100);
            self.c.on_pointer_move(Point { x, y })
        }

        fn batch_count(&self) -> usize {
            self.batches.borrow().len()
        }
    }

    #[test]
    fn a_move_with_attraction_applies_the_held_position() {
        // SNAP-020, SNAP-023, SNAP-040
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(NEAR); // the first location change classifies the move and is handled too
        assert_eq!(*r.batches.borrow(), vec![vec![(1, HELD)]]);
        assert_eq!((r.c.live.move_drags.get(), r.c.live.snaps.get(), r.c.live.drags.get()), (1, 1, 0));
        assert!(r.logged("snap hold right=500 target=edge-to-edge"));
    }

    #[test]
    fn a_move_end_reports_the_held_position_to_the_inventory() {
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        r.c.on_move_size_end(1);
        assert_eq!(*r.results.borrow(), vec![(1, Some(HELD))]);
    }

    #[test]
    fn a_move_end_without_a_held_axis_reports_none() {
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(Rect::new(400, 500, 600, 700));
        r.c.on_move_size_end(1);
        assert_eq!(*r.results.borrow(), vec![(1, None)]);
    }

    #[test]
    fn a_resize_end_does_not_report_a_move_result() {
        let mut r = rig(two(), None);
        r.c.on_move_size_start(1);
        r.set(1, Rect::from_xywh(0, 0, 1820, 1440));
        r.c.on_location_change(1);
        r.c.on_move_size_end(1);
        assert!(r.results.borrow().is_empty());
    }

    #[test]
    fn a_free_position_is_not_moved() {
        // SNAP-023
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(Rect::new(100, 250, 300, 450));
        assert!(r.batches.borrow().is_empty());
    }

    #[test]
    fn the_own_move_echo_is_ignored_and_the_next_free_position_is_handled() {
        // SNAP-024, SNAP-025
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        assert_eq!(r.batch_count(), 1);
        r.move_to(HELD); // Windows reports the position we just set
        assert_eq!(r.batch_count(), 1);
        r.move_to(Rect::new(296, 250, 496, 450)); // no throttling of the dragged window
        assert_eq!(r.batch_count(), 2);
        assert_eq!(r.batches.borrow()[1][0].1, HELD);
    }

    #[test]
    fn the_echo_as_read_back_from_the_mover_is_ignored_too() {
        // SNAP-024
        let mut r = move_rig(false);
        let forced = Rect::new(301, 250, 501, 450);
        r.force.borrow_mut().insert(1, forced);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        assert_eq!(r.batch_count(), 1);
        r.move_to(forced);
        assert_eq!(r.batch_count(), 1);
    }

    #[test]
    fn sliding_and_breakaway() {
        // SNAP-022, SNAP-041
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        r.move_to(Rect::new(292, 300, 492, 500)); // the window slides down while held
        assert_eq!(r.batch_count(), 2);
        assert_eq!(r.batches.borrow()[1][0].1, Rect::new(300, 300, 500, 500));
        // 30 px from the line: released, the window is already at the free position
        r.move_to(Rect::new(270, 300, 470, 500));
        assert_eq!(r.batch_count(), 2);
        assert!(r.logged("snap release right=500 reason=breakaway"));
    }

    #[test]
    fn a_fast_move_does_not_snap() {
        // SNAP-012
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(Rect::new(100, 250, 300, 450));
        r.time.set(r.time.get() + 5);
        r.set(1, NEAR);
        r.c.on_location_change(1); // 195 px in 5 ms
        assert!(r.batches.borrow().is_empty());
        assert_eq!(r.c.live.snaps.get(), 0);
    }

    #[test]
    fn the_end_applies_the_held_position_once_when_windows_put_the_window_back_at_the_free_position() {
        // SNAP-050, SNAP-052
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        assert_eq!(r.batch_count(), 1);
        r.set(1, NEAR);
        r.c.on_move_size_end(1);
        assert_eq!(r.batch_count(), 2);
        assert_eq!(r.batches.borrow()[1][0].1, HELD);
        assert!(!r.armed.borrow().iter().any(|a| a.0 == TimerKind::Verify));
        assert!(r.logged("move end id=1 snaps=1 applied"));
        r.c.on_timer(TimerKind::Verify);
        r.c.on_timer(TimerKind::Settle);
        r.c.on_timer(TimerKind::Flush);
        assert_eq!(r.batch_count(), 2);
    }

    #[test]
    fn the_end_does_nothing_when_the_window_is_already_held() {
        // SNAP-050
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        r.set(1, HELD);
        r.c.on_move_size_end(1);
        assert_eq!(r.batch_count(), 1);
    }

    #[test]
    fn the_end_does_nothing_when_another_tool_placed_the_window() {
        // SNAP-051: FancyZones or Windows Snap
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        r.set(1, Rect::new(0, 0, 500, 800));
        r.c.on_move_size_end(1);
        assert_eq!(r.batch_count(), 1);
    }

    #[test]
    fn the_end_does_nothing_when_the_size_changed() {
        // SNAP-051
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        r.move_to(Rect::new(296, 250, 496, 500)); // another tool resized the window during the drag
        let before = r.batch_count();
        r.c.on_move_size_end(1);
        assert_eq!(r.batch_count(), before);
    }

    #[test]
    fn the_end_does_nothing_when_nothing_is_held_or_the_frame_is_unreadable() {
        // SNAP-050
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(Rect::new(100, 250, 300, 450));
        r.c.on_move_size_end(1);
        assert!(r.batches.borrow().is_empty());
        assert!(r.logged("move end id=1 snaps=0"));
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        r.frames.borrow_mut().remove(&1);
        r.c.on_move_size_end(1);
        assert_eq!(r.batch_count(), 1);
    }

    #[test]
    fn attraction_still_handles_moves_when_coupling_is_off() {
        let mut r = move_rig(false);
        r.c.live.active.set(false);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        assert_eq!(*r.batches.borrow(), vec![vec![(1, HELD)]]);
        r.c.on_move_size_end(1);
        let before = r.batch_count();
        r.c.on_move_size_start(1);
        r.set(1, Rect::new(200, 250, 450, 450)); // a resize is ignored without coupling
        r.c.on_location_change(1);
        r.c.on_move_size_end(1);
        assert_eq!(r.c.live.drags.get(), 0);
        assert_eq!(r.batch_count(), before);
    }

    #[test]
    fn resizes_stay_glued_when_attraction_is_off() {
        let mut r = move_rig(false);
        r.c.live.snap_active.set(false);
        r.snap.borrow_mut().windows = vec![
            WinInfo { id: 1, frame: Rect::new(0, 0, 500, 800), min_w: 1, min_h: 1 },
            WinInfo { id: 2, frame: Rect::new(500, 0, 1000, 800), min_w: 1, min_h: 1 },
        ];
        r.set(1, Rect::new(0, 0, 500, 800));
        r.c.on_move_size_start(1);
        r.set(1, Rect::new(0, 0, 600, 800));
        r.c.on_location_change(1);
        assert_eq!(*r.batches.borrow(), vec![vec![(2, Rect::new(600, 0, 1000, 800))]]);
    }

    #[test]
    fn an_ineligible_dragged_window_is_ignored_when_moved() {
        let mut r = move_rig(false);
        r.snap.borrow_mut().windows.remove(0);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        assert!(r.batches.borrow().is_empty());
    }

    #[test]
    fn distances_are_scaled_by_the_dpi() {
        // SNAP-010
        let free = Rect::new(280, 250, 480, 450); // 20 px from window 2
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(free);
        assert!(r.batches.borrow().is_empty());
        r.snap.borrow_mut().dpi_scale = 2.0; // 16 px become 32 px
        r.c.on_move_size_end(1);
        r.c.on_move_size_start(1);
        r.move_to(free);
        assert_eq!(*r.batches.borrow(), vec![vec![(1, HELD)]]);
    }

    #[test]
    fn options_apply_on_the_next_drag() {
        // SNAP-032, SNAP-043
        let mut r = move_rig(false);
        let o = SnapOptions { windows: false, ..SnapOptions::default() };
        r.c.live.snap.set(o);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        assert!(r.batches.borrow().is_empty());
        r.c.on_move_size_end(1);
        r.c.live.snap.set(SnapOptions { windows: true, distance_px: 0, ..o });
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        assert!(r.batches.borrow().is_empty()); // 5 px is out of a 0 px pull-in
    }

    #[test]
    fn a_side_on_another_monitor_is_not_a_target() {
        // SNAP-031
        let mut r = move_rig(false);
        {
            let mut s = r.snap.borrow_mut();
            s.windows = vec![WinInfo { id: 1, frame: Rect::new(700, 250, 900, 450), min_w: 1, min_h: 1 }];
            s.work_areas = vec![s.work_area, Rect::new(1000, 0, 2000, 800)];
        }
        r.set(1, Rect::new(700, 250, 900, 450));
        r.c.on_move_size_start(1);
        r.move_to(Rect::new(795, 250, 995, 450));
        assert!(r.batches.borrow().is_empty());
        r.move_to(Rect::new(600, 250, 800, 450)); // nothing near
        assert!(r.batches.borrow().is_empty());
    }

    #[test]
    fn a_move_on_a_screen_edge_is_logged() {
        // SNAP-060
        let mut r = move_rig(false);
        r.snap.borrow_mut().windows.truncate(1);
        r.c.on_move_size_start(1);
        r.move_to(Rect::new(10, 250, 210, 450));
        assert_eq!(*r.batches.borrow(), vec![vec![(1, Rect::new(0, 250, 200, 450))]]);
        assert!(r.logged("snap hold left=0 target=screen"));
        assert!(r.logged("move start id=1"));
        r.move_to(Rect::new(-30, 250, 170, 450));
        assert!(r.logged("snap release left=0 reason=breakaway"));
        r.c.on_move_size_end(1);
        assert!(r.logged("move end id=1 snaps=1"));
    }

    #[test]
    fn a_slide_off_a_window_is_logged_as_target_gone() {
        // SNAP-035, SNAP-060
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        r.move_to(Rect::new(295, 600, 495, 800)); // far below window 2: it is no longer a target
        assert!(r.logged("reason=target-gone"));
    }

    #[test]
    fn an_aligned_hold_is_logged() {
        // SNAP-034, SNAP-060
        let mut r = move_rig(false);
        r.c.on_move_size_start(1);
        r.move_to(Rect::new(505, 410, 705, 610)); // below window 2, left edge 5 px from its left edge
        assert!(r.logged("target=aligned"));
    }

    #[test]
    fn a_window_hidden_behind_another_does_not_attract() {
        // SNAP-038: window 3 sits above window 2 and covers it completely.
        let mut r = move_rig(false);
        {
            let mut s = r.snap.borrow_mut();
            let (f1, f2) = (s.windows[0].frame, s.windows[1].frame);
            s.stack = vec![(1, f1), (3, Rect::new(450, 150, 750, 450)), (2, f2)];
        }
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        assert!(r.batches.borrow().is_empty());
    }

    #[test]
    fn the_dragged_window_never_covers_a_target() {
        // SNAP-038: the dragged window is on top and overlaps window 2's left edge at drag start.
        let mut r = move_rig(false);
        {
            let mut s = r.snap.borrow_mut();
            let f2 = s.windows[1].frame;
            s.stack = vec![(1, Rect::new(450, 200, 650, 400)), (2, f2)];
        }
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        assert_eq!(*r.batches.borrow(), vec![vec![(1, HELD)]]);
    }

    // ---- magnetic attraction with the pointer hook ----

    /// Starts a move: the first location change leaves the window where it was.
    fn begin(r: &mut Rig) {
        r.c.on_move_size_start(1);
        r.move_to(Rect::new(200, 250, 400, 450));
    }

    #[test]
    fn a_move_starts_the_hook_and_holds_the_cursor_instead_of_moving_the_window() {
        // SNAP-070, SNAP-072: the cursor at (300, 260) grabs window 1 100 px right of its left edge.
        let mut r = move_rig(true);
        begin(&mut r);
        assert_eq!(r.hook.borrow().starts, 1);
        assert!(r.logged("move start id=1 pointer"));
        // Window 1's right edge would be 5 px short of window 2 (at 500): the cursor goes 5 px further instead.
        assert_eq!(r.pointer(395, 260), Some(Point { x: 400, y: 260 }));
        assert!(r.batches.borrow().is_empty());
        assert!(r.logged("snap hold right=500 target=edge-to-edge"));
    }

    #[test]
    fn a_free_movement_goes_through_unchanged() {
        // SNAP-072
        let mut r = move_rig(true);
        begin(&mut r);
        assert_eq!(r.pointer(310, 270), None);
    }

    #[test]
    fn location_changes_never_move_the_window_with_the_hook() {
        // SNAP-072
        let mut r = move_rig(true);
        begin(&mut r);
        r.move_to(NEAR);
        assert!(r.batches.borrow().is_empty());
    }

    #[test]
    fn the_end_stops_the_hook_and_applies_nothing() {
        // SNAP-075
        let mut r = move_rig(true);
        begin(&mut r);
        r.pointer(395, 260);
        r.set(1, HELD);
        r.c.on_move_size_end(1);
        assert_eq!(r.hook.borrow().stops, 1);
        assert!(r.batches.borrow().is_empty());
        assert!(r.logged("move end id=1 snaps=1 pointer"));
        assert_eq!(r.pointer(390, 260), None);
    }

    #[test]
    fn a_button_release_stops_the_hook_before_the_end_event() {
        // SNAP-075
        let mut r = move_rig(true);
        begin(&mut r);
        r.c.on_pointer_release();
        assert_eq!(r.hook.borrow().stops, 1);
        r.c.on_move_size_end(1);
        assert_eq!(r.hook.borrow().stops, 1);
    }

    #[test]
    fn a_new_drag_stops_a_hook_left_running() {
        // SNAP-075
        let mut r = move_rig(true);
        begin(&mut r);
        r.c.on_move_size_start(1);
        assert_eq!(r.hook.borrow().stops, 1);
    }

    #[test]
    fn without_the_hook_the_window_is_moved_back() {
        // SNAP-076
        let mut r = move_rig(true);
        r.hook.borrow_mut().fail = true;
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        assert_eq!(*r.batches.borrow(), vec![vec![(1, HELD)]]);
    }

    #[test]
    fn an_unreadable_cursor_stops_the_hook_and_falls_back() {
        // SNAP-076
        let mut r = move_rig(true);
        r.hook.borrow_mut().at = None;
        r.c.on_move_size_start(1);
        r.move_to(NEAR);
        assert_eq!((r.hook.borrow().starts, r.hook.borrow().stops), (1, 1));
        assert_eq!(r.batch_count(), 1);
    }

    #[test]
    fn a_resize_never_starts_the_hook() {
        // SNAP-002
        let mut r = move_rig(true);
        r.c.on_move_size_start(1);
        r.move_to(Rect::new(200, 250, 420, 450));
        assert_eq!(r.hook.borrow().starts, 0);
        assert_eq!(r.c.on_pointer_move(Point { x: 1, y: 1 }), None);
    }

    #[test]
    fn a_pointer_release_outside_a_move_does_nothing() {
        let mut r = move_rig(true);
        r.c.on_pointer_release();
        assert_eq!(r.hook.borrow().stops, 0);
    }
}
