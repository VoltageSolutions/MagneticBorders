// Path and string helpers: UTF-8/UTF-16 conversion, data folder, settings and log paths, settings app launcher.
#pragma once
#include <filesystem>
#include <optional>
#include <string>

namespace mb::win32 {

/// Converts UTF-8 to UTF-16.
[[nodiscard]] std::wstring utf8_to_wide(const std::string& s);
/// Converts UTF-16 to UTF-8.
[[nodiscard]] std::string wide_to_utf8(const std::wstring& s);

/// Returns %LOCALAPPDATA%\MagneticBorders, creating it on demand (falls back to the temp folder).
[[nodiscard]] std::filesystem::path data_dir();
/// Returns `settings.json` inside data_dir().
[[nodiscard]] std::filesystem::path settings_path();
/// Returns `logs\<engine>.log` inside data_dir(), creating the folder.
[[nodiscard]] std::filesystem::path log_path(const std::string& engine);
/// Returns the full path of the running executable.
[[nodiscard]] std::filesystem::path exe_path();
/// Finds MagneticBorders.Settings.exe next to the engine, else in ..\settings\ relative to the engine's folder.
[[nodiscard]] std::optional<std::filesystem::path> find_settings_app();
/// Launches the settings app with the given arguments; returns false if it was not found or could not start.
/// Uses CreateProcessW with STARTF_FORCEOFFFEEDBACK so Windows does not show the "app starting" busy cursor
/// while the new process creates its first window.
bool launch_settings_app(const wchar_t* args);

}  // namespace mb::win32
