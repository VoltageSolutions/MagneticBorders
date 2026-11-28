// PointerDrag implementation.
#include "pointer_drag.hpp"

#include <utility>

namespace mb {
namespace {
/// Returns r with its left edge at x, same width.
Rect at_x(Rect r, int x) {
    r.r += x - r.l;
    r.l = x;
    return r;
}
/// Returns r with its top edge at y, same height.
Rect at_y(Rect r, int y) {
    r.b += y - r.t;
    r.t = y;
    return r;
}
}  // namespace

PointerDrag::PointerDrag(Attraction attraction, const Rect& frame, const Point& cursor)
    : attraction_(std::move(attraction)), frame0_(frame), cursor0_(cursor), cursor_(cursor),
      free_(frame) {}

Rect PointerDrag::window() const noexcept {
    return at_y(at_x(frame0_, frame0_.l + cursor_.x - cursor0_.x),
                frame0_.t + cursor_.y - cursor0_.y);
}

std::optional<Point> PointerDrag::calibrate(bool x_axis, int error) {
    if (error == 0 || !(x_axis ? attraction_.held_x() : attraction_.held_y())) return std::nullopt;
    // SNAP-077: the grab offset is constant for the drag, so the reference frame moves by the error for good.
    const Rect held = attraction_.current();
    if (x_axis) {
        frame0_ = at_x(frame0_, frame0_.l + error);
        free_ = at_x(free_, free_.l + error);
        cursor_.x = cursor0_.x + held.l - frame0_.l;
    } else {
        frame0_ = at_y(frame0_, frame0_.t + error);
        free_ = at_y(free_, free_.t + error);
        cursor_.y = cursor0_.y + held.t - frame0_.t;
    }
    return cursor_;
}

Point PointerDrag::move(const Point& proposed, std::uint64_t now_ms) {
    const int dx = proposed.x - cursor_.x;
    const int dy = proposed.y - cursor_.y;
    const bool held_x = attraction_.held_x();
    const bool held_y = attraction_.held_y();
    // SNAP-071: a held axis builds up the mouse's movement; a free axis follows the cursor, so a free position
    // left far out by an earlier hold starts again from where the window is.
    free_ = held_x ? at_x(free_, free_.l + dx) : at_x(free_, frame0_.l + proposed.x - cursor0_.x);
    free_ = held_y ? at_y(free_, free_.t + dy) : at_y(free_, frame0_.t + proposed.y - cursor0_.y);

    const Rect held = attraction_.update(free_, now_ms);
    // SNAP-072: the cursor goes where the held window position puts it.
    Point out{cursor0_.x + held.l - frame0_.l, cursor0_.y + held.t - frame0_.t};
    // SNAP-073: an axis let go in this move carries on from where the cursor is, so neither the cursor nor the
    // window jumps.
    if (held_x && !attraction_.held_x()) out.x = cursor_.x + dx;
    if (held_y && !attraction_.held_y()) out.y = cursor_.y + dy;
    cursor_ = out;
    return out;
}

}  // namespace mb
