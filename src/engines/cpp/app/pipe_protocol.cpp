// handle_pipe_request: parses one JSON command line and builds the reply.
#include "pipe_protocol.hpp"

#include <nlohmann/json.hpp>

namespace mb {
namespace {
using nlohmann::ordered_json;
}

PipeReply handle_pipe_request(std::string_view request_line, const EngineStatus& st) {
    const ordered_json req =
        ordered_json::parse(request_line.begin(), request_line.end(), nullptr, false);
    std::string cmd;
    if (req.is_object()) {
        const auto it = req.find("cmd");
        if (it != req.end() && it->is_string()) cmd = it->get<std::string>();
    }

    ordered_json r;
    PipeAction action = PipeAction::None;
    if (cmd == "status") {
        r = {{"ok", true},
             {"engine", st.engine},
             {"version", st.version},
             {"pid", st.pid},
             {"enabled", st.settings.enabled},
             {"glue",
              {{"enabled", st.settings.glue_enabled},
               {"tolerance_px", st.settings.glue_tolerance_px}}},
             {"drags", st.drags},
             {"glued_drags", st.glued_drags},
             {"uptime_s", st.uptime_s}};
    } else if (cmd == "reload") {
        r = {{"ok", true}};
        action = PipeAction::Reload;
    } else if (cmd == "quit") {
        r = {{"ok", true}};
        action = PipeAction::Quit;
    } else {
        r = {{"ok", false}, {"error", "unknown command"}};
    }
    return {r.dump(), action};
}

}  // namespace mb
