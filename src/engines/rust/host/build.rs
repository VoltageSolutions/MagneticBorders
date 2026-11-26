//! Build script: fills `app.rc` with the version and icon path and embeds it on Windows targets.

use std::path::PathBuf;

// app.rc is a template: the version comes from the workspace package version (CARGO_PKG_VERSION*)
// and the icon path is made absolute, then the result is compiled from OUT_DIR.
fn main() {
    println!("cargo:rerun-if-changed=app.rc");
    println!("cargo:rerun-if-changed=../../../common/assets/icon.ico");
    if std::env::var("CARGO_CFG_TARGET_OS").as_deref() == Ok("windows") {
        let manifest_dir = PathBuf::from(std::env::var("CARGO_MANIFEST_DIR").unwrap());
        let icon = manifest_dir.join("../../../common/assets/icon.ico").canonicalize().expect("icon.ico not found");
        let icon = icon.to_string_lossy().trim_start_matches(r"\\?\").replace('\\', "/");
        let template = std::fs::read_to_string(manifest_dir.join("app.rc")).expect("reading app.rc failed");
        let rc = template
            .replace("@ICON@", &icon)
            .replace("@MAJOR@", &std::env::var("CARGO_PKG_VERSION_MAJOR").unwrap())
            .replace("@MINOR@", &std::env::var("CARGO_PKG_VERSION_MINOR").unwrap())
            .replace("@PATCH@", &std::env::var("CARGO_PKG_VERSION_PATCH").unwrap())
            .replace("@VERSION@", &std::env::var("CARGO_PKG_VERSION").unwrap());
        let out = PathBuf::from(std::env::var("OUT_DIR").unwrap()).join("app.rc");
        std::fs::write(&out, rc).expect("writing app.rc failed");
        embed_resource::compile(&out, embed_resource::NONE).manifest_required().expect("embedding resources failed");
    }
}
