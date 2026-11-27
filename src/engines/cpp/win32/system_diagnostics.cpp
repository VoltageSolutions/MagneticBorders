// log_system_diagnostics implementation.
#include "system_diagnostics.hpp"

#include "win_common.hpp"  // first among the Windows headers: it defines NOMINMAX and includes windows.h

#include <dwmapi.h>
#include <shellscalingapi.h>

#include <string>
#include <vector>

#include "move_diagnostics.hpp"
#include "paths.hpp"

namespace mb::win32 {
namespace {

constexpr const wchar_t* kVersionKey = L"SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion";

/// Returns `<build>.<revision>` from the registry, or `unknown`.
std::string os_build() {
    wchar_t build[16]{};
    DWORD size = sizeof(build);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, kVersionKey, L"CurrentBuildNumber", RRF_RT_REG_SZ, nullptr,
                     build, &size) != ERROR_SUCCESS)
        return "unknown";
    std::string text = wide_to_utf8(build);
    DWORD revision = 0;
    size = sizeof(revision);
    if (RegGetValueW(HKEY_LOCAL_MACHINE, kVersionKey, L"UBR", RRF_RT_REG_DWORD, nullptr, &revision,
                     &size) == ERROR_SUCCESS)
        text += "." + std::to_string(revision);
    return text;
}

/// Returns the DPI awareness of the calling thread.
const char* dpi_awareness() {
    const DPI_AWARENESS_CONTEXT ctx = GetThreadDpiAwarenessContext();
    if (AreDpiAwarenessContextsEqual(ctx, DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2))
        return "per-monitor-v2";
    switch (GetAwarenessFromDpiAwarenessContext(ctx)) {
        case DPI_AWARENESS_UNAWARE: return "unaware";
        case DPI_AWARENESS_SYSTEM_AWARE: return "system";
        case DPI_AWARENESS_PER_MONITOR_AWARE: return "per-monitor";
        default: return "unknown";
    }
}

/// EnumDisplayMonitors callback: appends one monitor line to the vector behind `lp`.
BOOL CALLBACK on_monitor(HMONITOR monitor, HDC /*dc*/, LPRECT /*rect*/, LPARAM lp) {
    auto& lines = *reinterpret_cast<std::vector<std::string>*>(lp);
    MONITORINFO info{};
    info.cbSize = sizeof(info);
    if (!GetMonitorInfoW(monitor, &info)) return TRUE;
    UINT dpi_x = 0;
    UINT dpi_y = 0;
    if (FAILED(GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, &dpi_x, &dpi_y))) {
        dpi_x = 0;
        dpi_y = 0;
    }
    const auto rect = [](const RECT& r) { return Rect{r.left, r.top, r.right, r.bottom}; };
    lines.push_back(diag::monitor_line(static_cast<int>(lines.size()), rect(info.rcMonitor),
                                       rect(info.rcWork), dpi_x, dpi_y,
                                       (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
    return TRUE;
}

}  // namespace

void log_system_diagnostics(Log& log) {
    std::vector<std::string> monitors;
    EnumDisplayMonitors(nullptr, nullptr, on_monitor, reinterpret_cast<LPARAM>(&monitors));
    BOOL composition = FALSE;
    if (FAILED(DwmIsCompositionEnabled(&composition))) composition = FALSE;
    log.debug(diag::system_line(os_build(), dpi_awareness(), composition != FALSE,
                                static_cast<int>(monitors.size())));
    for (const auto& line : monitors) log.debug(line);
}

}  // namespace mb::win32
