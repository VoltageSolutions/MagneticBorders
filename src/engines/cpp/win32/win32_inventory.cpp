// Win32Inventory implementation: eligibility rules and window enumeration.
#include "win32_inventory.hpp"

#include <dwmapi.h>

namespace mb::win32 {
namespace {

struct EnumContext {
    WindowRegistry* registry;
    HMONITOR monitor;
    DWORD own_pid;
    std::vector<WinInfo>* out;
};

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
    if (!eligible(h, ctx)) return TRUE;
    const auto frame = visual_frame(h);
    RECT wr{};
    if (!frame || !GetWindowRect(h, &wr)) return TRUE;
    ctx.registry->remember_offsets(to_id(h), {wr.left - frame->l, wr.top - frame->t,
                                              wr.right - frame->r, wr.bottom - frame->b});
    ctx.out->push_back(
        {to_id(h), *frame, GetSystemMetrics(SM_CXMINTRACK), GetSystemMetrics(SM_CYMINTRACK)});
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

    EnumContext ctx{&registry_, mon, GetCurrentProcessId(), &snap.windows};
    EnumWindows(enum_proc, reinterpret_cast<LPARAM>(&ctx));
    return snap;
}

std::optional<Rect> Win32Inventory::frame_of(WindowId id) { return visual_frame(to_hwnd(id)); }

}  // namespace mb::win32
