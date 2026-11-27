// parse_settings implementation (BOM tolerant, clamped, type-checked).
#include "settings.hpp"

#include <algorithm>
#include <cmath>

#include <nlohmann/json.hpp>

namespace mb {
namespace {
using nlohmann::json;

bool get_bool(const json& o, const char* key, bool def) {
    const auto it = o.find(key);
    return it != o.end() && it->is_boolean() ? it->get<bool>() : def;
}

int get_int(const json& o, const char* key, int def, int lo, int hi) {
    const auto it = o.find(key);
    if (it == o.end() || !it->is_number()) return def;
    const double v = std::round(it->get<double>());  // CFG-017: halves away from zero
    return static_cast<int>(std::clamp(v, static_cast<double>(lo), static_cast<double>(hi)));
}

const json* get_obj(const json& o, const char* key) {
    const auto it = o.find(key);
    return it != o.end() && it->is_object() ? &*it : nullptr;
}
}  // namespace

std::optional<Settings> parse_settings(std::string_view text) {
    // Tolerate a UTF-8 BOM.
    if (text.starts_with("\xEF\xBB\xBF")) text.remove_prefix(3);
    const json doc = json::parse(text.begin(), text.end(), nullptr, false);
    if (doc.is_discarded() || !doc.is_object()) return std::nullopt;

    Settings s;
    s.enabled = get_bool(doc, "enabled", s.enabled);
    s.start_at_login = get_bool(doc, "start_at_login", s.start_at_login);
    if (const json* g = get_obj(doc, "glue")) {
        s.glue_enabled = get_bool(*g, "enabled", s.glue_enabled);
        s.glue_tolerance_px = get_int(*g, "tolerance_px", s.glue_tolerance_px, 0, 64);
    }
    if (const json* sn = get_obj(doc, "snap")) {
        s.snap_enabled = get_bool(*sn, "enabled", s.snap_enabled);
        s.snap_distance_px = get_int(*sn, "distance_px", s.snap_distance_px, 0, 64);
        s.snap_breakaway_px = get_int(*sn, "breakaway_px", s.snap_breakaway_px, 0, 128);
        s.snap_max_speed = get_int(*sn, "max_speed", s.snap_max_speed, 100, 5000);
        s.snap_screen_edges = get_bool(*sn, "screen_edges", s.snap_screen_edges);
        s.snap_windows = get_bool(*sn, "windows", s.snap_windows);
    }
    if (const auto it = doc.find("excluded_apps"); it != doc.end() && it->is_array()) {
        for (const auto& e : *it)
            if (e.is_string() && !e.get<std::string>().empty())
                s.excluded_apps.push_back(e.get<std::string>());
    }
    return s;
}

std::optional<std::string> with_enabled(std::string_view text, bool enabled) {
    using nlohmann::ordered_json;
    if (text.starts_with("\xEF\xBB\xBF")) text.remove_prefix(3);
    ordered_json doc = ordered_json::object();
    if (text.find_first_not_of(" \t\r\n") != std::string_view::npos) {
        doc = ordered_json::parse(text.begin(), text.end(), nullptr, false);
        if (doc.is_discarded() || !doc.is_object()) return std::nullopt;
    }
    doc["enabled"] = enabled;
    return doc.dump(2) + "\n";
}

}  // namespace mb
