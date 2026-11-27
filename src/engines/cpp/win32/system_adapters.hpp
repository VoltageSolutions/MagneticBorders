// Small adapters for the Clock, Timer, and Log ports: SteadyClock, Win32Timer, ConsoleLog.
#pragma once
#include <chrono>
#include <cstdio>
#include <string>

#include "app/ports.hpp"
#include "win_common.hpp"

namespace mb::win32 {

/// Clock adapter over std::chrono::steady_clock.
class SteadyClock final : public Clock {
public:
    /// Milliseconds on the steady clock.
    std::uint64_t now_ms() override {
        using namespace std::chrono;
        return static_cast<std::uint64_t>(
            duration_cast<milliseconds>(steady_clock::now().time_since_epoch()).count());
    }
};

/// SetTimer-based one-shot timers; must be used on the UI thread that runs the message loop.
/// At most one instance per thread. Kills its timers on destruction.
class Win32Timer final : public Timer {
public:
    /// Registers this instance as the thread's timer target; throws std::logic_error if one exists.
    Win32Timer();
    /// Cancels all timers.
    ~Win32Timer() override;
    Win32Timer(const Win32Timer&) = delete;
    Win32Timer& operator=(const Win32Timer&) = delete;
    /// Sets the receiver of timer expiry (the DragController); borrowed.
    void set_sink(TimerEvents& sink) { sink_ = &sink; }
    /// Replaces the timer of this kind with a new one-shot timer.
    void arm(TimerKind kind, unsigned delay_ms) override;
    /// Kills every pending timer.
    void cancel_all() override;

private:
    static void CALLBACK on_timer(HWND, UINT, UINT_PTR id, DWORD);
    TimerEvents* sink_{nullptr};
    UINT_PTR ids_[3]{};
};

/// Log adapter that prints to stdout.
class ConsoleLog final : public Log {
public:
    /// Creates the logger; debug lines print only when debug_enabled.
    explicit ConsoleLog(bool debug_enabled = false) : debug_(debug_enabled) {}
    /// Prints one line.
    void info(std::string_view msg) override {
        std::fprintf(stdout, "%.*s\n", static_cast<int>(msg.size()), msg.data());
        std::fflush(stdout);
    }
    /// Prints one line when debug output is enabled.
    void debug(std::string_view msg) override {
        if (debug_) info(msg);
    }

private:
    bool debug_;
};

}  // namespace mb::win32
