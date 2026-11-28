// Text of the verbose `diag:` lines that the drag controller writes. No Win32 types, so the formatting is
// unit-testable on any platform. The same text is produced by the Rust and C# engines (docs/spec/engine.md).
#pragma once
#include <optional>
#include <string>
#include <string_view>

#include "core/geometry.hpp"

namespace mb::drag_diag {

/// Formats a rectangle as `l,t,r,b`.
[[nodiscard]] inline std::string rect_text(const Rect& r) {
    return std::to_string(r.l) + "," + std::to_string(r.t) + "," + std::to_string(r.r) + "," +
           std::to_string(r.b);
}

/// Formats a point as `x,y`.
[[nodiscard]] inline std::string point_text(const Point& p) {
    return std::to_string(p.x) + "," + std::to_string(p.y);
}

/// Line for a mouse movement that the hook changed: the position Windows proposed, the cursor position the engine
/// set instead, and the window frame that cursor position gives.
[[nodiscard]] inline std::string pointer_hold_line(const Point& proposed, const Point& cursor,
                                                   const Rect& window) {
    return "diag: pointer hold proposed=" + point_text(proposed) + " cursor=" + point_text(cursor) +
           " window=" + rect_text(window);
}

/// Line for a button release seen by the hook: the position of the release event and the cursor position the engine
/// last set or let through.
[[nodiscard]] inline std::string pointer_release_line(const Point& cursor, const Point& expected) {
    return "diag: pointer release cursor=" + point_text(cursor) +
           " expected=" + point_text(expected);
}

/// Line for a grab-offset calibration: the axis (`x` or `y`), the error that was measured, the cursor position the
/// engine set, and the window frame that position now gives.
[[nodiscard]] inline std::string pointer_calibrate_line(bool x_axis, int error, const Point& cursor,
                                                        const Rect& window) {
    return std::string("diag: pointer calibrate axis=") + (x_axis ? "x" : "y") +
           " error=" + std::to_string(error) + " cursor=" + point_text(cursor) +
           " window=" + rect_text(window);
}

/// Line for the decision at the end of a pointer-mode move: the window's frame (`unavailable` when unreadable), the
/// target position (`none` when no axis was held), and the action, `applied`, `none`, or `skipped-size`.
[[nodiscard]] inline std::string move_correction_line(WindowId id, const std::optional<Rect>& frame,
                                                      const std::optional<Rect>& held,
                                                      std::string_view action) {
    return "diag: move correction id=" + std::to_string(id) +
           " frame=" + (frame ? rect_text(*frame) : std::string("unavailable")) +
           " held=" + (held ? rect_text(*held) : std::string("none")) +
           " action=" + std::string(action);
}

}  // namespace mb::drag_diag
