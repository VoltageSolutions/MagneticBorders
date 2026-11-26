// Implementation of build_seam (seam discovery with stacking closure) and propagate (clamped member moves).
#include "seam.hpp"

#include <algorithm>
#include <cstdlib>
#include <limits>

namespace mb {
namespace {

struct Span {
    int a, b;
};

Span perpendicular_span(const Rect& r, bool vertical_line) {
    return vertical_line ? Span{r.t, r.b} : Span{r.l, r.r};
}

int gap(Span p, Span q) { return std::max({p.a - q.b, q.a - p.b, 0}); }

}  // namespace

std::optional<Seam> build_seam(std::span<const WinInfo> windows, WindowId dragged_id, Edge edge,
                               const Rect& work_area, const Config& cfg) {
    const auto it = std::ranges::find(windows, dragged_id, &WinInfo::id);
    if (it == windows.end()) return std::nullopt;

    const int tol = cfg.glue_tolerance;
    const bool vertical = is_vertical_line(edge);
    const int x = edge_coord(it->frame, edge);
    if (std::abs(x - edge_coord(work_area, edge)) <= tol) return std::nullopt;

    std::vector<SeamMember> candidates;
    for (const auto& w : windows) {
        if (w.id == dragged_id) continue;
        const int opp = edge_coord(w.frame, opposite(edge));
        const int same = edge_coord(w.frame, edge);
        if (std::abs(opp - x) <= tol) {
            candidates.push_back({w.id, Side::Opposite, opp - x, w.frame, w.min_w, w.min_h});
        } else if (std::abs(same - x) <= tol) {
            candidates.push_back({w.id, Side::Same, same - x, w.frame, w.min_w, w.min_h});
        }
    }

    Seam seam{dragged_id, edge, x, {}};
    Span span = perpendicular_span(it->frame, vertical);
    std::vector<bool> taken(candidates.size(), false);
    for (bool changed = true; changed;) {
        changed = false;
        for (std::size_t i = 0; i < candidates.size(); ++i) {
            if (taken[i]) continue;
            const Span cs = perpendicular_span(candidates[i].frame, vertical);
            if (gap(cs, span) > tol) continue;
            taken[i] = true;
            changed = true;
            span = {std::min(span.a, cs.a), std::max(span.b, cs.b)};
            seam.members.push_back(candidates[i]);
        }
    }
    if (seam.members.empty()) return std::nullopt;
    return seam;
}

Plan propagate(const Seam& seam, int new_pos) {
    const bool vertical = is_vertical_line(seam.edge);
    int lo = std::numeric_limits<int>::min();
    int hi = std::numeric_limits<int>::max();
    for (const auto& m : seam.members) {
        const Edge me = moving_edge(seam, m);
        const int min = vertical ? m.min_w : m.min_h;
        switch (me) {
            case Edge::Left: hi = std::min(hi, m.frame.r - min - m.d); break;
            case Edge::Top: hi = std::min(hi, m.frame.b - min - m.d); break;
            case Edge::Right: lo = std::max(lo, m.frame.l + min - m.d); break;
            case Edge::Bottom: lo = std::max(lo, m.frame.t + min - m.d); break;
        }
    }
    const int pos = lo > hi ? seam.x : std::clamp(new_pos, lo, hi);

    Plan plan{{}, pos};
    plan.moves.reserve(seam.members.size());
    for (const auto& m : seam.members) {
        plan.moves.push_back({m.id, with_edge(m.frame, moving_edge(seam, m), pos + m.d)});
    }
    return plan;
}

}  // namespace mb
