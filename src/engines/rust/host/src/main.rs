//! Host (`mb-host`, binary `magnetic-borders-rs`): the composition root, the only place that knows concrete
//! types. Wires `mb-win32` adapters into `mb-app::DragController`, loads settings, runs the message loop.
//! On non-Windows targets it only prints a message.
#![cfg_attr(windows, windows_subsystem = "windows")]

#[cfg(windows)]
mod app {
    use mb_app::{
        apply_reload, handle_request, with_enabled, Action, DragController, EngineState, Live, Settings, VERSION,
    };
    use mb_win32::*;
    use std::path::PathBuf;
    use std::rc::Rc;
    use std::time::Instant;

    const TITLE: &str = "Magnetic Borders";

    /// Host state behind the tray and the pipe: implements `AppHandler`. Owned by the `Tray` thread-local
    /// slot after `Tray::create`; UI thread only.
    struct App {
        live: Rc<Live>,
        settings: Settings,
        shared: Shared,
        path: PathBuf,
        log: AppLog,
        started: Instant,
    }

    impl App {
        fn read_settings(&self) -> Result<Option<String>, String> {
            match std::fs::read_to_string(&self.path) {
                Ok(t) => Ok(Some(t)),
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
                Err(e) => Err(e.to_string()),
            }
        }

        /// Pushes current settings into the live knobs.
        fn apply(&mut self) {
            let s = &self.settings;
            self.live.tolerance_px.set(s.glue_tolerance_px);
            self.live.active.set(s.glue_active());
            self.live.snap_active.set(s.snap_active());
            self.live.snap.set(s.snap_options());
            self.shared.set_excluded(&s.excluded_apps);
            let exe = std::env::current_exe().ok();
            if let Err(e) = set_run_at_login(if s.start_at_login { exe.as_deref() } else { None }) {
                self.log.warn(&e);
            }
        }

        fn state(&self) -> EngineState {
            EngineState {
                engine: "rust".into(),
                version: VERSION.into(),
                pid: std::process::id(),
                enabled: self.settings.enabled,
                glue_enabled: self.settings.glue_enabled,
                glue_tolerance_px: self.settings.glue_tolerance_px,
                drags: self.live.drags.get(),
                glued_drags: self.live.glued_drags.get(),
                uptime_s: self.started.elapsed().as_secs(),
            }
        }

        fn reload(&mut self) {
            let (new, warn) = apply_reload(&self.settings, self.read_settings());
            if let Some(w) = warn {
                self.log.warn(&w);
            }
            if new != self.settings {
                use mb_app::Log;
                self.log.info(&format!("settings reloaded: {new:?}"));
                self.settings = new;
                self.apply();
            }
        }
    }

    impl AppHandler for App {
        fn on_settings_changed(&mut self) {
            self.reload();
        }

        fn on_pipe(&mut self, line: &str) -> (String, bool) {
            let r = handle_request(line, &self.state());
            match r.action {
                Action::Reload => self.reload(),
                Action::Quit | Action::None => {}
            }
            (r.line, r.action == Action::Quit)
        }

        fn is_enabled(&self) -> bool {
            self.settings.enabled
        }

        fn toggle_enabled(&mut self) {
            use mb_app::Log;
            let text = match self.read_settings() {
                Ok(t) => t.unwrap_or_default(),
                Err(e) => {
                    self.log.warn(&format!("could not read settings.json; the Enabled switch was not changed: {e}"));
                    return;
                }
            };
            let current = Settings::parse(&text).map_or(true, |s| s.enabled);
            let result = with_enabled(&text, !current).and_then(|out| write_atomic(&self.path, &out));
            match result {
                Ok(()) => self
                    .log
                    .info(&format!("Enabled switched {} from the tray menu", if current { "off" } else { "on" })),
                Err(e) => {
                    self.log.warn(&format!("could not update settings.json; the Enabled switch was not changed: {e}"));
                }
            }
        }
    }

