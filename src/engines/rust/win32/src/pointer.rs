//! `Win32PointerHook`: a low-level mouse hook that lets the controller hold the cursor during a move (SNAP-070).

use crate::events::with_engine;
use mb_app::{PointerEvents, PointerHook};
use mb_core::Point;
use std::cell::Cell;
use std::ffi::c_void;
use windows::Win32::Foundation::{LPARAM, LRESULT, POINT, WPARAM};
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::UI::WindowsAndMessaging::{
    CallNextHookEx, GetCursorPos, SetCursorPos, SetWindowsHookExW, UnhookWindowsHookEx, HC_ACTION, HHOOK,
    MSLLHOOKSTRUCT, WH_MOUSE_LL, WM_LBUTTONUP, WM_MBUTTONUP, WM_MOUSEMOVE, WM_RBUTTONUP, WM_XBUTTONUP,
};

thread_local! {
    // The installed hook handle (0 when none), so the callback and `stop` find it on the UI thread.
    static HOOK: Cell<usize> = const { Cell::new(0) };
}

/// True for the release of any mouse button.
fn is_button_up(msg: u32) -> bool {
    matches!(msg, WM_LBUTTONUP | WM_RBUTTONUP | WM_MBUTTONUP | WM_XBUTTONUP)
}

unsafe extern "system" fn on_mouse(code: i32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    unsafe {
        if code == HC_ACTION as i32 {
            let msg = wp.0 as u32;
            if msg == WM_MOUSEMOVE {
                let m = &*(lp.0 as *const MSLLHOOKSTRUCT);
                let proposed = Point { x: m.pt.x, y: m.pt.y };
                if let Some(Some(to)) = with_engine(|h| PointerEvents::on_pointer_move(h, proposed)) {
                    let _ = SetCursorPos(to.x, to.y);
                    return LRESULT(1); // the original movement is replaced by the held cursor position
                }
            } else if is_button_up(msg) {
                let m = &*(lp.0 as *const MSLLHOOKSTRUCT);
                let cursor = Point { x: m.pt.x, y: m.pt.y };
                with_engine(|h| PointerEvents::on_pointer_release(h, cursor)); // may stop (unhook) this hook
            }
        }
        CallNextHookEx(None, code, wp, lp)
    }
}

/// `PointerHook` over a `WH_MOUSE_LL` hook, installed only between `start` and `stop` (SNAP-070). For each mouse
/// movement it asks the controller where the cursor may go; when that differs, it blocks the movement and puts the
/// cursor there with `SetCursorPos`, so Windows moves the window to the held position itself. A button release is
/// passed on to the controller. The hook runs on the installing thread, which must pump messages.
#[derive(Default)]
pub struct Win32PointerHook;

impl Win32PointerHook {
    /// Creates the adapter; no hook is installed until `start`.
    pub fn new() -> Self {
        Self
    }
}

impl PointerHook for Win32PointerHook {
    fn start(&mut self) -> bool {
        self.stop();
        unsafe {
            let Ok(hinst) = GetModuleHandleW(None) else { return false };
            match SetWindowsHookExW(WH_MOUSE_LL, Some(on_mouse), Some(hinst.into()), 0) {
                Ok(h) => {
                    HOOK.with(|c| c.set(h.0 as usize));
                    true
                }
                Err(_) => false,
            }
        }
    }

    fn stop(&mut self) {
        let raw = HOOK.with(|c| c.replace(0));
        if raw != 0 {
            unsafe {
                let _ = UnhookWindowsHookEx(HHOOK(raw as *mut c_void));
            }
        }
    }

    fn cursor(&mut self) -> Option<Point> {
        let mut p = POINT::default();
        unsafe { GetCursorPos(&raw mut p).ok()? };
        Some(Point { x: p.x, y: p.y })
    }

    fn set_cursor(&mut self, to: Point) {
        let _ = unsafe { SetCursorPos(to.x, to.y) };
    }
}

impl Drop for Win32PointerHook {
    fn drop(&mut self) {
        self.stop();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn only_button_releases_count_as_releases() {
        for m in [WM_LBUTTONUP, WM_RBUTTONUP, WM_MBUTTONUP, WM_XBUTTONUP] {
            assert!(is_button_up(m));
        }
        assert!(!is_button_up(WM_MOUSEMOVE));
    }

    #[test]
    fn stop_without_start_is_harmless() {
        let mut h = Win32PointerHook::new();
        h.stop();
        h.stop();
    }
}
