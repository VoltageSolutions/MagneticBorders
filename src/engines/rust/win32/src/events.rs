//! `WinEvent` hooks: MOVESIZESTART/END permanently, LOCATIONCHANGE only for the dragged window's process.
//! Callbacks reach the controller through a `thread_local` slot.

use crate::util::*;
use crate::Win32Error;
use mb_app::{DragEvents, TimerKind};
use std::cell::{Cell, RefCell};
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::Accessibility::{SetWinEventHook, UnhookWinEvent, HWINEVENTHOOK};
use windows::Win32::UI::WindowsAndMessaging::GetWindowThreadProcessId;

const EVENT_MOVESIZESTART: u32 = 0x000A;
const EVENT_MOVESIZEEND: u32 = 0x000B;
const EVENT_LOCATIONCHANGE: u32 = 0x800B;
const OUTOFCONTEXT_SKIPOWN: u32 = 0x0002;

thread_local! {
    // The single slot routing the C callbacks to the controller.
    static HANDLER: RefCell<Option<Box<dyn DragEvents>>> = const { RefCell::new(None) };
    static LOC_HOOK: Cell<usize> = const { Cell::new(0) };
    static DRAGGED: Cell<u64> = const { Cell::new(0) };
}

fn with_handler(f: impl FnOnce(&mut dyn DragEvents)) {
    HANDLER.with(|h| {
        if let Ok(mut g) = h.try_borrow_mut() {
            if let Some(b) = g.as_mut() {
                f(b.as_mut());
            }
        }
    });
}

pub(crate) fn dispatch_timer(kind: TimerKind) {
    with_handler(|h| h.on_timer(kind));
}

fn remove_loc_hook() {
    let raw = LOC_HOOK.with(|c| c.replace(0));
    if raw != 0 {
        unsafe {
            let _ = UnhookWinEvent(HWINEVENTHOOK(raw as *mut _));
        }
    }
}

unsafe extern "system" fn loc_proc(_: HWINEVENTHOOK, _ev: u32, hwnd: HWND, obj: i32, child: i32, _: u32, _: u32) {
    if obj == 0 && child == 0 && id_of(hwnd) == DRAGGED.with(std::cell::Cell::get) {
        with_handler(|h| h.on_location_change(id_of(hwnd)));
    }
}

unsafe extern "system" fn move_size_proc(_: HWINEVENTHOOK, ev: u32, hwnd: HWND, _: i32, _: i32, _: u32, _: u32) {
    unsafe {
        let id = id_of(hwnd);
        match ev {
            EVENT_MOVESIZESTART => {
                remove_loc_hook();
                DRAGGED.with(|d| d.set(id));
                let mut pid = 0u32;
                GetWindowThreadProcessId(hwnd, Some(&raw mut pid));
                let hook = SetWinEventHook(
                    EVENT_LOCATIONCHANGE,
                    EVENT_LOCATIONCHANGE,
                    None,
                    Some(loc_proc),
                    pid,
                    0,
                    OUTOFCONTEXT_SKIPOWN,
                );
                LOC_HOOK.with(|c| c.set(hook.0 as usize));
                with_handler(|h| h.on_move_size_start(id));
            }
            EVENT_MOVESIZEEND => {
                if id == DRAGGED.with(std::cell::Cell::get) {
                    with_handler(|h| h.on_move_size_end(id));
                }
                remove_loc_hook();
                DRAGGED.with(|d| d.set(0));
            }
            _ => {}
        }
    }
}

/// Permanent MOVESIZESTART..END hook; must live on the UI thread running the message loop.
pub struct EventSource {
    /// Handle of the permanent hook; unhooked on drop.
    hook: HWINEVENTHOOK,
}

impl EventSource {
    /// Stores `handler` in the thread-local slot and installs the hook. Fails when the hook cannot be set.
    pub fn install(handler: Box<dyn DragEvents>) -> Result<Self, Win32Error> {
        HANDLER.with(|h| *h.borrow_mut() = Some(handler));
        let hook = unsafe {
            SetWinEventHook(
                EVENT_MOVESIZESTART,
                EVENT_MOVESIZEEND,
                None,
                Some(move_size_proc),
                0,
                0,
                OUTOFCONTEXT_SKIPOWN,
            )
        };
        if hook.0.is_null() {
            return Err(windows::core::Error::from_thread().into());
        }
        Ok(Self { hook })
    }
}

impl Drop for EventSource {
    fn drop(&mut self) {
        remove_loc_hook();
        unsafe {
            let _ = UnhookWinEvent(self.hook);
        }
        HANDLER.with(|h| *h.borrow_mut() = None);
    }
}
