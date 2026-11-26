// Fixture model and API for `.layout` test files (parse, run, check, format).
#pragma once
#include <optional>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

#include "core/seam.hpp"

namespace mb::fixture {

/// A window in a fixture, addressed by name.
struct NamedWin {
    /// Name used by `drag` and `expect` lines.
    std::string name;
    /// Domain info (id, frame, minimum size).
    WinInfo info;
};

/// One parsed `.layout` case: layout, drag and expectations.
struct Fixture {
    /// Glue tolerance in pixels.
    int tolerance{12};
    /// Work area; defaults to a huge rectangle so no edge is a screen border.
    Rect work_area{-100000, -100000, 100000, 100000};
    /// Windows in file order; ids are 1-based indices.
    std::vector<NamedWin> windows;  // ids are 1-based indices
    /// Name of the dragged window.
    std::string drag_name;
    /// Dragged edge.
    Edge drag_edge{Edge::Right};
    /// Pixels the edge moves (signed).
    int drag_delta{0};
    /// Expected final frames by window name.
    std::vector<std::pair<std::string, Rect>> expects;
    /// Expected clamped edge position, if given.
    std::optional<int> expect_clamp;
};

/// Result of running a fixture.
struct Outcome {
    /// Final frame of every window, sorted by name.
    std::vector<std::pair<std::string, Rect>> finals;  // sorted by name
    /// Clamped position of the dragged edge; unset when no seam was built.
    std::optional<int> clamp;
};

/// Parses fixture text; throws std::runtime_error on a bad line.
Fixture parse(std::string_view text);  // throws std::runtime_error
/// Runs build_seam and propagate for the fixture and moves the dragged window to the clamped position.
Outcome run(const Fixture& f);
/// Compares the outcome with the expectations (unnamed windows must be unchanged). Returns an empty string on pass,
/// otherwise a failure message.
std::string check(const Fixture& f, const Outcome& o);  // empty == pass
/// Formats the final frames, one `name x,y WxH` line per window.
std::string format(const Outcome& o);

}  // namespace mb::fixture
