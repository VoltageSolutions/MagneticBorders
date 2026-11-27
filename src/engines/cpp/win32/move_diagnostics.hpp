// Text of the verbose diagnostic lines written by Win32Mover and the start-up system report. No Win32 types, so the
// formatting is unit-testable on any platform. The same text is produced by the Rust and C# engines (docs/spec/engine.md).
#pragma once
#include <cstddef>
#include <cstdio>
#include <optional>
#include <string>
#include <string_view>

#include "core/geometry.hpp"

namespace mb::win32::diag {

/// Formats a rectangle as `l,t,r,b`.
[[nodiscard]] inline std::string rect_text(const Rect& r) {
    return std::to_string(r.l) + "," + std::to_string(r.t) + "," + std::to_string(r.r) + "," +
           std::to_string(r.b);
}

/// Returns `actual - requested` per side.
[[nodiscard]] constexpr Rect delta(const Rect& requested, const Rect& actual) noexcept {
    return {actual.l - requested.l, actual.t - requested.t, actual.r - requested.r,
            actual.b - requested.b};
}

/// Formats a window id as lower-case hex with a `0x` prefix.
[[nodiscard]] inline std::string hex_id(WindowId id) {
    char buf[24];
    std::snprintf(buf, sizeof(buf), "0x%llx", static_cast<unsigned long long>(id));
    return buf;
}

/// Formats SetWindowPos flags as `0x` and four hex digits.
[[nodiscard]] inline std::string flags_text(unsigned flags) {
    char buf[16];
    std::snprintf(buf, sizeof(buf), "0x%04x", flags);
    return buf;
}

/// Line for one applied batch. `mode` is `deferred` or `individual`.
[[nodiscard]] inline std::string batch_line(std::size_t windows, std::string_view mode,
                                            unsigned flags, long long elapsed_us,
                                            std::size_t mismatches) {
    return "move: batch windows=" + std::to_string(windows) + " mode=" + std::string(mode) +
           " flags=" + flags_text(flags) + " elapsed_us=" + std::to_string(elapsed_us) +
           " mismatches=" + std::to_string(mismatches);
}

/// Line for one window after a batch: the requested visual frame, the visual frame read back (`unavailable` when the
/// read fails), `delta=` (read back minus requested) when they differ, and `window_delta=` (GetWindowRect minus the
/// requested window rectangle) when that differs.
[[nodiscard]] inline std::string window_line(WindowId id, const Rect& requested,
                                             const std::optional<Rect>& actual,
                                             const Rect& window_delta) {
    std::string s = "move: hwnd=" + hex_id(id) + " requested=" + rect_text(requested) +
                    " actual=" + (actual ? rect_text(*actual) : std::string("unavailable"));
    if (actual && *actual != requested) s += " delta=" + rect_text(delta(requested, *actual));
    if (window_delta != Rect{}) s += " window_delta=" + rect_text(window_delta);
    return s;
}

/// Line for a failed deferred batch. `step` is `begin`, `defer` or `end`; `id` is 0 when no window is involved.
[[nodiscard]] inline std::string deferred_failure_line(std::string_view step, WindowId id,
                                                       unsigned long error) {
    return "move: deferred batch failed step=" + std::string(step) + " hwnd=" + hex_id(id) +
           " error=" + std::to_string(error) + ", falling back to SetWindowPos";
}

/// Line for a failed SetWindowPos call.
[[nodiscard]] inline std::string set_window_pos_failure_line(WindowId id, unsigned long error) {
    return "move: SetWindowPos failed hwnd=" + hex_id(id) + " error=" + std::to_string(error);
}

/// Line for a window left out of a batch. `reason` is `no-offsets`, `banned` or `hung`.
[[nodiscard]] inline std::string skip_line(WindowId id, std::string_view reason) {
    return "move: skipped hwnd=" + hex_id(id) + " reason=" + std::string(reason);
}

/// Start-up line with the system facts. `dpi_awareness` is `unaware`, `system`, `per-monitor`, `per-monitor-v2` or
/// `unknown`; `os_build` is `<build>.<revision>` or `unknown`.
[[nodiscard]] inline std::string system_line(std::string_view os_build,
                                             std::string_view dpi_awareness, bool dwm_composition,
                                             int monitors) {
    return "diag: system os_build=" + std::string(os_build) +
           " dpi_awareness=" + std::string(dpi_awareness) +
           " dwm_composition=" + (dwm_composition ? "on" : "off") +
           " monitors=" + std::to_string(monitors);
}

/// Start-up line for one monitor.
[[nodiscard]] inline std::string monitor_line(int index, const Rect& bounds, const Rect& work,
                                              unsigned dpi_x, unsigned dpi_y, bool primary) {
    return "diag: monitor " + std::to_string(index) + " rect=" + rect_text(bounds) +
           " work=" + rect_text(work) + " dpi=" + std::to_string(dpi_x) + "x" +
           std::to_string(dpi_y) + " primary=" + (primary ? "yes" : "no");
}

}  // namespace mb::win32::diag
