// Public entry point of the engine composition root.
#pragma once
#include <string>

namespace mb::host {

/// Engine id used in the log file name and pipe status.
inline constexpr const char* kEngineId = "cpp";

/// Command-line options for run_engine.
struct EngineOptions {
    /// Attach a console and mirror the log to it (`--console`).
    bool console{false};
    /// Enable debug logging (`--verbose`).
    bool verbose{false};
};

/// Composition root for the running engine: wires adapters, controller, tray, watcher, and pipe, runs the message loop
/// and returns the exit code (2 after a fatal exception). Everything touching engine state runs on the calling (UI)
/// thread; called from main().
int run_engine(const EngineOptions& options);

}  // namespace mb::host
