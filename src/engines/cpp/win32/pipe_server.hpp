// Named-pipe server and one-shot client for the engine's JSON-lines control protocol.
#pragma once
#include <atomic>
#include <functional>
#include <optional>
#include <string>
#include <thread>

#include "win_common.hpp"

namespace mb::win32 {

/// Name of the control pipe.
inline constexpr const wchar_t* kPipeName = L"\\\\.\\pipe\\MagneticBorders";

/// Named-pipe server (UTF-8, one JSON line per request/response). Runs on background threads (one accept thread plus
/// one detached thread per client); the handler is invoked on those threads and must be thread-safe. Joins the accept
/// thread on destruction.
class PipeServer {
public:
    /// Maps a request line to a response line.
    using Handler = std::function<std::string(const std::string& request_line)>;
    /// Starts the accept thread.
    explicit PipeServer(Handler handler);
    /// Stops accepting and joins the accept thread.
    ~PipeServer();
    PipeServer(const PipeServer&) = delete;
    PipeServer& operator=(const PipeServer&) = delete;

private:
    void accept_loop();
    Handler handler_;
    std::atomic<bool> stopping_{false};
    std::thread thread_;
};

/// Client: sends one request line and returns the response line (nullopt if no server or on timeout). Any thread.
[[nodiscard]] std::optional<std::string> pipe_request(const std::string& request_line,
                                                      DWORD timeout_ms = 1000);

}  // namespace mb::win32
