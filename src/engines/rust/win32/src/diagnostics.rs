//! Verbose diagnostic lines of the Win32 adapters: pure text helpers and the start-up system report.
//! The same text is produced by the C++ and C# engines (`docs/spec/engine.md`).

use crate::util::from_rect;
use mb_app::Log;
use mb_core::Rect;
use std::ffi::c_void;
use windows::core::{w, BOOL};
use windows::Win32::Foundation::{LPARAM, RECT};
use windows::Win32::Graphics::Dwm::DwmIsCompositionEnabled;
use windows::Win32::Graphics::Gdi::{EnumDisplayMonitors, GetMonitorInfoW, HDC, HMONITOR, MONITORINFO};
use windows::Win32::System::Registry::{RegGetValueW, HKEY_LOCAL_MACHINE, RRF_RT_REG_DWORD, RRF_RT_REG_SZ};
use windows::Win32::UI::HiDpi::{
    AreDpiAwarenessContextsEqual, GetAwarenessFromDpiAwarenessContext, GetDpiForMonitor, GetThreadDpiAwarenessContext,
    DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, DPI_AWARENESS_PER_MONITOR_AWARE, DPI_AWARENESS_SYSTEM_AWARE,
    DPI_AWARENESS_UNAWARE, MDT_EFFECTIVE_DPI,
};

/// `MONITORINFO.dwFlags` bit of the primary monitor.
const MONITORINFOF_PRIMARY: u32 = 1;

/// Formats a rectangle as `l,t,r,b`.
pub(crate) fn rect_text(r: Rect) -> String {
    format!("{},{},{},{}", r.l, r.t, r.r, r.b)
}

/// Returns `actual - requested` per side.
pub(crate) fn delta(requested: Rect, actual: Rect) -> Rect {
    Rect::new(actual.l - requested.l, actual.t - requested.t, actual.r - requested.r, actual.b - requested.b)
}

/// Formats a window id as lower-case hex with a `0x` prefix.
pub(crate) fn hex_id(id: u64) -> String {
    format!("0x{id:x}")
}

/// Formats `SetWindowPos` flags as `0x` and four hex digits.
pub(crate) fn flags_text(flags: u32) -> String {
    format!("0x{flags:04x}")
}

/// Returns the Win32 error number of a `windows` error (the `HRESULT_FROM_WIN32` wrapper is removed).
pub(crate) fn win32_code(e: &windows::core::Error) -> u32 {
    let hr = e.code().0 as u32;
    if (hr & 0xFFFF_0000) == 0x8007_0000 {
        hr & 0xFFFF
    } else {
        hr
    }
}

/// Line for one applied batch. `mode` is `deferred` or `individual`.
pub(crate) fn batch_line(windows: usize, mode: &str, flags: u32, elapsed_us: u128, mismatches: usize) -> String {
    format!(
        "move: batch windows={windows} mode={mode} flags={} elapsed_us={elapsed_us} mismatches={mismatches}",
        flags_text(flags)
    )
}

/// Line for one window after a batch: the requested visual frame, the visual frame read back (`unavailable` when the
/// read fails), `delta=` (read back minus requested) when they differ, and `window_delta=` (`GetWindowRect` minus the
/// requested window rectangle) when that differs.
pub(crate) fn window_line(id: u64, requested: Rect, actual: Option<Rect>, window_delta: Rect) -> String {
    let actual_text = actual.map_or_else(|| "unavailable".to_string(), rect_text);
    let delta_part = match actual {
        Some(a) if a != requested => format!(" delta={}", rect_text(delta(requested, a))),
        _ => String::new(),
    };
    let window_part = if window_delta == Rect::new(0, 0, 0, 0) {
        String::new()
    } else {
        format!(" window_delta={}", rect_text(window_delta))
    };
    format!(
        "move: hwnd={} requested={} actual={actual_text}{delta_part}{window_part}",
        hex_id(id),
        rect_text(requested)
    )
}

/// Line for a failed deferred batch. `step` is `begin`, `defer` or `end`; `id` is 0 when no window is involved.
pub(crate) fn deferred_failure_line(step: &str, id: u64, error: u32) -> String {
    format!("move: deferred batch failed step={step} hwnd={} error={error}, falling back to SetWindowPos", hex_id(id))
}

