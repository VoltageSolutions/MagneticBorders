// Start-at-login registry helper.
#pragma once
#include <filesystem>

namespace mb::win32 {

/// Sets HKCU\...\Run\MagneticBorders to the quoted exe path when enabled; deletes the value when not.
void set_start_at_login(bool enabled, const std::filesystem::path& exe);

}  // namespace mb::win32
