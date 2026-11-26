//! Pure named-pipe protocol: request line + engine state -> response line + action. No I/O; the transport is in `mb-win32`.
use serde::Serialize;
use serde_json::Value;

#[derive(Clone, Debug)]
/// Snapshot of engine state reported by the `status` command. Built by the host per request.
pub struct EngineState {
    /// Engine id (`rust`).
    pub engine: String,
    /// Engine version.
    pub version: String,
    /// Process id.
    pub pid: u32,
    /// Master switch from settings.
    pub enabled: bool,
    /// Magnetic coupling switch from settings.
    pub glue_enabled: bool,
    /// Magnetic coupling tolerance in pixels.
    pub glue_tolerance_px: i32,
    /// Drags that changed window size.
    pub drags: u64,
    /// Drags that found a seam.
    pub glued_drags: u64,
    /// Seconds since start.
    pub uptime_s: u64,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
/// Side effect the host must perform after sending the reply.
pub enum Action {
    /// Nothing to do.
    None,
    /// Re-read `settings.json`.
    Reload,
    /// Exit the message loop.
    Quit,
}

#[derive(Clone, Debug)]
/// Outcome of one request.
pub struct Reply {
    /// JSON response line, without the trailing newline.
    pub line: String,
    /// What the host should do besides replying.
    pub action: Action,
}

#[derive(Serialize)]
struct Glue {
    enabled: bool,
    tolerance_px: i32,
}

#[derive(Serialize)]
struct Status<'a> {
    ok: bool,
    engine: &'a str,
    version: &'a str,
    pid: u32,
    enabled: bool,
    glue: Glue,
    drags: u64,
    glued_drags: u64,
    uptime_s: u64,
}

fn reply(v: &Value, action: Action) -> Reply {
    Reply { line: v.to_string(), action }
}

/// Parses one request line (`{"cmd":...}`) and builds the response. Unknown or malformed input yields an
/// `ok:false` reply. Called by the host's `AppHandler::on_pipe` on the UI thread.
pub fn handle_request(line: &str, st: &EngineState) -> Reply {
    let cmd = serde_json::from_str::<Value>(line.trim())
        .ok()
        .and_then(|v| v.get("cmd").and_then(Value::as_str).map(str::to_string));
    match cmd.as_deref() {
        Some("status") => {
            let s = Status {
                ok: true,
                engine: &st.engine,
                version: &st.version,
                pid: st.pid,
                enabled: st.enabled,
                glue: Glue { enabled: st.glue_enabled, tolerance_px: st.glue_tolerance_px },
                drags: st.drags,
                glued_drags: st.glued_drags,
                uptime_s: st.uptime_s,
            };
            Reply { line: serde_json::to_string(&s).unwrap_or_default(), action: Action::None }
        }
        Some("reload") => reply(&serde_json::json!({"ok":true}), Action::Reload),
        Some("quit") => reply(&serde_json::json!({"ok":true}), Action::Quit),
        _ => reply(&serde_json::json!({"ok":false,"error":"unknown command"}), Action::None),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn st() -> EngineState {
        EngineState {
            engine: "rust".into(),
            version: crate::VERSION.into(),
            pid: 1234,
            enabled: true,
            glue_enabled: true,
            glue_tolerance_px: 12,
            drags: 17,
            glued_drags: 9,
            uptime_s: 360,
        }
    }

    fn json(r: &Reply) -> Value {
        serde_json::from_str(&r.line).unwrap()
    }

    #[test]
    fn status() {
        let r = handle_request(r#"{"cmd":"status"}"#, &st());
        assert_eq!(r.action, Action::None);
        let v = json(&r);
        assert_eq!(v["ok"], true);
        assert_eq!(v["engine"], "rust");
        assert_eq!(v["version"], crate::VERSION);
        assert_eq!(v["pid"], 1234);
        assert_eq!(v["glue"]["tolerance_px"], 12);
        assert_eq!(v["drags"], 17);
        assert_eq!(v["glued_drags"], 9);
        assert_eq!(v["uptime_s"], 360);
        assert!(v.get("paused").is_none());
    }

    #[test]
    fn commands() {
        let r = handle_request("{\"cmd\":\"reload\"}\r\n", &st());
        assert_eq!((r.action, r.line.as_str()), (Action::Reload, r#"{"ok":true}"#));
        let r = handle_request(r#"{"cmd":"quit"}"#, &st());
        assert_eq!((r.action, r.line.as_str()), (Action::Quit, r#"{"ok":true}"#));
    }

    #[test]
    fn unknown() {
        for l in [r#"{"cmd":"dance"}"#, r#"{"cmd":"pause"}"#, r#"{"cmd":"resume"}"#, "garbage", "{}", ""] {
            let r = handle_request(l, &st());
            assert_eq!(r.action, Action::None);
            assert_eq!(json(&r)["ok"], false);
            assert_eq!(json(&r)["error"], "unknown command");
        }
    }
}
