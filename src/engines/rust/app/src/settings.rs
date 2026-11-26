//! Settings model (schema 1), lenient parsing and the reload rule.

use serde_json::Value;

/// Settings model (schema 1). Unknown keys are ignored here (the settings app preserves them).
#[derive(Clone, Debug, PartialEq, Eq)]
pub struct Settings {
    /// Master switch.
    pub enabled: bool,
    /// Glue feature switch (`glue.enabled`).
    pub glue_enabled: bool,
    /// Glue tolerance in pixels, 0 to 64 (`glue.tolerance_px`).
    pub glue_tolerance_px: i32,
    /// Snap feature switch (`snap.enabled`); parsed but not acted on.
    pub snap_enabled: bool,
    /// Snap distance in pixels, 0 to 64 (`snap.distance_px`).
    pub snap_distance_px: i32,
    /// Executable names whose windows are never glued.
    pub excluded_apps: Vec<String>,
    /// Whether the engine registers itself to start at login.
    pub start_at_login: bool,
}

impl Default for Settings {
    fn default() -> Self {
        Self {
            enabled: true,
            glue_enabled: true,
            glue_tolerance_px: 12,
            snap_enabled: true,
            snap_distance_px: 16,
            excluded_apps: vec![],
            start_at_login: false,
        }
    }
}

fn bool_of(v: Option<&Value>, d: bool) -> bool {
    v.and_then(Value::as_bool).unwrap_or(d)
}

fn px_of(v: Option<&Value>, d: i32) -> i32 {
    match v.and_then(|v| v.as_i64().or_else(|| v.as_f64().map(|f| f.round() as i64))) {
        Some(n) => n.clamp(0, 64) as i32,
        None => d,
    }
}

impl Settings {
    /// Parses settings text; missing keys take defaults. Err on invalid JSON / non-object.
    pub fn parse(text: &str) -> Result<Settings, String> {
        let v: Value = serde_json::from_str(text).map_err(|e| e.to_string())?;
        let Some(root) = v.as_object() else { return Err("settings root is not an object".into()) };
        let d = Settings::default();
        let glue = root.get("glue").and_then(Value::as_object);
        let snap = root.get("snap").and_then(Value::as_object);
        // legacy Prototype 1 flat key as a fallback
        let legacy_tol = px_of(root.get("glue_tolerance_px"), d.glue_tolerance_px);
        let excluded = root
            .get("excluded_apps")
            .and_then(Value::as_array)
            .map(|a| a.iter().filter_map(|x| x.as_str().map(str::to_string)).collect())
            .unwrap_or(d.excluded_apps.clone());
        Ok(Settings {
            enabled: bool_of(root.get("enabled"), d.enabled),
            glue_enabled: bool_of(glue.and_then(|g| g.get("enabled")), d.glue_enabled),
            glue_tolerance_px: px_of(glue.and_then(|g| g.get("tolerance_px")), legacy_tol),
            snap_enabled: bool_of(snap.and_then(|g| g.get("enabled")), d.snap_enabled),
            snap_distance_px: px_of(snap.and_then(|g| g.get("distance_px")), d.snap_distance_px),
            excluded_apps: excluded,
            start_at_login: bool_of(root.get("start_at_login"), d.start_at_login),
        })
    }
}

/// Returns the settings document with the top-level `enabled` key set, keeping every other key (known or
/// unknown). Blank text gives a fresh object. Err when the text is not a JSON object, so the caller never
/// overwrites a file it cannot understand. Pure: the host writes the file atomically.
pub fn with_enabled(text: &str, enabled: bool) -> Result<String, String> {
    let text = text.trim_start_matches('\u{feff}');
    let mut v: Value = if text.trim().is_empty() {
        Value::Object(serde_json::Map::new())
    } else {
        serde_json::from_str(text).map_err(|e| e.to_string())?
    };
    let Some(root) = v.as_object_mut() else { return Err("settings root is not an object".into()) };
    root.insert("enabled".into(), Value::Bool(enabled));
    let mut out = serde_json::to_string_pretty(&v).map_err(|e| e.to_string())?;
    out.push('\n');
    Ok(out)
}

