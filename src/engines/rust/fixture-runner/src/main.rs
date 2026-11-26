//! `fixture-runner` binary: `fixture-runner [--print] <file-or-dir>`; exits non-zero when a fixture fails.

use mb_fixtures::{collect, load};
use std::path::Path;
use std::process::ExitCode;

fn main() -> ExitCode {
    let args: Vec<String> = std::env::args().skip(1).collect();
    if let [flag, file] = args.as_slice() {
        if flag == "--print" {
            return match load(Path::new(file)) {
                Ok(f) => {
                    print!("{}", f.print());
                    ExitCode::SUCCESS
                }
                Err(e) => {
                    eprintln!("{e}");
                    ExitCode::FAILURE
                }
            };
        }
    }
    let [target] = args.as_slice() else {
        eprintln!("usage: fixture-runner [--print] <file-or-dir>");
        return ExitCode::FAILURE;
    };
    let files = match collect(Path::new(target)) {
        Ok(f) => f,
        Err(e) => {
            eprintln!("{e}");
            return ExitCode::FAILURE;
        }
    };
    let mut failed = false;
    for p in files {
        match load(&p).and_then(|f| f.check()) {
            Ok(()) => println!("PASS {}", p.display()),
            Err(e) => {
                failed = true;
                println!("FAIL {}: {e}", p.display());
            }
        }
    }
    if failed {
        ExitCode::FAILURE
    } else {
        ExitCode::SUCCESS
    }
}
