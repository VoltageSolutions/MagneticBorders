// SettingsWatcher, a background folder watcher that notifies the UI thread.
#pragma once
#include <filesystem>
#include <thread>

#include "win_common.hpp"

namespace mb::win32 {

/// Background thread watching a directory; it only posts `message` to `target` (the UI thread does the work).
/// Owns the thread and a stop event; stops and joins on destruction.
class SettingsWatcher {
public:
    /// Starts watching `dir` for file changes.
    SettingsWatcher(const std::filesystem::path& dir, HWND target, UINT message);
    /// Signals the stop event and joins the thread.
    ~SettingsWatcher();
    SettingsWatcher(const SettingsWatcher&) = delete;
    SettingsWatcher& operator=(const SettingsWatcher&) = delete;

private:
    UniqueHandle stop_;
    std::thread thread_;
};

}  // namespace mb::win32
