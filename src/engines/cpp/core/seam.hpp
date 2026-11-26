// Seam model and the two core operations: build_seam (find glued windows) and propagate (plan their moves).
#pragma once
#include <optional>
#include <span>
#include <vector>

#include "geometry.hpp"

namespace mb {

/// Which edge of a glued window sits on the seam: its edge of the same kind as the dragged edge, or the facing one.
enum class Side { Same, Opposite };

/// One window glued to the dragged edge.
struct SeamMember {
    /// Member window.
    WindowId id{};
    /// Which of the member's edges lies on the seam.
    Side side{};
    /// Signed offset of the member's seam edge from the dragged edge at drag start (member_edge - x). Preserved while dragging.
    int d{};  // member_edge - X (signed)
    /// Member frame at drag start.
    Rect frame{};
    /// Minimum width; the controller raises it when a window refuses to shrink.
    int min_w{1};
    /// Minimum height; the controller raises it when a window refuses to shrink.
    int min_h{1};
};

/// The set of windows that move together with one dragged edge. Built once per resize by build_seam and
/// owned by DragController for the rest of the drag.
struct Seam {
    /// The window the user is resizing.
    WindowId dragged_id{};
    /// The dragged edge of that window.
    Edge edge{};
    /// Coordinate of the dragged edge at drag start.
    int x{};  // coordinate of the dragged edge at drag start
    /// Glued windows, including those reached through the stacking closure.
    std::vector<SeamMember> members;
};

/// Result of propagate.
struct Plan {
    /// New frames for the seam members (never the dragged window).
    std::vector<Move> moves;
    /// Position of the dragged edge after clamping to every member's minimum size.
    int clamped_pos{};
};

/// Returns the edge of member m that follows the dragged edge of seam s.
[[nodiscard]] constexpr Edge moving_edge(const Seam& s, const SeamMember& m) noexcept {
    return m.side == Side::Same ? s.edge : opposite(s.edge);
}

/// Finds the windows glued to the given edge of the dragged window.
/// A window is a candidate when its facing or same-kind edge is within cfg.glue_tolerance of the dragged edge;
/// candidates join the seam when they touch the growing perpendicular span. Returns nullopt when the dragged window
/// is unknown, the edge is on the work-area border, or nothing is glued. Pure; called from DragController::classify.
[[nodiscard]] std::optional<Seam> build_seam(std::span<const WinInfo> windows, WindowId dragged_id,
                                             Edge edge, const Rect& work_area, const Config& cfg);

/// Plans member frames for a new dragged-edge position.
/// Clamps new_pos so no member drops below its minimum size (falls back to seam.x if the limits conflict) and keeps
/// each member's offset d. Pure; called from DragController::plan_for and the fixture runner.
[[nodiscard]] Plan propagate(const Seam& seam, int new_pos);

}  // namespace mb
