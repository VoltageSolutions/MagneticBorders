// Win32Mover implementation (deferred batch move with per-window fallback).
#include "win32_mover.hpp"

namespace mb::win32 {
namespace {

constexpr UINT kFlags = SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER;

struct Target {
    WindowId id;
    HWND hwnd;
    RECT wr;
};

bool apply_deferred(const std::vector<Target>& targets) {
    HDWP hdwp = BeginDeferWindowPos(static_cast<int>(targets.size()));
    if (!hdwp) return false;
    for (const auto& t : targets) {
        hdwp = DeferWindowPos(hdwp, t.hwnd, nullptr, t.wr.left, t.wr.top, t.wr.right - t.wr.left,
                              t.wr.bottom - t.wr.top, kFlags);
        if (!hdwp) return false;  // handle is invalid after a failure
    }
    return EndDeferWindowPos(hdwp) != FALSE;
}

}  // namespace

std::vector<Move> Win32Mover::apply(std::span<const Move> moves) {
    std::vector<Target> targets;
    for (const auto& m : moves) {
        const HWND h = to_hwnd(m.id);
        const auto off = registry_.offsets(m.id);
        if (!off || registry_.banned(m.id) || IsHungAppWindow(h)) continue;
        targets.push_back(
            {m.id, h,
             RECT{m.frame.l + off->l, m.frame.t + off->t, m.frame.r + off->r, m.frame.b + off->b}});
    }

    if (!targets.empty() && !apply_deferred(targets)) {
        for (const auto& t : targets) {
            if (!SetWindowPos(t.hwnd, nullptr, t.wr.left, t.wr.top, t.wr.right - t.wr.left,
                              t.wr.bottom - t.wr.top, kFlags)) {
                registry_.ban(t.id);  // e.g. elevated window: access denied
            }
        }
    }

    std::vector<Move> applied;
    for (const auto& t : targets) {
        if (registry_.banned(t.id)) continue;
        if (const auto f = visual_frame(t.hwnd)) applied.push_back({t.id, *f});
    }
    return applied;
}

}  // namespace mb::win32
