//! Tray icon, hidden message window, settings-file watcher and named-pipe server.
//! The watcher and pipe threads only post messages to the hidden window; `AppHandler` runs on the UI thread.
use crate::Win32Error;
use std::cell::RefCell;
use std::fs::File;
use std::io::{Read, Write};
use std::mem::ManuallyDrop;
use std::os::windows::io::FromRawHandle;
use std::path::{Path, PathBuf};
use std::sync::mpsc;
use std::time::Duration;
use windows::core::{w, PCWSTR, PWSTR};
use windows::Win32::Foundation::{CloseHandle, HANDLE, HWND, LPARAM, LRESULT, POINT, WPARAM};
use windows::Win32::Storage::FileSystem::*;
use windows::Win32::System::LibraryLoader::GetModuleHandleW;
use windows::Win32::System::Pipes::*;
use windows::Win32::System::Threading::{
    CreateProcessW, PROCESS_CREATION_FLAGS, PROCESS_INFORMATION, STARTF_FORCEOFFFEEDBACK, STARTUPINFOW,
};
use windows::Win32::UI::Shell::*;
use windows::Win32::UI::WindowsAndMessaging::*;

/// Name of the engine's named pipe.
pub const PIPE_NAME: &str = r"\\.\pipe\MagneticBorders";
const WM_TRAY: u32 = WM_APP + 1;
const WM_SETTINGS: u32 = WM_APP + 2;
const WM_PIPE: u32 = WM_APP + 3;
const DEBOUNCE_TIMER: usize = 1;
const ID_ENABLED: usize = 1;
const ID_SETTINGS: usize = 2;
const ID_EXIT: usize = 3;
const TRAY_UID: u32 = 1;

/// Engine-side behaviour behind the window; called on the UI thread only.
pub trait AppHandler {
    /// Settings file changed (debounced) or a reload was requested.
    fn on_settings_changed(&mut self);
    /// One pipe request line -> (response line, quit requested).
    fn on_pipe(&mut self, line: &str) -> (String, bool);
    /// Whether the saved master switch (`enabled`) is on.
    fn is_enabled(&self) -> bool;
    /// Flips the saved master switch in `settings.json`; the settings watcher then applies it.
    fn toggle_enabled(&mut self);
}

/// Static facts the tray shows and the watcher needs.
pub struct AppInfo {
    /// e.g. "Magnetic Borders (Rust)"
    pub title: String,
    /// Version shown in the menu header.
    pub version: String,
    /// Folder holding `settings.json`; watched for changes.
    pub data_dir: PathBuf,
}

thread_local! {
    static HANDLER: RefCell<Option<Box<dyn AppHandler>>> = const { RefCell::new(None) };
    static INFO: RefCell<Option<AppInfo>> = const { RefCell::new(None) };
    static TASKBAR_CREATED: std::cell::Cell<u32> = const { std::cell::Cell::new(0) };
}

struct PipeReq {
    line: String,
    reply: mpsc::Sender<String>,
}

/// Owns the hidden window and tray icon; dropping it removes both and clears the handler slot.
pub struct Tray {
    hwnd: HWND,
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(Some(0)).collect()
}

fn with_handler<R>(f: impl FnOnce(&mut dyn AppHandler) -> R) -> Option<R> {
    HANDLER.with(|h| h.try_borrow_mut().ok().and_then(|mut g| g.as_mut().map(|b| f(b.as_mut()))))
}

fn title() -> String {
    INFO.with(|i| i.borrow().as_ref().map_or_default(|i| i.title.clone()))
}

fn nid(hwnd: HWND) -> NOTIFYICONDATAW {
    let mut n: NOTIFYICONDATAW = unsafe { std::mem::zeroed() };
    n.cbSize = std::mem::size_of::<NOTIFYICONDATAW>() as u32;
    n.hWnd = hwnd;
    n.uID = TRAY_UID;
    n
}

fn tip_text() -> String {
    let enabled = with_handler(|h| h.is_enabled()).unwrap_or(true);
    if enabled {
        title()
    } else {
        format!("{} \u{2013} disabled", title())
    }
}

fn set_tip(n: &mut NOTIFYICONDATAW) {
    let t: Vec<u16> = tip_text().encode_utf16().take(127).collect();
    n.szTip = [0; 128];
    n.szTip[..t.len()].copy_from_slice(&t);
}

