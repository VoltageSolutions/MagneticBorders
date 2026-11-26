// Implementations of the path and string helpers.
#include "paths.hpp"

#include "win_common.hpp"

namespace mb::win32 {

std::wstring utf8_to_wide(const std::string& s) {
    if (s.empty()) return {};
    const int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), nullptr, 0);
    std::wstring w(static_cast<std::size_t>(n), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, s.data(), static_cast<int>(s.size()), w.data(), n);
    return w;
}

std::string wide_to_utf8(const std::wstring& w) {
    if (w.empty()) return {};
    const int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), nullptr, 0,
                                      nullptr, nullptr);
    std::string s(static_cast<std::size_t>(n), '\0');
    WideCharToMultiByte(CP_UTF8, 0, w.data(), static_cast<int>(w.size()), s.data(), n, nullptr,
                        nullptr);
    return s;
}

std::filesystem::path data_dir() {
    wchar_t buf[MAX_PATH];
    const DWORD n = GetEnvironmentVariableW(L"LOCALAPPDATA", buf, MAX_PATH);
    std::filesystem::path dir = (n == 0 || n >= MAX_PATH) ? std::filesystem::temp_directory_path()
                                                          : std::filesystem::path(buf);
    dir /= L"MagneticBorders";
    std::error_code ec;
    std::filesystem::create_directories(dir, ec);
    return dir;
}

std::filesystem::path settings_path() { return data_dir() / L"settings.json"; }

std::filesystem::path log_path(const std::string& engine) {
    const auto dir = data_dir() / L"logs";
    std::error_code ec;
    std::filesystem::create_directories(dir, ec);
    return dir / (utf8_to_wide(engine) + L".log");
}

std::filesystem::path exe_path() {
    wchar_t buf[32768];
    const DWORD n = GetModuleFileNameW(nullptr, buf, static_cast<DWORD>(std::size(buf)));
    return {std::wstring(buf, n)};
}

std::optional<std::filesystem::path> find_settings_app() {
    const auto folder = exe_path().parent_path();
    std::error_code ec;
    constexpr auto exe = L"MagneticBorders.Settings.exe";
    // Next to the app; ..\settings\ (release zip, artifacts\local\<arch>\); ..\..\settings\<arch>\ (artifacts\<build>\<arch>\).
    for (const auto& cand : {folder / exe, folder / L".." / L"settings" / exe,
                             folder / L".." / L".." / L"settings" / folder.filename() / exe}) {
        if (std::filesystem::exists(cand, ec)) return std::filesystem::weakly_canonical(cand, ec);
    }
    return std::nullopt;
}

bool launch_settings_app(const wchar_t* args) {
    const auto app = find_settings_app();
    if (!app) return false;
    std::wstring cmd = L"\"" + app->wstring() + L"\"";
    if (args != nullptr && *args != L'\0') {
        cmd += L' ';
        cmd += args;
    }
    STARTUPINFOW si{};
    si.cb = sizeof(si);
    si.dwFlags = STARTF_FORCEOFFFEEDBACK;
    PROCESS_INFORMATION pi{};
    const std::wstring dir = app->parent_path().wstring();
    const wchar_t* exe = app->c_str();
    const wchar_t* cwd = dir.c_str();
    const BOOL started =
        CreateProcessW(exe, cmd.data(), nullptr, nullptr, FALSE, 0, nullptr, cwd, &si, &pi);
    if (started == FALSE) return false;
    CloseHandle(pi.hThread);
    CloseHandle(pi.hProcess);
    return true;
}

}  // namespace mb::win32
