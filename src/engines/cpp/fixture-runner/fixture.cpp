// Fixture parser, runner and checker (pure domain code, no Windows headers).
#include "fixture.hpp"

#include <algorithm>
#include <sstream>
#include <stdexcept>

namespace mb::fixture {
namespace {

[[noreturn]] void fail(const std::string& m) { throw std::runtime_error(m); }

Rect parse_pos_size(const std::string& pos, const std::string& size) {
    int x = 0, y = 0, w = 0, h = 0;
    char c1 = 0, c2 = 0;
    std::istringstream p(pos), s(size);
    if (!(p >> x >> c1 >> y) || c1 != ',') fail("bad position '" + pos + "'");
    if (!(s >> w >> c2 >> h) || (c2 != 'x' && c2 != 'X')) fail("bad size '" + size + "'");
    return {x, y, x + w, y + h};
}

Edge parse_edge(const std::string& s) {
    if (s == "left") return Edge::Left;
    if (s == "right") return Edge::Right;
    if (s == "top") return Edge::Top;
    if (s == "bottom") return Edge::Bottom;
    fail("bad edge '" + s + "'");
}

const NamedWin* find_win(const Fixture& f, const std::string& name) {
    const auto it = std::ranges::find(f.windows, name, &NamedWin::name);
    return it == f.windows.end() ? nullptr : &*it;
}

std::string rect_str(const Rect& r) {
    return std::to_string(r.l) + "," + std::to_string(r.t) + " " + std::to_string(r.width()) + "x" +
           std::to_string(r.height());
}

}  // namespace

Fixture parse(std::string_view text) {
    Fixture f;
    std::istringstream in{std::string(text)};
    std::string line;
    while (std::getline(in, line)) {
        if (const auto hash = line.find('#'); hash != std::string::npos) line.erase(hash);
        std::istringstream ls(line);
        std::vector<std::string> tok;
        for (std::string t; ls >> t;) tok.push_back(t);
        if (tok.empty()) continue;
        const auto& k = tok[0];
        if (k == "tolerance" && tok.size() == 2) {
            f.tolerance = std::stoi(tok[1]);
        } else if (k == "workarea" && tok.size() == 3) {
            f.work_area = parse_pos_size(tok[1], tok[2]);
        } else if (k == "win" && (tok.size() == 4 || tok.size() == 6)) {
            NamedWin w{tok[1], {}};
            w.info.id = f.windows.size() + 1;
            w.info.frame = parse_pos_size(tok[2], tok[3]);
            if (tok.size() == 6) {
                if (tok[4] != "min") fail("expected 'min' in: " + line);
                const Rect m = parse_pos_size("0,0", tok[5]);
                w.info.min_w = m.width();
                w.info.min_h = m.height();
            }
            f.windows.push_back(std::move(w));
        } else if (k == "drag" && tok.size() == 4) {
            f.drag_name = tok[1];
            f.drag_edge = parse_edge(tok[2]);
            f.drag_delta = std::stoi(tok[3]);
        } else if (k == "expect" && tok.size() == 3 && tok[1] == "clamp") {
            f.expect_clamp = std::stoi(tok[2]);
        } else if (k == "expect" && tok.size() == 4) {
            f.expects.emplace_back(tok[1], parse_pos_size(tok[2], tok[3]));
        } else {
            fail("cannot parse line: " + line);
        }
    }
    return f;
}

Outcome run(const Fixture& f) {
    const auto* dragged = find_win(f, f.drag_name);
    if (!dragged) fail("unknown drag window '" + f.drag_name + "'");

    std::vector<WinInfo> infos;
    infos.reserve(f.windows.size());
    for (const auto& w : f.windows) infos.push_back(w.info);

    Outcome out;
    const int x = edge_coord(dragged->info.frame, f.drag_edge);
    const int target = x + f.drag_delta;
    int final_pos = target;
    std::vector<Move> moves;
    if (auto seam = build_seam(infos, dragged->info.id, f.drag_edge, f.work_area, {f.tolerance})) {
        Plan plan = propagate(*seam, target);
        final_pos = plan.clamped_pos;
        moves = std::move(plan.moves);
        out.clamp = final_pos;
    }
    moves.push_back({dragged->info.id, with_edge(dragged->info.frame, f.drag_edge, final_pos)});

    for (const auto& w : f.windows) {
        Rect r = w.info.frame;
        if (const auto it = std::ranges::find(moves, w.info.id, &Move::id); it != moves.end())
            r = it->frame;
        out.finals.emplace_back(w.name, r);
    }
    std::ranges::sort(out.finals, {}, &std::pair<std::string, Rect>::first);
    return out;
}

std::string check(const Fixture& f, const Outcome& o) {
    for (const auto& [name, want] : f.expects) {
        const auto it = std::ranges::find(o.finals, name, &std::pair<std::string, Rect>::first);
        if (it == o.finals.end()) return "expect names unknown window " + name;
        if (!(it->second == want))
            return name + ": expected " + rect_str(want) + " got " + rect_str(it->second);
    }
    for (const auto& w : f.windows) {
        const bool named =
            std::ranges::any_of(f.expects, [&](const auto& e) { return e.first == w.name; });
        if (named) continue;
        const auto it = std::ranges::find(o.finals, w.name, &std::pair<std::string, Rect>::first);
        if (!(it->second == w.info.frame))
            return w.name + ": expected unchanged " + rect_str(w.info.frame) + " got " +
                   rect_str(it->second);
    }
    if (f.expect_clamp && o.clamp != f.expect_clamp)
        return "clamp: expected " + std::to_string(*f.expect_clamp) + " got " +
               (o.clamp ? std::to_string(*o.clamp) : std::string("none"));
    return {};
}

std::string format(const Outcome& o) {
    std::string s;
    for (const auto& [name, r] : o.finals) s += name + " " + rect_str(r) + "\n";
    return s;
}

}  // namespace mb::fixture
