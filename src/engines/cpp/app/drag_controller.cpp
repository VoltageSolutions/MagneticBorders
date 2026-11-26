// DragController implementation: classification, throttled apply, minimum-size learning, settle and verify passes.
#include "drag_controller.hpp"

#include <algorithm>
#include <cmath>

namespace mb {
namespace {
constexpr std::uint64_t kThrottleMs = 8;
constexpr unsigned kSettleMs = 150;
constexpr unsigned kVerifyMs = 60;

const char* edge_name(Edge e) {
    switch (e) {
        case Edge::Left: return "left";
        case Edge::Right: return "right";
        case Edge::Top: return "top";
        default: return "bottom";
    }
}

std::string size_str(const Rect& r) {
    return std::to_string(r.width()) + "x" + std::to_string(r.height());
}
}  // namespace

DragController::DragController(WindowInventory& inventory, WindowMover& mover, Clock& clock,
                               Timer& timer, Log& log, ControllerOptions options)
    : inventory_(inventory), mover_(mover), clock_(clock), timer_(timer), log_(log),
      opts_(std::move(options)) {}

void DragController::say(const std::string& msg) {
    log_.info("[" + opts_.build_name + " +" + std::to_string(since_start()) + "ms] " + msg);
}

void DragController::on_move_size_start(WindowId id) {
    timer_.cancel_all();
    seams_.clear();
    requested_.clear();
    verify_pending_ = false;
    pending_ = false;
    last_apply_ms_.reset();
    dragged_ = id;
    drag_start_ms_ = clock_.now_ms();
    if (!glue_active_) {
        state_ = State::Ignoring;
        return;
    }
    snapshot_ = inventory_.snapshot(id);
    for (auto& w : snapshot_.windows) {
        if (const auto it = learned_min_.find(w.id); it != learned_min_.end()) {
            w.min_w = std::max(w.min_w, it->second.first);
            w.min_h = std::max(w.min_h, it->second.second);
        }
    }
    const auto it = std::ranges::find(snapshot_.windows, id, &WinInfo::id);
    if (it == snapshot_.windows.end()) {
        state_ = State::Ignoring;
        return;
    }
    start_frame_ = last_frame_ = it->frame;
    state_ = State::Classifying;
}

bool DragController::classify(const Rect& frame) {
    if (frame.width() == start_frame_.width() && frame.height() == start_frame_.height())
        return false;  // a move: P1 does nothing
    ++drags_;

    std::vector<Edge> grabbed;
    if (frame.l != start_frame_.l)
        grabbed.push_back(Edge::Left);
    else if (frame.r != start_frame_.r)
        grabbed.push_back(Edge::Right);
    if (frame.t != start_frame_.t)
        grabbed.push_back(Edge::Top);
    else if (frame.b != start_frame_.b)
        grabbed.push_back(Edge::Bottom);

    const Config cfg{static_cast<int>(std::lround(opts_.glue_tolerance_px * snapshot_.dpi_scale))};
    std::string line = "resize start id=" + std::to_string(dragged_);
    for (const Edge e : grabbed) {
        auto seam = build_seam(snapshot_.windows, dragged_, e, snapshot_.work_area, cfg);
        if (!seam) return false;
        line += std::string(" ") + edge_name(e) + "->[";
        for (const auto& m : seam->members) line += std::to_string(m.id) + " ";
        line += "]";
        seams_.push_back(std::move(*seam));
    }
    if (seams_.empty()) return false;
    ++glued_drags_;
    say(line);
    return true;
}

DragController::Planned DragController::plan_for(const Rect& frame) const {
    Planned out;
    for (const auto& seam : seams_) {
        const Plan plan = propagate(seam, edge_coord(frame, seam.edge));
        out.clamped.push_back(plan.clamped_pos);
        const bool vertical = is_vertical_line(seam.edge);
        for (const auto& mv : plan.moves) {
            auto it = std::ranges::find(out.moves, mv.id, &Move::id);
            if (it == out.moves.end()) {
                out.moves.push_back(mv);
            } else if (vertical) {
                it->frame.l = mv.frame.l;
                it->frame.r = mv.frame.r;
            } else {
                it->frame.t = mv.frame.t;
                it->frame.b = mv.frame.b;
            }
        }
    }
    return out;
}

void DragController::learn_minimums(const std::vector<Move>& requested,
                                    const std::vector<Move>& applied) {
    for (const auto& a : applied) {
        const auto r = std::ranges::find(requested, a.id, &Move::id);
        if (r == requested.end()) continue;
        const bool wider = a.frame.width() > r->frame.width();
        const bool taller = a.frame.height() > r->frame.height();
        if (!wider && !taller) continue;
        auto& learned = learned_min_.try_emplace(a.id, 1, 1).first->second;
        if (wider) learned.first = std::max(learned.first, a.frame.width());
        if (taller) learned.second = std::max(learned.second, a.frame.height());
        for (auto& seam : seams_) {
            for (auto& m : seam.members) {
                if (m.id != a.id) continue;
                m.min_w = std::max(m.min_w, learned.first);
                m.min_h = std::max(m.min_h, learned.second);
            }
        }
    }
}

void DragController::apply(const std::vector<Move>& moves) {
    if (moves.empty()) return;
    const auto applied = mover_.apply(moves);
    last_apply_ms_ = clock_.now_ms();
    pending_ = false;
    for (const auto& m : moves)
        if (m.id != dragged_) requested_[m.id] = m.frame;
    learn_minimums(moves, applied);
}

void DragController::apply_frame(const Rect& frame) {
    const Planned planned = plan_for(frame);
    if (opts_.verbose && !seams_.empty()) {
        say("apply pos=" + std::to_string(edge_coord(frame, seams_[0].edge)) +
            " clamp=" + std::to_string(planned.clamped[0]) +
            " members=" + std::to_string(planned.moves.size()));
    }
    apply(planned.moves);
}

void DragController::on_location_change(WindowId id) {
    if (id != dragged_ || (state_ != State::Classifying && state_ != State::Resizing)) return;
    const auto frame = inventory_.frame_of(id);
    if (!frame) return;
    last_frame_ = *frame;

    if (state_ == State::Classifying) {
        if (!classify(*frame)) {
            state_ = State::Ignoring;
            seams_.clear();
            return;
        }
        state_ = State::Resizing;
    }

    timer_.arm(TimerKind::Settle, kSettleMs);
    if (last_apply_ms_) {
        const auto elapsed = clock_.now_ms() - *last_apply_ms_;
        if (elapsed < kThrottleMs) {
            pending_frame_ = *frame;
            pending_ = true;
            timer_.arm(TimerKind::Flush,
                       static_cast<unsigned>(kThrottleMs - elapsed));  // trailing flush
            return;
        }
    }
    apply_frame(*frame);
}

std::vector<Move> DragController::final_moves(const Rect& frame, bool& snapped) {
    Planned planned = plan_for(frame);
    Rect fixed = frame;
    snapped = false;
    for (std::size_t i = 0; i < seams_.size(); ++i) {
        if (planned.clamped[i] != edge_coord(frame, seams_[i].edge)) {
            fixed = with_edge(fixed, seams_[i].edge, planned.clamped[i]);
            snapped = true;
        }
    }
    final_frame_ = fixed;
    if (snapped) planned.moves.push_back({dragged_, fixed});
    return std::move(planned.moves);
}

void DragController::on_move_size_end(WindowId id) {
    if (id != dragged_) return;
    timer_.cancel_all();
    const State prev = std::exchange(state_, State::Idle);
    if (prev != State::Resizing) {
        seams_.clear();
        return;
    }
    const Rect frame = inventory_.frame_of(id).value_or(last_frame_);
    bool snapped = false;
    apply(final_moves(frame, snapped));
    say(std::string("resize end id=") + std::to_string(id) + (snapped ? " snapped-back" : " ok"));
    verify_pending_ = true;  // seams are kept for Verify
    timer_.arm(TimerKind::Verify, kVerifyMs);
}

std::string DragController::find_mismatches() {
    std::string details;
    std::vector<Move> req, got;
    for (const auto& [id, want] : requested_) {
        const auto actual = inventory_.frame_of(id);
        if (!actual) continue;
        if (actual->width() == want.width() && actual->height() == want.height()) continue;
        details +=
            " id" + std::to_string(id) + " req=" + size_str(want) + " got=" + size_str(*actual);
        req.push_back({id, want});
        got.push_back({id, *actual});
    }
    learn_minimums(req, got);
    return details;
}

void DragController::on_timer(TimerKind kind) {
    switch (kind) {
        case TimerKind::Flush: on_flush(); break;
        case TimerKind::Settle: on_settle(); break;
        case TimerKind::Verify: on_verify(); break;
    }
}

void DragController::on_flush() {
    if (state_ != State::Resizing || !pending_) return;
    apply_frame(pending_frame_);
}

void DragController::on_settle() {
    if (state_ != State::Resizing || seams_.empty()) return;
    const std::string details = find_mismatches();
    const Rect frame = inventory_.frame_of(dragged_).value_or(last_frame_);
    last_frame_ = frame;
    const Planned planned = plan_for(frame);
    if (!details.empty()) apply(planned.moves);
    const int pos = edge_coord(frame, seams_[0].edge);
    say("settled +" + std::to_string(since_start()) + " " + edge_name(seams_[0].edge) + "=" +
        std::to_string(pos) + " clamp=" + std::to_string(planned.clamped[0]) +
        " gap=" + std::to_string(pos - planned.clamped[0]) + details);
}

void DragController::on_verify() {
    if (!verify_pending_) return;
    verify_pending_ = false;
    std::string details = find_mismatches();
    const auto actual = inventory_.frame_of(dragged_);
    for (const auto& seam : seams_) {
        if (!actual) break;
        if (edge_coord(*actual, seam.edge) != edge_coord(final_frame_, seam.edge)) {
            details += std::string(" dragged ") + edge_name(seam.edge) + "=" +
                       std::to_string(edge_coord(*actual, seam.edge)) +
                       " want=" + std::to_string(edge_coord(final_frame_, seam.edge));
        }
    }
    if (details.empty()) {
        say("end +" + std::to_string(since_start()) + " ok");
    } else {
        bool snapped = false;
        auto moves = final_moves(final_frame_, snapped);
        if (!std::ranges::any_of(moves, [&](const Move& m) { return m.id == dragged_; }))
            moves.push_back({dragged_, final_frame_});
        apply(moves);  // ONCE
        say("end +" + std::to_string(since_start()) + " fixed" + details);
    }
    seams_.clear();
}

}  // namespace mb
