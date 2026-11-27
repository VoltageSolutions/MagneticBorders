//! Test tooling (`mb-fixtures`): parses `.layout` fixtures and runs them over the pure domain (`mb-core`).
//! Used by the `fixture-runner` binary and the integration test over `tests/fixtures`.
use mb_core::{build_seam, propagate, Config, Edge, Rect, WinInfo};
use std::fmt::Write;
use std::path::Path;

#[derive(Debug, thiserror::Error)]
/// Failure while loading or checking a fixture.
pub enum FixtureError {
    /// The fixture text is malformed.
    #[error("parse error: {0}")]
    Parse(String),
    /// The file could not be read.
    #[error("io: {0}")]
    Io(#[from] std::io::Error),
    /// A computed frame or clamp differs from an `expect` line.
    #[error("{0}")]
    Mismatch(String),
}

type Result<T> = std::result::Result<T, FixtureError>;

fn perr<T>(m: impl Into<String>) -> Result<T> {
    Err(FixtureError::Parse(m.into()))
}

/// One parsed `.layout` test case: windows, one drag, and the expected result.
pub struct Fixture {
    /// Glue tolerance (default 12).
    pub tolerance: i32,
    /// Work area (default: effectively unbounded).
    pub work_area: Rect,
    /// Window names, parallel to `windows`.
    pub names: Vec<String>,
    /// Windows; the id is the index.
    pub windows: Vec<WinInfo>,
    /// The drag: window index, edge, and signed delta. Required.
    pub drag: Option<(usize, Edge, i32)>,
    /// Expected final frames by window index; unlisted windows must stay put.
    pub expects: Vec<(usize, Rect)>,
    /// Expected final position of the dragged edge.
    pub expect_clamp: Option<i32>,
}

fn pair(s: &str, sep: char) -> Result<(i32, i32)> {
    let (a, b) = s.split_once(sep).ok_or_else(|| FixtureError::Parse(format!("bad pair '{s}'")))?;
    let p = |x: &str| x.parse::<i32>().map_err(|_| FixtureError::Parse(format!("bad number '{x}'")));
    Ok((p(a)?, p(b)?))
}

fn rect(pos: &str, size: &str) -> Result<Rect> {
    let (x, y) = pair(pos, ',')?;
    let (w, h) = pair(size, 'x')?;
    Ok(Rect::from_xywh(x, y, w, h))
}

impl Fixture {
    fn idx(&self, n: &str) -> Result<usize> {
        self.names.iter().position(|x| x == n).ok_or_else(|| FixtureError::Parse(format!("unknown window {n}")))
    }

    /// Parses fixture text; `#` starts a comment.
    #[allow(clippy::many_single_char_names)] // the parser binds one short name per token of the line grammar
    pub fn parse(text: &str) -> Result<Fixture> {
        let mut f = Fixture {
            tolerance: 12,
            work_area: Rect::from_xywh(-100_000, -100_000, 200_000, 200_000),
            names: vec![],
            windows: vec![],
            drag: None,
            expects: vec![],
            expect_clamp: None,
        };
        for line in text.lines() {
            let line = line.split('#').next().unwrap_or("");
            let t: Vec<&str> = line.split_whitespace().collect();
            match t.as_slice() {
                [] => {}
                ["tolerance", v] => f.tolerance = v.parse().map_err(|_| FixtureError::Parse("tolerance".into()))?,
                ["workarea", p, s] => f.work_area = rect(p, s)?,
                ["win", n, p, s, rest @ ..] => {
                    let fr = rect(p, s)?;
                    let (min_w, min_h) = match rest {
                        [] => (1, 1),
                        ["min", m] => pair(m, 'x')?,
                        _ => return perr(format!("bad win line: {line}")),
                    };
                    f.names.push(n.to_string());
                    f.windows.push(WinInfo { id: f.windows.len() as u64, frame: fr, min_w, min_h });
                }
                ["drag", n, e, d] => {
                    let edge = match *e {
                        "left" => Edge::Left,
                        "right" => Edge::Right,
                        "top" => Edge::Top,
                        "bottom" => Edge::Bottom,
                        _ => return perr(format!("bad edge {e}")),
                    };
                    let delta = d.parse().map_err(|_| FixtureError::Parse(format!("bad delta {d}")))?;
                    f.drag = Some((f.idx(n)?, edge, delta));
                }
                ["expect", "clamp", v] => {
                    f.expect_clamp = Some(v.parse().map_err(|_| FixtureError::Parse("clamp".into()))?);
                }
                ["expect", n, p, s] => {
                    let i = f.idx(n)?;
                    f.expects.push((i, rect(p, s)?));
                }
                _ => return perr(format!("unrecognised line: {line}")),
            }
        }
        if f.drag.is_none() {
            return perr("no drag line");
        }
        Ok(f)
    }

