// PointerDrag: Magnetic attraction driven by the mouse cursor during a move (SNAP-070 to SNAP-077). No OS types.
#pragma once
#include <cstdint>
#include <optional>

#include "attraction.hpp"
#include "geometry.hpp"

namespace mb {

/// Runs an Attraction from mouse movement instead of from window positions. Windows keeps the window at a fixed
/// offset from the cursor during a move, so holding the window means holding the cursor: on a held axis the cursor
/// stays put while the mouse's movement builds up in the free position, and on a free axis the cursor goes where
/// the mouse sends it. Windows then moves the window to the held position itself, so nothing is corrected after
/// the fact. Single-threaded and pure: the caller supplies the clock.
class PointerDrag {
public:
    /// Starts from the window's frame and the cursor position at the same moment.
    PointerDrag(Attraction attraction, const Rect& frame, const Point& cursor);

    /// Takes the position Windows is about to give the cursor and returns where the cursor must go instead. The
    /// result equals `proposed` when nothing is held.
    [[nodiscard]] Point move(const Point& proposed, std::uint64_t now_ms);

    /// Corrects the estimate of the window's offset from the cursor on one axis. `error` is the window's actual
    /// leading coordinate (left for x, top for y) minus window() on that axis, measured while the cursor was still
    /// on that axis. The reference frame and the free position shift by `error` on the axis, so window() matches
    /// the actual window, and the result is the cursor that puts the window on the held position: the last cursor
    /// with the axis moved there. Returns nullopt when the axis is not held or `error` is 0.
    [[nodiscard]] std::optional<Point> calibrate(bool x_axis, int error);

    /// The attraction, for its events and counters.
    [[nodiscard]] Attraction& attraction() noexcept { return attraction_; }
    /// The cursor position returned by the last move (the start position before the first).
    [[nodiscard]] Point cursor() const noexcept { return cursor_; }
    /// The window position that matches the cursor returned by the last move.
    [[nodiscard]] Rect window() const noexcept;

private:
    Attraction attraction_;
    Rect frame0_;
    Point cursor0_;
    Point cursor_;
    Rect free_;
};

}  // namespace mb
