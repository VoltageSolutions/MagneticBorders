// PipeServer accept loop, per-client line reader, and the pipe_request client.
#include "pipe_server.hpp"

namespace mb::win32 {
namespace {

bool read_line(HANDLE pipe, std::string& pending, std::string& line) {
    for (;;) {
        if (const auto nl = pending.find('\n'); nl != std::string::npos) {
            line = pending.substr(0, nl);
            if (!line.empty() && line.back() == '\r') line.pop_back();
            pending.erase(0, nl + 1);
            return true;
        }
        char buf[4096];
        DWORD got = 0;
        if (!ReadFile(pipe, buf, sizeof(buf), &got, nullptr) || got == 0) return false;
        pending.append(buf, got);
        if (pending.size() > (1u << 20)) return false;
    }
}

void serve_client(HANDLE raw, const PipeServer::Handler& handler) {
    UniqueHandle pipe{raw};
    std::string pending, line;
    while (read_line(pipe.get(), pending, line)) {
        std::string reply = handler(line);
        reply.push_back('\n');
        DWORD written = 0;
        if (!WriteFile(pipe.get(), reply.data(), static_cast<DWORD>(reply.size()), &written,
                       nullptr))
            break;
        FlushFileBuffers(pipe.get());
    }
    DisconnectNamedPipe(pipe.get());
}

}  // namespace

PipeServer::PipeServer(Handler handler) : handler_(std::move(handler)) {
    thread_ = std::thread([this] { accept_loop(); });
}

PipeServer::~PipeServer() {
    stopping_ = true;
    // Unblock ConnectNamedPipe with a throw-away client.
    const HANDLE h =
        CreateFileW(kPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
    if (h != INVALID_HANDLE_VALUE) CloseHandle(h);
    if (thread_.joinable()) thread_.join();
}

void PipeServer::accept_loop() {
    bool first = true;
    while (!stopping_) {
        const HANDLE pipe = CreateNamedPipeW(
            kPipeName, PIPE_ACCESS_DUPLEX | (first ? FILE_FLAG_FIRST_PIPE_INSTANCE : 0),
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
            PIPE_UNLIMITED_INSTANCES, 65536, 65536, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) {
            if (stopping_) break;
            Sleep(200);
            first = false;
            continue;
        }
        first = false;
        const BOOL ok = ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED;
        if (!ok || stopping_) {
            CloseHandle(pipe);
            continue;
        }
        std::thread(serve_client, pipe, handler_).detach();
    }
}

std::optional<std::string> pipe_request(const std::string& request_line, DWORD timeout_ms) {
    if (!WaitNamedPipeW(kPipeName, timeout_ms)) return std::nullopt;
    UniqueHandle pipe{CreateFileW(kPipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr,
                                  OPEN_EXISTING, 0, nullptr)};
    if (pipe.get() == INVALID_HANDLE_VALUE || !pipe) return std::nullopt;
    const std::string msg = request_line + "\n";
    DWORD n = 0;
    if (!WriteFile(pipe.get(), msg.data(), static_cast<DWORD>(msg.size()), &n, nullptr))
        return std::nullopt;
    std::string pending, line;
    if (!read_line(pipe.get(), pending, line)) return std::nullopt;
    return line;
}

}  // namespace mb::win32
