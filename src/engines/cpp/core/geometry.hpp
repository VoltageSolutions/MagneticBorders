// Pure geometry primitives shared by every layer: Rect, Edge, WinInfo, Config, Move.
#pragma once
#include <cstdint>

namespace mb {

/// Opaque window identifier. The win32 layer stores an HWND in it; core never interprets it.
using WindowId = std::uint64_t;

/// Axis-aligned rectangle in screen pixels with half-open extents (r and b are exclusive).
/// Used for visual frames, work areas, and planned frames.
struct Rect {
    /// Left, top, right, and bottom coordinates.
    int l{}, t{}, r{}, b{};
    /// Width in pixels (r - l).
    [[nodiscard]] constexpr int width() const noexcept { return r - l; }
    /// Height in pixels (b - t).
    [[nodiscard]] constexpr int height() const noexcept { return b - t; }
    /// Member-wise equality.
    bool operator==(const Rect&) const = default;
};

/// A screen point in pixels, for example the mouse cursor.
struct Point {
    /// Horizontal coordinate.
    int x{};
    /// Vertical coordinate.
    int y{};
    /// Member-wise equality.
    bool operator==(const Point&) const = default;
};

/// One side of a Rect. The grabbed side of a resize is the dragged edge of a seam.
enum class Edge { Left, Top, Right, Bottom };

/// Returns the facing edge (Left <-> Right, Top <-> Bottom).
[[nodiscard]] constexpr Edge opposite(Edge e) noexcept {
    switch (e) {
        case Edge::Left: return Edge::Right;
        case Edge::Right: return Edge::Left;
        case Edge::Top: return Edge::Bottom;
        default: return Edge::Top;
    }
}

/// True for Left and Right, whose edges lie on a vertical line (an x coordinate).
[[nodiscard]] constexpr bool is_vertical_line(Edge e) noexcept {
    return e == Edge::Left || e == Edge::Right;
}

/// Returns the coordinate of edge e of rect f (x for Left/Right, y for Top/Bottom).
[[nodiscard]] constexpr int edge_coord(const Rect& f, Edge e) noexcept {
    switch (e) {
        case Edge::Left: return f.l;
        case Edge::Right: return f.r;
        case Edge::Top: return f.t;
        default: return f.b;
    }
}

/// Returns a copy of f with edge e moved to coordinate v.
[[nodiscard]] constexpr Rect with_edge(Rect f, Edge e, int v) noexcept {
    switch (e) {
        case Edge::Left: f.l = v; break;
        case Edge::Right: f.r = v; break;
        case Edge::Top: f.t = v; break;
        default: f.b = v; break;
    }
    return f;
}

/// A window as the domain sees it: id, visual frame, and minimum size.
/// Built by the inventory adapter; the controller may raise the minimums with learned values.
struct WinInfo {
    /// Window identifier.
    WindowId id{};
    /// Visual frame in screen pixels.
    Rect frame{};
    /// Minimum width in pixels.
    int min_w{1};
    /// Minimum height in pixels.
    int min_h{1};
};

/// Tunables for seam building.
struct Config {
    /// Maximum distance in pixels at which edges still count as touching.
    int glue_tolerance{12};
};

/// A requested new visual frame for one window. Output of propagate, input of WindowMover::apply.
struct Move {
    /// Window to move.
    WindowId id{};
    /// Target visual frame.
    Rect frame{};
};

}  // namespace mb
