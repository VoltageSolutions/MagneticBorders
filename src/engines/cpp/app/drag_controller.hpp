// DragController, the drag state machine, and its options.
#pragma once
#include <cstdint>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include "core/seam.hpp"
#include "ports.hpp"

namespace mb {

/// Construction-time options for DragController.
struct ControllerOptions {
    /// Glue tolerance in pixels at 96 DPI (scaled by Snapshot::dpi_scale).
    int glue_tolerance_px{12};
    /// Name used in log line prefixes.
    std::string build_name{"MagneticBorders"};
    /// Log every apply.
    bool verbose{false};
};

/// Drag state machine: Idle -> Classifying -> Resizing | Ignoring -> Idle.
/// On a resize it builds seams, throttles and applies member moves, learns minimum sizes, snaps the dragged window
/// back when a neighbor hits its minimum, and schedules a Verify pass. Single-threaded: every method runs on the UI
/// thread. Borrows its ports (they must outlive it); owns no OS resources.
class DragController final : public DragEvents, public TimerEvents {
public:
    /// Wires the controller to its ports (held by reference, not owned).
    DragController(WindowInventory& inventory, WindowMover& mover, Clock& clock, Timer& timer,
                   Log& log, ControllerOptions options = {});

    /// Resets drag state, takes a snapshot and enters Classifying (or Ignoring if glue is off or the window is ineligible).
    void on_move_size_start(WindowId id) override;
    /// Classifies the first size change, then throttles and applies member moves; re-arms the Settle timer.
    void on_location_change(WindowId id) override;
    /// Applies the final frames (snapping the dragged window back if clamped) and arms the Verify timer.
    void on_move_size_end(WindowId id) override;
    /// Dispatches Flush, Settle and Verify timer expiry.
    void on_timer(TimerKind kind) override;

    /// Sets the glue tolerance in pixels; takes effect on the next drag. UI thread.
    void set_glue_tolerance(int px) { opts_.glue_tolerance_px = px; }
    /// Turns glue on or off; takes effect on the next drag. UI thread.
    void set_glue_active(bool active) { glue_active_ = active; }
    /// Number of resize drags seen since start.
    [[nodiscard]] std::uint64_t drags() const { return drags_; }
    /// Number of resize drags that had at least one seam.
    [[nodiscard]] std::uint64_t glued_drags() const { return glued_drags_; }

private:
    enum class State { Idle, Classifying, Resizing, Ignoring };
    struct Planned {
        std::vector<Move> moves;
        std::vector<int> clamped;
    };

    bool classify(const Rect& frame);
    [[nodiscard]] Planned plan_for(const Rect& frame) const;
    std::vector<Move> final_moves(const Rect& frame, bool& snapped);
    void apply_frame(const Rect& frame);
    void apply(const std::vector<Move>& moves);
    void learn_minimums(const std::vector<Move>& requested, const std::vector<Move>& applied);
    // Members whose current size differs from the last request; learns minimums as a side effect.
    std::string find_mismatches();
    void on_flush();
    void on_settle();
    void on_verify();
    void say(const std::string& msg);
    [[nodiscard]] std::uint64_t since_start() { return clock_.now_ms() - drag_start_ms_; }

    WindowInventory& inventory_;
    WindowMover& mover_;
    Clock& clock_;
    Timer& timer_;
    Log& log_;
    ControllerOptions opts_;

    State state_{State::Idle};
    bool glue_active_{true};
    std::uint64_t drags_{0};
    std::uint64_t glued_drags_{0};
    bool verify_pending_{false};
    WindowId dragged_{};
    std::uint64_t drag_start_ms_{0};
    Rect start_frame_{};
    Rect last_frame_{};
    Rect pending_frame_{};
    Rect final_frame_{};
    Snapshot snapshot_;
    std::vector<Seam> seams_;
    std::unordered_map<WindowId, Rect> requested_;  // last requested frame per member
    std::optional<std::uint64_t> last_apply_ms_;
    bool pending_{false};
    std::unordered_map<WindowId, std::pair<int, int>> learned_min_;  // id -> (min_w, min_h)
};

}  // namespace mb