/// Reload rule. `read`: Ok(None) = file missing (defaults), Ok(Some(text)), Err = read error.
/// Returns the new settings and an optional warning (previous settings are kept on error).
pub fn apply_reload(prev: &Settings, read: Result<Option<String>, String>) -> (Settings, Option<String>) {
    match read {
        Ok(None) => (Settings::default(), None),
        Ok(Some(text)) => match Settings::parse(&text) {
            Ok(s) => (s, None),
            Err(e) => (prev.clone(), Some(format!("settings.json parse error, keeping previous settings: {e}"))),
        },
        Err(e) => (prev.clone(), Some(format!("settings.json read error, keeping previous settings: {e}"))),
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn defaults_for_empty_object() {
        assert_eq!(Settings::parse("{}").unwrap(), Settings::default());
    }

    #[test]
    fn full_file() {
        let s = Settings::parse(
            r#"{"schema":1,"enabled":false,"glue":{"enabled":false,"tolerance_px":30},
            "snap":{"enabled":false,"distance_px":5},"excluded_apps":["b.exe"],"start_at_login":true,"x":1}"#,
        )
        .unwrap();
        assert!(!s.enabled && !s.glue_enabled && !s.snap_enabled && s.start_at_login);
        assert_eq!((s.glue_tolerance_px, s.snap_distance_px), (30, 5));
        assert_eq!(s.excluded_apps, vec!["b.exe".to_string()]);
    }

    #[test]
    fn partial_file_and_clamp() {
        let s = Settings::parse(r#"{"glue":{"tolerance_px":500},"snap":{"distance_px":-3}}"#).unwrap();
        assert_eq!(s.glue_tolerance_px, 64);
        assert_eq!(s.snap_distance_px, 0);
        assert!(s.enabled && s.glue_enabled);
    }

    #[test]
    fn wrong_types_fall_back() {
        let s = Settings::parse(r#"{"enabled":"yes","glue":5,"excluded_apps":[1,"a.exe"]}"#).unwrap();
        assert!(s.enabled);
        assert_eq!(s.glue_tolerance_px, 12);
        assert_eq!(s.excluded_apps, vec!["a.exe".to_string()]);
    }

    #[test]
    fn with_enabled_preserves_unknown_keys() {
        let input = r#"{"schema":1,"enabled":true,"future":{"a":[1,2,{"b":null}]},"glue":{"enabled":true,"tolerance_px":12,"x":"y"},"zzz":"last"}"#;
        let out = with_enabled(input, false).unwrap();
        let mut expected: Value = serde_json::from_str(input).unwrap();
        expected["enabled"] = Value::Bool(false);
        assert_eq!(serde_json::from_str::<Value>(&out).unwrap(), expected);
        let back = with_enabled(&out, true).unwrap();
        assert_eq!(serde_json::from_str::<Value>(&back).unwrap(), serde_json::from_str::<Value>(input).unwrap());
    }

    #[test]
    fn with_enabled_adds_key_and_rejects_non_objects() {
        let v: Value = serde_json::from_str(&with_enabled(r#"{"keep":1}"#, false).unwrap()).unwrap();
        assert_eq!(v, serde_json::json!({"keep":1,"enabled":false}));
        let v: Value = serde_json::from_str(&with_enabled("  ", false).unwrap()).unwrap();
        assert_eq!(v, serde_json::json!({"enabled":false}));
        assert!(with_enabled("[1]", true).is_err());
        assert!(with_enabled("{ broken", true).is_err());
    }

    #[test]
    fn legacy_flat_tolerance() {
        assert_eq!(Settings::parse(r#"{"glue_tolerance_px":20}"#).unwrap().glue_tolerance_px, 20);
    }

    #[test]
    fn bad_json_keeps_previous() {
        let prev = Settings { glue_tolerance_px: 33, ..Settings::default() };
        let (s, w) = apply_reload(&prev, Ok(Some("{ nope".into())));
        assert_eq!(s, prev);
        assert!(w.is_some());
        let (s, w) = apply_reload(&prev, Err("locked".into()));
        assert_eq!(s, prev);
        assert!(w.is_some());
    }

    #[test]
    fn missing_file_is_defaults() {
        let prev = Settings { enabled: false, ..Settings::default() };
        let (s, w) = apply_reload(&prev, Ok(None));
        assert_eq!(s, Settings::default());
        assert!(w.is_none());
    }

    #[test]
    fn with_enabled_keeps_key_order() {
        let out = with_enabled(r#"{"zeta":1,"enabled":true,"alpha":{"y":1,"x":2},"mid":[3,1]}"#, false).unwrap();
        assert_eq!(
            out,
            "{\n  \"zeta\": 1,\n  \"enabled\": false,\n  \"alpha\": {\n    \"y\": 1,\n    \"x\": 2\n  },\n  \"mid\": [\n    3,\n    1\n  ]\n}\n"
        );
    }

    #[test]
    fn with_enabled_appends_a_missing_key_last() {
        let out = with_enabled(r#"{"b":1,"a":2}"#, true).unwrap();
        assert_eq!(out, "{\n  \"b\": 1,\n  \"a\": 2,\n  \"enabled\": true\n}\n");
    }

    #[test]
    fn with_enabled_ignores_a_byte_order_mark() {
        let out = with_enabled("\u{feff}{\"k\":1}", false).unwrap();
        assert!(out.starts_with("{\n  \"k\": 1"));
    }

    #[test]
    fn fractional_pixels_round_to_the_nearest_integer() {
        let s = Settings::parse(r#"{"glue":{"tolerance_px":12.6},"snap":{"distance_px":3.2}}"#).unwrap();
        assert_eq!((s.glue_tolerance_px, s.snap_distance_px), (13, 3));
    }

    #[test]
    fn non_object_root_is_an_error() {
        assert!(Settings::parse("[1,2]").is_err());
        assert!(Settings::parse("").is_err());
    }

    #[test]
    fn valid_reload_replaces_settings_without_warning() {
        let (s, w) = apply_reload(&Settings::default(), Ok(Some(r#"{"enabled":false}"#.into())));
        assert!(!s.enabled);
        assert!(w.is_none());
    }

    #[test]
    fn read_error_warns_and_keeps_previous() {
        let prev = Settings { snap_distance_px: 7, ..Settings::default() };
        let (s, w) = apply_reload(&prev, Err("locked".into()));
        assert_eq!(s, prev);
        assert!(w.unwrap().contains("read error"));
    }
}
