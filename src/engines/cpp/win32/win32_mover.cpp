// Win32Mover implementation (deferred batch move with per-window fallback).
#include "win32_mover.hpp"

#include <chrono>
#include <string>

#include "move_diagnostics.hpp"

namespace mb::win32 {
namespace {

constexpr UINT kFlags = SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER;

struct Target {
    WindowId id;
    HWND hwnd;
    Rect frame;
    RECT wr;
};

/// Outcome of a deferred batch: `step` and `error` describe the first failing call.
struct DeferredResult {
    bool ok{true};
    const char* step{""};
    WindowId id{};
    DWORD error{};
};

/// Why a window is left out of a batch: it was never inventoried, it is banned, or it is hung.
const char* skip_reason(bool known, bool banned) {
    if (!known) return "no-offsets";
    return banned ? "banned" : "hung";
}

DeferredResult apply_deferred(const std::vector<Target>& targets) {
    HDWP hdwp = BeginDeferWindowPos(static_cast<int>(targets.size()));
    if (!hdwp) return {false, "begin", 0, GetLastError()};
    for (const auto& t : targets) {
        hdwp = DeferWindowPos(hdwp, t.hwnd, nullptr, t.wr.left, t.wr.top, t.wr.right - t.wr.left,
                              t.wr.bottom - t.wr.top, kFlags);
        if (!hdwp)
            return {false, "defer", t.id, GetLastError()};  // handle is invalid after a failure
    }
    if (EndDeferWindowPos(hdwp) == FALSE) return {false, "end", 0, GetLastError()};
    return {};
}

}  // namespace

std::vector<Move> Win32Mover::apply(std::span<const Move> moves) {
    std::vector<Target> targets;
    for (const auto& m : moves) {
        const HWND h = to_hwnd(m.id);
        const auto off = registry_.offsets(m.id);
        if (!off || registry_.banned(m.id) || IsHungAppWindow(h)) {
            if (diagnostics_)
                diagnostics_->debug(
                    diag::skip_line(m.id, skip_reason(off.has_value(), registry_.banned(m.id))));
            continue;
        }
        targets.push_back(
            {m.id, h, m.frame,
             RECT{m.frame.l + off->l, m.frame.t + off->t, m.frame.r + off->r, m.frame.b + off->b}});
    }

    const auto started = std::chrono::steady_clock::now();
    bool deferred = true;
    if (!targets.empty()) {
        const DeferredResult result = apply_deferred(targets);
        if (!result.ok) {
            deferred = false;
            if (diagnostics_)
                diagnostics_->debug(
                    diag::deferred_failure_line(result.step, result.id, result.error));
            for (const auto& t : targets) {
                if (!SetWindowPos(t.hwnd, nullptr, t.wr.left, t.wr.top, t.wr.right - t.wr.left,
                                  t.wr.bottom - t.wr.top, kFlags)) {
                    if (diagnostics_)
                        diagnostics_->debug(
                            diag::set_window_pos_failure_line(t.id, GetLastError()));
                    registry_.ban(t.id);  // e.g. elevated window: access denied
                }
            }
        }
    }
    const auto elapsed_us = std::chrono::duration_cast<std::chrono::microseconds>(
                                std::chrono::steady_clock::now() - started)
                                .count();

    std::vector<Move> applied;
    std::vector<std::string> lines;
    std::size_t mismatches = 0;
    for (const auto& t : targets) {
        const bool banned = registry_.banned(t.id);
        if (banned && !diagnostics_) continue;
        const auto f = visual_frame(t.hwnd);
        if (f && !banned) applied.push_back({t.id, *f});
        if (!diagnostics_) continue;
        RECT now{};
        Rect window_delta{};
        if (GetWindowRect(t.hwnd, &now))
            window_delta = diag::delta(Rect{t.wr.left, t.wr.top, t.wr.right, t.wr.bottom},
                                       Rect{now.left, now.top, now.right, now.bottom});
        if (f && *f != t.frame) ++mismatches;
        lines.push_back(diag::window_line(t.id, t.frame, f, window_delta));
    }
    if (diagnostics_ && !targets.empty()) {
        diagnostics_->debug(diag::batch_line(targets.size(), deferred ? "deferred" : "individual",
                                             kFlags, elapsed_us, mismatches));
        for (const auto& line : lines) diagnostics_->debug(line);
    }
    return applied;
}

}  // namespace mb::win32