/// Line for a failed `SetWindowPos` call.
pub(crate) fn set_window_pos_failure_line(id: u64, error: u32) -> String {
    format!("move: SetWindowPos failed hwnd={} error={error}", hex_id(id))
}

/// Line for a window left out of a batch. `reason` is `no-offsets`, `banned` or `hung`.
pub(crate) fn skip_line(id: u64, reason: &str) -> String {
    format!("move: skipped hwnd={} reason={reason}", hex_id(id))
}

/// Formats an optional rectangle as `l,t,r,b`, or `unavailable` when it could not be read.
pub(crate) fn rect_or_unavailable(r: Option<Rect>) -> String {
    r.map_or_else(|| "unavailable".to_string(), rect_text)
}

/// Returns `rect - frame` per side: the invisible border between `GetWindowRect` and the visual frame.
pub(crate) fn border(rect: Rect, frame: Rect) -> Rect {
    delta(frame, rect)
}

/// What the inventory reads about one window for a `diag: window` line.
pub(crate) struct Candidate<'a> {
    pub id: u64,
    /// DWM extended frame bounds.
    pub frame: Rect,
    /// `GetWindowRect`.
    pub rect: Rect,
    pub dpi: u32,
    /// Window class name; empty when unreadable.
    pub window_class: &'a str,
    /// Process executable file name; empty when unreadable.
    pub exe: &'a str,
    pub dragged: bool,
}

/// Line for one candidate window of a drag snapshot. An empty class or exe is written as `unknown`.
pub(crate) fn candidate_line(c: &Candidate) -> String {
    let or_unknown = |s: &str| if s.is_empty() { "unknown".to_string() } else { s.to_string() };
    format!(
        "diag: window id={} frame={} rect={} border={} dpi={} class={} exe={} dragged={}",
        c.id,
        rect_text(c.frame),
        rect_text(c.rect),
        rect_text(border(c.rect, c.frame)),
        c.dpi,
        or_unknown(c.window_class),
        or_unknown(c.exe),
        if c.dragged { "yes" } else { "no" }
    )
}

/// Line for a window at the end of a move: where it is (`frame`, `rect`; `unavailable` when unreadable) and the
/// position Magnetic attraction held (`none` when no axis was held).
pub(crate) fn move_result_line(id: u64, frame: Option<Rect>, rect: Option<Rect>, held: Option<Rect>) -> String {
    format!(
        "diag: move result id={id} frame={} rect={} held={}",
        rect_or_unavailable(frame),
        rect_or_unavailable(rect),
        held.map_or_else(|| "none".to_string(), rect_text)
    )
}

/// Start-up line with the system facts. `dpi_awareness` is `unaware`, `system`, `per-monitor`, `per-monitor-v2` or
/// `unknown`; `os_build` is `<build>.<revision>` or `unknown`.
pub(crate) fn system_line(os_build: &str, dpi_awareness: &str, dwm_composition: bool, monitors: usize) -> String {
    let composition = if dwm_composition { "on" } else { "off" };
    format!("diag: system os_build={os_build} dpi_awareness={dpi_awareness} dwm_composition={composition} monitors={monitors}")
}

/// Start-up line for one monitor.
pub(crate) fn monitor_line(index: usize, bounds: Rect, work: Rect, dpi: (u32, u32), primary: bool) -> String {
    format!(
        "diag: monitor {index} rect={} work={} dpi={}x{} primary={}",
        rect_text(bounds),
        rect_text(work),
        dpi.0,
        dpi.1,
        if primary { "yes" } else { "no" }
    )
}

/// Returns `<build>.<revision>` from the registry, or `unknown`.
fn os_build() -> String {
    let key = w!("SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion");
    let mut build = [0u16; 16];
    let mut size = std::mem::size_of_val(&build) as u32;
    let error = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            key,
            w!("CurrentBuildNumber"),
            RRF_RT_REG_SZ,
            None,
            Some(build.as_mut_ptr().cast::<c_void>()),
            Some(&raw mut size),
        )
    };
    if error.0 != 0 {
        return "unknown".to_string();
    }
    let len = build.iter().position(|&c| c == 0).unwrap_or(build.len());
    let text = String::from_utf16_lossy(&build[..len]);
    let mut revision = 0u32;
    size = std::mem::size_of::<u32>() as u32;
    let error = unsafe {
        RegGetValueW(
            HKEY_LOCAL_MACHINE,
            key,
            w!("UBR"),
            RRF_RT_REG_DWORD,
            None,
            Some((&raw mut revision).cast::<c_void>()),
            Some(&raw mut size),
        )
    };
    if error.0 == 0 {
        format!("{text}.{revision}")
    } else {
        text
    }
}

