//! `Win32Timer`: one-shot `Timer` over `SetTimer`; callbacks go to the controller slot in `events`.

use mb_app::{Timer, TimerKind};
use std::cell::Cell;
use windows::Win32::Foundation::HWND;
use windows::Win32::UI::WindowsAndMessaging::{KillTimer, SetTimer};

const KINDS: [TimerKind; 3] = [TimerKind::Flush, TimerKind::Settle, TimerKind::Verify];

thread_local! {
    // Live SetTimer ids per kind (0 = not armed); UI thread only.
    static IDS: Cell<[usize; 3]> = const { Cell::new([0; 3]) };
}

fn slot(kind: TimerKind) -> usize {
    KINDS.iter().position(|k| *k == kind).unwrap()
}

fn kill(i: usize) {
    let mut ids = IDS.get();
    if ids[i] != 0 {
        unsafe {
            let _ = KillTimer(None, ids[i]);
        }
        ids[i] = 0;
        IDS.set(ids);
    }
}

unsafe extern "system" fn timer_proc(_: HWND, _: u32, id: usize, _: u32) {
    unsafe {
        let ids = IDS.get();
        if let Some(i) = ids.iter().position(|&x| x == id && x != 0) {
            kill(i);
            crate::events::dispatch_timer(KINDS[i]);
        } else {
            let _ = KillTimer(None, id);
        }
    }
}

/// One-shot timers on the UI thread's message loop; fires via the controller slot.
pub struct Win32Timer;

impl Timer for Win32Timer {
    fn arm(&mut self, kind: TimerKind, ms: u64) {
        let i = slot(kind);
        kill(i);
        let id = unsafe { SetTimer(None, 0, ms.max(1) as u32, Some(timer_proc)) };
        let mut ids = IDS.get();
        ids[i] = id;
        IDS.set(ids);
    }

    fn cancel_all(&mut self) {
        for i in 0..3 {
            kill(i);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn each_timer_kind_has_its_own_slot() {
        assert_eq!([TimerKind::Flush, TimerKind::Settle, TimerKind::Verify].map(slot), [0, 1, 2]);
    }

    #[test]
    fn cancel_all_without_armed_timers_is_a_no_op() {
        Win32Timer.cancel_all();
        assert_eq!(IDS.get(), [0; 3]);
    }
}