    /// Writes `text` to a temp file next to `path`, then replaces `path` with it.
    fn write_atomic(path: &std::path::Path, text: &str) -> Result<(), String> {
        let mut tmp = path.as_os_str().to_owned();
        tmp.push(".tmp");
        let tmp = PathBuf::from(tmp);
        let r = std::fs::write(&tmp, text).and_then(|()| std::fs::rename(&tmp, path));
        if r.is_err() {
            let _ = std::fs::remove_file(&tmp);
        }
        r.map_err(|e| e.to_string())
    }

    /// Starts the engine: single-instance check, settings, controller, hooks, tray, then blocks in the
    /// message loop until quit.
    pub fn run() -> anyhow::Result<()> {
        use mb_app::Log;
        let args: Vec<String> = std::env::args().collect();
        let console = args.iter().any(|a| a == "--console");
        let verbose = args.iter().any(|a| a == "--verbose" || a == "-v");
        if console {
            alloc_console();
        }
        set_dpi_awareness();
        let Some(_instance) = SingleInstance::acquire() else {
            message_box("Magnetic Borders", "Magnetic Borders is already running.");
            return Ok(());
        };
        let data = std::env::var_os("LOCALAPPDATA").map_or_default(PathBuf::from).join("MagneticBorders");
        let _ = std::fs::create_dir_all(&data);
        let path = data.join("settings.json");
        let log = AppLog::open(&data.join("logs").join("rust.log"), console, verbose);
        log.info(&format!("Magnetic Borders (Rust) {VERSION} starting, pid {}", std::process::id()));
        if verbose {
            log_system_diagnostics(&log);
        }

        let (s, warn) = apply_reload(
            &Settings::default(),
            match std::fs::read_to_string(&path) {
                Ok(t) => Ok(Some(t)),
                Err(e) if e.kind() == std::io::ErrorKind::NotFound => Ok(None),
                Err(e) => Err(e.to_string()),
            },
        );
        if let Some(w) = warn {
            log.warn(&w);
        }
        log.info(&format!("settings: {s:?}"));
        if !drag_full_windows_enabled() {
            log.warn("'Show window contents while dragging' is off; live magnetic coupling will not work.");
        }

        let shared = Shared::default();
        let live = Rc::new(Live::new(s.glue_active(), s.glue_tolerance_px));
        let controller = DragController::new(
            Win32Inventory::new(shared.clone(), &s.excluded_apps),
            if verbose {
                Win32Mover::with_diagnostics(shared.clone(), log.clone())
            } else {
                Win32Mover::new(shared.clone())
            },
            MonotonicClock::new(),
            Win32Timer,
            log.clone(),
            "rust",
            s.glue_tolerance_px,
        )
        .with_live(live.clone())
        .with_pointer_hook(Box::new(Win32PointerHook::new()));
        let mut app = App { live, settings: s, shared, path, log: log.clone(), started: Instant::now() };
        app.apply();
        let _events = EventSource::install(Box::new(controller))?;
        let _tray =
            Tray::create(AppInfo { title: TITLE.into(), version: VERSION.into(), data_dir: data }, Box::new(app))?;
        log.info("running");
        run_message_loop();
        log.info("exiting");
        Ok(())
    }

    #[cfg(test)]
    mod tests {
        use super::*;

        #[test]
        fn write_atomic_replaces_the_file_and_leaves_no_temp_file() {
            let dir = std::env::temp_dir().join(format!("mb-host-{}", std::process::id()));
            std::fs::create_dir_all(&dir).unwrap();
            let path = dir.join("settings.json");
            write_atomic(&path, "one").unwrap();
            write_atomic(&path, "two").unwrap();
            assert_eq!(std::fs::read_to_string(&path).unwrap(), "two");
            assert!(!dir.join("settings.json.tmp").exists());
            std::fs::remove_dir_all(&dir).unwrap();
        }

        #[test]
        fn write_atomic_reports_an_unwritable_path() {
            let missing =
                std::env::temp_dir().join(format!("mb-host-missing-{}", std::process::id())).join("settings.json");
            assert!(write_atomic(&missing, "x").is_err());
        }
    }
}

#[cfg(windows)]
fn main() -> anyhow::Result<()> {
    app::run()
}

#[cfg(not(windows))]
fn main() {
    eprintln!("magnetic-borders-rs only runs on Windows");
}
