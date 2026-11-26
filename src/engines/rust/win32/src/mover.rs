//! `Win32Mover`: applies move batches with `DeferWindowPos` and reads back the resulting frames.

use crate::util::*;
use mb_app::WindowMover;
use mb_core::Rect;
use windows::Win32::UI::WindowsAndMessaging::*;

/// `WindowMover` over Win32. Falls back to per-window `SetWindowPos` and bans windows that refuse to move.
pub struct Win32Mover {
    shared: Shared,
}

impl Win32Mover {
    /// Creates the mover sharing offsets and the ban list with the inventory.
    pub fn new(shared: Shared) -> Self {
        Self { shared }
    }

    fn window_rect(&self, id: u64, f: Rect) -> Rect {
        let o = self.shared.offsets.borrow().get(&id).copied().unwrap_or(Rect::new(0, 0, 0, 0));
        Rect::new(f.l + o.l, f.t + o.t, f.r + o.r, f.b + o.b)
    }

    fn batch(items: &[(u64, Rect)], flags: SET_WINDOW_POS_FLAGS) -> windows::core::Result<()> {
        unsafe {
            let mut hdwp = BeginDeferWindowPos(items.len() as i32)?;
            for &(id, r) in items {
                hdwp = DeferWindowPos(hdwp, hwnd_of(id), None, r.l, r.t, r.width(), r.height(), flags)?;
            }
            EndDeferWindowPos(hdwp)
        }
    }
}

impl WindowMover for Win32Mover {
    fn apply(&mut self, moves: &[(u64, Rect)]) -> Vec<(u64, Rect)> {
        let flags = SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER;
        let items: Vec<(u64, Rect)> = moves
            .iter()
            .filter(|(id, _)| {
                !self.shared.banned.borrow().contains(id) && unsafe { !IsHungAppWindow(hwnd_of(*id)).as_bool() }
            })
            .map(|&(id, f)| (id, self.window_rect(id, f)))
            .collect();
        if items.is_empty() {
            return vec![];
        }
        if Self::batch(&items, flags).is_err() {
            for &(id, r) in &items {
                let res = unsafe { SetWindowPos(hwnd_of(id), None, r.l, r.t, r.width(), r.height(), flags) };
                if res.is_err() {
                    self.shared.banned.borrow_mut().insert(id);
                }
            }
        }
        items.iter().filter_map(|&(id, _)| frame_bounds(hwnd_of(id)).map(|f| (id, f))).collect()
    }
}
