//! `Win32Mover`: applies move batches with `DeferWindowPos` and reads back the resulting frames.

use crate::diagnostics::*;
use crate::misc::AppLog;
use crate::util::*;
use mb_app::{Log, WindowMover};
use mb_core::Rect;
use std::time::Instant;
use windows::Win32::Foundation::RECT;
use windows::Win32::UI::WindowsAndMessaging::*;

/// First failing call of a deferred batch.
struct BatchFailure {
    /// `begin`, `defer` or `end`.
    step: &'static str,
    /// Window of a failing `DeferWindowPos`, else 0.
    id: u64,
    /// Win32 error number.
    error: u32,
}

impl BatchFailure {
    fn new(step: &'static str, id: u64, e: &windows::core::Error) -> Self {
        Self { step, id, error: win32_code(e) }
    }
}

/// `WindowMover` over Win32. Falls back to per-window `SetWindowPos` and bans windows that refuse to move.
/// With a diagnostics log it also writes the `move:` lines described in `docs/spec/engine.md`.
pub struct Win32Mover {
    shared: Shared,
    diagnostics: Option<AppLog>,
}

impl Win32Mover {
    /// Creates the mover sharing offsets and the ban list with the inventory.
    pub fn new(shared: Shared) -> Self {
        Self { shared, diagnostics: None }
    }

    /// Like `new`, and writes the verbose `move:` lines to `log`.
    pub fn with_diagnostics(shared: Shared, log: AppLog) -> Self {
        Self { shared, diagnostics: Some(log) }
    }

    fn window_rect(&self, id: u64, f: Rect) -> Rect {
        let o = self.shared.offsets.borrow().get(&id).copied().unwrap_or(Rect::new(0, 0, 0, 0));
        Rect::new(f.l + o.l, f.t + o.t, f.r + o.r, f.b + o.b)
    }

    fn batch(items: &[(u64, Rect)], flags: SET_WINDOW_POS_FLAGS) -> Result<(), BatchFailure> {
        unsafe {
            let mut hdwp = BeginDeferWindowPos(items.len() as i32).map_err(|e| BatchFailure::new("begin", 0, &e))?;
            for &(id, r) in items {
                hdwp = DeferWindowPos(hdwp, hwnd_of(id), None, r.l, r.t, r.width(), r.height(), flags)
                    .map_err(|e| BatchFailure::new("defer", id, &e))?;
            }
            EndDeferWindowPos(hdwp).map_err(|e| BatchFailure::new("end", 0, &e))
        }
    }

    /// Writes the batch line and one line per window. `frames` are the requested visual frames, parallel to `items`
    /// (requested window rectangles); `actual` are the frames read back.
    fn log_batch(
        log: &AppLog,
        items: &[(u64, Rect)],
        frames: &[Rect],
        actual: &[Option<Rect>],
        summary: (bool, u32, u128),
    ) {
        let (deferred, flags, elapsed_us) = summary;
        let mut mismatches = 0;
        let mut lines = Vec::with_capacity(items.len());
        for (i, &(id, requested_window)) in items.iter().enumerate() {
            let mut now = RECT::default();
            let window_delta = if unsafe { GetWindowRect(hwnd_of(id), &raw mut now) }.is_ok() {
                delta(requested_window, from_rect(now))
            } else {
                Rect::new(0, 0, 0, 0)
            };
            if actual[i].is_some_and(|a| a != frames[i]) {
                mismatches += 1;
            }
            lines.push(window_line(id, frames[i], actual[i], window_delta));
        }
        let mode = if deferred { "deferred" } else { "individual" };
        log.debug(&batch_line(items.len(), mode, flags, elapsed_us, mismatches));
        for line in &lines {
            log.debug(line);
        }
    }
}

impl WindowMover for Win32Mover {
    fn apply(&mut self, moves: &[(u64, Rect)]) -> Vec<(u64, Rect)> {
        let flags = SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER;
        let mut items: Vec<(u64, Rect)> = Vec::with_capacity(moves.len());
        let mut frames: Vec<Rect> = Vec::new();
        for &(id, f) in moves {
            let banned = self.shared.banned.borrow().contains(&id);
            let hung = !banned && unsafe { IsHungAppWindow(hwnd_of(id)).as_bool() };
            if banned || hung {
                if let Some(log) = &self.diagnostics {
                    log.debug(&skip_line(id, if banned { "banned" } else { "hung" }));
                }
                continue;
            }
            items.push((id, self.window_rect(id, f)));
            if self.diagnostics.is_some() {
                frames.push(f);
            }
        }
        if items.is_empty() {
            return vec![];
        }
        let started = Instant::now();
        let mut deferred = true;
        if let Err(failure) = Self::batch(&items, flags) {
            deferred = false;
            if let Some(log) = &self.diagnostics {
                log.debug(&deferred_failure_line(failure.step, failure.id, failure.error));
            }
            for &(id, r) in &items {
                let res = unsafe { SetWindowPos(hwnd_of(id), None, r.l, r.t, r.width(), r.height(), flags) };
                if let Err(e) = res {
                    if let Some(log) = &self.diagnostics {
                        log.debug(&set_window_pos_failure_line(id, win32_code(&e)));
                    }
                    self.shared.banned.borrow_mut().insert(id);
                }
            }
        }
        let elapsed_us = started.elapsed().as_micros();
        let actual: Vec<Option<Rect>> = items.iter().map(|&(id, _)| frame_bounds(hwnd_of(id))).collect();
        if let Some(log) = &self.diagnostics {
            Self::log_batch(log, &items, &frames, &actual, (deferred, flags.0, elapsed_us));
        }
        items.iter().zip(actual).filter_map(|(&(id, _), f)| f.map(|f| (id, f))).collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn diagnostics_log_failures_and_the_batch_for_an_invalid_window() {
        let dir = std::env::temp_dir().join(format!("mb-win32-mover-{}", std::process::id()));
        let path = dir.join("mover.log");
        let log = AppLog::open(&path, false, true);
        let mut mover = Win32Mover::with_diagnostics(Shared::default(), log);
        let applied = mover.apply(&[(0x3039, Rect::new(0, 0, 100, 100))]);
        assert_eq!(applied, vec![]);
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(text.contains("move: deferred batch failed step="));
        assert!(text.contains("move: SetWindowPos failed hwnd=0x3039"));
        assert!(text.contains("move: batch windows=1 mode=individual"));
        assert!(text.contains("move: hwnd=0x3039 requested=0,0,100,100 actual=unavailable"));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn without_diagnostics_nothing_is_logged_and_banned_windows_are_skipped() {
        let shared = Shared::default();
        shared.banned.borrow_mut().insert(7);
        let mut mover = Win32Mover::new(shared);
        assert_eq!(mover.apply(&[(7, Rect::new(0, 0, 10, 10))]), vec![]);
    }
}