fn load_icon() -> HICON {
    unsafe {
        let hinst = GetModuleHandleW(None).unwrap_or_default();
        let (cx, cy) = (GetSystemMetrics(SM_CXSMICON), GetSystemMetrics(SM_CYSMICON));
        match LoadImageW(
            Some(hinst.into()),
            // MAKEINTRESOURCEW(1): the icon's resource ID 1, passed in place of a name pointer.
            // Not `ptr::dangling()`: that is the type's alignment (2 for u16), so it loads resource 2.
            PCWSTR(std::ptr::without_provenance::<u16>(1)),
            IMAGE_ICON,
            cx,
            cy,
            LR_DEFAULTCOLOR | LR_SHARED,
        ) {
            Ok(h) => HICON(h.0),
            Err(_) => LoadIconW(None, IDI_APPLICATION).unwrap_or_default(),
        }
    }
}

fn add_icon(hwnd: HWND) {
    unsafe {
        let mut n = nid(hwnd);
        n.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        n.uCallbackMessage = WM_TRAY;
        n.hIcon = load_icon();
        set_tip(&mut n);
        let _ = Shell_NotifyIconW(NIM_ADD, &raw const n);
        n.Anonymous.uVersion = NOTIFYICON_VERSION_4;
        let _ = Shell_NotifyIconW(NIM_SETVERSION, &raw const n);
    }
}

fn refresh_tip(hwnd: HWND) {
    unsafe {
        let mut n = nid(hwnd);
        n.uFlags = NIF_TIP;
        set_tip(&mut n);
        let _ = Shell_NotifyIconW(NIM_MODIFY, &raw const n);
    }
}

/// `MagneticBorders.Settings.exe` next to the app, else in `settings\` (release zip and tester build),
/// `..\settings\` (`artifacts\local\<arch>\`) or `..\..\settings\<arch>\` (`artifacts\<build>\<arch>\`).
fn find_settings_app() -> Option<PathBuf> {
    const EXE: &str = "MagneticBorders.Settings.exe";
    let dir = std::env::current_exe().ok()?.parent()?.to_path_buf();
    let mut candidates = vec![dir.join(EXE), dir.join("settings").join(EXE), dir.join("..").join("settings").join(EXE)];
    if let Some(arch) = dir.file_name() {
        candidates.push(dir.join("..").join("..").join("settings").join(arch).join(EXE));
    }
    candidates.into_iter().find(|p| p.is_file())
}

/// Starts the settings app with `--flyout` or as a full window. Uses `CreateProcessW` with
/// `STARTF_FORCEOFFFEEDBACK` so Windows does not show the "app starting" busy cursor while the
/// new process creates its first window.
fn launch_settings(flyout: bool) -> bool {
    let Some(exe) = find_settings_app() else { return false };
    let mut cmd = wide(&format!("\"{}\"{}", exe.display(), if flyout { " --flyout" } else { "" }));
    let dir = exe.parent().map(|d| wide(&d.display().to_string()));
    let app = wide(&exe.display().to_string());
    let si =
        STARTUPINFOW { cb: size_of::<STARTUPINFOW>() as u32, dwFlags: STARTF_FORCEOFFFEEDBACK, ..Default::default() };
    let mut pi = PROCESS_INFORMATION::default();
    unsafe {
        let started = CreateProcessW(
            PCWSTR(app.as_ptr()),
            Some(PWSTR(cmd.as_mut_ptr())),
            None,
            None,
            false,
            PROCESS_CREATION_FLAGS(0),
            None,
            dir.as_ref().map_or(PCWSTR::null(), |d| PCWSTR(d.as_ptr())),
            &raw const si,
            &raw mut pi,
        )
        .is_ok();
        if started {
            let _ = CloseHandle(pi.hThread);
            let _ = CloseHandle(pi.hProcess);
        }
        started
    }
}

