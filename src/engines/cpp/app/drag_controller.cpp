// DragController implementation: classification, throttled apply, minimum-size learning, settle, and verify passes.
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

const char* kind_name(TargetKind k) {
    switch (k) {
        case TargetKind::Screen: return "screen";
        case TargetKind::EdgeToEdge: return "edge-to-edge";
        default: return "aligned";
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
    // SNAP-071: read the reference first. The snapshot below takes milliseconds, and Windows keeps the cursor's
    // offset in the window from this moment for the whole drag.
    pointer_ref_.reset();
    if (hook_ != nullptr) {
        const auto frame = inventory_.frame_of(id);
        const auto cursor = hook_->cursor();
        if (frame && cursor) pointer_ref_ = PointerRef{*frame, *cursor};
    }
    timer_.cancel_all();
    seams_.clear();
    requested_.clear();
    verify_pending_ = false;
    pending_ = false;
    last_apply_ms_.reset();
    dragged_ = id;
    drag_start_ms_ = clock_.now_ms();
    stop_pointer();
    attraction_.reset();
    last_held_.reset();
    drag_snaps_ = 0;
    drag_glue_ = glue_active_;
    drag_snap_ = snap_active_;
    if (!drag_glue_ && !drag_snap_) {
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
    if (!drag_glue_) return false;  // a resize needs magnetic coupling
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
    if (id != dragged_ ||
        (state_ != State::Classifying && state_ != State::Resizing && state_ != State::Moving))
        return;
    const auto frame = inventory_.frame_of(id);
    if (!frame) return;
    if (state_ == State::Moving) {
        on_move(*frame);
        return;
    }
    last_frame_ = *frame;

    if (state_ == State::Classifying) {
        if (frame->width() == start_frame_.width() && frame->height() == start_frame_.height()) {
            if (!drag_snap_) {
                state_ = State::Ignoring;
                return;
            }
            begin_move();
            state_ = State::Moving;
            on_move(*frame);
            return;
        }
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

void DragController::begin_move() {
    ++move_drags_;
    const double scale = snapshot_.dpi_scale;
    const SnapOptions& o = opts_.snap;
    AttractionConfig cfg;
    cfg.pull_in = static_cast<int>(std::lround(o.distance_px * scale));
    cfg.breakaway = static_cast<int>(std::lround(o.breakaway_px * scale));
    cfg.max_speed = o.max_speed * scale;
    cfg.screen_edges = o.screen_edges;
    cfg.windows = o.windows;
    std::vector<TargetWindow> others;
    for (const auto& w : snapshot_.windows) {
        if (w.id == dragged_) continue;
        // SNAP-038: only the parts of w that the windows above it leave visible attract. The dragged window moves,
        // so it never counts as covering anything.
        std::vector<Rect> above;
        for (const auto& [id, frame] : snapshot_.stack) {
            if (id == w.id) break;
            if (id != dragged_) above.push_back(frame);
        }
        others.push_back(make_target(w.frame, above));
    }
    const SideTargets sides = screen_side_targets(snapshot_.work_area, snapshot_.work_areas);
    attraction_.emplace(cfg, std::move(others), snapshot_.work_area, sides);
    start_pointer();
    last_free_ = start_frame_;
    say("move start id=" + std::to_string(dragged_) + (pointer_ ? " pointer" : ""));
}

void DragController::start_pointer() {
    if (hook_ == nullptr || !attraction_) return;
    PointerRef ref{};
    if (pointer_ref_) {
        if (!hook_->start(*this)) return;  // SNAP-076: fall back to moving the window back
        ref = *pointer_ref_;
    } else {
        const auto frame = inventory_.frame_of(dragged_);
        if (!frame || !hook_->start(*this)) return;
        const auto cursor = hook_->cursor();
        if (!cursor) {
            hook_->stop();
            return;
        }
        ref = PointerRef{*frame, *cursor};
    }
    pointer_.emplace(std::move(*attraction_), ref.frame, ref.cursor);
    attraction_.reset();
}

void DragController::stop_pointer() {
    if (!pointer_) return;
    if (hook_ != nullptr) hook_->stop();
    pointer_.reset();
}

Attraction* DragController::active_attraction() {
    if (pointer_) return &pointer_->attraction();
    return attraction_ ? &*attraction_ : nullptr;
}

std::optional<Point> DragController::on_pointer_move(const Point& proposed) {
    if (!pointer_ || state_ != State::Moving) return std::nullopt;
    const Point out = pointer_->move(proposed, clock_.now_ms());
    log_snap_events();
    if (out == proposed) return std::nullopt;
    return out;
}

void DragController::on_pointer_release() {
    if (pointer_ && hook_ != nullptr) hook_->stop();
}

void DragController::log_snap_events() {
    Attraction* attraction = active_attraction();
    if (attraction == nullptr) return;
    for (const AttractionEvent& ev : attraction->take_events()) {
        if (ev.engaged) {
            ++snaps_;
            ++drag_snaps_;
            say(std::string("snap hold ") + edge_name(ev.edge) + "=" + std::to_string(ev.line) +
                " target=" + kind_name(ev.kind) +
                " speed=" + std::to_string(static_cast<int>(std::lround(ev.speed))));
        } else {
            say(std::string("snap release ") + edge_name(ev.edge) + "=" + std::to_string(ev.line) +
                (ev.reason == ReleaseReason::Breakaway ? " reason=breakaway"
                                                       : " reason=target-gone"));
        }
    }
}

void DragController::on_move(const Rect& frame) {
    if (!attraction_) return;
    if (last_held_ && frame == *last_held_ && frame != last_free_)
        return;  // SNAP-024: our own move
    last_free_ = frame;
    const Rect held = attraction_->update(frame, clock_.now_ms());
    log_snap_events();
    if (held == frame) {
        last_held_.reset();
        return;
    }
    const std::vector<Move> moves{{dragged_, held}};
    const auto applied = mover_.apply(moves);  // SNAP-025: never throttled
    last_held_ = applied.empty() ? held : applied.front().frame;
}

std::optional<Rect> DragController::held_position() {
    const Attraction* a = active_attraction();
    if (a == nullptr || !a->held()) return std::nullopt;
    return a->current();
}

void DragController::finish_move(WindowId id) {
    const auto held = opts_.verbose ? held_position() : std::nullopt;
    if (pointer_) {
        // SNAP-075: Windows already put the window where the cursor is, so nothing is applied.
        stop_pointer();
        say("move end id=" + std::to_string(id) + " snaps=" + std::to_string(drag_snaps_) +
            " pointer");
        if (opts_.verbose) inventory_.report_move_result(id, held);
        return;
    }
    const auto frame = inventory_.frame_of(id);
    if (!attraction_) return;
    bool applied = false;
    if (frame && attraction_->held() && *frame == last_free_ &&
        frame->width() == start_frame_.width() && frame->height() == start_frame_.height() &&
        attraction_->current() != *frame) {  // SNAP-050, SNAP-051
        const std::vector<Move> moves{{id, attraction_->current()}};
        mover_.apply(moves);
        applied = true;
    }
    say("move end id=" + std::to_string(id) + " snaps=" + std::to_string(drag_snaps_) +
        (applied ? " applied" : ""));
    if (opts_.verbose) inventory_.report_move_result(id, held);
    attraction_.reset();  // SNAP-052: nothing more is applied
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
    if (prev == State::Moving) {
        finish_move(id);
        return;
    }
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
