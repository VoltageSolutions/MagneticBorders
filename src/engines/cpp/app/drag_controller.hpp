// DragController, the drag state machine, and its options.
#pragma once
#include <cstdint>
#include <optional>
#include <string>
#include <unordered_map>
#include <utility>
#include <vector>

#include "core/attraction.hpp"
#include "core/pointer_drag.hpp"
#include "core/seam.hpp"
#include "ports.hpp"

namespace mb {

/// Magnetic attraction tunables in effective pixels (96 DPI); the controller scales them by Snapshot::dpi_scale.
struct SnapOptions {
    /// Pull-in distance (`snap.distance_px`).
    int distance_px{16};
    /// Breakaway distance (`snap.breakaway_px`).
    int breakaway_px{24};
    /// Snapping speed limit in pixels per second (`snap.max_speed`).
    int max_speed{600};
    /// Work-area sides are targets (`snap.screen_edges`).
    bool screen_edges{true};
    /// Other windows are targets (`snap.windows`).
    bool windows{true};
    /// Member-wise equality.
    bool operator==(const SnapOptions&) const = default;
};

/// Construction-time options for DragController.
struct ControllerOptions {
    /// Glue tolerance in pixels at 96 DPI (scaled by Snapshot::dpi_scale).
    int glue_tolerance_px{12};
    /// Name used in log line prefixes.
    std::string build_name{"MagneticBorders"};
    /// Log every apply.
    bool verbose{false};
    /// Magnetic attraction tunables.
    SnapOptions snap{};
};

/// Drag state machine: Idle -> Classifying -> Resizing | Moving | Ignoring -> Idle.
/// On a resize it builds seams, throttles, and applies member moves, learns minimum sizes, snaps the dragged window
/// back when a neighbor hits its minimum, and schedules a Verify pass. On a move it runs an Attraction and puts the
/// dragged window on the held position after every location change that is not its own. Single-threaded: every method runs on the UI
/// thread. Borrows its ports (they must outlive it); owns no OS resources.
class DragController final : public DragEvents, public TimerEvents, public PointerEvents {
public:
    /// Wires the controller to its ports (held by reference, not owned).
    DragController(WindowInventory& inventory, WindowMover& mover, Clock& clock, Timer& timer,
                   Log& log, ControllerOptions options = {});

    /// Resets drag state, takes a snapshot, and enters Classifying (or Ignoring if glue and attraction are off or the
    /// window is ineligible).
    void on_move_size_start(WindowId id) override;
    /// Classifies the first location change as a move or a resize. A resize throttles and applies member moves and
    /// re-arms the Settle timer; a move updates the Attraction and applies the held position.
    void on_location_change(WindowId id) override;
    /// After a resize applies the final frames (snapping the dragged window back if clamped) and arms the Verify
    /// timer. After a move applies the held position once when it still matters (SNAP-050 to SNAP-052).
    void on_move_size_end(WindowId id) override;
    /// Dispatches Flush, Settle, and Verify timer expiry.
    void on_timer(TimerKind kind) override;
    /// During a move with the pointer hook, returns where the cursor must go so the window lands on its held
    /// position (SNAP-070 to SNAP-074). Otherwise lets the movement through.
    [[nodiscard]] std::optional<Point> on_pointer_move(const Point& proposed) override;
    /// Stops the pointer hook as soon as the mouse button is released, so the cursor can never stay held after
    /// a move (SNAP-075).
    void on_pointer_release() override;

    /// Uses `hook` (or none) to hold the cursor during moves; takes effect on the next drag. Without a hook, or
    /// when it cannot start, a move moves the window back to the held position after Windows moves it. UI thread.
    void set_pointer_hook(PointerHook* hook) { hook_ = hook; }

    /// Sets the glue tolerance in pixels; takes effect on the next drag. UI thread.
    void set_glue_tolerance(int px) { opts_.glue_tolerance_px = px; }
    /// Turns glue on or off; takes effect on the next drag. UI thread.
    void set_glue_active(bool active) { glue_active_ = active; }
    /// Turns magnetic attraction on or off; takes effect on the next drag. UI thread.
    void set_snap_active(bool active) { snap_active_ = active; }
    /// Sets the attraction tunables; takes effect on the next drag. UI thread.
    void set_snap_options(const SnapOptions& options) { opts_.snap = options; }
    /// Number of move drags handled by attraction since start.
    [[nodiscard]] std::uint64_t move_drags() const { return move_drags_; }
    /// Number of times an edge started being held since start.
    [[nodiscard]] std::uint64_t snaps() const { return snaps_; }
    /// Number of resize drags seen since start.
    [[nodiscard]] std::uint64_t drags() const { return drags_; }
    /// Number of resize drags that had at least one seam.
    [[nodiscard]] std::uint64_t glued_drags() const { return glued_drags_; }

private:
    enum class State { Idle, Classifying, Resizing, Moving, Ignoring };
    struct Planned {
        std::vector<Move> moves;
        std::vector<int> clamped;
    };

    bool classify(const Rect& frame);
    void begin_move();
    void start_pointer();
    void stop_pointer();
    [[nodiscard]] Attraction* active_attraction();
    void on_move(const Rect& frame);
    void finish_move(WindowId id);
    void log_snap_events();
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
    /// The position the active attraction holds, or nullopt when no axis is held.
    std::optional<Rect> held_position();
    [[nodiscard]] std::uint64_t since_start() { return clock_.now_ms() - drag_start_ms_; }

    WindowInventory& inventory_;
    WindowMover& mover_;
    Clock& clock_;
    Timer& timer_;
    Log& log_;
    ControllerOptions opts_;

    State state_{State::Idle};
    bool glue_active_{true};
    bool snap_active_{true};
    bool drag_glue_{false};  // glue_active_ and snap_active_ as they were at drag start
    bool drag_snap_{false};
    std::uint64_t drags_{0};
    std::uint64_t glued_drags_{0};
    std::uint64_t move_drags_{0};
    std::uint64_t snaps_{0};
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
    std::optional<Attraction> attraction_;  // set while Moving without the pointer hook
    std::optional<PointerDrag> pointer_;    // set while Moving with the pointer hook (SNAP-070)
    PointerHook* hook_{nullptr};
    Rect last_free_{};               // last free position fed to attraction_
    std::optional<Rect> last_held_;  // frame the window reported after our last held move
    std::uint64_t drag_snaps_{0};
    std::unordered_map<WindowId, std::pair<int, int>> learned_min_;  // id -> (min_w, min_h)
};

}  // namespace mb
