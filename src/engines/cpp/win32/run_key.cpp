// set_start_at_login implementation.
#include "run_key.hpp"

#include "win_common.hpp"

namespace mb::win32 {
namespace {
constexpr wchar_t kRunKey[] = L"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
constexpr wchar_t kValue[] = L"MagneticBorders";
}  // namespace

void set_start_at_login(bool enabled, const std::filesystem::path& exe) {
    if (enabled) {
        const std::wstring value = L"\"" + exe.wstring() + L"\"";
        RegSetKeyValueW(HKEY_CURRENT_USER, kRunKey, kValue, REG_SZ, value.c_str(),
                        static_cast<DWORD>((value.size() + 1) * sizeof(wchar_t)));
    } else {
        RegDeleteKeyValueW(HKEY_CURRENT_USER, kRunKey, kValue);
    }
}

}  // namespace mb::win32