/// Returns the DPI awareness of the calling thread.
fn dpi_awareness() -> &'static str {
    unsafe {
        let ctx = GetThreadDpiAwarenessContext();
        if AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2).as_bool() {
            return "per-monitor-v2";
        }
        match GetAwarenessFromDpiAwarenessContext(ctx) {
            DPI_AWARENESS_UNAWARE => "unaware",
            DPI_AWARENESS_SYSTEM_AWARE => "system",
            DPI_AWARENESS_PER_MONITOR_AWARE => "per-monitor",
            _ => "unknown",
        }
    }
}

/// `EnumDisplayMonitors` callback: appends one monitor line to the `Vec<String>` behind `data`.
unsafe extern "system" fn on_monitor(monitor: HMONITOR, _dc: HDC, _rect: *mut RECT, data: LPARAM) -> BOOL {
    let lines = unsafe { &mut *(data.0 as *mut Vec<String>) };
    let mut info = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
    if unsafe { GetMonitorInfoW(monitor, &raw mut info) }.as_bool() {
        let (mut dpi_x, mut dpi_y) = (0u32, 0u32);
        if unsafe { GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &raw mut dpi_x, &raw mut dpi_y) }.is_err() {
            dpi_x = 0;
            dpi_y = 0;
        }
        let primary = (info.dwFlags & MONITORINFOF_PRIMARY) != 0;
        lines.push(monitor_line(
            lines.len(),
            from_rect(info.rcMonitor),
            from_rect(info.rcWork),
            (dpi_x, dpi_y),
            primary,
        ));
    }
    BOOL(1)
}

