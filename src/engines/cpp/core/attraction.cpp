// Attraction implementation: target search, holding, sliding and releasing.
#include "attraction.hpp"

#include <algorithm>
#include <cmath>
#include <cstdlib>
#include <utility>

namespace mb {
namespace {
constexpr std::uint64_t kSpeedWindowMs = 80;  // SNAP-013

/// True when two half-open spans [a0, a1) and [b0, b1) overlap.
bool spans_overlap(int a0, int a1, int b0, int b1) { return a0 < b1 && b0 < a1; }

/// Returns r moved by (dx, dy).
Rect shifted(Rect r, int dx, int dy) {
    r.l += dx;
    r.r += dx;
    r.t += dy;
    r.b += dy;
    return r;
}

/// The two edges of an axis.
std::array<Edge, 2> axis_edges(bool vertical) {
    return vertical ? std::array<Edge, 2>{Edge::Left, Edge::Right}
                    : std::array<Edge, 2>{Edge::Top, Edge::Bottom};
}

/// True when candidate `a` beats `b`: nearer, or equally near and a window rather than a screen edge (SNAP-037).
bool better(int a_dist, TargetKind a_kind, int b_dist, TargetKind b_kind) {
    if (a_dist != b_dist) return a_dist < b_dist;
    return a_kind != TargetKind::Screen && b_kind == TargetKind::Screen;
}
}  // namespace

TargetWindow make_target(const Rect& frame, std::span<const Rect> above) {
    TargetWindow t{frame, {}};
    for (const Edge e : {Edge::Left, Edge::Top, Edge::Right, Edge::Bottom}) {
        const bool vertical = is_vertical_line(e);
        // The edge's own row or column of pixels, inside the frame (r and b are exclusive).
        const int at = e == Edge::Right    ? frame.r - 1
                       : e == Edge::Bottom ? frame.b - 1
                                           : edge_coord(frame, e);
        std::vector<Span> spans{vertical ? Span{frame.t, frame.b} : Span{frame.l, frame.r}};
        for (const Rect& o : above) {
            const bool crosses = vertical ? (o.l <= at && at < o.r) : (o.t <= at && at < o.b);
            if (!crosses) continue;
            const Span cut = vertical ? Span{o.t, o.b} : Span{o.l, o.r};
            std::vector<Span> next;
            for (const Span& s : spans) {
                if (cut.hi <= s.lo || s.hi <= cut.lo) {
                    next.push_back(s);
                    continue;
                }
                if (s.lo < cut.lo) next.push_back({s.lo, cut.lo});
                if (cut.hi < s.hi) next.push_back({cut.hi, s.hi});
            }
            spans = std::move(next);
        }
        t.visible.at(static_cast<std::size_t>(e)) = std::move(spans);
    }
    return t;
}

SideTargets screen_side_targets(const Rect& work, std::span<const Rect> all) {
    SideTargets s;
    for (const Rect& o : all) {
        if (o == work) continue;
        const bool x_overlap = spans_overlap(work.l, work.r, o.l, o.r);
        const bool y_overlap = spans_overlap(work.t, work.b, o.t, o.b);
        if (y_overlap && std::abs(o.r - work.l) <= 1) s.left = false;
        if (y_overlap && std::abs(o.l - work.r) <= 1) s.right = false;
        if (x_overlap && std::abs(o.b - work.t) <= 1) s.top = false;
        if (x_overlap && std::abs(o.t - work.b) <= 1) s.bottom = false;
    }
    return s;
}

Attraction::Attraction(const AttractionConfig& config, std::vector<TargetWindow> others,
                       const Rect& work, const SideTargets& sides)
    : cfg_(config), others_(std::move(others)), work_(work), sides_(sides) {
    cfg_.pull_in = std::max(cfg_.pull_in, 0);
    cfg_.breakaway = std::max(cfg_.breakaway, cfg_.pull_in);  // SNAP-043
}

namespace {
std::vector<TargetWindow> unoccluded(const std::vector<Rect>& frames) {
    std::vector<TargetWindow> out;
    out.reserve(frames.size());
    for (const Rect& f : frames) out.push_back(make_target(f, {}));
    return out;
}
}  // namespace

Attraction::Attraction(const AttractionConfig& config, const std::vector<Rect>& others,
                       const Rect& work, const SideTargets& sides)
    : Attraction(config, unoccluded(others), work, sides) {}

std::vector<AttractionEvent> Attraction::take_events() { return std::exchange(events_, {}); }

void Attraction::measure(const Rect& free, std::uint64_t now_ms) {
    samples_.push_back({now_ms, free.l, free.t});
    while (samples_.size() > 1 && now_ms - samples_.front().ms > kSpeedWindowMs)
        samples_.pop_front();
    const Sample& first = samples_.front();
    const Sample& last = samples_.back();
    if (samples_.size() < 2 || last.ms <= first.ms) return;  // keep the last measured value
    const auto dx = static_cast<double>(last.l - first.l);
    const auto dy = static_cast<double>(last.t - first.t);
    speed_ = std::hypot(dx, dy) * 1000.0 / static_cast<double>(last.ms - first.ms);
}

bool Attraction::blocked(Edge edge, int line) const {
    const auto& lines = blocked_[static_cast<std::size_t>(edge)];
    return std::ranges::find(lines, line) != lines.end();
}

bool Attraction::window_target_valid(Edge edge, TargetKind kind, const Rect& ref,
                                     const TargetWindow& target) const {
    const bool vertical = is_vertical_line(edge);
    const Rect& other = target.frame;
    // The other axis of a vertical line is y, and the other axis of a horizontal line is x.
    const int d_lo = vertical ? ref.t : ref.l;
    const int d_hi = vertical ? ref.b : ref.r;
    const int w_lo = vertical ? other.t : other.l;
    const int w_hi = vertical ? other.b : other.r;
    // The edge of the other window this target depends on, and the span of the dragged window along it.
    Edge needed = opposite(edge);
    int near_lo = d_lo - cfg_.pull_in;
    int near_hi = d_hi + cfg_.pull_in;
    if (kind == TargetKind::EdgeToEdge) {
        const int gap = std::max(d_lo - w_hi, w_lo - d_hi);  // negative while they overlap
        if (gap > cfg_.pull_in) return false;
    } else if (kind == TargetKind::Aligned) {
        const int below = std::abs(d_lo - w_hi);  // dragged window below (or right of) the other
        const int above = std::abs(d_hi - w_lo);
        if (std::min(below, above) > cfg_.pull_in) return false;
        // Stacked: the facing edge of the other window must be visible where the dragged window is.
        needed = below <= above ? (vertical ? Edge::Bottom : Edge::Right)
                                : (vertical ? Edge::Top : Edge::Left);
        near_lo = (vertical ? ref.l : ref.t) - cfg_.pull_in;
        near_hi = (vertical ? ref.r : ref.b) + cfg_.pull_in;
    } else {
        return true;
    }
    const auto& spans = target.visible.at(static_cast<std::size_t>(needed));
    return std::ranges::any_of(spans,
                               [&](const Span& s) { return s.lo < near_hi && near_lo < s.hi; });
}

std::optional<Attraction::Candidate> Attraction::best_candidate(bool vertical, const Rect& ref,
                                                                const Rect& free) const {
    std::optional<Candidate> best;
    const auto consider = [&](Edge edge, int line, TargetKind kind, int window) {
        const int dist = std::abs(edge_coord(free, edge) - line);
        if (dist > cfg_.pull_in || blocked(edge, line)) return;
        if (!best || better(dist, kind, best->dist, best->kind))
            best = Candidate{edge, line, kind, window, dist};
    };
    for (const Edge edge : axis_edges(vertical)) {
        if (cfg_.screen_edges && sides_.allows(edge))
            consider(edge, edge_coord(work_, edge), TargetKind::Screen, -1);
        if (!cfg_.windows) continue;
        for (std::size_t i = 0; i < others_.size(); ++i) {
            const TargetWindow& w = others_[i];
            const auto index = static_cast<int>(i);
            if (window_target_valid(edge, TargetKind::EdgeToEdge, ref, w))
                consider(edge, edge_coord(w.frame, opposite(edge)), TargetKind::EdgeToEdge, index);
            if (window_target_valid(edge, TargetKind::Aligned, ref, w))
                consider(edge, edge_coord(w.frame, edge), TargetKind::Aligned, index);
        }
    }
    return best;
}

void Attraction::release(Axis& axis, ReleaseReason reason) {
    const Held h = *axis.held;
    axis.held.reset();
    blocked_[static_cast<std::size_t>(h.edge)].push_back(h.line);
    AttractionEvent ev;
    ev.engaged = false;
    ev.edge = h.edge;
    ev.line = h.line;
    ev.kind = h.kind;
    ev.speed = speed_;
    ev.reason = reason;
    events_.push_back(ev);
}

int Attraction::resolve(Axis& axis, bool vertical, const Rect& ref, const Rect& free) {
    // A released line stays blocked until the free edge is more than the pull-in distance away (SNAP-042).
    const auto prune = [&] {
        for (const Edge edge : axis_edges(vertical)) {
            const int coord = edge_coord(free, edge);
            std::erase_if(blocked_[static_cast<std::size_t>(edge)],
                          [&](int line) { return std::abs(coord - line) > cfg_.pull_in; });
        }
    };
    prune();
    if (axis.held) {
        const Held h = *axis.held;
        const int coord = edge_coord(free, h.edge);
        if (std::abs(coord - h.line) > cfg_.breakaway) {
            release(axis, ReleaseReason::Breakaway);
        } else if (h.window >= 0 &&
                   !window_target_valid(h.edge, h.kind, ref,
                                        others_[static_cast<std::size_t>(h.window)])) {
            release(axis, ReleaseReason::TargetGone);
        } else {
            return h.line - coord;  // SNAP-022: the other axis keeps following the mouse
        }
        // The released line stays blocked (SNAP-042), and nothing else is held on this axis in the same update,
        // so the edge goes free before it can be caught again.
        return 0;
    }
    if (speed_ > cfg_.max_speed) return 0;  // SNAP-012, SNAP-040
    const auto c = best_candidate(vertical, ref, free);
    if (!c) return 0;
    axis.held = Held{c->edge, c->line, c->kind, c->window};
    ++snaps_;
    AttractionEvent ev;
    ev.engaged = true;
    ev.edge = c->edge;
    ev.line = c->line;
    ev.kind = c->kind;
    ev.speed = speed_;
    events_.push_back(ev);
    return c->line - edge_coord(free, c->edge);
}

Rect Attraction::update(const Rect& free, std::uint64_t now_ms) {
    measure(free, now_ms);
    // x targets are checked against the y position the window had (held or free), y targets against the new x
    // position. When y changed, x is checked once more so a slide off a target is seen in the same update.
    int dy = 0;
    if (y_.held) dy = y_.held->line - edge_coord(free, y_.held->edge);
    int dx = resolve(x_, true, shifted(free, 0, dy), free);
    const int new_dy = resolve(y_, false, shifted(free, dx, 0), free);
    if (new_dy != dy) dx = resolve(x_, true, shifted(free, 0, new_dy), free);
    current_ = shifted(free, dx, new_dy);
    return current_;
}

}  // namespace mb