    /// Returns final frames (by window index) and the final dragged-edge position.
    pub fn run(&self) -> (Vec<Rect>, i32) {
        let (di, edge, delta) = self.drag.unwrap();
        let mut out: Vec<Rect> = self.windows.iter().map(|w| w.frame).collect();
        let x = self.windows[di].frame.get(edge);
        let cfg = Config { glue_tolerance: self.tolerance };
        let pos = match build_seam(&self.windows, di as u64, edge, self.work_area, cfg) {
            Some(seam) => {
                let plan = propagate(&seam, x + delta);
                for (id, r) in plan.moves {
                    out[id as usize] = r;
                }
                plan.clamped_pos
            }
            None => x + delta,
        };
        out[di] = out[di].with(edge, pos);
        (out, pos)
    }

    /// Runs the fixture and compares against the `expect` lines.
    pub fn check(&self) -> Result<()> {
        let (out, pos) = self.run();
        for (i, w) in self.windows.iter().enumerate() {
            let want = self.expects.iter().find(|(e, _)| *e == i).map_or(w.frame, |(_, r)| *r);
            if out[i] != want {
                return Err(FixtureError::Mismatch(format!("{}: got {:?} want {:?}", self.names[i], out[i], want)));
            }
        }
        if let Some(c) = self.expect_clamp {
            if c != pos {
                return Err(FixtureError::Mismatch(format!("clamp got {pos} want {c}")));
            }
        }
        Ok(())
    }

    /// `id x,y WxH` lines sorted by id (name).
    pub fn print(&self) -> String {
        let (out, _) = self.run();
        let mut rows: Vec<(String, Rect)> = self.names.iter().cloned().zip(out).collect();
        rows.sort_by(|a, b| a.0.cmp(&b.0));
        rows.iter().fold(String::new(), |mut out, (n, r)| {
            let _ = writeln!(out, "{n} {},{} {}x{}", r.l, r.t, r.width(), r.height());
            out
        })
    }
}

/// Reads and parses a fixture file.
pub fn load(path: &Path) -> Result<Fixture> {
    Fixture::parse(&std::fs::read_to_string(path)?)
}

/// All `.layout` files under `path` (a file or directory), sorted.
pub fn collect(path: &Path) -> Result<Vec<std::path::PathBuf>> {
    if path.is_file() {
        return Ok(vec![path.to_path_buf()]);
    }
    let mut v: Vec<_> = std::fs::read_dir(path)?
        .filter_map(|e| e.ok().map(|e| e.path()))
        .filter(|p| p.extension().is_some_and(|x| x == "layout"))
        .collect();
    v.sort();
    Ok(v)
}

#[cfg(test)]
mod tests {
    use super::*;

    const BASIC: &str = "win a 0,0 1720x1440\nwin b 1720,0 1720x1440 min 1600x1\ndrag a right 200\n";