/// Writes the start-up system lines (`diag: system ...` and one `diag: monitor ...` per monitor) with `log.debug`.
/// UI thread; called once when verbose logging is on.
pub fn log_system_diagnostics(log: &dyn Log) {
    let mut monitors: Vec<String> = Vec::new();
    unsafe {
        let _ = EnumDisplayMonitors(None, None, Some(on_monitor), LPARAM((&raw mut monitors) as isize));
    }
    let composition = unsafe { DwmIsCompositionEnabled() }.is_ok_and(BOOL::as_bool);
    log.debug(&system_line(&os_build(), dpi_awareness(), composition, monitors.len()));
    for line in &monitors {
        log.debug(line);
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::cell::RefCell;
    use windows::core::HRESULT;

    #[derive(Default)]
    struct CaptureLog(RefCell<Vec<String>>);

    impl Log for CaptureLog {
        fn info(&self, msg: &str) {
            self.0.borrow_mut().push(msg.to_string());
        }
        fn debug(&self, msg: &str) {
            self.0.borrow_mut().push(msg.to_string());
        }
    }

    #[test]
    fn rect_and_delta_text() {
        assert_eq!(rect_text(Rect::new(1, -2, 30, 40)), "1,-2,30,40");
        assert_eq!(delta(Rect::new(0, 0, 100, 100), Rect::new(1, 0, 98, 101)), Rect::new(1, 0, -2, 1));
    }

    #[test]
    fn hex_and_flags_text() {
        assert_eq!(hex_id(0x1a2b), "0x1a2b");
        assert_eq!(flags_text(0x214), "0x0214");
    }

    #[test]
    fn win32_code_strips_the_hresult_wrapper() {
        let wrapped = windows::core::Error::from_hresult(HRESULT(0x8007_0005_u32 as i32));
        assert_eq!(win32_code(&wrapped), 5);
        let other = windows::core::Error::from_hresult(HRESULT(0x8000_4005_u32 as i32));
        assert_eq!(win32_code(&other), 0x8000_4005);
    }

    #[test]
    fn candidate_line_text() {
        let frame = Rect::new(1000, 100, 1900, 890);
        let rect = Rect::new(990, 100, 1910, 900);
        assert_eq!(border(rect, frame), Rect::new(-10, 0, 10, 10));
        let c = Candidate {
            id: 12,
            frame,
            rect,
            dpi: 120,
            window_class: "ConsoleWindowClass",
            exe: "WindowsTerminal.exe",
            dragged: true,
        };
        assert_eq!(
            candidate_line(&c),
            "diag: window id=12 frame=1000,100,1900,890 rect=990,100,1910,900 border=-10,0,10,10 dpi=120 class=ConsoleWindowClass exe=WindowsTerminal.exe dragged=yes"
        );
        let r = Rect::new(0, 0, 10, 10);
        let c = Candidate { id: 3, frame: r, rect: r, dpi: 96, window_class: "", exe: "", dragged: false };
        assert_eq!(
            candidate_line(&c),
            "diag: window id=3 frame=0,0,10,10 rect=0,0,10,10 border=0,0,0,0 dpi=96 class=unknown exe=unknown dragged=no"
        );
    }

    #[test]
    fn move_result_line_text() {
        let r = Rect::new(1, 2, 3, 4);
        assert_eq!(
            move_result_line(12, Some(r), Some(Rect::new(0, 2, 4, 5)), Some(r)),
            "diag: move result id=12 frame=1,2,3,4 rect=0,2,4,5 held=1,2,3,4"
        );
        assert_eq!(
            move_result_line(12, None, None, None),
            "diag: move result id=12 frame=unavailable rect=unavailable held=none"
        );
    }

    #[test]
    fn batch_line_text() {
        assert_eq!(
            batch_line(3, "deferred", 0x214, 812, 1),
            "move: batch windows=3 mode=deferred flags=0x0214 elapsed_us=812 mismatches=1"
        );
    }

    #[test]
    fn window_line_omits_deltas_when_the_window_matches() {
        let r = Rect::new(10, 10, 210, 110);
        assert_eq!(
            window_line(0xabc, r, Some(r), Rect::new(0, 0, 0, 0)),
            "move: hwnd=0xabc requested=10,10,210,110 actual=10,10,210,110"
        );
    }

    #[test]
    fn window_line_reports_a_refused_size() {
        let line = window_line(1, Rect::new(0, 0, 100, 100), Some(Rect::new(0, 0, 120, 100)), Rect::new(0, 0, 20, 0));
        assert_eq!(
            line,
            "move: hwnd=0x1 requested=0,0,100,100 actual=0,0,120,100 delta=0,0,20,0 window_delta=0,0,20,0"
        );
    }

    #[test]
    fn window_line_without_read_back() {
        assert_eq!(
            window_line(2, Rect::new(0, 0, 1, 1), None, Rect::new(0, 0, 0, 0)),
            "move: hwnd=0x2 requested=0,0,1,1 actual=unavailable"
        );
    }

    #[test]
    fn failure_and_skip_lines() {
        assert_eq!(
            deferred_failure_line("defer", 0x10, 5),
            "move: deferred batch failed step=defer hwnd=0x10 error=5, falling back to SetWindowPos"
        );
        assert_eq!(set_window_pos_failure_line(0x10, 5), "move: SetWindowPos failed hwnd=0x10 error=5");
        assert_eq!(skip_line(0x10, "hung"), "move: skipped hwnd=0x10 reason=hung");
    }

    #[test]
    fn system_lines() {
        assert_eq!(
            system_line("22631.3880", "per-monitor-v2", true, 2),
            "diag: system os_build=22631.3880 dpi_awareness=per-monitor-v2 dwm_composition=on monitors=2"
        );
        assert_eq!(
            monitor_line(0, Rect::new(0, 0, 1920, 1200), Rect::new(0, 0, 1920, 1160), (144, 144), true),
            "diag: monitor 0 rect=0,0,1920,1200 work=0,0,1920,1160 dpi=144x144 primary=yes"
        );
    }

    #[test]
    fn system_report_starts_with_the_system_line() {
        let log = CaptureLog::default();
        log_system_diagnostics(&log);
        let lines = log.0.borrow();
        assert!(lines[0].starts_with("diag: system os_build="));
    }
}
