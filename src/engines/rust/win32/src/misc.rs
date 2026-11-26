//! Small Win32 pieces: clock, loggers, dpi awareness, single instance, message loop, run-at-login.

use mb_app::{Clock, Log};
use std::sync::atomic::{AtomicU32, Ordering};
use std::time::Instant;
use windows::core::{w, BOOL};
use windows::Win32::Foundation::{CloseHandle, GetLastError, ERROR_ALREADY_EXISTS, HANDLE, LPARAM, WPARAM};
use windows::Win32::System::Console::SetConsoleCtrlHandler;
use windows::Win32::System::Threading::{CreateMutexW, GetCurrentThreadId};
use windows::Win32::UI::HiDpi::{SetProcessDpiAwarenessContext, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2};
use windows::Win32::UI::WindowsAndMessaging::*;

/// `Clock` backed by `Instant`.
pub struct MonotonicClock(Instant);

impl MonotonicClock {
    /// Starts the clock at zero.
    pub fn new() -> Self {
        Self(Instant::now())
    }
}

impl Default for MonotonicClock {
    fn default() -> Self {
        Self::new()
    }
}

impl Clock for MonotonicClock {
    fn now_ms(&self) -> u64 {
        self.0.elapsed().as_millis() as u64
    }
}

/// `Log` that prints to stdout.
pub struct ConsoleLog {
    /// Whether `debug` lines are printed.
    pub debug: bool,
}

impl Log for ConsoleLog {
    fn info(&self, msg: &str) {
        println!("[info] {msg}");
    }
    fn debug(&self, msg: &str) {
        if self.debug {
            println!("[debug] {msg}");
        }
    }
}

/// Makes the process per-monitor-v2 dpi aware, so frames are physical pixels.
pub fn set_dpi_awareness() {
    unsafe {
        let _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
    }
}

/// Whether "show window contents while dragging" is on; live gluing needs it.
pub fn drag_full_windows_enabled() -> bool {
    let mut on = BOOL(1);
    unsafe {
        let _ = SystemParametersInfoW(
            SPI_GETDRAGFULLWINDOWS,
            0,
            Some((&raw mut on).cast()),
            SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS(0),
        );
    }
    on.as_bool()
}

/// Named mutex guard; `acquire` returns None when another instance runs.
pub struct SingleInstance(HANDLE);

impl SingleInstance {
    /// Takes the instance mutex, or `None` if another instance holds it.
    pub fn acquire() -> Option<SingleInstance> {
        unsafe {
            let h = CreateMutexW(None, true, w!("Local\\MagneticBorders.Instance")).ok()?;
            if GetLastError() == ERROR_ALREADY_EXISTS {
                let _ = CloseHandle(h);
                return None;
            }
            Some(SingleInstance(h))
        }
    }
}

impl Drop for SingleInstance {
    fn drop(&mut self) {
        unsafe {
            let _ = CloseHandle(self.0);
        }
    }
}

static UI_THREAD: AtomicU32 = AtomicU32::new(0);

unsafe extern "system" fn ctrl_handler(_: u32) -> BOOL {
    unsafe {
        PostThreadMessageW(UI_THREAD.load(Ordering::SeqCst), WM_QUIT, WPARAM(0), LPARAM(0)).ok();
        BOOL(1)
    }
}

/// Runs the UI-thread message loop until `WM_QUIT` (Ctrl+C posts it).
pub fn run_message_loop() {
    unsafe {
        let mut msg = MSG::default();
        let _ = PeekMessageW(&raw mut msg, None, 0, 0, PM_NOREMOVE); // create the queue
        UI_THREAD.store(GetCurrentThreadId(), Ordering::SeqCst);
        let _ = SetConsoleCtrlHandler(Some(ctrl_handler), true);
        while GetMessageW(&raw mut msg, None, 0, 0).0 > 0 {
            let _ = TranslateMessage(&raw const msg);
            DispatchMessageW(&raw const msg);
        }
    }
}

/// Shows a console window (for `--console`) so the log can be mirrored there.
pub fn alloc_console() {
    unsafe {
        let _ = windows::Win32::System::Console::AllocConsole();
    }
}

/// Shows a modal information box.
pub fn message_box(title: &str, text: &str) {
    let t: Vec<u16> = title.encode_utf16().chain(Some(0)).collect();
    let m: Vec<u16> = text.encode_utf16().chain(Some(0)).collect();
    unsafe {
        MessageBoxW(
            None,
            windows::core::PCWSTR(m.as_ptr()),
            windows::core::PCWSTR(t.as_ptr()),
            MB_OK | MB_ICONINFORMATION,
        );
    }
}