unsafe fn show_menu(hwnd: HWND) {
    unsafe {
        let Ok(menu) = CreatePopupMenu() else { return };
        let enabled = with_handler(|h| h.is_enabled()).unwrap_or(true);
        let version = INFO.with(|i| i.borrow().as_ref().map_or_default(|i| i.version.clone()));
        let header = wide(&format!("{} {}", title(), version));
        let _ = AppendMenuW(menu, MF_STRING | MF_DISABLED, 0, PCWSTR(header.as_ptr()));
        let _ = AppendMenuW(menu, if enabled { MF_STRING | MF_CHECKED } else { MF_STRING }, ID_ENABLED, w!("Enabled"));
        let _ = AppendMenuW(menu, MF_STRING, ID_SETTINGS, w!("Settings\u{2026}"));
        let _ = AppendMenuW(menu, MF_SEPARATOR, 0, PCWSTR::null());
        let _ = AppendMenuW(menu, MF_STRING, ID_EXIT, w!("Exit"));
        let mut pt = POINT::default();
        let _ = GetCursorPos(&raw mut pt);
        let _ = SetForegroundWindow(hwnd);
        let cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN, pt.x, pt.y, None, hwnd, None);
        let _ = PostMessageW(Some(hwnd), WM_NULL, WPARAM(0), LPARAM(0));
        let _ = DestroyMenu(menu);
        match cmd.0 as usize {
            ID_ENABLED => {
                with_handler(|h| h.toggle_enabled());
            }
            ID_SETTINGS => {
                launch_settings(false);
            }
            ID_EXIT => PostQuitMessage(0),
            _ => {}
        }
    }
}

