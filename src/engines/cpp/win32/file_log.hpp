// FileLog, the Log adapter that writes the engine log file.
#pragma once
#include <filesystem>
#include <fstream>
#include <mutex>

#include "app/ports.hpp"

namespace mb::win32 {

/// Log adapter: always writes to the log file (truncated at 1 MB on start); optionally mirrors to stdout.
/// Thread-safe: the pipe and watcher threads may log. Owns the file stream.
class FileLog final : public Log {
public:
    /// Opens `file` for appending (truncating it first when over 1 MB).
    FileLog(const std::filesystem::path& file, bool mirror_console, bool debug_enabled);
    /// Appends a timestamped line.
    void info(std::string_view msg) override;
    /// Like info, but only when debug output is enabled.
    void debug(std::string_view msg) override;
    /// Logs msg prefixed with "WARNING: ".
    void warn(std::string_view msg) { info("WARNING: " + std::string(msg)); }

private:
    std::mutex mu_;
    std::ofstream out_;
    bool mirror_;
    bool debug_;
};

}  // namespace mb::win32
