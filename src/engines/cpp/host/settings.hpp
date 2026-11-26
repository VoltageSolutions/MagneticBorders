// Settings file reader (the only file I/O for settings).
#pragma once
#include <optional>
#include <string>

namespace mb::host {

/// Returns the raw text of %LOCALAPPDATA%\MagneticBorders\settings.json, or nullopt when the file is absent or
/// unreadable. Parsing lives in the pure mb::parse_settings; called by EngineApp on the UI thread.
std::optional<std::string> read_settings_file();

/// Replaces settings.json atomically (temp file in the same folder, then replace). Returns false on failure.
bool write_settings_file(const std::string& text);

}  // namespace mb::host
