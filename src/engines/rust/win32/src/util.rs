//! `Shared` state and HWND/RECT conversion helpers.

use mb_core::Rect;
use std::cell::RefCell;
use std::collections::{HashMap, HashSet};
use std::ffi::c_void;
use std::rc::Rc;
use windows::Win32::Foundation::{HWND, RECT};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_EXTENDED_FRAME_BOUNDS};

impl Shared {
    /// Replaces the excluded executable names (stored lower-cased).
    pub fn set_excluded(&self, apps: &[String]) {
        *self.excluded.borrow_mut() = apps.iter().map(|s| s.to_lowercase()).collect();
    }
}

/// State shared between inventory and mover (same UI thread).
#[derive(Clone, Default)]
pub struct Shared {
    /// window rect minus visual frame, per window id
    pub offsets: Rc<RefCell<HashMap<u64, Rect>>>,
    /// windows that failed to move (e.g. elevated); excluded for the session
    pub banned: Rc<RefCell<HashSet<u64>>>,
    /// lower-cased excluded exe names; updated live from settings
    pub excluded: Rc<RefCell<Vec<String>>>,
}

/// Window id back to an `HWND`.
pub fn hwnd_of(id: u64) -> HWND {
    HWND(id as usize as *mut c_void)
}

/// `HWND` to window id.
pub fn id_of(h: HWND) -> u64 {
    h.0 as usize as u64
}

/// Win32 `RECT` to domain `Rect`.
pub fn from_rect(r: RECT) -> Rect {
    Rect::new(r.left, r.top, r.right, r.bottom)
}

/// Visible frame (DWM extended frame bounds), or `None` when the window is gone.
pub fn frame_bounds(h: HWND) -> Option<Rect> {
    let mut r = RECT::default();
    unsafe {
        DwmGetWindowAttribute(
            h,
            DWMWA_EXTENDED_FRAME_BOUNDS,
            (&raw mut r).cast::<c_void>(),
            std::mem::size_of::<RECT>() as u32,
        )
        .ok()?;
    }
    Some(from_rect(r))
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn window_id_round_trips_through_hwnd() {
        for id in [0u64, 1, 0x1234_5678] {
            assert_eq!(id_of(hwnd_of(id)), id);
        }
    }

    #[test]
    fn from_rect_copies_the_four_sides() {
        let r = from_rect(RECT { left: 1, top: 2, right: 3, bottom: 4 });
        assert_eq!(r, Rect::new(1, 2, 3, 4));
    }

    #[test]
    fn set_excluded_lower_cases_names() {
        let s = Shared::default();
        s.set_excluded(&["First.EXE".to_string(), "a.exe".to_string()]);
        assert_eq!(*s.excluded.borrow(), vec!["first.exe".to_string(), "a.exe".to_string()]);
    }

    #[test]
    fn frame_of_an_invalid_window_is_none() {
        assert!(frame_bounds(hwnd_of(0)).is_none());
    }
}