    fn temp_dir(name: &str) -> std::path::PathBuf {
        let d = std::env::temp_dir().join(format!("mb-fixtures-{name}-{}", std::process::id()));
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn parse_reads_windows_minimums_and_comments() {
        let f = Fixture::parse(&format!("# header\ntolerance 20 # inline\nworkarea 0,0 3440x1440\n{BASIC}")).unwrap();
        assert_eq!(f.tolerance, 20);
        assert_eq!(f.work_area, Rect::from_xywh(0, 0, 3440, 1440));
        assert_eq!(f.names, ["a", "b"]);
        assert_eq!((f.windows[1].min_w, f.windows[1].min_h), (1600, 1));
        assert_eq!(f.drag, Some((0, Edge::Right, 200)));
    }

    #[test]
    fn run_clamps_the_dragged_edge_to_neighbour_minimum() {
        let f = Fixture::parse(BASIC).unwrap();
        let (frames, pos) = f.run();
        assert_eq!(pos, 1840);
        assert_eq!(frames[0], Rect::new(0, 0, 1840, 1440));
        assert_eq!(frames[1], Rect::new(1840, 0, 3440, 1440));
    }

    #[test]
    fn run_without_a_seam_moves_only_the_dragged_window() {
        let f = Fixture::parse("win a 0,0 100x100\ndrag a bottom 5\n").unwrap();
        let (frames, pos) = f.run();
        assert_eq!((frames[0], pos), (Rect::new(0, 0, 100, 105), 105));
    }

    #[test]
    fn check_passes_on_matching_expectations_and_fails_on_mismatch() {
        let ok =
            Fixture::parse(&format!("{BASIC}expect a 0,0 1840x1440\nexpect b 1840,0 1600x1440\nexpect clamp 1840\n"))
                .unwrap();
        assert!(ok.check().is_ok());
        let bad = Fixture::parse(&format!("{BASIC}expect a 0,0 1x1\n")).unwrap();
        assert!(matches!(bad.check(), Err(FixtureError::Mismatch(_))));
        let bad_clamp =
            Fixture::parse(&format!("{BASIC}expect a 0,0 1840x1440\nexpect b 1840,0 1600x1440\nexpect clamp 1\n"))
                .unwrap();
        assert!(bad_clamp.check().unwrap_err().to_string().contains("clamp"));
    }

    #[test]
    fn print_lists_windows_sorted_by_name() {
        let f = Fixture::parse("win b 0,0 10x10\nwin a 20,0 10x10\ndrag b bottom 0\n").unwrap();
        assert_eq!(f.print(), "a 20,0 10x10\nb 0,0 10x10\n");
    }

    #[test]
    fn parse_rejects_malformed_fixtures() {
        for text in [
            "win a 0,0 10x10\n",
            "win a 0,0 10x10\ndrag a middle 5\n",
            "win a 0,0 10x10\ndrag z left 5\n",
            "win a 0,0 10x10\ndrag a left x\n",
            "win a 0,0 10x10 junk\ndrag a left 1\n",
            "win a 0;0 10x10\ndrag a left 1\n",
            "win a 0,0 10xq\ndrag a left 1\n",
            "tolerance x\n",
            "win a 0,0 10x10\ndrag a left 1\nexpect clamp x\n",
            "win a 0,0 10x10\ndrag a left 1\nexpect z 0,0 1x1\n",
            "frobnicate\n",
        ] {
            assert!(matches!(Fixture::parse(text), Err(FixtureError::Parse(_))), "accepted: {text:?}");
        }
    }

    #[test]
    fn load_reads_a_file_and_reports_missing_files() {
        let d = temp_dir("load");
        let p = d.join("one.layout");
        std::fs::write(&p, BASIC).unwrap();
        assert!(load(&p).is_ok());
        assert!(matches!(load(&d.join("missing.layout")), Err(FixtureError::Io(_))));
        std::fs::remove_dir_all(&d).unwrap();
    }

    #[test]
    fn collect_returns_sorted_layout_files_only() {
        let d = temp_dir("collect");
        for n in ["b.layout", "a.layout", "notes.txt"] {
            std::fs::write(d.join(n), BASIC).unwrap();
        }
        let names: Vec<_> =
            collect(&d).unwrap().iter().map(|p| p.file_name().unwrap().to_string_lossy().into_owned()).collect();
        assert_eq!(names, ["a.layout", "b.layout"]);
        assert_eq!(collect(&d.join("a.layout")).unwrap().len(), 1);
        std::fs::remove_dir_all(&d).unwrap();
    }
}
