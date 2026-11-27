// Win32Inventory implementation: eligibility rules and window enumeration.
#include "win32_inventory.hpp"

#include <dwmapi.h>

#include <string>

#include "move_diagnostics.hpp"
#include "paths.hpp"

namespace mb::win32 {
namespace {

struct EnumContext {
    WindowRegistry* registry;
    HMONITOR monitor;
    DWORD own_pid;
    std::vector<WinInfo>* out;
    std::vector<std::pair<WindowId, Rect>>* stack;
    Log* diagnostics;
    HWND dragged;
};

/// True when a window hides what is below it: visible, not minimized, not cloaked, not click-through (overlays
/// such as the FancyZones editor), and not this process's own window (SNAP-038).
bool covers(HWND h, DWORD own_pid) {
    if (!IsWindowVisible(h) || IsIconic(h)) return false;
    DWORD cloaked = 0;
    if (SUCCEEDED(DwmGetWindowAttribute(h, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked)
        return false;
    const auto ex = static_cast<DWORD>(GetWindowLongPtrW(h, GWL_EXSTYLE));
    if (ex & WS_EX_TRANSPARENT) return false;
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    return pid != own_pid;
}

std::wstring exe_name_of(DWORD pid) {
    UniqueHandle proc{OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid)};
    if (!proc) return {};
    wchar_t buf[MAX_PATH * 2];
    auto len = static_cast<DWORD>(std::size(buf));
    if (!QueryFullProcessImageNameW(proc.get(), 0, buf, &len)) return {};
    std::wstring path(buf, len);
    const auto slash = path.find_last_of(L"\\/");
    return slash == std::wstring::npos ? path : path.substr(slash + 1);
}

std::wstring class_name_of(HWND h) {
    wchar_t buf[256];
    const int len = GetClassNameW(h, buf, static_cast<int>(std::size(buf)));
    return len > 0 ? std::wstring(buf, static_cast<std::size_t>(len)) : std::wstring{};
}

std::wstring exe_name_of_window(HWND h) {
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    return exe_name_of(pid);
}

void log_candidate(const EnumContext& ctx, HWND h, const Rect& frame, const RECT& wr) {
    ctx.diagnostics->debug(diag::candidate_line(
        to_id(h), frame, {wr.left, wr.top, wr.right, wr.bottom}, GetDpiForWindow(h),
        wide_to_utf8(class_name_of(h)), wide_to_utf8(exe_name_of_window(h)), h == ctx.dragged));
}

bool eligible(HWND h, const EnumContext& ctx) {
    if (!IsWindowVisible(h) || IsIconic(h) || IsZoomed(h)) return false;
    DWORD cloaked = 0;
    if (SUCCEEDED(DwmGetWindowAttribute(h, DWMWA_CLOAKED, &cloaked, sizeof(cloaked))) && cloaked)
        return false;
    const auto style = static_cast<DWORD>(GetWindowLongPtrW(h, GWL_STYLE));
    const auto ex = static_cast<DWORD>(GetWindowLongPtrW(h, GWL_EXSTYLE));
    if (!(style & WS_THICKFRAME) || (ex & WS_EX_TOOLWINDOW)) return false;
    if (GetWindow(h, GW_OWNER) != nullptr || GetWindowTextLengthW(h) == 0) return false;
    if (ctx.registry->banned(to_id(h))) return false;
    if (MonitorFromWindow(h, MONITOR_DEFAULTTONEAREST) != ctx.monitor) return false;
    DWORD pid = 0;
    GetWindowThreadProcessId(h, &pid);
    if (pid == ctx.own_pid) return false;
    return !ctx.registry->is_excluded_exe(exe_name_of(pid));
}

BOOL CALLBACK monitor_proc(HMONITOR mon, HDC, LPRECT, LPARAM lp) {
    MONITORINFO mi{};
    mi.cbSize = sizeof(mi);
    if (GetMonitorInfoW(mon, &mi)) {
        reinterpret_cast<std::vector<Rect>*>(lp)->push_back(
            {mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom});
    }
    return TRUE;
}

BOOL CALLBACK enum_proc(HWND h, LPARAM lp) {
    auto& ctx = *reinterpret_cast<EnumContext*>(lp);
    // EnumWindows walks the z-order from the top, so the stack comes out top first.
    if (covers(h, ctx.own_pid)) {
        if (const auto f = visual_frame(h); f && f->width() > 0 && f->height() > 0)
            ctx.stack->emplace_back(to_id(h), *f);
    }
    if (!eligible(h, ctx)) return TRUE;
    const auto frame = visual_frame(h);
    RECT wr{};
    if (!frame || !GetWindowRect(h, &wr)) return TRUE;
    ctx.registry->remember_offsets(to_id(h), {wr.left - frame->l, wr.top - frame->t,
                                              wr.right - frame->r, wr.bottom - frame->b});
    ctx.out->push_back(
        {to_id(h), *frame, GetSystemMetrics(SM_CXMINTRACK), GetSystemMetrics(SM_CYMINTRACK)});
    if (ctx.diagnostics) log_candidate(ctx, h, *frame, wr);
    return TRUE;
}

}  // namespace

Snapshot Win32Inventory::snapshot(WindowId dragged) {
    Snapshot snap;
    const HWND dh = to_hwnd(dragged);
    const HMONITOR mon = MonitorFromWindow(dh, MONITOR_DEFAULTTONEAREST);
    MONITORINFO mi{};
    mi.cbSize = sizeof(mi);
    if (GetMonitorInfoW(mon, &mi)) {
        snap.work_area = {mi.rcWork.left, mi.rcWork.top, mi.rcWork.right, mi.rcWork.bottom};
    }
    EnumDisplayMonitors(nullptr, nullptr, monitor_proc, reinterpret_cast<LPARAM>(&snap.work_areas));
    const UINT dpi = GetDpiForWindow(dh);
    snap.dpi_scale = dpi ? dpi / 96.0 : 1.0;

    EnumContext ctx{&registry_,   mon, GetCurrentProcessId(), &snap.windows, &snap.stack,
                    diagnostics_, dh};
    EnumWindows(enum_proc, reinterpret_cast<LPARAM>(&ctx));
    return snap;
}

std::optional<Rect> Win32Inventory::frame_of(WindowId id) { return visual_frame(to_hwnd(id)); }

void Win32Inventory::report_move_result(WindowId id, const std::optional<Rect>& held) {
    if (!diagnostics_) return;
    const HWND h = to_hwnd(id);
    RECT wr{};
    std::optional<Rect> rect;
    if (GetWindowRect(h, &wr)) rect = Rect{wr.left, wr.top, wr.right, wr.bottom};
    diagnostics_->debug(diag::move_result_line(id, visual_frame(h), rect, held));
}

}  // namespace mb::win32
