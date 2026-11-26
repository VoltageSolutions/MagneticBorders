//! Integration test: every fixture under `tests/fixtures` must pass.

use std::path::Path;

#[test]
fn all_fixtures_pass() {
    let dir = Path::new(env!("CARGO_MANIFEST_DIR")).join("../../../../tests/fixtures");
    let files = mb_fixtures::collect(&dir).unwrap();
    assert!(!files.is_empty(), "no fixtures found in {}", dir.display());
    let mut fails = vec![];
    for p in files {
        if let Err(e) = mb_fixtures::load(&p).and_then(|f| f.check()) {
            fails.push(format!("{}: {e}", p.display()));
        }
    }
    assert!(fails.is_empty(), "{}", fails.join("\n"));
}
