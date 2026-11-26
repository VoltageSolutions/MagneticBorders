// Settings value type and the pure settings.json parser.
#pragma once
#include <optional>
#include <string>
#include <string_view>
#include <vector>

namespace mb {

/// Engine settings as stored in settings.json (schema 1). Pure: no file or OS access.
struct Settings {
    /// Master switch (`enabled`).
    bool enabled{true};
    /// Glue switch (`glue.enabled`).
    bool glue_enabled{true};
    /// Glue tolerance in pixels (`glue.tolerance_px`).
    int glue_tolerance_px{12};
    /// Magnetic attraction switch (`snap.enabled`).
    bool snap_enabled{true};
    /// Pull-in distance in pixels (`snap.distance_px`, 0..64).
    int snap_distance_px{16};
    /// Breakaway distance in pixels (`snap.breakaway_px`, 0..128).
    int snap_breakaway_px{24};
    /// Snapping speed limit in pixels per second (`snap.max_speed`, 100..5000).
    int snap_max_speed{600};
    /// Work-area sides are targets (`snap.screen_edges`).
    bool snap_screen_edges{true};
    /// Other windows are targets (`snap.windows`).
    bool snap_windows{true};
    /// Executable names (UTF-8) of apps never glued (`excluded_apps`).
    std::vector<std::string> excluded_apps;  // UTF-8
    /// Run at login (`start_at_login`).
    bool start_at_login{false};

    /// Member-wise equality.
    friend bool operator==(const Settings&, const Settings&) = default;
    /// True when magnetic coupling should act: enabled && glue_enabled.
    [[nodiscard]] bool glue_active() const { return enabled && glue_enabled; }
    /// True when magnetic attraction should act: enabled && snap_enabled (SNAP-001).
    [[nodiscard]] bool snap_active() const { return enabled && snap_enabled; }
};

/// Parses a settings document. Missing or ill-typed keys fall back to defaults; numbers are clamped into the range of their key (CFG-011).
/// Returns nullopt when the text is not a JSON object (the caller keeps the previous settings). Called by the host.
[[nodiscard]] std::optional<Settings> parse_settings(std::string_view text);

/// Returns the settings document with the top-level `enabled` key set to `enabled`, every other key (known or
/// unknown) preserved in order. Empty or whitespace-only text yields a fresh object. Returns nullopt when the text
/// is not a JSON object (the caller must not overwrite the file). Pure: the host does the atomic file write.
[[nodiscard]] std::optional<std::string> with_enabled(std::string_view text, bool enabled);

}  // namespace mb
