// Named-pipe protocol: status snapshot type and the pure request handler.
#pragma once
#include <cstdint>
#include <string>
#include <string_view>

#include "mb_version.hpp"
#include "settings.hpp"

namespace mb {

/// Snapshot of engine state, read on the UI thread when a request is handled.
/// Filled by host EngineApp::status and serialized by handle_pipe_request.
struct EngineStatus {
    /// Engine id.
    std::string engine{"cpp"};
    /// Engine version string.
    std::string version{kVersion};
    /// Process id.
    std::uint32_t pid{0};
    /// Settings currently in effect.
    Settings settings;
    /// Resize drags seen.
    std::uint64_t drags{0};
    /// Resize drags that were glued.
    std::uint64_t glued_drags{0};
    /// Seconds since the engine started.
    std::uint64_t uptime_s{0};
};

/// Side effect the host must perform after a request.
enum class PipeAction { None, Reload, Quit };

/// Result of handling one request line.
struct PipeReply {
    /// Response: one JSON object, no trailing newline.
    std::string line;  // one JSON object, no trailing newline
    /// Action for the host to perform on the UI thread.
    PipeAction action{PipeAction::None};
};

/// Pure protocol logic: request line + state -> response line + action. Commands: status, reload, quit;
/// anything else (or malformed JSON) yields {"ok":false,"error":"unknown command"}.
[[nodiscard]] PipeReply handle_pipe_request(std::string_view request_line,
                                            const EngineStatus& status);

}  // namespace mb
