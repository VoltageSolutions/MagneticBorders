//! `Win32Inventory`: window eligibility, monitor snapshot and frame lookup via `EnumWindows` and DWM.

use crate::util::*;
use mb_app::{Snapshot, WindowInventory};
use mb_core::{Rect, WinInfo};
use std::ffi::c_void;
use windows::core::{BOOL, PWSTR};
use windows::Win32::Foundation::{CloseHandle, HWND, LPARAM, RECT};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED};
use windows::Win32::Graphics::Gdi::{
    GetMonitorInfoW, MonitorFromWindow, HMONITOR, MONITORINFO, MONITOR_DEFAULTTONEAREST,
};
use windows::Win32::System::Threading::{
    GetCurrentProcessId, OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::HiDpi::GetDpiForWindow;
use windows::Win32::UI::WindowsAndMessaging::*;

/// `WindowInventory` over Win32. Shares `Shared` with the mover; UI thread only.
pub struct Win32Inventory {
    shared: Shared,
}

impl Win32Inventory {
    /// Creates the inventory and sets the excluded executable names.
    pub fn new(shared: Shared, excluded_apps: &[String]) -> Self {
        shared.set_excluded(excluded_apps);
        Self { shared }
    }

    fn exe_excluded(&self, pid: u32) -> bool {
        if self.shared.excluded.borrow().is_empty() {
            return false;
        }
        unsafe {
            let Ok(h) = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid) else { return false };
            let mut buf = [0u16; 520];
            let mut len = buf.len() as u32;
            let ok = QueryFullProcessImageNameW(h, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &raw mut len).is_ok();
            let _ = CloseHandle(h);
            if !ok {
                return false;
            }
            let path = String::from_utf16_lossy(&buf[..len as usize]).to_lowercase();
            let name = path.rsplit('\\').next().unwrap_or("");
            self.shared.excluded.borrow().iter().any(|e| e == name)
        }
    }

    fn eligible(&self, h: HWND, monitor: HMONITOR, own_pid: u32) -> bool {
        unsafe {
            if !IsWindowVisible(h).as_bool() || IsIconic(h).as_bool() || IsZoomed(h).as_bool() {
                return false;
            }
            let mut cloaked = 0u32;
            let _ = DwmGetWindowAttribute(h, DWMWA_CLOAKED, (&raw mut cloaked).cast::<c_void>(), 4);
            if cloaked != 0 {
                return false;
            }
            let style = GetWindowLongPtrW(h, GWL_STYLE) as u32;
            let ex = GetWindowLongPtrW(h, GWL_EXSTYLE) as u32;
            if style & WS_THICKFRAME.0 == 0 || ex & WS_EX_TOOLWINDOW.0 != 0 {
                return false;
            }
            if GetWindow(h, GW_OWNER).is_ok_and(|o| !o.is_invalid()) || GetWindowTextLengthW(h) == 0 {
                return false;
            }
            let mut pid = 0u32;
            GetWindowThreadProcessId(h, Some(&raw mut pid));
            if pid == own_pid || self.shared.banned.borrow().contains(&id_of(h)) || self.exe_excluded(pid) {
                return false;
            }
            MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) == monitor
        }
    }
}

struct Ctx<'a> {
    inv: &'a Win32Inventory,
    monitor: HMONITOR,
    pid: u32,
    out: Vec<WinInfo>,
    min: (i32, i32),
}

unsafe extern "system" fn enum_cb(h: HWND, lp: LPARAM) -> BOOL {
    unsafe {
        let ctx = &mut *(lp.0 as *mut Ctx);
        if ctx.inv.eligible(h, ctx.monitor, ctx.pid) {
            let mut wr = RECT::default();
            if let (Some(frame), Ok(())) = (frame_bounds(h), GetWindowRect(h, &raw mut wr)) {
                let wr = from_rect(wr);
                let id = id_of(h);
                ctx.inv
                    .shared
                    .offsets
                    .borrow_mut()
                    .insert(id, Rect::new(wr.l - frame.l, wr.t - frame.t, wr.r - frame.r, wr.b - frame.b));
                ctx.out.push(WinInfo { id, frame, min_w: ctx.min.0, min_h: ctx.min.1 });
            }
        }
        BOOL(1)
    }
}

impl WindowInventory for Win32Inventory {
    fn snapshot(&mut self, dragged_id: u64) -> Snapshot {
        let h = hwnd_of(dragged_id);
        unsafe {
            let monitor = MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST);
            let mut mi = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
            let _ = GetMonitorInfoW(monitor, &raw mut mi);
            let dpi = GetDpiForWindow(h);
            let mut ctx = Ctx {
                inv: self,
                monitor,
                pid: GetCurrentProcessId(),
                out: Vec::new(),
                min: (GetSystemMetrics(SM_CXMINTRACK), GetSystemMetrics(SM_CYMINTRACK)),
            };
            let _ = EnumWindows(Some(enum_cb), LPARAM(&raw mut ctx as isize));
            Snapshot {
                windows: ctx.out,
                work_area: from_rect(mi.rcWork),
                dpi_scale: if dpi == 0 { 1.0 } else { f64::from(dpi) / 96.0 },
            }
        }
    }

    fn frame_of(&mut self, id: u64) -> Option<Rect> {
        frame_bounds(hwnd_of(id))
    }
}
