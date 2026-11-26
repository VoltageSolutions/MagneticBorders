// read_settings_file implementation.
#include "settings.hpp"

#include <windows.h>

#include <fstream>
#include <sstream>

#include "win32/paths.hpp"

namespace mb::host {

std::optional<std::string> read_settings_file() {
    std::ifstream in(win32::settings_path(), std::ios::binary);
    if (!in) return std::nullopt;
    std::ostringstream ss;
    ss << in.rdbuf();
    return ss.str();
}

bool write_settings_file(const std::string& text) {
    const auto target = win32::settings_path();
    auto temp = target;
    temp += L".tmp";
    {
        std::ofstream out(temp, std::ios::binary | std::ios::trunc);
        if (!out) return false;
        out.write(text.data(), static_cast<std::streamsize>(text.size()));
        out.flush();
        if (!out) return false;
    }
    if (!MoveFileExW(temp.c_str(), target.c_str(),
                     MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH)) {
        DeleteFileW(temp.c_str());
        return false;
    }
    return true;
}

}  // namespace mb::host
