// FileLog implementation.
#include "file_log.hpp"

#include <chrono>
#include <cstdint>
#include <cstdio>
#include <ctime>

namespace mb::win32 {

FileLog::FileLog(const std::filesystem::path& file, bool mirror_console, bool debug_enabled)
    : mirror_(mirror_console), debug_(debug_enabled) {
    std::error_code ec;
    const auto size = std::filesystem::file_size(file, ec);
    const bool truncate = !ec && size > (std::uintmax_t{1} << 20);
    out_.open(file, std::ios::binary | (truncate ? std::ios::trunc : std::ios::app));
}

void FileLog::info(std::string_view msg) {
    std::scoped_lock lock(mu_);
    if (out_) {
        using namespace std::chrono;
        const auto now = system_clock::now();
        const std::time_t t = system_clock::to_time_t(now);
        std::tm tm{};
        localtime_s(&tm, &t);
        char stamp[32];
        std::snprintf(
            stamp, sizeof(stamp), "%04d-%02d-%02d %02d:%02d:%02d.%03d ", tm.tm_year + 1900,
            tm.tm_mon + 1, tm.tm_mday, tm.tm_hour, tm.tm_min, tm.tm_sec,
            static_cast<int>(duration_cast<milliseconds>(now.time_since_epoch()).count() % 1000));
        out_ << stamp << msg << '\n';
        out_.flush();
    }
    if (mirror_) {
        std::fprintf(stdout, "%.*s\n", static_cast<int>(msg.size()), msg.data());
        std::fflush(stdout);
    }
}

void FileLog::debug(std::string_view msg) {
    if (debug_) info(msg);
}

}  // namespace mb::win32