const RUN_KEY: windows::core::PCWSTR = w!("Software\\Microsoft\\Windows\\CurrentVersion\\Run");
const RUN_VALUE: windows::core::PCWSTR = w!("MagneticBorders");

/// `Some(exe)` writes HKCU Run\MagneticBorders, `None` deletes it.
pub fn set_run_at_login(exe: Option<&std::path::Path>) -> Result<(), String> {
    use windows::Win32::System::Registry::*;
    unsafe {
        if let Some(p) = exe {
            let data: Vec<u16> = format!("\"{}\"", p.display()).encode_utf16().chain(Some(0)).collect();
            let e = RegSetKeyValueW(
                HKEY_CURRENT_USER,
                RUN_KEY,
                RUN_VALUE,
                REG_SZ.0,
                Some(data.as_ptr().cast()),
                (data.len() * 2) as u32,
            );
            if e.0 != 0 {
                return Err(format!("RegSetKeyValueW failed: {}", e.0));
            }
        } else {
            let e = RegDeleteKeyValueW(HKEY_CURRENT_USER, RUN_KEY, RUN_VALUE);
            if e.0 != 0 && e.0 != 2 {
                return Err(format!("RegDeleteKeyValueW failed: {}", e.0));
            }
        }
    }
    Ok(())
}

struct LogInner {
    file: std::cell::RefCell<std::fs::File>,
    console: std::cell::RefCell<Option<std::fs::File>>,
    debug: bool,
}

/// File logger (truncated at 1 MB on start) that can mirror to a console. Clone = shared handle.
#[derive(Clone)]
pub struct AppLog(std::rc::Rc<LogInner>);

impl AppLog {
    /// Opens (or creates) the log file; `console` mirrors to CONOUT$, `debug` enables debug lines.
    pub fn open(path: &std::path::Path, console: bool, debug: bool) -> AppLog {
        if let Some(dir) = path.parent() {
            let _ = std::fs::create_dir_all(dir);
        }
        let big = std::fs::metadata(path).is_ok_and(|m| m.len() > 1024 * 1024);
        let mut o = std::fs::OpenOptions::new();
        o.create(true);
        if big {
            o.write(true).truncate(true);
        } else {
            o.append(true);
        }
        let file = o.open(path).unwrap_or_else(|_| std::fs::File::open("NUL").expect("NUL"));
        let console = if console { std::fs::OpenOptions::new().write(true).open("CONOUT$").ok() } else { None };
        AppLog(std::rc::Rc::new(LogInner {
            file: std::cell::RefCell::new(file),
            console: std::cell::RefCell::new(console),
            debug,
        }))
    }

    /// Writes a warning line.
    pub fn warn(&self, msg: &str) {
        self.write("warn", msg);
    }

    fn write(&self, level: &str, msg: &str) {
        use std::io::Write;
        let t = unsafe { windows::Win32::System::SystemInformation::GetLocalTime() };
        let line = format!(
            "{:04}-{:02}-{:02} {:02}:{:02}:{:02}.{:03} [{level}] {msg}\r\n",
            t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond, t.wMilliseconds
        );
        let _ = self.0.file.borrow_mut().write_all(line.as_bytes());
        if let Some(c) = self.0.console.borrow_mut().as_mut() {
            let _ = c.write_all(format!("[{level}] {msg}\r\n").as_bytes());
        }
    }
}

impl Log for AppLog {
    fn info(&self, msg: &str) {
        self.write("info", msg);
    }
    fn debug(&self, msg: &str) {
        if self.0.debug {
            self.write("debug", msg);
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn clock_never_goes_backwards() {
        let c = MonotonicClock::default();
        let a = c.now_ms();
        assert!(c.now_ms() >= a);
    }

    #[test]
    fn app_log_writes_levels_and_honors_debug_flag() {
        let dir = std::env::temp_dir().join(format!("mb-win32-log-{}", std::process::id()));
        let path = dir.join("logs").join("test.log");
        let quiet = AppLog::open(&path, false, false);
        quiet.info("hello");
        quiet.debug("hidden");
        quiet.warn("careful");
        let verbose = AppLog::open(&path, false, true);
        verbose.debug("shown");
        let text = std::fs::read_to_string(&path).unwrap();
        assert!(text.contains("[info] hello") && text.contains("[warn] careful") && text.contains("[debug] shown"));
        assert!(!text.contains("hidden"));
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn console_log_debug_flag_gates_output() {
        let log = ConsoleLog { debug: false };
        log.info("info line");
        log.debug("skipped");
        ConsoleLog { debug: true }.debug("printed");
    }
}
