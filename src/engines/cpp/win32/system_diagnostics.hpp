// System report for the verbose log: OS build, DPI awareness, DWM composition and monitors.
#pragma once
#include "app/ports.hpp"

namespace mb::win32 {

/// Writes the start-up system lines (`diag: system ...` and one `diag: monitor ...` per monitor) with log.debug.
/// UI thread; called once when verbose logging is on.
void log_system_diagnostics(Log& log);

}  // namespace mb::win32
