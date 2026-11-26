// Magnetic attraction: the pure per-drag state that pulls a moved window's edges onto screen edges and onto the
// edges of other windows (SNAP-001 to SNAP-060). No OS types.
#pragma once
#include <array>
#include <cstdint>
#include <deque>
#include <optional>
#include <span>
#include <vector>

#include "geometry.hpp"

namespace mb {

/// What kind of line an edge is held on.
enum class TargetKind {
    /// A side of the work area (SNAP-030).
    Screen,
    /// Another window's facing edge, for example left to right (SNAP-033).
    EdgeToEdge,
    /// Another window's edge of the same side, for stacked windows (SNAP-034).
    Aligned
};

/// Why a held edge was let go.
enum class ReleaseReason {
    /// The free position moved farther than the breakaway distance from the line (SNAP-041).
    Breakaway,
    /// The target stopped being a target, for example the window slid off its end (SNAP-035).
    TargetGone
};

/// Tunables of one attraction, already scaled to physical pixels.
struct AttractionConfig {
    /// Pull-in distance in pixels (SNAP-010).
    int pull_in{16};
    /// Breakaway distance in pixels (SNAP-011). Values below pull_in are raised to pull_in (SNAP-043).
    int breakaway{24};
    /// Highest speed in pixels per second at which an edge may start being held (SNAP-012).
    double max_speed{600.0};
    /// Work-area sides are targets (SNAP-030).
    bool screen_edges{true};
    /// Other windows are targets (SNAP-032).
    bool windows{true};
};

/// Which sides of the work area are targets for the same side of the dragged window (SNAP-031).
struct SideTargets {
    /// Left side.
    bool left{true};
    /// Top side.
    bool top{true};
    /// Right side.
    bool right{true};
    /// Bottom side.
    bool bottom{true};
    /// True when the side of the given edge is a target.
    [[nodiscard]] constexpr bool allows(Edge e) const noexcept {
        switch (e) {
            case Edge::Left: return left;
            case Edge::Top: return top;
            case Edge::Right: return right;
            default: return bottom;
        }
    }
};

/// Decides which sides of `work` are targets: a side is not a target when another work area in `all` touches it
/// (within 1 px) with an overlapping span (SNAP-031). `work` itself, found in `all`, is skipped.
[[nodiscard]] SideTargets screen_side_targets(const Rect& work, std::span<const Rect> all);

/// One engage or release, for the log (SNAP-060).
struct AttractionEvent {
    /// True when an edge started being held, false when it was released.
    bool engaged{};
    /// The window edge concerned.
    Edge edge{};
    /// The target line.
    int line{};
    /// Kind of the target line.
    TargetKind kind{};
    /// Speed in pixels per second at the time of the event.
    double speed{};
    /// Reason of a release; meaningless for an engage.
    ReleaseReason reason{};
};

/// The attraction of one move drag. Feed it every free position with update; it returns the held position.
/// The x axis (left and right edges) and the y axis (top and bottom edges) are independent, so a corner holds two
/// lines. Single-threaded and pure: the caller supplies the clock.
class Attraction {
public:
    /// Creates the attraction. `others` are the frames of the other eligible windows on the same monitor, `work`
    /// is the work area of that monitor and `sides` tells which of its sides are targets.
    Attraction(const AttractionConfig& config, std::vector<Rect> others, const Rect& work,
               const SideTargets& sides);

    /// Takes the free position at time `now_ms` and returns the held position: the free position with each held
    /// edge moved onto its line. The size never changes.
    [[nodiscard]] Rect update(const Rect& free, std::uint64_t now_ms);

    /// True when at least one edge is held after the last update.
    [[nodiscard]] bool held() const noexcept { return x_.held.has_value() || y_.held.has_value(); }
    /// The position returned by the last update (an empty Rect before the first one).
    [[nodiscard]] const Rect& current() const noexcept { return current_; }
    /// Number of times an edge started being held.
    [[nodiscard]] std::uint64_t snaps() const noexcept { return snaps_; }
    /// Speed in pixels per second measured at the last update (SNAP-013).
    [[nodiscard]] double speed() const noexcept { return speed_; }
    /// Returns the engage and release events since the last call and clears them.
    [[nodiscard]] std::vector<AttractionEvent> take_events();

private:
    /// A held edge: which edge, the line, what it is held on and, for window targets, which window.
    struct Held {
        /// Held window edge.
        Edge edge{};
        /// Target line.
        int line{};
        /// Target kind.
        TargetKind kind{};
        /// Index into others_, or -1 for a screen side.
        int window{-1};
    };
    /// A possible target found for a free edge.
    struct Candidate {
        /// Window edge that would be held.
        Edge edge{};
        /// Target line.
        int line{};
        /// Target kind.
        TargetKind kind{};
        /// Index into others_, or -1 for a screen side.
        int window{-1};
        /// Distance from the free edge to the line.
        int dist{};
    };
    /// State of one axis.
    struct Axis {
        /// The held edge, if any.
        std::optional<Held> held;
    };
    /// One free position with its time.
    struct Sample {
        /// Time in milliseconds.
        std::uint64_t ms{};
        /// Left of the free position.
        int l{};
        /// Top of the free position.
        int t{};
    };

    /// Records the sample and refreshes speed_ (SNAP-013).
    void measure(const Rect& free, std::uint64_t now_ms);
    /// Resolves one axis against `free` and returns the shift to add to it. `ref` is the position used for the
    /// other-axis conditions of window targets.
    int resolve(Axis& axis, bool vertical, const Rect& ref, const Rect& free);
    /// Finds the best target for a free edge of the axis, or nullopt.
    [[nodiscard]] std::optional<Candidate> best_candidate(bool vertical, const Rect& ref,
                                                          const Rect& free) const;
    /// True when a window target still satisfies its condition (SNAP-033, SNAP-034).
    [[nodiscard]] bool window_target_valid(bool vertical, TargetKind kind, const Rect& ref,
                                           const Rect& other) const;
    /// Releases the held edge of the axis and blocks its line (SNAP-042).
    void release(Axis& axis, ReleaseReason reason);
    /// True when `line` is blocked for `edge`.
    [[nodiscard]] bool blocked(Edge edge, int line) const;

    AttractionConfig cfg_;
    std::vector<Rect> others_;
    Rect work_;
    SideTargets sides_;
    Axis x_;
    Axis y_;
    std::array<std::vector<int>, 4> blocked_;  // released lines per Edge (SNAP-042)
    std::deque<Sample> samples_;
    double speed_{0.0};
    Rect current_{};
    std::uint64_t snaps_{0};
    std::vector<AttractionEvent> events_;
};

}  // namespace mb