unsafe extern "system" fn wndproc(hwnd: HWND, msg: u32, wp: WPARAM, lp: LPARAM) -> LRESULT {
    unsafe {
        if msg != 0 && msg == TASKBAR_CREATED.with(std::cell::Cell::get) {
            add_icon(hwnd);
            return LRESULT(0);
        }
        match msg {
            WM_TRAY => {
                match (lp.0 as u32) & 0xFFFF {
                    WM_LBUTTONUP => {
                        if !launch_settings(true) {
                            show_menu(hwnd);
                        }
                    }
                    WM_RBUTTONUP | WM_CONTEXTMENU => show_menu(hwnd),
                    _ => {}
                }
                LRESULT(0)
            }
            WM_SETTINGS => {
                SetTimer(Some(hwnd), DEBOUNCE_TIMER, 100, None);
                LRESULT(0)
            }
            WM_TIMER if wp.0 == DEBOUNCE_TIMER => {
                let _ = KillTimer(Some(hwnd), DEBOUNCE_TIMER);
                with_handler(|h| h.on_settings_changed());
                refresh_tip(hwnd);
                LRESULT(0)
            }
            WM_PIPE => {
                let req = Box::from_raw(lp.0 as *mut PipeReq);
                let (line, quit) = with_handler(|h| h.on_pipe(&req.line))
                    .unwrap_or_else(|| (r#"{"ok":false,"error":"busy"}"#.to_string(), false));
                let _ = req.reply.send(line);
                refresh_tip(hwnd);
                if quit {
                    std::thread::sleep(Duration::from_millis(60)); // let the reply reach the client
                    PostQuitMessage(0);
                }
                LRESULT(0)
            }
            _ => DefWindowProcW(hwnd, msg, wp, lp),
        }
    }
}

fn watch_thread(dir: &Path, hwnd: usize) {
    unsafe {
        let d = wide(&dir.to_string_lossy());
        let Ok(h) = CreateFileW(
            PCWSTR(d.as_ptr()),
            FILE_LIST_DIRECTORY.0,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            None,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            None,
        ) else {
            return;
        };
        let mut buf = [0u32; 2048];
        loop {
            let mut got = 0u32;
            let r = windows::Win32::Storage::FileSystem::ReadDirectoryChangesW(
                h,
                buf.as_mut_ptr().cast(),
                (buf.len() * 4) as u32,
                false,
                FILE_NOTIFY_CHANGE_FILE_NAME | FILE_NOTIFY_CHANGE_LAST_WRITE | FILE_NOTIFY_CHANGE_SIZE,
                Some(&raw mut got),
                None,
                None,
            );
            if r.is_err() {
                return;
            }
            if PostMessageW(Some(HWND(hwnd as *mut _)), WM_SETTINGS, WPARAM(0), LPARAM(0)).is_err() {
                return;
            }
        }
    }
}

fn serve_one(f: &File, hwnd: usize) {
    let mut req = Vec::new();
    let mut b = [0u8; 1024];
    let mut r = f;
    while req.len() < 65536 && !req.contains(&b'\n') {
        match r.read(&mut b) {
            Ok(0) | Err(_) => break,
            Ok(n) => req.extend_from_slice(&b[..n]),
        }
    }
    if req.is_empty() {
        return;
    }
    let line = String::from_utf8_lossy(&req).lines().next().unwrap_or("").to_string();
    let (tx, rx) = mpsc::channel();
    let boxed = Box::into_raw(Box::new(PipeReq { line, reply: tx }));
    let posted = unsafe { PostMessageW(Some(HWND(hwnd as *mut _)), WM_PIPE, WPARAM(0), LPARAM(boxed as isize)) };
    let resp = if posted.is_err() {
        unsafe { drop(Box::from_raw(boxed)) };
        r#"{"ok":false,"error":"engine busy"}"#.to_string()
    } else {
        rx.recv_timeout(Duration::from_millis(500)).unwrap_or_else(|_| r#"{"ok":false,"error":"timeout"}"#.to_string())
    };
    let mut w = f;
    let _ = w.write_all(format!("{resp}\n").as_bytes());
    let _ = w.sync_all_pipe();
}

trait SyncPipe {
    fn sync_all_pipe(&mut self) -> std::io::Result<()>;
}
impl SyncPipe for &File {
    fn sync_all_pipe(&mut self) -> std::io::Result<()> {
        File::sync_all(self)
    }
}

fn pipe_thread(hwnd: usize) {
    unsafe {
        let name = wide(PIPE_NAME);
        let h = CreateNamedPipeW(
            PCWSTR(name.as_ptr()),
            PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            PIPE_UNLIMITED_INSTANCES,
            4096,
            4096,
            0,
            None,
        );
        if h.is_invalid() {
            return;
        }
        let file = ManuallyDrop::new(File::from_raw_handle(h.0));
        loop {
            // ERROR_PIPE_CONNECTED (client raced us) also counts as connected
            let _ = ConnectNamedPipe(HANDLE(h.0), None);
            serve_one(&file, hwnd);
            let _ = DisconnectNamedPipe(HANDLE(h.0));
        }
    }
}

impl Tray {
    /// Creates the hidden window + tray icon and starts the watcher and pipe threads.
    /// Call on the UI thread, before the message loop.
    pub fn create(info: AppInfo, handler: Box<dyn AppHandler>) -> Result<Tray, Win32Error> {
        let data_dir = info.data_dir.clone();
        INFO.with(|i| *i.borrow_mut() = Some(info));
        HANDLER.with(|h| *h.borrow_mut() = Some(handler));
        unsafe {
            TASKBAR_CREATED.with(|t| t.set(RegisterWindowMessageW(w!("TaskbarCreated"))));
            let hinst = GetModuleHandleW(None)?;
            let class = w!("MagneticBordersHost");
            let wc = WNDCLASSW {
                lpfnWndProc: Some(wndproc),
                hInstance: hinst.into(),
                lpszClassName: class,
                ..Default::default()
            };
            RegisterClassW(&raw const wc);
            let hwnd = CreateWindowExW(
                WINDOW_EX_STYLE(0),
                class,
                w!("Magnetic Borders"),
                WINDOW_STYLE(0),
                0,
                0,
                0,
                0,
                None,
                None,
                Some(hinst.into()),
                None,
            )?;
            add_icon(hwnd);
            let raw = hwnd.0 as usize;
            std::thread::spawn(move || watch_thread(&data_dir, raw));
            std::thread::spawn(move || pipe_thread(raw));
            Ok(Tray { hwnd })
        }
    }
}

impl Drop for Tray {
    fn drop(&mut self) {
        unsafe {
            let n = nid(self.hwnd);
            let _ = Shell_NotifyIconW(NIM_DELETE, &raw const n);
            let _ = DestroyWindow(self.hwnd);
        }
        HANDLER.with(|h| *h.borrow_mut() = None);
    }
}

/// Client side: send one request line to the running engine, return the response line.
pub fn pipe_request(line: &str) -> Option<String> {
    let mut f = std::fs::OpenOptions::new().read(true).write(true).open(PIPE_NAME).ok()?;
    f.write_all(format!("{line}\n").as_bytes()).ok()?;
    let mut out = Vec::new();
    let mut b = [0u8; 1024];
    while !out.contains(&b'\n') {
        match f.read(&mut b) {
            Ok(0) | Err(_) => break,
            Ok(n) => out.extend_from_slice(&b[..n]),
        }
    }
    Some(String::from_utf8_lossy(&out).trim().to_string())
}
